using NetScope.Domain;

namespace NetScope.Application;

/// <summary>
/// One attempted observation of one data domain. A domain is never silently replaced by an
/// empty collection: when it could not be read, <see cref="Status"/> says why and
/// <see cref="Reason"/> is written for the user, so the UI can show a gap instead of health.
/// </summary>
public sealed record Collected<T>
    where T : class
{
    public required EventStatus Status { get; init; }

    public T? Value { get; init; }

    /// <summary>Short, user-facing explanation when the domain is unavailable.</summary>
    public string? Reason { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool HasValue => Status.ProducedData() && Value is not null;

    public static Collected<T> Ok(T value) => new() { Status = EventStatus.Ok, Value = value };

    public static Collected<T> Partial(T value, string reason, IEnumerable<string>? errors = null) =>
        new() { Status = EventStatus.Partial, Value = value, Reason = reason, Errors = errors?.ToArray() ?? [] };

    public static Collected<T> Unavailable(EventStatus status, string reason, IEnumerable<string>? errors = null) =>
        new() { Status = status, Reason = reason, Errors = errors?.ToArray() ?? [] };
}

/// <summary>
/// Everything a collector needs to produce one snapshot. Supplied by the caller so tests can
/// pin the clock and the identifiers instead of taking a dependency on the wall clock.
/// </summary>
public sealed record CollectionContext
{
    public required string SessionId { get; init; }

    public required string BootId { get; init; }

    public required string SnapshotId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required double MonotonicMs { get; init; }

    public required bool Synthetic { get; init; }

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A snapshot plus the status of every data domain it tried to read.</summary>
public sealed record NetworkSnapshot
{
    public required string SnapshotId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required double MonotonicMs { get; init; }

    public required bool Synthetic { get; init; }

    public required Collected<IReadOnlyList<InterfaceState>> Interfaces { get; init; }

    public required Collected<IReadOnlyList<RouteEntry>> Routes { get; init; }

    public required Collected<DnsState> Dns { get; init; }

    public required Collected<ProxyState> Proxies { get; init; }

    /// <summary>Domain name to status, so a partial snapshot is visible as a partial snapshot.</summary>
    public IReadOnlyDictionary<string, EventStatus> Completeness() => new Dictionary<string, EventStatus>
    {
        ["interfaces"] = Interfaces.Status,
        ["routes"] = Routes.Status,
        ["dns"] = Dns.Status,
        ["proxy"] = ProxyCompleteness(),
    };

    public EventStatus ProxyCompleteness()
    {
        if (Proxies.Status != EventStatus.Ok || Proxies.Value is not { } proxy) return Proxies.Status;
        return new[] { proxy.ToolProcessEnvironment, proxy.UserEnvironment, proxy.MachineEnvironment, proxy.WinInet, proxy.WinHttp }
            .All(layer => layer.Status == EventStatus.Ok) ? EventStatus.Ok : EventStatus.Partial;
    }
}

/// <summary>One network interface, identified by stable identifiers rather than a bare ifIndex.</summary>
public sealed record InterfaceState
{
    public required string InterfaceGuid { get; init; }

    public required string Name { get; init; }

    public required int IfIndex { get; init; }

    /// <summary>ipv4 / ipv6 / unknown; identifies the family of IfIndex and the scalar metric/flags.</summary>
    public string Family { get; init; } = "unknown";

    public string? Description { get; init; }

    /// <summary>ethernet / wifi / tunnel / loopback / unknown.</summary>
    public required string Kind { get; init; }

    /// <summary>up / down / dormant / unknown.</summary>
    public required string OperationalStatus { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>Interface metric as currently in effect, when readable.</summary>
    public int? Metric { get; init; }

    public bool? AutomaticMetric { get; init; }

    /// <summary>True when the interface refuses to install default routes.</summary>
    public bool? DisableDefaultRoutes { get; init; }

    public string? Luid { get; init; }

    public string? Reason { get; init; }
}

/// <summary>One routing table entry together with its metric attribution.</summary>
public sealed record RouteEntry
{
    public required string DestinationPrefix { get; init; }

    /// <summary>ipv4 or ipv6.</summary>
    public required string Family { get; init; }

    public string? NextHop { get; init; }

    public required int IfIndex { get; init; }

    public required int RouteMetric { get; init; }

    /// <summary>Metric of the interface the route is bound to, when readable.</summary>
    public int? InterfaceMetric { get; init; }

    /// <summary>Route protocol, for example "netmgmt", "dhcp" or "local".</summary>
    public required string Protocol { get; init; }

    /// <summary>Route origin, for example "manual", "dhcp" or "router_advertisement".</summary>
    public string? Origin { get; init; }

    /// <summary>Explains an observation gap, such as an unavailable interface metric.</summary>
    public string? Reason { get; init; }

    /// <summary>Effective metric, unknown when the interface metric could not be read.</summary>
    public long? TotalMetric => InterfaceMetric is int metric ? (long)RouteMetric + metric : null;
}

/// <summary>DNS configuration observed per interface.</summary>
public sealed record DnsState
{
    public IReadOnlyDictionary<int, IReadOnlyList<string>> ServersByInterface { get; init; } =
        new Dictionary<int, IReadOnlyList<string>>();

    public IReadOnlyList<string> SuffixSearchList { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Proxy configuration, kept in separate layers on purpose. A variable existing in one layer
/// says nothing about which requests the application actually routed through it.
/// </summary>
public sealed record ProxyState
{
    public required Collected<ProxyLayer> ToolProcessEnvironment { get; init; }

    public required Collected<ProxyLayer> UserEnvironment { get; init; }

    public required Collected<ProxyLayer> MachineEnvironment { get; init; }

    public required Collected<ProxyLayer> WinInet { get; init; }

    public required Collected<ProxyLayer> WinHttp { get; init; }
}

/// <summary>One proxy configuration layer and where its values came from.</summary>
public sealed record ProxyLayer
{
    public required string Layer { get; init; }

    /// <summary>Where the values were read from; never a guess about runtime behaviour.</summary>
    public required string Provenance { get; init; }

    public bool? Enabled { get; init; }

    public string? ProxyServer { get; init; }

    public string? AutoConfigUrl { get; init; }

    public bool? AutoDetect { get; init; }

    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();

    /// <summary>What this layer does and does not prove.</summary>
    public string? Notice { get; init; }
}
