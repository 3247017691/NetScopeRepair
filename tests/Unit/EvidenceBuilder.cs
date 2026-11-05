using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

/// <summary>Builders for evidence fixtures, so each rule test states only what it cares about.</summary>
internal static class EvidenceBuilder
{
    private static int _probeCounter;

    public static ProbeResult Probe(
        ProbePath path,
        EventStatus status,
        string? failedStage = null,
        string group = "group-1",
        string target = "target-1",
        bool topologyChanged = false,
        IEnumerable<string>? resolvedAddresses = null,
        int? httpStatus = null,
        string? transportError = null,
        string? failedEndpoint = null,
        bool? proxyIsLocal = null) => new()
    {
        ProbeId = $"{group}-{path}-{Interlocked.Increment(ref _probeCounter)}",
        ProbeGroupId = group,
        TargetId = target,
        Path = path,
        Status = status,
        StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        StartedMonotonicMs = 1000,
        DurationMs = 100,
        HttpStatus = httpStatus,
        AddressFamily = "ipv4",
        Method = "GET",
        RequestOptionsKey = ProbeIdentity.OptionsKey(Plan()),
        ObservationWindowId = "window-1",
        BootId = "boot-1",
        TimingComparable = true,
        ProxyEndpointId = path == ProbePath.ExplicitHttpProxy ? "proxy-1" : null,
        FailedEndpoint = failedEndpoint,
        ProxyIsLocal = proxyIsLocal,
        Classification = status.IsSuccess()
            ? ProbeClassification.ReachableExpected
            : status is EventStatus.Failed or EventStatus.Timeout
                ? ProbeClassification.TransportFailure
                : ProbeClassification.Inconclusive,
        TransportError = transportError,
        FailedStage = failedStage,
        ResolvedAddresses = resolvedAddresses?.ToArray() ?? Array.Empty<string>(),
        TopologyChanged = topologyChanged,
    };

    public static EvidenceRecord Record(string id, string kind, params (string Key, string Value)[] facts) => new()
    {
        EvidenceId = id,
        Kind = kind,
        Summary = $"synthetic {kind}",
        Facts = facts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        ObservedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        MonotonicMs = 1000,
        BootId = "boot-1",
        ObservationWindowId = "window-1",
        Source = "synthetic-test-collector",
        Provenance = kind == "client_runtime_config" ? "runtime_api" : "synthetic_test",
        Status = EventStatus.Ok,
        AddressFamily = "ipv4",
        ProxyEndpointId = "proxy-1",
        TargetId = "target-1",
    };

    public static ProbeGroupPlan Plan() => new()
    {
        ProbeGroupId = "group-1",
        Target = new ProbeTarget { TargetId = "target-1", Endpoint = "https://example.invalid/health" },
        AddressFamily = "ipv4",
        ObservationWindowId = "window-1",
        BootId = "boot-1",
        StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
    };

    public static InterfaceState Interface(
        string guid = "guid-1",
        string name = "Ethernet",
        int ifIndex = 12,
        int? metric = 25,
        bool? automaticMetric = true,
        bool? disableDefaultRoutes = false) => new()
    {
        InterfaceGuid = guid,
        Name = name,
        IfIndex = ifIndex,
        Kind = "ethernet",
        OperationalStatus = "up",
        Metric = metric,
        AutomaticMetric = automaticMetric,
        DisableDefaultRoutes = disableDefaultRoutes,
    };

    public static RouteEntry Route(
        string prefix,
        string family = "ipv4",
        int ifIndex = 12,
        int routeMetric = 0,
        int? interfaceMetric = 25) => new()
    {
        DestinationPrefix = prefix,
        Family = family,
        IfIndex = ifIndex,
        RouteMetric = routeMetric,
        InterfaceMetric = interfaceMetric,
        Protocol = "netmgmt",
    };

    public static ProxyState Proxies(bool configured = false)
    {
        Collected<ProxyLayer> Layer(string name, bool enabled) => Collected<ProxyLayer>.Ok(new ProxyLayer
        {
            Layer = name,
            Provenance = "synthetic",
            Enabled = enabled,
            ProxyServer = enabled ? "http://127.0.0.1:7890" : null,
            Variables = enabled ? new[] { "HTTPS_PROXY=http://127.0.0.1:7890" } : Array.Empty<string>(),
        });

        var unsupported = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not collected in this fixture");

        return new ProxyState
        {
            ToolProcessEnvironment = configured ? Layer("tool_process_environment", true) : unsupported,
            UserEnvironment = configured ? Layer("user_environment", true) : unsupported,
            MachineEnvironment = unsupported,
            WinInet = unsupported,
            WinHttp = unsupported,
        };
    }

    public static NetworkSnapshot Snapshot(
        InterfaceState[]? interfaces = null,
        RouteEntry[]? routes = null,
        DnsState? dns = null,
        ProxyState? proxies = null) => new()
    {
        SnapshotId = "snapshot-1",
        ObservedAt = DateTimeOffset.UnixEpoch,
        MonotonicMs = 0,
        Synthetic = true,
        Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(interfaces ?? Array.Empty<InterfaceState>()),
        Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(routes ?? Array.Empty<RouteEntry>()),
        Dns = Collected<DnsState>.Ok(dns ?? new DnsState()),
        Proxies = Collected<ProxyState>.Ok(proxies ?? Proxies()),
    };

    public static EvidenceSet Evidence(
        IEnumerable<ProbeResult>? probes = null,
        IEnumerable<EvidenceRecord>? records = null,
        NetworkSnapshot? snapshot = null,
        IEnumerable<ProposedChange>? changes = null) => new()
    {
        Probes = probes?.ToArray() ?? Array.Empty<ProbeResult>(),
        Records = records?.ToArray() ?? Array.Empty<EvidenceRecord>(),
        Snapshot = snapshot,
        ProposedChanges = changes?.ToArray() ?? Array.Empty<ProposedChange>(),
    };
}
