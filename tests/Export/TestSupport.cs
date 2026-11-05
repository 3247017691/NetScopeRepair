using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Export.Tests;

/// <summary>Per-test temporary directory that is removed even when an assertion fails.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "netscope-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public string Subdirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Root))
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test that already passed.
        }
    }
}

/// <summary>
/// Builds a clean typed session with invented secrets. Every sensitive marker is synthetic and
/// exists only to prove that the exporter removes it.
/// </summary>
internal static class ExportSession
{
    public static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T01:00:00Z");
    public const string Boot = "boot-export";
    public const string SessionId = "session-export";
    public const string SnapshotId = "snapshot-export-1";
    public const string ProbeOne = "probe-export-1";
    public const string ProbeTwo = "probe-export-2";
    public const string RecordId = "record-export-1";
    public const string DiagnosisId = "diagnosis-export-1";

    public static readonly string[] Secrets =
    {
        "{{PASSWORD_export_a}}", "{{PASSWORD_export_b}}", "{{PASSWORD_export_p}}",
        "SYNTHETIC_QUERY_A", "SYNTHETIC_QUERY_B", "SYNTHETIC_QUERY_C", "SYNTHETIC_QUERY_E",
        "SYNTHETIC_QUERY_G", "SYNTHETIC_QUERY_I",
        "SYNTHETIC_TOKEN_C", "SYNTHETIC_TOKEN_H", "SYNTHETIC_TOKEN_J",
        "SYNTHETIC_BEARER_D", "SYNTHETIC_BEARER_F", "SYNTHETIC_COOKIE_K",
        "alice", "bob", "DESKTOP-SYNTHETIC",
        @"C:\Users\alice", "/home/alice",
        "192.168.1.10", "192.168.1.1", "192.168.1.5", "10.0.0.7",
        "api.example.com", "pac.example.invalid", "corp.internal", "sub.example.invalid",
    };

    public static async Task<string> CreateAsync(string path)
    {
        var snapshot = Snapshot();
        var probes = new[]
        {
            Probe(ProbeOne, EventStatus.Timeout, 1000, "connect to 192.168.1.5:7890 failed token=SYNTHETIC_QUERY_E",
                "Authorization: Bearer SYNTHETIC_BEARER_F"),
            Probe(ProbeTwo, EventStatus.Ok, 2000, null, null),
        };
        var records = new[] { Record() };
        var diagnosis = Diagnosis();
        var events = EvidenceSessionEvents.Create(new EvidenceSet
        {
            Snapshot = snapshot,
            Probes = probes,
            Records = records,
        }, new[] { diagnosis }, SessionId, Boot, synthetic: true, Origin.AddSeconds(30), 30000);

        using var log = new NdjsonEventLog(path);
        foreach (var envelope in events) await log.AppendAsync(envelope, CancellationToken.None);
        return path;
    }

    /// <summary>A legacy envelope-level session: structurally valid but without typed evidence.</summary>
    public static EventEnvelope UntypedEvent() => new()
    {
        SchemaVersion = EventEnvelope.SupportedSchemaVersion,
        EventId = "event-untyped-001",
        SessionId = "session-untyped",
        BootId = "boot-untyped",
        Sequence = 1,
        ObservedAt = Origin,
        MonotonicMs = 0,
        EventType = EventType.IncidentMarker,
        Status = EventStatus.Ok,
        Source = "synthetic.untyped",
        SourceVersion = "1.0",
        Synthetic = true,
        Refs = new EventRefs { IncidentId = "incident-untyped" },
        Payload = new JsonObject { ["notice"] = "envelope only" },
    };

