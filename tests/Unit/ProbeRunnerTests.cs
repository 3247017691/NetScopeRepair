using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>
/// Probe-runner behaviour: every requested path is attempted, failures stay distinguishable and
/// a path that never ran is reported as missing instead of as a success.
/// </summary>
public sealed class ProbeRunnerTests
{
    private static HttpProbeRunner Runner(FakeTransport transport, IClock? clock = null) =>
        new(transport, clock ?? new FakeClock());

    [Fact]
    public async Task Runs_every_requested_path_in_order()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var result = await Runner(transport).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.Equal(3, result.Results.Count);
        Assert.Empty(result.MissingPaths);
        Assert.Equal(
            new[] { ProbePath.SystemDefault, ProbePath.NoExplicitProxy, ProbePath.ExplicitHttpProxy },
            result.Results.Select(item => item.Path).ToArray());
        Assert.All(result.Results, item => Assert.Equal(ProbeClassification.ReachableExpected, item.Classification));
    }

    [Fact]
    public async Task Requests_share_target_and_method_and_differ_only_by_path()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var plan = ProbePlans.Default();

        await Runner(transport).RunAsync(plan, CancellationToken.None);

        Assert.Equal(3, transport.Requests.Count);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal(plan.Target.Endpoint, request.Endpoint);
            Assert.Equal(plan.Target.Method, request.Method);
        });
        Assert.Equal(
            plan.Paths.OrderBy(path => path).ToArray(),
            transport.Requests.Select(request => request.Path).OrderBy(path => path).ToArray());
    }

    [Fact]
    public async Task Probe_timeout_is_reported_as_timeout_not_as_failure_or_success()
    {
        var transport = new FakeTransport(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportOutcome.Reachable(204);
        });

        var result = await Runner(transport)
            .RunAsync(ProbePlans.Default(perProbeTimeout: TimeSpan.FromMilliseconds(30)), CancellationToken.None);

        Assert.Equal(3, result.Results.Count);
        Assert.All(result.Results, item =>
        {
            Assert.Equal(EventStatus.Timeout, item.Status);
            Assert.Equal(ProbeClassification.TransportFailure, item.Classification);
            Assert.Null(item.HttpStatus);
            Assert.False(item.IsSuccess);
        });
    }

    [Fact]
    public async Task Cancelling_the_group_is_recorded_as_cancelled()
    {
        using var source = new CancellationTokenSource();
        var transport = new FakeTransport(async (_, token) =>
        {
            source.Cancel();
            await Task.Delay(50, token);
            return TransportOutcome.Reachable(204);
        });

        var result = await Runner(transport).RunAsync(ProbePlans.Default(), source.Token);

        Assert.All(result.Results, item => Assert.Equal(EventStatus.Cancelled, item.Status));
        Assert.All(result.Results, item => Assert.False(item.IsSuccess));
    }

    [Fact]
    public async Task Success_without_http_status_is_downgraded_to_partial()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(new TransportOutcome { Status = EventStatus.Ok }));

        var result = await Runner(transport).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.All(result.Results, item =>
        {
            Assert.Equal(EventStatus.Partial, item.Status);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.False(item.IsSuccess);
        });
    }

    [Theory]
    [InlineData(204, ProbeClassification.ReachableExpected)]
    [InlineData(200, ProbeClassification.ReachableUnexpected)]
    [InlineData(404, ProbeClassification.ReachableUnexpected)]
    [InlineData(407, ProbeClassification.AuthenticationRequired)]
    [InlineData(401, ProbeClassification.AuthenticationRequired)]
    [InlineData(403, ProbeClassification.AuthenticationRequired)]
    public async Task Classification_separates_http_outcomes(int status, ProbeClassification expected)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(status)));

        var result = await Runner(transport).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.All(result.Results, item => Assert.Equal(expected, item.Classification));
    }

    [Fact]
    public async Task Unsupported_path_is_inconclusive_and_sends_no_request()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(new TransportOutcome
        {
            Status = EventStatus.Unsupported,
            TransportError = "address_family_not_enforced",
        }));

        var result = await Runner(transport).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.All(result.Results, item =>
        {
            Assert.Equal(EventStatus.Unsupported, item.Status);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.False(item.IsSuccess);
        });
    }

    [Fact]
    public async Task A_path_that_never_started_is_reported_as_missing_not_as_success()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var clock = new FakeClock(stepPerRead: TimeSpan.FromMilliseconds(10));

        var result = await Runner(transport, clock).RunAsync(
            ProbePlans.Default(perProbeTimeout: TimeSpan.FromSeconds(10), groupBudget: TimeSpan.FromMilliseconds(25), maxConcurrency: 1),
            CancellationToken.None);

        Assert.True(result.BudgetExhausted);
        Assert.NotEmpty(result.MissingPaths);
        Assert.Equal(3, result.Results.Count);
        Assert.All(result.Results, item => Assert.Equal(EventStatus.Cancelled, item.Status));
        Assert.All(result.Results.Where(item => result.MissingPaths.Contains(item.Path)), item =>
        {
            Assert.Equal("not_started_group_budget_exhausted", item.TransportError);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.Equal(0, item.DurationMs);
        });
        Assert.False(result.TimingComparable);
        Assert.All(result.Results, item => Assert.False(item.TimingComparable));
    }

    [Fact]
    public async Task No_explicit_proxy_path_never_claims_physical_direct()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));

        var result = await Runner(transport).RunAsync(ProbePlans.Default(), CancellationToken.None);
        var direct = result.Results.Single(item => item.Path == ProbePath.NoExplicitProxy);

        Assert.Equal("no-application-proxy", direct.ProxyStack);
        Assert.Null(direct.ProxyEndpointId);
        Assert.Contains("physical-direct", direct.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_the_explicit_path_carries_the_proxy_endpoint_id()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));

        var result = await Runner(transport).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.All(result.Results, item =>
            Assert.Equal(
                item.Path == ProbePath.ExplicitHttpProxy ? "proxy-test" : null,
                item.ProxyEndpointId));
    }

    [Fact]
    public void Classification_cannot_claim_reachable_without_data()
    {
        Assert.False(ProbeClassificationPolicy.IsConsistent(ProbeClassification.ReachableExpected, EventStatus.Timeout));
        Assert.False(ProbeClassificationPolicy.IsConsistent(ProbeClassification.AuthenticationRequired, EventStatus.Failed));
        Assert.True(ProbeClassificationPolicy.IsConsistent(ProbeClassification.ReachableExpected, EventStatus.Ok));
        Assert.True(ProbeClassificationPolicy.IsConsistent(ProbeClassification.TransportFailure, EventStatus.Timeout));
        Assert.True(ProbeClassificationPolicy.IsConsistent(ProbeClassification.Inconclusive, EventStatus.Unsupported));
    }
}
