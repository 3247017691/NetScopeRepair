using NetScope.Domain;

namespace NetScope.Application.Mock;

/// <summary>Per-path outcomes for the synthetic probe runner, so tests can build variants.</summary>
public sealed record SyntheticScenario
{
    public EventStatus SystemDefault { get; init; } = EventStatus.Ok;

    public EventStatus NoExplicitProxy { get; init; } = EventStatus.Timeout;

    public EventStatus ExplicitHttpProxy { get; init; } = EventStatus.Ok;

    public IReadOnlyList<string> LocalResolvedAddresses { get; init; } = Array.Empty<string>();

    /// <summary>The M0 demo shape: the proxied paths work, the direct path times out.</summary>
    public static SyntheticScenario Baseline { get; } = new();

    /// <summary>All three paths work; useful as a negative control for failure rules.</summary>
    public static SyntheticScenario AllReachable { get; } = new()
    {
        SystemDefault = EventStatus.Ok,
        NoExplicitProxy = EventStatus.Ok,
        ExplicitHttpProxy = EventStatus.Ok,
    };

    public EventStatus For(ProbePath path) => path switch
    {
        ProbePath.SystemDefault => SystemDefault,
        ProbePath.NoExplicitProxy => NoExplicitProxy,
        ProbePath.ExplicitHttpProxy => ExplicitHttpProxy,
        _ => EventStatus.Unknown,
    };
}

/// <summary>
/// Deterministic probe runner that never opens a socket. It produces the same three-path shape
/// a real run would, so the storage, replay and rule layers can be built and tested first.
/// </summary>
public sealed class SyntheticProbeRunner : IProbeRunner
{
    private readonly SyntheticScenario _scenario;

    public SyntheticProbeRunner(SyntheticScenario? scenario = null)
    {
        _scenario = scenario ?? SyntheticScenario.Baseline;
    }

    public Task<ProbeGroupResult> RunAsync(ProbeGroupPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<ProbeResult>();
        var index = 0;

        foreach (var path in plan.Paths)
        {
            var status = _scenario.For(path);
            var durationMs = 200 + (index * 100);
            results.Add(Build(plan, path, status, durationMs, index));
            index++;
        }

        return Task.FromResult(new ProbeGroupResult
        {
            ProbeGroupId = plan.ProbeGroupId,
            StartedAt = plan.StartedAt,
            DurationMs = results.Count == 0 ? 0 : results.Max(result => result.DurationMs),
            Results = results,
            TimingComparable = true,
        });
    }

    private ProbeResult Build(ProbeGroupPlan plan, ProbePath path, EventStatus status, double durationMs, int index)
    {
        var isExplicit = path == ProbePath.ExplicitHttpProxy;
        var isSuccess = status.IsSuccess();

        return new ProbeResult
        {
            ProbeId = $"{plan.ProbeGroupId}-probe-{index + 1}",
            ProbeGroupId = plan.ProbeGroupId,
            TargetId = plan.Target.TargetId,
            Path = path,
            Status = status,
            StartedAt = plan.StartedAt.AddMilliseconds(index * 20),
            DurationMs = durationMs,
            HttpStatus = isSuccess ? plan.Target.ExpectedStatusCodes.FirstOrDefault() : null,
            Classification = status switch
            {
                EventStatus.Ok => ProbeClassification.ReachableExpected,
                EventStatus.Timeout => ProbeClassification.TransportFailure,
                EventStatus.AccessDenied => ProbeClassification.Inconclusive,
                EventStatus.Unsupported => ProbeClassification.Inconclusive,
                _ => ProbeClassification.Inconclusive,
            },
            TransportError = status switch
            {
                EventStatus.Timeout => "request_timeout",
                EventStatus.AccessDenied => "access_denied",
                EventStatus.Unsupported => "unsupported_path",
                _ => null,
            },
            ResolutionLocation = isExplicit ? "proxy" : "local",
            ProxyEndpointId = isExplicit ? plan.ProxyEndpointId : null,
            ProxyIsLocal = isExplicit && Uri.TryCreate(plan.ExplicitProxyEndpoint, UriKind.Absolute, out var proxy) ? proxy.IsLoopback : null,
            AddressFamily = plan.AddressFamily,
            Method = plan.Target.Method,
            RequestOptionsKey = ProbeIdentity.OptionsKey(plan),
            ObservationWindowId = plan.ObservationWindowId,
            BootId = plan.BootId,
            StartedMonotonicMs = index * 20,
            TimingComparable = true,
            FailedEndpoint = status.IsSuccess() ? null : "unknown",
            ResolvedAddresses = isExplicit ? Array.Empty<string>() : _scenario.LocalResolvedAddresses,
            ProxyStack = path switch
            {
                ProbePath.SystemDefault => "mock-windows-system-proxy",
                ProbePath.NoExplicitProxy => "no-application-proxy",
                ProbePath.ExplicitHttpProxy => "explicit-http-proxy",
                _ => "unknown",
            },
            FailedStage = status.IsSuccess() ? null : "unknown",
            TopologyChanged = false,
            Notice = "Response supplied by mock transport; no request was sent.",
        };
    }
}