    private static NetworkSnapshot Snapshot() => new()
    {
        SnapshotId = SnapshotId,
        ObservedAt = Origin,
        MonotonicMs = 0,
        Synthetic = true,
        Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(new[]
        {
            new InterfaceState
            {
                InterfaceGuid = "interface-export-1",
                Name = "Synthetic-Ethernet",
                IfIndex = 12,
                Family = "ipv4",
                Description = @"Synthetic adapter; local config C:\Users\alice\AppData\Local\export\adapter.ini",
                Kind = "ethernet",
                OperationalStatus = "up",
                Addresses = new[] { "192.168.1.10/24" },
                Metric = 25,
                AutomaticMetric = true,
                DisableDefaultRoutes = false,
                Luid = "luid-export-1",
            },
        }),
        Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(new[]
        {
            new RouteEntry
            {
                DestinationPrefix = "192.168.1.0/24",
                Family = "ipv4",
                NextHop = "192.168.1.1",
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
        }),
        Dns = Collected<DnsState>.Ok(new DnsState
        {
            ServersByInterface = new Dictionary<int, IReadOnlyList<string>>
            {
                [12] = new[] { "192.168.1.1", "198.18.0.2" },
            },
            SuffixSearchList = new[] { "corp.internal" },
        }),
        Proxies = Collected<ProxyState>.Ok(new ProxyState
        {
            ToolProcessEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "tool_process_environment",
                Provenance = "synthetic",
                Enabled = true,
                ProxyServer = "http://alice:{{PASSWORD_export_a}}@192.168.1.5:7890/private?token=SYNTHETIC_QUERY_A",
                Variables = new[]
                {
                    "HTTPS_PROXY=http://bob:{{PASSWORD_export_b}}@10.0.0.7:8080/secret?access_token=SYNTHETIC_QUERY_B",
                },
                Notice = @"Read C:\Users\alice\AppData\Roaming\proxy.ini and /home/alice/.config/proxy",
            }),
            UserEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "user_environment",
                Provenance = "synthetic",
                Enabled = false,
                Variables = Array.Empty<string>(),
            }),
            MachineEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "machine_environment",
                Provenance = "synthetic",
                Enabled = false,
                Variables = Array.Empty<string>(),
                Notice = "Cookie: session=SYNTHETIC_COOKIE_K",
            }),
            WinInet = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "wininet",
                Provenance = "synthetic",
                Enabled = true,
                AutoConfigUrl = "https://pac.example.invalid/config.pac?access_token=SYNTHETIC_TOKEN_C",
                AutoDetect = true,
                Notice = "Authorization: Bearer SYNTHETIC_BEARER_D",
            }),
            WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported,
                "Synthetic session does not read the WinHTTP store."),
        }),
    };

    private static ProbeResult Probe(string id, EventStatus status, double monotonic, string? transportError,
        string? notice) => new()
    {
        ProbeId = id,
        ProbeGroupId = "group-export-1",
        TargetId = "target-export",
        Path = ProbePath.NoExplicitProxy,
        Status = status,
        StartedAt = Origin.AddMilliseconds(monotonic),
        DurationMs = 100,
        HttpStatus = status == EventStatus.Ok ? 204 : null,
        Classification = status == EventStatus.Ok
            ? ProbeClassification.ReachableExpected
            : ProbeClassification.TransportFailure,
        TransportError = transportError,
        AddressFamily = "ipv4",
        Method = "GET",
        RequestOptionsKey = "options-export",
        ObservationWindowId = "window-export",
        BootId = Boot,
        StartedMonotonicMs = monotonic,
        TimingComparable = true,
        FailedEndpoint = status == EventStatus.Ok ? null : "target",
        FailedStage = status == EventStatus.Ok ? null : "connect",
        ResolvedAddresses = new[] { "192.168.1.1", "198.18.0.11" },
        Notice = notice,
    };

    private static EvidenceRecord Record() => new()
    {
        EvidenceId = RecordId,
        Kind = "client_runtime_config",
        Summary = @"读取 C:\Users\alice\AppData\Roaming\client\config.json?token=SYNTHETIC_QUERY_G 失败",
        ObservedAt = Origin,
        MonotonicMs = 0,
        BootId = Boot,
        Source = "synthetic.runtime",
        Provenance = "runtime_api",
        Status = EventStatus.Ok,
        TargetId = "target-export",
        AddressFamily = "ipv4",
        ObservationWindowId = "window-export",
        Facts = new Dictionary<string, string>
        {
            ["snapshot_id"] = SnapshotId,
            ["if_index"] = "12",
            ["client"] = "synthetic",
            ["running"] = "true",
            ["dns_mode"] = "fake-ip",
            ["user_name"] = "alice",
            ["account_name"] = "alice",
            ["computer_name"] = "DESKTOP-SYNTHETIC",
            ["host_name"] = "DESKTOP-SYNTHETIC",
            ["api_key"] = "SYNTHETIC_TOKEN_H",
            ["password"] = "{{PASSWORD_export_p}}",
        },
    };

    private static Diagnosis Diagnosis() => new()
    {
        DiagnosisId = DiagnosisId,
        RuleId = "DX-EXPORT",
        RuleVersion = "1.0.0",
        Level = EvidenceGrade.Supported,
        Summary = @"公网目标 https://api.example.com/private/path?token=SYNTHETIC_QUERY_I 超时；本地配置 C:\Users\alice\AppData\Roaming\client\config.json",
        EvidenceIds = new[] { ProbeOne, RecordId },
        CounterEvidenceIds = Array.Empty<string>(),
        MissingEvidence = Array.Empty<string>(),
        AllowedRepairActions = Array.Empty<string>(),
        SuggestedNextStep = "只读核对；subscription https://sub.example.invalid/link?token=SYNTHETIC_TOKEN_J",
        Scope = new DiagnosisScope
        {
            TargetId = "target-export",
            AddressFamily = "ipv4",
            StartedAt = Origin,
            EndedAt = Origin.AddSeconds(30),
            ValidUntil = Origin.AddSeconds(30),
            BootId = Boot,
            ObservationWindowId = "window-export",
        },
    };
}

internal static class PackageReader
{
    public static IReadOnlyDictionary<string, byte[]> Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            result[entry.FullName] = memory.ToArray();
        }

        return result;
    }

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public static JsonNode Json(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false })
        ?? throw new InvalidDataException("The package file is not valid JSON.");

    public static IReadOnlyList<string> StringsIn(byte[] bytes, string name)
    {
        var text = Text(bytes);
        if (name.EndsWith(".ndjson", StringComparison.Ordinal))
        {
            var values = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Collect(JsonNode.Parse(line), values);
            }

            return values;
        }

        if (name.EndsWith(".json", StringComparison.Ordinal))
        {
            var values = new List<string>();
            Collect(JsonNode.Parse(text), values);
            return values;
        }

        return new[] { text };
    }

    private static void Collect(JsonNode? node, List<string> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj) Collect(property.Value, values);
                break;
            case JsonArray array:
                foreach (var item in array) Collect(item, values);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                values.Add(text);
                break;
        }
    }
}

internal static class ZipFactory
{
    public static string Create(string path, Action<ZipArchive> write)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        write(archive);
        return path;
    }

    public static void AddText(ZipArchive archive, string name, string text) =>
        AddBytes(archive, name, Encoding.UTF8.GetBytes(text));

    public static void AddBytes(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}
