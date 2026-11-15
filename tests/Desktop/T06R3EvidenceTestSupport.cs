using NetScope.Adapters;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

// Deterministic fakes for the T06-R3 evidence-chain tests. They replace every adapter boundary so
// no real process, listener or client configuration is read and no network request is made.

internal sealed class FakeProcessSource(
    IReadOnlyList<ProcessObservation> processes,
    EventStatus status = EventStatus.Ok,
    string? reason = null) : IProcessSource
{
    public Collected<IReadOnlyList<ProcessObservation>> ReadProcesses() => status == EventStatus.Ok
        ? Collected<IReadOnlyList<ProcessObservation>>.Ok(processes)
        : Collected<IReadOnlyList<ProcessObservation>>.Unavailable(status, reason ?? "test process gap");
}

internal sealed class FakeListenerSource(
    IReadOnlyList<ListenerObservation> listeners,
    EventStatus status = EventStatus.Ok,
    string? reason = null) : IListenerSource
{
    public Collected<IReadOnlyList<ListenerObservation>> ReadListeners() => status == EventStatus.Ok
        ? Collected<IReadOnlyList<ListenerObservation>>.Ok(listeners)
        : Collected<IReadOnlyList<ListenerObservation>>.Unavailable(status, reason ?? "test listener gap");
}

internal sealed class FakeAdapterSource : INetworkAdapterSource
{
    public Collected<IReadOnlyList<NetworkAdapterObservation>> ReadAdapters() =>
        Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(Array.Empty<NetworkAdapterObservation>());
}

internal sealed class FakeProxyLayerSource : IProxyLayerSource
{
    public Collected<IReadOnlyList<ProxyLayerObservation>> ReadLayers() =>
        Collected<IReadOnlyList<ProxyLayerObservation>>.Ok(Array.Empty<ProxyLayerObservation>());
}

internal sealed class FakeConfigFileSource : IConfigFileSource
{
    private readonly Dictionary<string, ConfigFileReadResult> _files;

    public FakeConfigFileSource(params (string Path, ConfigFileReadResult Result)[] files) =>
        _files = files.ToDictionary(item => item.Path, item => item.Result, StringComparer.OrdinalIgnoreCase);

    public ConfigFileReadResult Read(string path) =>
        _files.TryGetValue(path, out var result) ? result : ConfigFileReadResult.NotFound();
}

/// <summary>Deterministic adapter data used by the evidence-chain tests.</summary>
internal static class T06R3Adapter
{
    public const string NiceConfigPath = @"C:\t06r3\nice\runtime.yaml";

    public const string NiceConfigText = "mixed-port: 7890\nmode: rule\ntun:\n  enable: true\n";

    public static readonly DateTimeOffset ConfigModifiedAt = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset ClientStartedAt = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    public const string ApexConfigPath = @"C:\t06r3\apex\shared_preferences.json";

    public const string ApexConfigText =
        "{\"flutter.mixed-port\":7890,\"flutter.mode\":\"rule\",\"flutter.tun.auto-route\":true}";

    public static ProcessObservation NiceUi(int processId = 100, DateTimeOffset? startedAt = null) => new()
    {
        ProcessId = processId,
        Name = "Nice加速",
        StartTime = startedAt ?? ClientStartedAt,
        UnreadableFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ObservationFieldNames.ExecutablePath] = "access_denied",
        },
    };

    public static ProcessObservation ApexUi(int processId = 200) => new()
    {
        ProcessId = processId,
        Name = "Apex",
        StartTime = ClientStartedAt.AddMinutes(1),
    };

    public static ListenerObservation Loopback(int port = 7890, int? owner = 100, string ownerName = "Nice加速") => new()
    {
        Protocol = "tcp",
        Host = "127.0.0.1",
        Port = port,
        AddressFamily = "ipv4",
        OwnerProcessId = owner,
        OwnerProcessName = owner is null ? null : ownerName,
        OwnerReadState = owner is null ? "unreadable" : "read",
        OwnerReadReason = owner is null ? "owner_row_not_found" : null,
    };

    public static NiceAdapter NiceDiskAdapter(
        string path = NiceConfigPath,
        string text = NiceConfigText,
        DateTimeOffset? modifiedAt = null) =>
        new(new FakeConfigFileSource((path, ConfigFileReadResult.Found(
            System.Text.Encoding.UTF8.GetBytes(text), modifiedAt ?? ConfigModifiedAt))), path);

    public static ApexAdapter ApexDiskAdapter(
        string path = ApexConfigPath,
        string text = ApexConfigText,
        DateTimeOffset? modifiedAt = null) =>
        new(new FakeConfigFileSource((path, ConfigFileReadResult.Found(
            System.Text.Encoding.UTF8.GetBytes(text), modifiedAt ?? ConfigModifiedAt))), path);

    public static GenericAdapter Create(
        IReadOnlyList<ProcessObservation> processes,
        IReadOnlyList<ListenerObservation> listeners,
        params IClientConfigAdapter[] configAdapters) =>
        new(
            new FakeProcessSource(processes),
            new FakeListenerSource(listeners),
            new FakeAdapterSource(),
            new FakeProxyLayerSource(),
            configAdapters);

    /// <summary>The composition the real Desktop app gets: one adapter read per observation.</summary>
    public static ObservationEvidenceContext Context(NetworkSnapshot snapshot, string? proxyEndpointId = null) => new()
    {
        SessionId = "session-t06r3-test",
        BootId = "boot-t06r3-test",
        SnapshotId = snapshot.SnapshotId,
        ObservationWindowId = "window-t06r3-test",
        ObservedAt = snapshot.ObservedAt,
        MonotonicMs = snapshot.MonotonicMs,
        Synthetic = false,
        ProxyEndpointId = proxyEndpointId,
    };
}

