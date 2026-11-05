using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

public sealed class ProbeMetadataTests
{
    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.AccessDenied)]
    [InlineData(EventStatus.Unsupported)]
    [InlineData(EventStatus.Unknown)]
    [InlineData(EventStatus.Partial)]
    public void Observation_gaps_stay_inconclusive_even_if_an_http_status_was_retained(EventStatus status)
    {
        var target = ProbePlans.Default().Target;
        Assert.Equal(ProbeClassification.Inconclusive, ProbeClassificationPolicy.Classify(
            new TransportOutcome { Status = status }, target));
        Assert.Equal(ProbeClassification.Inconclusive, ProbeClassificationPolicy.Classify(
            new TransportOutcome { Status = status, HttpStatus = 204 }, target));
    }

    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.AccessDenied)]
    public async Task Active_cancelled_or_denied_transport_is_an_observation_gap(EventStatus status)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(
            TransportOutcome.Failure(status, "synthetic_gap")));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.Equal(3, transport.Requests.Count);
        Assert.All(result.Results, item =>
        {
            Assert.Equal(status, item.Status);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.False(item.TimingComparable);
        });
        Assert.False(result.TimingComparable);
    }

    [Fact]
    public async Task Every_measured_result_carries_the_frozen_request_and_time_identity()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var plan = ProbePlans.Default();
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.True(result.TimingComparable);
        Assert.All(result.Results, item =>
        {
            AssertIdentity(plan, item);
            Assert.True(item.TimingComparable);
            Assert.Equal("unknown", item.FailedEndpoint);
        });
    }

    [Fact]
    public async Task Every_path_cancelled_before_start_retains_identity_and_a_gap_reason()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var plan = ProbePlans.Default();
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, source.Token);

        Assert.Empty(transport.Requests);
        Assert.Equal(plan.Paths.ToArray(), result.MissingPaths);
        Assert.False(result.BudgetExhausted);
        Assert.False(result.TimingComparable);
        Assert.Equal(3, result.Results.Count);
        Assert.All(result.Results, item =>
        {
            AssertIdentity(plan, item);
            Assert.Equal("not_started_group_cancelled", item.TransportError);
            Assert.Equal(EventStatus.Cancelled, item.Status);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.False(item.TimingComparable);
        });
    }

    [Theory]
    [InlineData("window")]
    [InlineData("boot")]
    [InlineData("family")]
    public async Task Missing_scope_metadata_prevents_timing_comparability(string missing)
    {
        var plan = ProbePlans.Default();
        plan = missing switch
        {
            "window" => plan with { ObservationWindowId = null },
            "boot" => plan with { BootId = null },
            _ => plan with { AddressFamily = null },
        };
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.False(result.TimingComparable);
        Assert.All(result.Results, item => Assert.False(item.TimingComparable));
    }

    [Fact]
    public async Task Starts_outside_the_monotonic_window_invalidate_group_and_all_results()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var clock = new FakeClock(stepPerRead: TimeSpan.FromMilliseconds(500));
        var result = await new HttpProbeRunner(transport, clock).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.Equal(3, transport.Requests.Count);
        Assert.False(result.TimingComparable);
        Assert.All(result.Results, item => Assert.False(item.TimingComparable));
    }

    [Fact]
    public async Task System_default_does_not_infer_proxy_use_or_remote_resolution()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);
        var system = result.Results.Single(item => item.Path == ProbePath.SystemDefault);

        Assert.Equal("dotnet-default-proxy", system.ProxyStack);
        Assert.Equal("unknown", system.ResolutionLocation);
        Assert.Null(system.ProxyEndpointId);
        Assert.Null(system.ProxyIsLocal);
        Assert.Contains("environment", system.Notice);
        Assert.Contains("PAC", system.Notice);
    }

    [Fact]
    public async Task Proven_transport_endpoint_metadata_is_preserved_only_for_the_explicit_proxy_path()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(
            TransportOutcome.Failure(EventStatus.Failed, "ConnectionRefused", "connect")
                with { FailedEndpoint = "proxy", ProxyIsLocal = true }));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);
        var proxy = result.Results.Single(item => item.Path == ProbePath.ExplicitHttpProxy);

        Assert.Equal("proxy", proxy.FailedEndpoint);
        Assert.True(proxy.ProxyIsLocal);
        Assert.Equal("proxy-test", proxy.ProxyEndpointId);
        Assert.All(result.Results.Where(item => item.Path != ProbePath.ExplicitHttpProxy), item => Assert.Null(item.ProxyIsLocal));
    }

    [Fact]
    public async Task Actual_sent_method_and_metadata_match_the_canonical_options_key()
    {
        var plan = ProbePlans.Default();
        plan = plan with { Target = plan.Target with { Method = "get" } };
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.All(transport.Requests, item => Assert.Equal("GET", item.Method));
        Assert.All(result.Results, item => Assert.Equal("GET", item.Method));
        Assert.All(result.Results, item => Assert.Equal(ProbeIdentity.OptionsKey(plan), item.RequestOptionsKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_transport_execution_errors_are_unknown_observations_with_request_identity(bool synchronous)
    {
        const string privateDetail = "private-handler-detail";
        var transport = new FakeTransport((_, _) =>
        {
            if (synchronous) throw new InvalidOperationException(privateDetail);
            return Task.FromException<TransportOutcome>(new InvalidOperationException(privateDetail));
        });
        var plan = ProbePlans.Default();
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.Equal(3, transport.Requests.Count);
        Assert.Empty(result.MissingPaths);
        Assert.False(result.BudgetExhausted);
        Assert.False(result.TimingComparable);
        Assert.All(result.Results, item =>
        {
            AssertIdentity(plan, item);
            Assert.Equal(EventStatus.Unknown, item.Status);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.Equal("transport_execution_error", item.TransportError);
            Assert.Equal("unknown", item.FailedEndpoint);
            Assert.Null(item.HttpStatus);
            Assert.False(item.TimingComparable);
            Assert.Contains("whether a request was sent is unknown", item.Notice);
            Assert.DoesNotContain(privateDetail, item.TransportError ?? string.Empty);
            Assert.DoesNotContain(privateDetail, item.Notice ?? string.Empty);
        });
    }

    [Fact]
    public async Task A_local_handler_factory_exception_cannot_become_a_network_failure()
    {
        var factoryCalls = 0;
        using var transport = new HttpProbeTransport(handlerFactory: _ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("private-factory-detail");
        });
        var plan = ProbePlans.Default() with
        {
            AddressFamily = null,
            Paths = new[] { ProbePath.NoExplicitProxy },
        };
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.Equal(1, factoryCalls);
        Assert.Empty(result.MissingPaths);
        var item = Assert.Single(result.Results);
        AssertIdentity(plan, item);
        Assert.Equal(EventStatus.Unknown, item.Status);
        Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
        Assert.Equal("transport_execution_error", item.TransportError);
        Assert.Equal("unknown", item.FailedEndpoint);
        Assert.False(result.TimingComparable);
        Assert.False(item.TimingComparable);
        Assert.DoesNotContain("private-factory-detail", item.Notice ?? string.Empty);
    }

    private static void AssertIdentity(ProbeGroupPlan plan, ProbeResult item)
    {
        Assert.Equal(plan.Target.TargetId, item.TargetId);
        Assert.Equal(plan.AddressFamily, item.AddressFamily);
        Assert.Equal(plan.Target.Method, item.Method);
        Assert.Equal(ProbeIdentity.OptionsKey(plan), item.RequestOptionsKey);
        Assert.Equal(plan.ObservationWindowId, item.ObservationWindowId);
        Assert.Equal(plan.BootId, item.BootId);
        Assert.NotNull(item.StartedMonotonicMs);
        Assert.True(item.StartedMonotonicMs >= 0);
    }
}
