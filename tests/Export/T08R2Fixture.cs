using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>One observation cycle: a snapshot plus only the evidence that belongs to it.</summary>
internal sealed record CycleEvidence
{
    public required NetworkSnapshot Snapshot { get; init; }

    public IReadOnlyList<ProbeResult> Probes { get; init; } = Array.Empty<ProbeResult>();

    public IReadOnlyList<EvidenceRecord> Records { get; init; } = Array.Empty<EvidenceRecord>();

    public IReadOnlyList<Diagnosis> Diagnoses { get; init; } = Array.Empty<Diagnosis>();
}

/// <summary>
/// Builds fully synthetic sessions for the T08-R2 privacy/completeness tests. Every identity,
/// address, URL and secret below is invented for this fixture; no captured package is used.
/// </summary>
internal static class T08R2Fixture
{
    public static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T05:00:00Z");

    public const string SessionId = "session-t08r2-synthetic";

    public const string BootId = "boot-t08r2-synthetic";

    public static async Task<string> WriteAsync(string path, IReadOnlyList<CycleEvidence> cycles,
        bool synthetic = true, Func<EventEnvelope, EventEnvelope>? transform = null)
    {
        using var log = new NdjsonEventLog(path);
        var sequence = 1;
        var cycleIndex = 0;
        foreach (var cycle in cycles)
        {
            cycleIndex++;
            var events = EvidenceSessionEvents.Create(
                new EvidenceSet
                {
                    Snapshot = cycle.Snapshot,
                    Probes = cycle.Probes,
                    Records = cycle.Records,
                },
                cycle.Diagnoses,
                SessionId,
                BootId,
                synthetic,
                cycle.Snapshot.ObservedAt.AddSeconds(1),
                cycle.Snapshot.MonotonicMs + 1000,
                batchId: cycleIndex.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var envelope in events)
            {
                var next = envelope with { Sequence = sequence++ };
                if (transform is not null)
                {
                    next = transform(next);
                }

                await log.AppendAsync(next, CancellationToken.None);
            }
        }

        return path;
    }

    public static NetworkSnapshot Snapshot(string snapshotId, DateTimeOffset observedAt, double monotonicMs,
        IReadOnlyList<InterfaceState> interfaces,
        EventStatus interfacesStatus = EventStatus.Ok,
        string? interfacesReason = null,
        IReadOnlyList<string>? interfacesErrors = null,
        IReadOnlyList<RouteEntry>? routes = null,
        EventStatus routesStatus = EventStatus.Ok,
        IReadOnlyList<string>? routesErrors = null,
        EventStatus winHttpStatus = EventStatus.Unsupported,
        string? winHttpReason = "Synthetic fixture does not read the WinHTTP store.",
        string? proxyNotice = null,
        IReadOnlyList<string>? proxyErrors = null,
        string? proxyServer = null,
        IReadOnlyList<string>? proxyVariables = null,
        string? autoConfigUrl = null,
        string suffix = "synthetic.invalid") => new()
    {
        SnapshotId = snapshotId,
        ObservedAt = observedAt,
        MonotonicMs = monotonicMs,
        Synthetic = true,
        Interfaces = interfacesStatus == EventStatus.Ok && interfacesErrors is null
            ? Collected<IReadOnlyList<InterfaceState>>.Ok(interfaces)
            : Collected<IReadOnlyList<InterfaceState>>.Partial(interfaces,
                interfacesReason ?? "Synthetic partial interface read.", interfacesErrors),
        Routes = routesStatus == EventStatus.Ok
            ? Collected<IReadOnlyList<RouteEntry>>.Ok(routes ?? DefaultRoutes())
            : Collected<IReadOnlyList<RouteEntry>>.Unavailable(routesStatus,
                "Synthetic partial route read.", routesErrors),
        Dns = Collected<DnsState>.Ok(new DnsState
        {
            ServersByInterface = new Dictionary<int, IReadOnlyList<string>> { [12] = new[] { "192.168.7.1" } },
            SuffixSearchList = new[] { suffix },
        }),
        Proxies = Collected<ProxyState>.Ok(new ProxyState
        {
            ToolProcessEnvironment = Layer("tool_process_environment",
                enabled: proxyServer is not null || proxyVariables is not null,
                proxyServer: proxyServer, variables: proxyVariables, notice: proxyNotice, errors: proxyErrors),
            UserEnvironment = Layer("user_environment"),
            MachineEnvironment = Layer("machine_environment"),
            WinInet = Layer("wininet", autoConfigUrl: autoConfigUrl, enabled: autoConfigUrl is not null),
            WinHttp = winHttpStatus == EventStatus.Ok
                ? Collected<ProxyLayer>.Ok(new ProxyLayer
                {
                    Layer = "winhttp",
                    Provenance = "synthetic",
                    Enabled = false,
                })
                : Collected<ProxyLayer>.Unavailable(winHttpStatus,
                    winHttpReason ?? "Synthetic fixture does not read the WinHTTP store."),
        }),
    };