/// <summary>Snapshot collector with a configurable local network state; reads no real system data.</summary>
internal sealed class T06R3SnapshotCollector(
    RouteEntry[]? routes = null,
    DnsState? dns = null,
    EventStatus interfacesStatus = EventStatus.Ok,
    bool proxyConfigured = false) : ISnapshotCollector
{
    public string Source => "test.t06r3.snapshot";

    public string SourceVersion => "1.0";

    public Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        var interfaces = interfacesStatus == EventStatus.Ok
            ? Collected<IReadOnlyList<InterfaceState>>.Ok(Array.Empty<InterfaceState>())
            : Collected<IReadOnlyList<InterfaceState>>.Unavailable(interfacesStatus, "test interface gap");
        return Task.FromResult(new NetworkSnapshot
        {
            SnapshotId = context.SnapshotId,
            ObservedAt = context.ObservedAt,
            MonotonicMs = context.MonotonicMs,
            Synthetic = context.Synthetic,
            Interfaces = interfaces,
            Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(routes ?? Array.Empty<RouteEntry>()),
            Dns = Collected<DnsState>.Ok(dns ?? new DnsState()),
            Proxies = Collected<ProxyState>.Ok(new ProxyState
            {
                ToolProcessEnvironment = Layer("tool_process_environment", proxyConfigured),
                UserEnvironment = Layer("user_environment", proxyConfigured),
                MachineEnvironment = Layer("machine_environment", false),
                WinInet = Layer("wininet", false),
                WinHttp = Layer("winhttp", false),
            }),
        });
    }

    private static Collected<ProxyLayer> Layer(string name, bool configured) =>
        Collected<ProxyLayer>.Ok(new ProxyLayer
        {
            Layer = name,
            Provenance = "test",
            Enabled = configured,
            ProxyServer = configured ? "http://127.0.0.1:7890" : null,
            Variables = configured ? new[] { "HTTPS_PROXY" } : Array.Empty<string>(),
        });
}

/// <summary>Counts calls and stamps a record from the supplied cycle scope.</summary>
internal sealed class CountingEvidenceSource : IObservationEvidenceSource
{
    public int Calls { get; private set; }

    public List<ObservationEvidenceContext> Contexts { get; } = new();

    public Task<ObservationEvidenceResult> CollectAsync(
        ObservationEvidenceContext context, CancellationToken cancellationToken)
    {
        Calls++;
        Contexts.Add(context);
        return Task.FromResult(new ObservationEvidenceResult
        {
            Records = new[]
            {
                new EvidenceRecord
                {
                    EvidenceId = "record-" + context.SnapshotId,
                    Kind = "client_process",
                    Summary = "计数用测试记录",
                    Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["snapshot_id"] = context.SnapshotId,
                        ["client"] = "nice",
                        ["pid"] = "100",
                    },
                    ObservedAt = context.ObservedAt,
                    MonotonicMs = context.MonotonicMs,
                    BootId = context.BootId,
                    ObservationWindowId = context.ObservationWindowId,
                    Source = "test.counting-evidence",
                    Provenance = "adapter_read",
                    Status = EventStatus.Ok,
                },
            },
        });
    }
}

internal sealed class ThrowingEvidenceSource : IObservationEvidenceSource
{
    public Task<ObservationEvidenceResult> CollectAsync(
        ObservationEvidenceContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("synthetic evidence source failure");
}
