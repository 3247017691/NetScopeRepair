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

/// <summary>
/// Wire vocabulary for the R1-B family contract. The requested family is a request policy;
/// the local connection family and the target family are observations and may be unknown.
/// </summary>
public static class ProbeFamilyPolicy
{
    /// <summary>No constraint was requested; the value covers null/empty/any/dual/unspecified.</summary>
    public const string Any = "any";

    public const string Ipv4 = "ipv4";

    public const string Ipv6 = "ipv6";

    /// <summary>Normalizes a raw plan/request value; an invalid value is returned unchanged so callers refuse it.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Any;
        var value = raw.Trim().ToLowerInvariant();
        return value switch
        {
            "ipv4" or "v4" => Ipv4,
            "ipv6" or "v6" => Ipv6,
            "any" or "dual" or "unspecified" => Any,
            _ => value,
        };
    }

    public static bool IsKnown(string? raw)
    {
        var value = Normalize(raw);
        return value is Any or Ipv4 or Ipv6;
    }

    public static bool IsEnforcedFamily(string? raw) => Normalize(raw) is Ipv4 or Ipv6;
}

/// <summary>What one probe attempt could prove about the target address family.</summary>
public static class ProbeTargetFamilyStatus
{
    /// <summary>This process resolved and dialled the target; TargetAddressFamily is proven.</summary>
    public const string Observed = "observed";

    /// <summary>The explicit proxy resolves the target; only the proxy-leg connection is observed.</summary>
    public const string UnknownThroughProxy = "unknown_through_proxy";

    /// <summary>The system stack may use a proxy/PAC/tunnel; the target family is not observable.</summary>
    public const string UnknownSystemDefault = "unknown_system_default";

    /// <summary>The request was attempted locally but no connection proved the target family.</summary>
    public const string UnknownAfterAttempt = "unknown_after_attempt";

    /// <summary>The request was refused or never started, so no family was attempted.</summary>
    public const string NotAttempted = "not_attempted";
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

    /// <summary>Provenance of the local resolution sample, for example "system-dns"; null when unknown.</summary>
    public string? ResolutionSource { get; init; }

    /// <summary>UTC time of the local resolution that fed the connection; null when none was observed.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>What this attempt really proved about its TCP connection; null when it observed nothing.</summary>
    public ProbeConnectionFact? Connection { get; init; }

    public string? ProxyEndpointId { get; init; }

    /// <summary>
    /// Legacy request value (null/any/ipv4/ipv6) kept for old readers and exports. New producers
    /// also write <see cref="RequestedAddressFamily"/> with the normalized policy.
    /// </summary>
    public string? AddressFamily { get; init; }

    /// <summary>Requested policy family: <c>any</c> (no constraint), <c>ipv4</c> or <c>ipv6</c>.</summary>
    public string? RequestedAddressFamily { get; init; }

    /// <summary>
    /// Family of the TCP connection this process really made: the target leg with no explicit
    /// proxy, the proxy leg with an explicit proxy, or the actual next hop for the system default
    /// stack when the connect seam observed it. Null when unknown; never filled from the policy.
    /// </summary>
    public string? LocalConnectionAddressFamily { get; init; }

    /// <summary>Proven target family; only set when this process itself resolved and dialled the target.</summary>
    public string? TargetAddressFamily { get; init; }

    /// <summary>One of <see cref="ProbeTargetFamilyStatus"/> constants; null only for legacy events.</summary>
    public string? TargetAddressFamilyStatus { get; init; }

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

/// <summary>
/// One attempt's proven TCP connection fact. Unknown parts stay null: a probe never guesses an
/// address, a leg or a reuse it did not observe.
/// </summary>
public sealed record ProbeConnectionFact
{
    /// <summary>target / proxy / unknown: the leg this connection was dialled for.</summary>
    public required string EndpointRole { get; init; }

    /// <summary>IP literal the socket really connected to; null when no new connection was made.</summary>
    public string? RemoteAddress { get; init; }

    public int? RemotePort { get; init; }

    /// <summary>
    /// True only when the attempt provably reused a pooled connection and therefore performed no
    /// new name resolution; null when the attempt could not tell.
    /// </summary>
    public bool? Reused { get; init; }
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


