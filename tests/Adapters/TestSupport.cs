using NetScope.Adapters;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters.Tests;

/// <summary>Locates synthetic fixtures and the product source tree; never touches user data.</summary>
internal static class RepoPaths
{
    /// <summary>A fake value that must never appear in any produced fact, note, gap or reason.</summary>
    public const string SyntheticSecret = "SYNTHETIC_SECRET_TOKEN_9F3A7C";

    public static string FixtureDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ProductSourceDirectory => Path.Combine(RepoRoot, "src", "NetScope.Adapters");

    public static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    public static byte[] FixtureBytes(string name) => File.ReadAllBytes(Fixture(name));

    public static string FixtureText(string name) => File.ReadAllText(Fixture(name));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "NetScope.Adapters")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located from the test output directory.");
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class SyntheticConfigFileSource : IConfigFileSource
{
    private readonly Dictionary<string, ConfigFileReadResult> _files = new(StringComparer.OrdinalIgnoreCase);

    public int ReadCount { get; private set; }

    public string? LastPath { get; private set; }

    public SyntheticConfigFileSource AddFile(string path, byte[] content, DateTimeOffset? modifiedAt = null)
    {
        _files[path] = ConfigFileReadResult.Found(content, modifiedAt ?? new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.Zero));
        return this;
    }

    public SyntheticConfigFileSource AddFixture(string path, string fixtureName, DateTimeOffset? modifiedAt = null) =>
        AddFile(path, RepoPaths.FixtureBytes(fixtureName), modifiedAt);

    public SyntheticConfigFileSource AddUnreadable(string path, string error)
    {
        _files[path] = ConfigFileReadResult.Unreadable(error);
        return this;
    }

    public ConfigFileReadResult Read(string path)
    {
        ReadCount++;
        LastPath = path;
        return _files.TryGetValue(path, out var result) ? result : ConfigFileReadResult.NotFound();
    }
}

internal sealed class FakeProcessSource : IProcessSource
{
    public Collected<IReadOnlyList<ProcessObservation>> Result { get; set; } =
        Collected<IReadOnlyList<ProcessObservation>>.Ok([]);

    public int ReadCount { get; private set; }

    public Collected<IReadOnlyList<ProcessObservation>> ReadProcesses()
    {
        ReadCount++;
        return Result;
    }
}

internal sealed class FakeListenerSource : IListenerSource
{
    public Collected<IReadOnlyList<ListenerObservation>> Result { get; set; } =
        Collected<IReadOnlyList<ListenerObservation>>.Ok([]);

    public int ReadCount { get; private set; }

    public Collected<IReadOnlyList<ListenerObservation>> ReadListeners()
    {
        ReadCount++;
        return Result;
    }
}

internal sealed class FakeNetworkAdapterSource : INetworkAdapterSource
{
    public Collected<IReadOnlyList<NetworkAdapterObservation>> Result { get; set; } =
        Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok([]);

    public int ReadCount { get; private set; }

    public Collected<IReadOnlyList<NetworkAdapterObservation>> ReadAdapters()
    {
        ReadCount++;
        return Result;
    }
}

internal sealed class FakeProxyLayerSource : IProxyLayerSource
{
    public Collected<IReadOnlyList<ProxyLayerObservation>> Result { get; set; } =
        Collected<IReadOnlyList<ProxyLayerObservation>>.Ok([]);

    public int ReadCount { get; private set; }

    public Collected<IReadOnlyList<ProxyLayerObservation>> ReadLayers()
    {
        ReadCount++;
        return Result;
    }
}

internal sealed class FakeApexRuntimeApi : IApexRuntimeApi
{
    public ApexRuntimeEvidence? Evidence { get; set; }

    public int ReadCount { get; private set; }

    public ApexRuntimeEvidence? TryRead()
    {
        ReadCount++;
        return Evidence;
    }
}

internal sealed class FakeStartReferences : IProcessStartReferenceSource
{
    public ProcessStartReference? Reference { get; set; }

    public int ReadCount { get; private set; }

    public ProcessStartReference? TryRead(string clientId)
    {
        ReadCount++;
        return Reference is not null && string.Equals(Reference.ClientId, clientId, StringComparison.Ordinal)
            ? Reference
            : null;
    }
}

internal static class Build
{
    public static ProcessObservation Process(
        int processId,
        string name,
        string? executablePath = null,
        string? version = null) => new()
    {
        ProcessId = processId,
        Name = name,
        ExecutablePath = executablePath,
        Version = version,
        StartTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero),
    };

    public static ListenerObservation Listener(
        int port,
        int? ownerProcessId = null,
        string? host = "127.0.0.1",
        string? ownerName = null) => new()
    {
        Protocol = "tcp",
        Host = host,
        Port = port,
        OwnerProcessId = ownerProcessId,
        OwnerProcessName = ownerName,
    };

    public static NetworkAdapterObservation Adapter(
        string name,
        string kind,
        string status,
        string? description = null,
        params string[] addresses) => new()
    {
        Name = name,
        Description = description,
        Kind = kind,
        OperationalStatus = status,
        Addresses = addresses,
    };
}

internal static class EvidenceText
{
    public static string Describe(ClientConfigEvidence evidence)
    {
        var parts = new List<string>
        {
            evidence.ClientId,
            evidence.ParserVersion,
            evidence.Provenance.ToString(),
            evidence.SourcePath ?? string.Empty,
            evidence.ContentHash ?? string.Empty,
            evidence.ValidityReason ?? string.Empty,
        };
        parts.AddRange(evidence.Facts.Select(pair => pair.Key + "=" + pair.Value));
        parts.AddRange(evidence.Gaps);
        return string.Join("\n", parts);
    }

    public static string Describe(ClientStatus status)
    {
        var parts = new List<string>
        {
            status.ClientId,
            status.DisplayName,
            status.Version ?? string.Empty,
            status.TunAddress ?? string.Empty,
        };
        parts.AddRange(status.Endpoints.Select(endpoint => endpoint.Display));
        parts.AddRange(status.Notes);
        parts.AddRange(status.Gaps);
        foreach (var evidence in status.Evidence)
        {
            parts.Add(Describe(evidence));
        }

        return string.Join("\n", parts);
    }
}

internal static class AdapterFactory
{
    public static GenericAdapter Create(
        FakeProcessSource? processes = null,
        FakeListenerSource? listeners = null,
        FakeNetworkAdapterSource? networkAdapters = null,
        FakeProxyLayerSource? proxyLayers = null,
        IReadOnlyList<IClientConfigAdapter>? configAdapters = null,
        TimeProvider? timeProvider = null) => new(
        processes ?? new FakeProcessSource(),
        listeners ?? new FakeListenerSource(),
        networkAdapters ?? new FakeNetworkAdapterSource(),
        proxyLayers ?? new FakeProxyLayerSource(),
        configAdapters ?? [],
        timeProvider ?? new FixedTimeProvider(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero)));
}
