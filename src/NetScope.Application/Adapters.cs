using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Application;

/// <summary>Where a client configuration observation came from. Never upgraded by inference.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<ConfigProvenance>))]
public enum ConfigProvenance
{
    [JsonStringEnumMemberName("runtime_api")]
    RuntimeApi,

    [JsonStringEnumMemberName("process_start_reference")]
    ProcessStartReference,

    [JsonStringEnumMemberName("disk_only")]
    DiskOnly,

    [JsonStringEnumMemberName("user_report")]
    UserReport,
}

/// <summary>One endpoint a client exposes or listens on, with the source that reported it.</summary>
public sealed record ClientEndpoint
{
    public required string Role { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Source { get; init; }

    /// <summary>ipv4 / ipv6 / unknown. Address families are reported separately, never merged.</summary>
    public string AddressFamily { get; init; } = "unknown";

    /// <summary>The owner PID exactly as read; null does not mean "no owner".</summary>
    public int? OwnerProcessId { get; init; }

    public string? OwnerProcessName { get; init; }

    /// <summary>read / unreadable / unsupported_family, mirroring the listener observation.</summary>
    public string? OwnerReadState { get; init; }

    public string? OwnerReadReason { get; init; }

    /// <summary>ui / core / unassigned. Attribution is a claim, so it always carries evidence.</summary>
    public string? Attribution { get; init; }

    /// <summary>Why this endpoint belongs to the shown client, or why it could not be assigned.</summary>
    public string? AttributionEvidence { get; init; }

    public string Display => Port is int port
        ? (string.IsNullOrWhiteSpace(Host) ? $"port {port}" : $"{Host}:{port}")
        : "unknown";
}

/// <summary>One observed process shown on a client card; UI and core instances stay separate.</summary>
public sealed record ClientProcess
{
    /// <summary>ui or core.</summary>
    public required string Role { get; init; }

    public required int ProcessId { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public string? Name { get; init; }

    public string? ExecutablePath { get; init; }

    public string? Version { get; init; }

    public int? ParentProcessId { get; init; }

    /// <summary>parent_child / same_install_directory / null when no attribution was made.</summary>
    public string? Association { get; init; }

    public string? AssociationEvidence { get; init; }

    /// <summary>Per-field reasons such as executable_path=access_denied.</summary>
    public IReadOnlyList<string> FieldGaps { get; init; } = Array.Empty<string>();
}

/// <summary>One machine-level TUN candidate with separate IPv4 and IPv6 address sets.</summary>
public sealed record TunCandidate
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Stable interface identity (GUID) when readable; an ifIndex alone is a locating hint.</summary>
    public string? Identity { get; init; }

    public int? InterfaceIndex { get; init; }

    public long? Luid { get; init; }

    public required string OperationalStatus { get; init; }

    /// <summary>The heuristic evidence that made this adapter a candidate.</summary>
    public required string Reason { get; init; }

    public IReadOnlyList<string> Ipv4Addresses { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Ipv6Addresses { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FieldGaps { get; init; } = Array.Empty<string>();
}

/// <summary>Per-fact coverage used to compute completeness without hiding ignored structures.</summary>
public static class ConfigFactCoverage
{
    public const string Present = "present";

    public const string MissingRequired = "missing_required";

    public const string MissingOptional = "missing_optional";

    public const string Invalid = "invalid";
}

/// <summary>
/// One configuration observation. Values are already minimized by the adapter; this type never
/// carries subscription bodies, credentials or full configuration text.
/// </summary>
public sealed record ClientConfigEvidence
{
    public required string ClientId { get; init; }

    public required string ParserVersion { get; init; }

    public required ConfigProvenance Provenance { get; init; }

    /// <summary>Local path of the source file, kept for the local UI only; export replaces it.</summary>
    public string? SourcePath { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    public DateTimeOffset? FileModifiedAt { get; init; }

    public string? ContentHash { get; init; }

    public bool? Valid { get; init; }

    public string? ValidityReason { get; init; }

    /// <summary>Whitelisted diagnostics facts, for example mixed_port or dns_mode.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Non-fatal parse notes: ignored structures and optional fields that are simply absent.
    /// They are shown to the user but never counted as gaps.
    /// </summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Whitelisted fact -> present / missing_required / missing_optional / invalid.</summary>
    public IReadOnlyDictionary<string, string> Coverage { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();
}

/// <summary>Read-only state of one proxy client; the UI must not offer stop/switch actions.</summary>
public sealed record ClientStatus
{
    public required string ClientId { get; init; }

    public required string DisplayName { get; init; }

    public bool? Running { get; init; }

    public string? Version { get; init; }

    /// <summary>UI process and every core process that could be attributed to this client.</summary>
    public IReadOnlyList<ClientProcess> Processes { get; init; } = Array.Empty<ClientProcess>();

    public IReadOnlyList<ClientEndpoint> Endpoints { get; init; } = Array.Empty<ClientEndpoint>();

    /// <summary>
    /// Per-client TUN ownership. Null/false/true never means "a machine-level adapter exists";
    /// M1 only has "unconfirmed" or "unknown" without a trustworthy owner source.
    /// </summary>
    public string? TunOwnership { get; init; }

    /// <summary>Kept for compatibility; only the system_tun row sets it from the machine observation.</summary>
    public bool? TunEnabled { get; init; }

    public string? TunAddress { get; init; }

    /// <summary>Machine-level candidates; populated on the system_tun row only.</summary>
    public IReadOnlyList<TunCandidate> TunCandidates { get; init; } = Array.Empty<TunCandidate>();

    public IReadOnlyList<ClientConfigEvidence> Evidence { get; init; } = Array.Empty<ClientConfigEvidence>();

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True only when every required fact of every evidence item is present and no gap remains.
    /// Optional or ignored structures are visible as notices/coverage but do not fake completeness.
    /// </summary>
    public bool IsComplete => Gaps.Count == 0
        && Evidence.Count > 0
        && Evidence.All(item => item.Valid == true
            && item.Gaps.Count == 0
            && item.Coverage.Count > 0
            && item.Coverage.Values.All(state => string.Equals(state, ConfigFactCoverage.Present, StringComparison.Ordinal)));
}

/// <summary>Supplies client state without writing anything or taking over a client.</summary>
public interface IClientStatusProvider
{
    Task<IReadOnlyList<ClientStatus>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>One layer an application can read proxy settings from, and whether it is known.</summary>
public sealed record DependencyLayer
{
    public required string Name { get; init; }

    /// <summary>known / unknown / unsupported.</summary>
    public required string State { get; init; }

    public required string Detail { get; init; }
}

/// <summary>Explains which settings a given application could inherit; it never changes them.</summary>
public sealed record DependencyExplanation
{
    public required string AppId { get; init; }

    public required string DisplayName { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<DependencyLayer> Layers { get; init; } = Array.Empty<DependencyLayer>();

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public required string Boundary { get; init; }
}

public interface IDependencyExplainer
{
    Task<IReadOnlyList<DependencyExplanation>> ExplainAsync(
        IReadOnlyList<ClientStatus> clients, CancellationToken cancellationToken);
}
