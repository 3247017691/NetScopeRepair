using System.Net;
using NetScope.Application;
using NetScope.Diagnostics.Rules;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>Uses production runner metadata rather than manually forcing gaps to be comparable.</summary>
public sealed class DiagnosticRunnerIntegrationTests
{
    [Fact]
    public async Task DX008_retains_a_production_denied_gap_alongside_comparable_success_and_timeout()
    {
        var outcomes = new Queue<TransportOutcome>(new[]
        {
            TransportOutcome.Reachable(204),
            TransportOutcome.Failure(EventStatus.Timeout, "Timeout", "connect"),
            TransportOutcome.Failure(EventStatus.AccessDenied, "access_denied", "connect"),
        });
        var transport = new FakeTransport((_, _) => Task.FromResult(outcomes.Dequeue()));
        var probes = await RunSinglePathGroups(transport, 3);

        Assert.True(probes[0].TimingComparable);
        Assert.True(probes[1].TimingComparable);
        Assert.False(probes[2].TimingComparable);
        Assert.Equal(ProbeClassification.Inconclusive, probes[2].Classification);
        Assert.Single(probes.Select(probe => probe.RequestOptionsKey).Distinct());
        Assert.All(probes, probe =>
        {
            Assert.Equal("window-test", probe.ObservationWindowId);
            Assert.Equal("boot-test", probe.BootId);
            Assert.Equal("proxy-test", probe.ProxyEndpointId);
            Assert.Equal("ipv4", probe.AddressFamily);
        });

        var diagnosis = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: probes));
        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Observed, diagnosis.Level);
        Assert.Contains("1/2 次有效网络失败", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("观察缺口 1/3", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("access_denied", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Equal(probes.Select(probe => probe.ProbeId), diagnosis.EvidenceIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DX008_local_transport_execution_error_is_a_gap_and_never_a_network_failure(bool withNetworkTimeout)
    {
        var calls = 0;
        var transport = new FakeTransport((_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) return Task.FromResult(TransportOutcome.Reachable(204));
            if (withNetworkTimeout && call == 2)
                return Task.FromResult(TransportOutcome.Failure(EventStatus.Timeout, "Timeout", "connect"));
            return Task.FromException<TransportOutcome>(new InvalidOperationException("synthetic local execution error"));
        });
        var probes = await RunSinglePathGroups(transport, withNetworkTimeout ? 3 : 2);
        var gap = probes[^1];
        Assert.Equal(EventStatus.Unknown, gap.Status);
        Assert.Equal("transport_execution_error", gap.TransportError);
        Assert.Equal(ProbeClassification.Inconclusive, gap.Classification);
        Assert.False(gap.TimingComparable);
        var diagnosis = new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: probes));

        if (!withNetworkTimeout)
        {
            Assert.Null(diagnosis);
        }
        else
        {
            Assert.NotNull(diagnosis);
            Assert.Equal(EvidenceGrade.Observed, diagnosis.Level);
            Assert.Contains("1/2 次有效网络失败", diagnosis.Summary, StringComparison.Ordinal);
            Assert.Contains("观察缺口 1/3", diagnosis.Summary, StringComparison.Ordinal);
            Assert.Contains("transport_execution_error", diagnosis.Summary, StringComparison.Ordinal);
            Assert.Contains(gap.ProbeId, diagnosis.EvidenceIds);
        }
    }

    [Fact]
    public async Task DX008_actual_handler_factory_exception_does_not_become_a_network_fault()
    {
        var factoryCalls = 0;
        using var healthyTransport = new HttpProbeTransport(handlerFactory: _ => new NoContentHandler());
        using var faultyTransport = new HttpProbeTransport(handlerFactory: _ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("synthetic handler setup failure");
        });
        // The real transport explicitly declines family enforcement; keep that metadata unknown.
        // Both handlers are local in-memory implementations and open no sockets.
        var plan = SinglePathPlan() with { AddressFamily = null };
        var healthy = Assert.Single((await new HttpProbeRunner(healthyTransport, new FakeClock())
            .RunAsync(plan with { ProbeGroupId = "factory-healthy" }, CancellationToken.None)).Results);
        var gap = Assert.Single((await new HttpProbeRunner(faultyTransport, new FakeClock())
            .RunAsync(plan with { ProbeGroupId = "factory-error" }, CancellationToken.None)).Results);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(EventStatus.Ok, healthy.Status);
        Assert.Equal(EventStatus.Unknown, gap.Status);
        Assert.Equal("transport_execution_error", gap.TransportError);
        Assert.Equal(ProbeClassification.Inconclusive, gap.Classification);
        Assert.False(gap.TimingComparable);
        Assert.Equal(healthy.RequestOptionsKey, gap.RequestOptionsKey);
        Assert.Null(new Dx008IntermittentFailure().Evaluate(EvidenceBuilder.Evidence(probes: new[] { healthy, gap })));
    }

    private static async Task<ProbeResult[]> RunSinglePathGroups(IProbeTransport transport, int count)
    {
        var runner = new HttpProbeRunner(transport, new FakeClock());
        var plan = SinglePathPlan();
        var probes = new List<ProbeResult>();
        for (var index = 0; index < count; index++)
        {
            var group = await runner.RunAsync(plan with { ProbeGroupId = $"scope-group-{index + 1}" }, CancellationToken.None);
            probes.Add(Assert.Single(group.Results));
        }
        return probes.ToArray();
    }

    private static ProbeGroupPlan SinglePathPlan() => ProbePlans.Default() with
    {
        Paths = new[] { ProbePath.ExplicitHttpProxy },
    };

    private sealed class NoContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
    }
}
