using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

// Synthetic fixtures for the T07-R2 display regressions. Nothing here reads a real interface,
// a real client configuration or the network: every value is constructed in the test process.

internal static class T07R2Data
{
    public static readonly DateTimeOffset BaseTime = new(2026, 11, 4, 17, 19, 48, TimeSpan.FromHours(8));

    public static NetworkSnapshot Snapshot(
        string id,
        DateTimeOffset at,
        double monotonicMs,
        IReadOnlyList<InterfaceState>? interfaces = null,
        EventStatus interfacesStatus = EventStatus.Ok,
        string? interfacesReason = null,
        ProxyState? proxies = null,
        bool synthetic = true) => new()
    {
        SnapshotId = id,
        ObservedAt = at,
        MonotonicMs = monotonicMs,
        Synthetic = synthetic,
        Interfaces = interfacesStatus switch
        {
            EventStatus.Ok => Collected<IReadOnlyList<InterfaceState>>.Ok(interfaces ?? Array.Empty<InterfaceState>()),
            EventStatus.Partial => Collected<IReadOnlyList<InterfaceState>>.Partial(
                interfaces ?? Array.Empty<InterfaceState>(), interfacesReason ?? "test interface partial"),
            _ => Collected<IReadOnlyList<InterfaceState>>.Unavailable(
                interfacesStatus, interfacesReason ?? "test interface gap"),
        },
        Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(Array.Empty<RouteEntry>()),
        Dns = Collected<DnsState>.Ok(new DnsState()),
        Proxies = Collected<ProxyState>.Ok(proxies ?? Proxy()),
    };

    public static InterfaceState Interface(
        string guid,
        string name = "以太网",
        string family = "ipv4",
        int ifIndex = 44,
        int? metric = 0,
        bool? automaticMetric = false,
        bool? disableDefaultRoutes = false,
        string? luid = null,
        IReadOnlyList<string>? addresses = null,
        string operationalStatus = "up",
        string kind = "ethernet",
        string? reason = null) => new()
    {
        InterfaceGuid = guid,
        Name = name,
        IfIndex = ifIndex,
        Family = family,
        Description = null,
        Kind = kind,
        OperationalStatus = operationalStatus,
        Addresses = addresses ?? new[] { "192.0.2.10" },
        Metric = metric,
        AutomaticMetric = automaticMetric,
        DisableDefaultRoutes = disableDefaultRoutes,
        Luid = luid,
        Reason = reason,
    };

    public static ProxyState Proxy(
        bool enabled = false,
        string? server = null,
        IReadOnlyList<string>? variables = null) => new()
        {
        ToolProcessEnvironment = Layer("tool_process_environment", enabled, server, variables),
        UserEnvironment = Layer("user_environment", enabled, server, variables),
        MachineEnvironment = Layer("machine_environment", false, null, null),
        WinInet = Layer("wininet", enabled, server, null),
        WinHttp = Layer("winhttp", false, null, null),
    };

    private static Collected<ProxyLayer> Layer(string name, bool enabled, string? server,
        IReadOnlyList<string>? variables) => Collected<ProxyLayer>.Ok(new ProxyLayer
    {
        Layer = name,
        Provenance = "test.synthetic",
        Enabled = enabled ? true : null,
        ProxyServer = server,
        Variables = variables ?? Array.Empty<string>(),
        Notice = "synthetic proxy layer; no request was made",
    });

