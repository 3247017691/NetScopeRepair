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

    public string Display => Port is int port
        ? (string.IsNullOrWhiteSpace(Host) ? $"port {port}" : $"{Host}:{port}")
        : "unknown";
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

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();
}

/// <summary>Read-only state of one proxy client; the UI must not offer stop/switch actions.</summary>
public sealed record ClientStatus
{
    public required string ClientId { get; init; }

    public required string DisplayName { get; init; }

    public bool? Running { get; init; }

    public string? Version { get; init; }

    public IReadOnlyList<ClientEndpoint> Endpoints { get; init; } = Array.Empty<ClientEndpoint>();

    /// <summary>True when a TUN adapter is observed; null when it could not be determined.</summary>
    public bool? TunEnabled { get; init; }

    public string? TunAddress { get; init; }

    public IReadOnlyList<ClientConfigEvidence> Evidence { get; init; } = Array.Empty<ClientConfigEvidence>();

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>True only when every displayed field has a verified source.</summary>
    public bool IsComplete => Gaps.Count == 0 && Evidence.All(item => item.Valid == true && item.Gaps.Count == 0);
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
