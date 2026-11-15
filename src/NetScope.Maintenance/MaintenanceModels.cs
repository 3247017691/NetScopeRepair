using System.Text.Json.Serialization;

namespace NetScope.Maintenance;

/// <summary>How serious a finding is. It never grants permission to change anything by itself.</summary>
public enum FindingSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Safe   = reversible, user-scope only, no elevation, applied by default.
/// Elevated = needs an administrator token (interface metric / machine scope / WinHTTP).
/// Risky  = touches hardware or DNS explicitly; only included with Deep = true and needs confirmation.
/// </summary>
public enum ActionRisk
{
    Safe,
    Elevated,
    Risky,
}

public sealed record ProxyLayerSnapshot(
    string Scope,
    string Name,
    string? Value,
    int Port,
    bool PortListening);

public sealed record InterfaceSnapshot(
    int Index,
    string Alias,
    bool HasDefaultRoute,
    string NextHop,
    int InterfaceMetric,
    string AutomaticMetric,
    string Dhcp,
    string Connectivity,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> DnsServers);

public sealed record GhostDeviceSnapshot(string InstanceId, string Name, string Status);

/// <summary>One connected TUN adapter; never a candidate for physical-egress metric raising.</summary>
public sealed record TunInterfaceSnapshot(
    int Index, string Alias, int InterfaceMetric, string AutomaticMetric,
    string IgnoreDefaultRoutes, int NlMtu, IReadOnlyList<string> Addresses);

/// <summary>Which process owns a listening candidate proxy port.</summary>
public sealed record PortOwnerSnapshot(int Port, int Pid, string ProcessName);

/// <summary>
/// One real, machine-local observation. Every field is either measured or explicitly unavailable;
/// an empty list never silently means "healthy".
/// </summary>
public sealed record MaintenanceSnapshot(
    DateTimeOffset ObservedAt,
    bool IsElevated,
    int ActiveEgressIndex,
    string ActiveEgressAlias,
    int? WorkingProxyPort,
    IReadOnlyList<int> ListeningProxyPorts,
    IReadOnlyList<ProxyLayerSnapshot> ProxyLayers,
    bool SystemProxyEnabled,
    string SystemProxyServer,
    int SystemProxyPort,
    bool WinHttpIsProxy,
    int WinHttpPort,
    IReadOnlyList<InterfaceSnapshot> Interfaces,
    IReadOnlyList<GhostDeviceSnapshot> GhostDevices,
    IReadOnlyList<string> Notes,
    IReadOnlyList<TunInterfaceSnapshot>? TunInterfaces = null,
    IReadOnlyList<PortOwnerSnapshot>? PortOwners = null,
    int BestRouteIndex = 0)
{
    public bool AnyProxyAlive => WorkingProxyPort is not null;
}

public sealed record Finding(
    string Code,
    string Title,
    string Detail,
    FindingSeverity Severity,
    IReadOnlyList<string> Evidence);

/// <summary>One typed, reversible change. No free-form command is ever stored or executed.</summary>
public sealed record RepairAction(
    string Id,
    string Title,
    string Detail,
    ActionRisk Risk,
    string Kind,
    string Target,
    string? Current,
    string? Desired,
    bool RequiresElevation,
    bool SelectedByDefault,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record RepairPlan(
    DateTimeOffset CreatedAt,
    MaintenanceSnapshot Snapshot,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RepairAction> Actions);

public sealed record ActionResult(
    string ActionId,
    string Title,
    bool Applied,
    string? Before,
    string? After,
    string? Error);

public sealed record RepairReport(
    bool DryRun,
    int Applied,
    int Skipped,
    int Failed,
    IReadOnlyList<ActionResult> Results,
    string? JournalPath,
    IReadOnlyList<string> Messages);

/// <summary>One recorded before/after pair. Restore replays Old, not a recomputed guess.</summary>
public sealed record JournalEntry(
    string Key,
    string Kind,
    string Scope,
    string Name,
    string? Old,
    string? New,
    string? Extra,
    DateTimeOffset RecordedAt);

public sealed class MaintenanceJournalDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("machine")]
    public string Machine { get; set; } = "";

    [JsonPropertyName("entries")]
    public List<JournalEntry> Entries { get; set; } = new();
}

public sealed record RestoreReport(
    int Restored,
    int Failed,
    IReadOnlyList<string> Messages,
    bool HadBackup);