    public static InterfaceState Interface(string guid, string name, int ifIndex,
        string? description = null, IReadOnlyList<string>? addresses = null, int? metric = 0,
        bool? automaticMetric = true, bool? disableDefaultRoutes = false, string family = "ipv4",
        string kind = "ethernet", string operationalStatus = "up") => new()
    {
        InterfaceGuid = guid,
        Name = name,
        IfIndex = ifIndex,
        Family = family,
        Description = description ?? "Synthetic adapter invented for an export fixture",
        Kind = kind,
        OperationalStatus = operationalStatus,
        Addresses = addresses ?? new[] { "192.168.7.10/24" },
        Metric = metric,
        AutomaticMetric = automaticMetric,
        DisableDefaultRoutes = disableDefaultRoutes,
        Luid = "synthetic-luid-" + ifIndex,
    };

    public static ProbeResult Probe(string probeId, string? transportError, string? notice = null,
        EventStatus status = EventStatus.Ok, string targetId = "target-t08r2") => new()
    {
        ProbeId = probeId,
        ProbeGroupId = "group-t08r2",
        TargetId = targetId,
        Path = ProbePath.NoExplicitProxy,
        Status = status,
        StartedAt = Origin.AddSeconds(1),
        DurationMs = 12,
        HttpStatus = status == EventStatus.Ok ? 204 : null,
        Classification = status == EventStatus.Ok
            ? ProbeClassification.ReachableExpected
            : ProbeClassification.TransportFailure,
        TransportError = transportError,
        AddressFamily = "ipv4",
        Method = "GET",
        RequestOptionsKey = "options-t08r2",
        ObservationWindowId = "window-t08r2",
        BootId = BootId,
        StartedMonotonicMs = 1000,
        TimingComparable = true,
        FailedEndpoint = status == EventStatus.Ok ? null : "target",
        FailedStage = status == EventStatus.Ok ? null : "connect",
        ResolvedAddresses = new[] { "192.168.7.1" },
        Notice = notice,
    };

    public static EvidenceRecord Record(string evidenceId, string summary,
        IReadOnlyDictionary<string, string>? facts = null, string? source = null) => new()
    {
        EvidenceId = evidenceId,
        Kind = "client_runtime_config",
        Summary = summary,
        ObservedAt = Origin,
        MonotonicMs = 0,
        BootId = BootId,
        Source = source ?? "synthetic.t08r2",
        Provenance = "runtime_api",
        Status = EventStatus.Ok,
        TargetId = "target-t08r2",
        AddressFamily = "ipv4",
        ObservationWindowId = "window-t08r2",
        Facts = facts ?? new Dictionary<string, string>
        {
            ["snapshot_id"] = "snapshot-t08r2-1",
            ["rule_name"] = "Synthetic Rule A",
        },
    };

