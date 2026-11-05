using NetScope.Application;

namespace NetScope.Adapters;

// All boundaries below are read-only. Nothing in this assembly starts, stops, restarts or
// reconfigures a client, and no boundary performs a network request. Tests replace every
// boundary with a deterministic fake so no real user file or machine state is read.

/// <summary>One process, as far as the platform let this process read it. Missing fields stay null.</summary>
public sealed record ProcessObservation
{
    public required int ProcessId { get; init; }

    public string? Name { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>File version from the executable metadata, when readable.</summary>
    public string? Version { get; init; }

    public DateTimeOffset? StartTime { get; init; }
}

/// <summary>Supplies process identity. It must never create, terminate or signal a process.</summary>
public interface IProcessSource
{
    Collected<IReadOnlyList<ProcessObservation>> ReadProcesses();
}

/// <summary>One TCP listening endpoint. Owner fields stay null when they could not be read.</summary>
public sealed record ListenerObservation
{
    public required string Protocol { get; init; }

    public required int Port { get; init; }

    public string? Host { get; init; }

    public int? OwnerProcessId { get; init; }

    public string? OwnerProcessName { get; init; }

    public string? OwnerExecutablePath { get; init; }
}

/// <summary>Supplies listening endpoints. It must never connect to them.</summary>
public interface IListenerSource
{
    Collected<IReadOnlyList<ListenerObservation>> ReadListeners();
}

/// <summary>One network adapter as reported by the platform, before any TUN heuristic is applied.</summary>
public sealed record NetworkAdapterObservation
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>ethernet / wifi / tunnel / loopback / ppp / unknown, mirroring the collector vocabulary.</summary>
    public required string Kind { get; init; }

    /// <summary>up / down / dormant / unknown.</summary>
    public required string OperationalStatus { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();
}

/// <summary>Supplies the adapter list. It must never enable, disable or reconfigure an adapter.</summary>
public interface INetworkAdapterSource
{
    Collected<IReadOnlyList<NetworkAdapterObservation>> ReadAdapters();
}

/// <summary>Outcome of one read-only configuration file read. No write method exists on purpose.</summary>
public sealed record ConfigFileReadResult
{
    public required bool Exists { get; init; }

    public byte[]? Content { get; init; }

    public DateTimeOffset? LastModifiedAt { get; init; }

    /// <summary>Short, non-sensitive reason code such as access_denied or io_error.</summary>
    public string? Error { get; init; }

    public static ConfigFileReadResult NotFound() => new() { Exists = false };

    public static ConfigFileReadResult Found(byte[] content, DateTimeOffset lastModifiedAt) => new()
    {
        Exists = true,
        Content = content,
        LastModifiedAt = lastModifiedAt,
    };

    public static ConfigFileReadResult Unreadable(string error) => new() { Exists = false, Error = error };
}

/// <summary>Reads a single already-known configuration file. It must never write or create a file.</summary>
public interface IConfigFileSource
{
    ConfigFileReadResult Read(string path);
}

/// <summary>
/// Reads one supported client configuration and returns already-minimized evidence. Implementations
/// must read only the documented allow-list and must never read subscriptions, credentials or
/// provider lists.
/// </summary>
public interface IClientConfigAdapter
{
    string ClientId { get; }

    Task<ClientConfigEvidence> ReadAsync(DateTimeOffset readAt, CancellationToken cancellationToken);
}

/// <summary>
/// Already-minimized facts supplied by a client runtime API through an injectable seam. The seam
/// itself is supplied by the caller; the default composition never calls a client controller.
/// </summary>
public sealed record ApexRuntimeEvidence
{
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    /// <summary>Short reason code when the runtime API was reachable but incomplete.</summary>
    public string? Error { get; init; }
}

/// <summary>Optional runtime API seam for the Apex client. M1 never performs a network call here.</summary>
public interface IApexRuntimeApi
{
    ApexRuntimeEvidence? TryRead();
}

/// <summary>Evidence that a client start command references a configuration path.</summary>
public sealed record ProcessStartReference
{
    public required string ClientId { get; init; }

    public required string ConfigPath { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// Supplies start-parameter evidence. A default implementation is not provided for M1 because
/// reading another process's command line is not available without broader privileges; when a
/// caller supplies this seam, disk evidence can be labelled process_start_reference.
/// </summary>
public interface IProcessStartReferenceSource
{
    ProcessStartReference? TryRead(string clientId);
}

/// <summary>One proxy configuration layer and whether it is set, without any proxy value.</summary>
public sealed record ProxyLayerObservation
{
    public required string Layer { get; init; }

    public required string Provenance { get; init; }

    public bool? Enabled { get; init; }

    /// <summary>Value-free explanation; never contains a proxy URL, credential or PAC body.</summary>
    public string? Detail { get; init; }

    /// <summary>Names of the proxy variables that exist, never their values.</summary>
    public IReadOnlyList<string> VariableNames { get; init; } = Array.Empty<string>();
}

/// <summary>Supplies proxy layers. It must never write an environment variable or registry value.</summary>
public interface IProxyLayerSource
{
    Collected<IReadOnlyList<ProxyLayerObservation>> ReadLayers();
}

/// <summary>Stable client ids used by the adapter and by the UI.</summary>
public static class KnownClientIds
{
    public const string Nice = "nice";

    public const string Apex = "apex";

    /// <summary>Aggregated listeners that cannot be attributed to a supported client.</summary>
    public const string UnknownListener = "unknown_listener";
}