    public static ProbeResult Probe(
        ProbePath path,
        EventStatus status,
        string? family = "ipv4",
        bool timingComparable = true,
        string optionsKey = "options-1",
        string window = "window-1",
        string boot = "boot-1",
        string group = "group-1",
        string target = "target-lab",
        string method = "GET",
        double startedMonotonicMs = 1000,
        DateTimeOffset? startedAt = null,
        string? transportError = null,
        string? notice = null,
        string? failedStage = null,
        int? httpStatus = null,
        ProbeClassification? classification = null,
        string? resolutionLocation = null,
        IReadOnlyList<string>? resolvedAddresses = null,
        ProbeConnectionFact? connection = null) => new()
    {
        ProbeId = group + "-" + path.ToString().ToLowerInvariant(),
        ProbeGroupId = group,
        TargetId = target,
        Path = path,
        Status = status,
        StartedAt = startedAt ?? BaseTime.AddSeconds(1),
        StartedMonotonicMs = startedMonotonicMs,
        DurationMs = 12,
        HttpStatus = httpStatus ?? (status == EventStatus.Ok ? 204 : null),
        Classification = classification ?? (status == EventStatus.Ok
            ? ProbeClassification.ReachableExpected
            : status is EventStatus.Failed or EventStatus.Timeout
                ? ProbeClassification.TransportFailure
                : ProbeClassification.Inconclusive),
        TransportError = transportError,
        ResolutionLocation = resolutionLocation ?? (path == ProbePath.ExplicitHttpProxy ? "proxy" : "local"),
        ResolvedAddresses = resolvedAddresses ?? Array.Empty<string>(),
        Connection = connection,
        ProxyEndpointId = path == ProbePath.ExplicitHttpProxy ? "proxy-lab" : null,
        AddressFamily = family,
        Method = method,
        RequestOptionsKey = optionsKey,
        ObservationWindowId = window,
        BootId = boot,
        TimingComparable = timingComparable,
        FailedEndpoint = status == EventStatus.Ok ? null : "unknown",
        ProxyIsLocal = path == ProbePath.ExplicitHttpProxy ? true : null,
        ProxyStack = path.ToString(),
        FailedStage = failedStage,
        TopologyChanged = false,
        Notice = notice,
    };

    public static Diagnosis Diagnosis(
        string id,
        IReadOnlyList<string> evidenceIds,
        EvidenceGrade level = EvidenceGrade.Observed,
        IReadOnlyList<string>? counterEvidenceIds = null) => new()
    {
        DiagnosisId = id,
        RuleId = "DX-001",
        RuleVersion = "1.1.0",
        Level = level,
        Summary = "合成观测：三条路径记录到不同结果。",
        EvidenceIds = evidenceIds,
        CounterEvidenceIds = counterEvidenceIds ?? Array.Empty<string>(),
        MissingEvidence = Array.Empty<string>(),
        SuggestedNextStep = "继续观察；不要据此修改系统。",
        Scope = new DiagnosisScope
        {
            TargetId = "target-lab",
            AddressFamily = "ipv4",
            Path = ProbePath.ExplicitHttpProxy,
        },
    };

    public static SessionCycle Cycle(
        NetworkSnapshot snapshot,
        IReadOnlyList<ProbeResult> probes,
        IReadOnlyList<Diagnosis> diagnoses,
        DateTimeOffset completedAt,
        bool persisted = true,
        bool diagnosisRequested = false,
        IReadOnlyList<RuleEvaluationFailure>? failures = null) => new()
    {
        Snapshot = snapshot,
        Evidence = new EvidenceSet
        {
            Snapshot = snapshot,
            Probes = probes,
            Records = Array.Empty<EvidenceRecord>(),
        },
        Diagnoses = diagnoses,
        RuleFailures = failures ?? Array.Empty<RuleEvaluationFailure>(),
        CompletedAt = completedAt,
        Persisted = persisted,
        AppendedEvents = 1,
        DiagnosisRequested = diagnosisRequested,
        ObservationWindowId = "window-1",
    };
}

/// <summary>A collector that never finishes by itself, so session state stays Observing deterministically.</summary>
internal sealed class BlockingSnapshotCollector : ISnapshotCollector
{
    public string Source => "test.blocking";

    public string SourceVersion => "1.0";

    public async Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The blocking test collector must be cancelled, not completed.");
    }
}

/// <summary>Two synthetic snapshots with a changed metric and flag, written through a real session log.</summary>
internal sealed class VaryingInterfaceCollector : ISnapshotCollector
{
    private int _calls;

    public string Source => "test.varying-interfaces";

    public string SourceVersion => "1.0";

    public Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        var interfaces = new[]
        {
            T07R2Data.Interface("guid-shared", metric: call == 1 ? 0 : 4225,
                disableDefaultRoutes: call > 1, addresses: new[] { "192.0.2.10" }),
        };
        return Task.FromResult(T07R2Data.Snapshot(context.SnapshotId, context.ObservedAt,
            context.MonotonicMs, interfaces, synthetic: context.Synthetic));
    }
}