    public static Diagnosis Diagnosis(string diagnosisId, string summary, string suggestedNextStep,
        params string[] evidenceIds) => new()
    {
        DiagnosisId = diagnosisId,
        RuleId = "DX-T08R2",
        RuleVersion = "9.9.9-synthetic",
        Level = EvidenceGrade.Supported,
        Summary = summary,
        EvidenceIds = evidenceIds.Length == 0 ? new[] { "snapshot-t08r2-1" } : evidenceIds,
        CounterEvidenceIds = Array.Empty<string>(),
        MissingEvidence = Array.Empty<string>(),
        AllowedRepairActions = Array.Empty<string>(),
        SuggestedNextStep = suggestedNextStep,
        Scope = new DiagnosisScope
        {
            TargetId = "target-t08r2",
            AddressFamily = "ipv4",
            StartedAt = Origin,
            EndedAt = Origin.AddSeconds(30),
            ValidUntil = Origin.AddSeconds(30),
            BootId = BootId,
            ObservationWindowId = "window-t08r2",
        },
    };

    public static IReadOnlyList<RouteEntry> DefaultRoutes() => new[]
    {
        new RouteEntry
        {
            DestinationPrefix = "192.168.7.0/24",
            Family = "ipv4",
            NextHop = "192.168.7.1",
            IfIndex = 12,
            RouteMetric = 0,
            InterfaceMetric = 25,
            Protocol = "dhcp",
        },
        new RouteEntry
        {
            DestinationPrefix = "198.18.0.0/15",
            Family = "ipv4",
            NextHop = "198.18.0.2",
            IfIndex = 12,
            RouteMetric = 0,
            InterfaceMetric = 25,
            Protocol = "manual",
        },
    };

    public static IReadOnlyDictionary<string, byte[]> ExportOrThrow(string sessionPath,
        TempWorkspace workspace, string folder = "packages")
    {
        var preview = new IncidentExporter().Preview(sessionPath);
        Assert.True(preview.CanExport, string.Join("; ", preview.BlockedReasons));
        var result = new IncidentExporter()
            .ExportAsync(sessionPath, workspace.Subdirectory(folder), CancellationToken.None)
            .GetAwaiter().GetResult();
        return PackageReader.Read(result.PackagePath);
    }

    public static IReadOnlyList<JsonObject> Envelopes(IReadOnlyDictionary<string, byte[]> entries) =>
        PackageReader.Text(entries["events.ndjson"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => PackageReader.Json(line)!.AsObject())
            .ToArray();

    public static IReadOnlyList<string> AllStrings(IReadOnlyDictionary<string, byte[]> entries) => entries
        .SelectMany(entry => PackageReader.StringsIn(entry.Value, entry.Key))
        .ToArray();

    public static void AssertAbsent(IReadOnlyDictionary<string, byte[]> entries, params string[] markers)
    {
        foreach (var (name, bytes) in entries)
        {
            var raw = PackageReader.Text(bytes);
            var strings = PackageReader.StringsIn(bytes, name);
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
                Assert.All(strings, value => Assert.DoesNotContain(marker, value, StringComparison.Ordinal));
            }
        }
    }

    private static Collected<ProxyLayer> Layer(string layer, bool enabled = false, string? proxyServer = null,
        IReadOnlyList<string>? variables = null, string? notice = null,
        IReadOnlyList<string>? errors = null, string? autoConfigUrl = null) => new()
    {
        Status = errors is null ? EventStatus.Ok : EventStatus.Partial,
        Value = new ProxyLayer
        {
            Layer = layer,
            Provenance = "synthetic",
            Enabled = enabled,
            ProxyServer = proxyServer,
            Variables = variables ?? Array.Empty<string>(),
            Notice = notice,
            AutoConfigUrl = autoConfigUrl,
        },
        Reason = errors is null ? null : "Synthetic partial proxy layer read.",
        Errors = errors ?? Array.Empty<string>(),
    };
}
