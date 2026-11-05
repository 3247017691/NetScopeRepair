using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Application;

/// <summary>
/// The three comparable probe paths. <see cref="NoExplicitProxy"/> still keeps the system
/// routing table, so under TUN it may still traverse the tunnel: it must never be labelled
/// physical-direct.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ProbePath>))]
public enum ProbePath
{
    [JsonStringEnumMemberName("system-default")]
    SystemDefault,

    [JsonStringEnumMemberName("no-explicit-proxy")]
    NoExplicitProxy,

    [JsonStringEnumMemberName("explicit-http-proxy")]
    ExplicitHttpProxy,
}

/// <summary>What one probe actually demonstrated. Connectivity and HTTP status stay separate.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<ProbeClassification>))]
public enum ProbeClassification
{
    [JsonStringEnumMemberName("reachable_expected")]
    ReachableExpected,

    [JsonStringEnumMemberName("reachable_unexpected")]
    ReachableUnexpected,

    [JsonStringEnumMemberName("authentication_required")]
    AuthenticationRequired,

    [JsonStringEnumMemberName("transport_failure")]
    TransportFailure,

    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}

/// <summary>The frozen target description shared by every path in one comparison group.</summary>
public sealed record ProbeTarget
{
    public required string TargetId { get; init; }

    public required string Endpoint { get; init; }

    public string Method { get; init; } = "GET";

    /// <summary>Status codes that prove the target answered as designed, for example 204.</summary>
    public IReadOnlyList<int> ExpectedStatusCodes { get; init; } = new[] { 204 };
}

/// <summary>
/// One comparison group. Every path runs with the same target, method and time window, so a
/// difference between the results can be attributed to the path instead of to the target.
/// </summary>
public sealed record ProbeGroupPlan
{
    public required string ProbeGroupId { get; init; }

    public required ProbeTarget Target { get; init; }

    public IReadOnlyList<ProbePath> Paths { get; init; } = new[]
    {
        ProbePath.SystemDefault,
        ProbePath.NoExplicitProxy,
        ProbePath.ExplicitHttpProxy,
    };

    /// <summary>Explicit proxy endpoint for the third path; never hardcoded to a well-known port.</summary>
    public string? ExplicitProxyEndpoint { get; init; }

    public string? ProxyEndpointId { get; init; }

    public string? AddressFamily { get; init; }

    public string? ObservationWindowId { get; init; }

    public string? BootId { get; init; }

    public TimeSpan PerProbeTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan GroupBudget { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Maximum response body the prober will read; a large page is not a probe target.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024;

    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>Outcome of exactly one path against exactly one target.</summary>
public sealed record ProbeResult
{
    public required string ProbeId { get; init; }

    public required string ProbeGroupId { get; init; }

    public required string TargetId { get; init; }

    public required ProbePath Path { get; init; }

    public required EventStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required double DurationMs { get; init; }

    public int? HttpStatus { get; init; }

    public required ProbeClassification Classification { get; init; }

    public string? TransportError { get; init; }

    public int? OsErrorCode { get; init; }

    /// <summary>local / proxy / unknown; DNS may be resolved by the proxy, not by this host.</summary>
    public string? ResolutionLocation { get; init; }

    public IReadOnlyList<string> ResolvedAddresses { get; init; } = Array.Empty<string>();

    public string? ProxyEndpointId { get; init; }

    public string? AddressFamily { get; init; }

    public string? Method { get; init; }

    public string? RequestOptionsKey { get; init; }

    public string? ObservationWindowId { get; init; }

    public string? BootId { get; init; }

    public double? StartedMonotonicMs { get; init; }

    /// <summary>True only when the producer proved this sample belongs to a comparable window.</summary>
    public bool TimingComparable { get; init; }

    /// <summary>Proven failed connection endpoint: proxy / target / unknown.</summary>
    public string? FailedEndpoint { get; init; }

    public bool? ProxyIsLocal { get; init; }

    /// <summary>Which proxy stack was used, for example "explicit http proxy" or "system default".</summary>
    public string? ProxyStack { get; init; }

    /// <summary>Stage that failed: resolve / connect / proxy_connect / tls / http / unknown.</summary>
    public string? FailedStage { get; init; }

    public bool TopologyChanged { get; init; }

    public string? Notice { get; init; }

    public bool IsSuccess => Status.IsSuccess();
}

/// <summary>All path results for one group, including the case where the budget ran out.</summary>
public sealed record ProbeGroupResult
{
    public required string ProbeGroupId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required double DurationMs { get; init; }

    public required IReadOnlyList<ProbeResult> Results { get; init; }

    /// <summary>True when the group stopped early; missing paths are a gap, not a success.</summary>
    public bool BudgetExhausted { get; init; }

    public IReadOnlyList<ProbePath> MissingPaths { get; init; } = Array.Empty<ProbePath>();

    /// <summary>True only when the producer proved scope, timing and topology are comparable.</summary>
    public bool TimingComparable { get; init; }
}
