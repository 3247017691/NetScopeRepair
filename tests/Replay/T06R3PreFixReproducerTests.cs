using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// T06-R3 pre-fix reproducers for the storage side. Both cases are accepted by the T09-R2
/// writer/decoder even though they mix evidence from different scopes. They must be rejected
/// after the evidence-chain task, without inventing replacement facts.
/// </summary>
public sealed class T06R3PreFixReproducerTests
{
    private const string Boot = "boot-t06r3-prefix";
    private const string Session = "session-t06r3-prefix";
    private const string SnapshotId = "snapshot-t06r3-prefix";
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T01:00:00Z");

    [Fact]
    public void A_record_from_another_observation_window_is_currently_written_without_error()
    {
        var evidence = Evidence(recordWindow: "window-other", probeWindow: "window-cycle");
        var exception = Record.Exception(() => EvidenceSessionEvents.Create(
            evidence, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true, Origin.AddSeconds(30), 30000));

        var rejected = Assert.IsType<JsonException>(exception);
        Assert.Contains("window", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_record_whose_snapshot_fact_disagrees_with_its_reference_is_currently_replayed_cleanly()
    {
        var evidence = Evidence(recordWindow: "window-cycle", probeWindow: "window-cycle");
        var events = EvidenceSessionEvents.Create(
                evidence, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true, Origin.AddSeconds(30), 30000)
            .ToArray();
        var index = Array.FindIndex(events, item => item.Payload.ContainsKey("evidence_record"));
        Assert.True(index >= 0);
        // The reference says this record belongs to the snapshot in this scope; the fact points
        // somewhere else. A reader that prefers the reference silently hides the contradiction.
        var payload = events[index].Payload.DeepClone().AsObject();
        payload["evidence_record"]!["facts"]!["snapshot_id"] = "snapshot-elsewhere";
        events[index] = events[index] with
        {
            Refs = events[index].Refs with { SnapshotId = SnapshotId },
            Payload = payload,
        };

        using var workspace = new TempWorkspace();
        var path = workspace.File("cross-reference.ndjson");
        using (var log = new NdjsonEventLog(path))
        {
            foreach (var item in events)
            {
                await log.AppendAsync(item, CancellationToken.None);
            }
        }

        var replay = new ReplayService().Replay(path);
        Assert.True(replay.Log.IsClean);
        Assert.False(replay.IsClean);
        Assert.Contains(replay.EvidenceProblems, problem =>
            problem.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    private static EvidenceSet Evidence(string recordWindow, string probeWindow) => new()
    {
        Snapshot = new NetworkSnapshot
        {
            SnapshotId = SnapshotId,
            ObservedAt = Origin,
            MonotonicMs = 0,
            Synthetic = true,
            Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(Array.Empty<InterfaceState>()),
            Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(Array.Empty<RouteEntry>()),
            Dns = Collected<DnsState>.Ok(new DnsState()),
            Proxies = Collected<ProxyState>.Ok(new ProxyState
            {
                ToolProcessEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
                UserEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
                MachineEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
                WinInet = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
                WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
            }),
        },
        Probes = new[]
        {
            new ProbeResult
            {
                ProbeId = "probe-prefix-1",
                ProbeGroupId = "group-prefix",
                TargetId = "target-prefix",
                Path = ProbePath.ExplicitHttpProxy,
                Status = EventStatus.Ok,
                StartedAt = Origin.AddSeconds(1),
                StartedMonotonicMs = 1000,
                DurationMs = 50,
                HttpStatus = 204,
                Classification = ProbeClassification.ReachableExpected,
                AddressFamily = "ipv4",
                Method = "GET",
                RequestOptionsKey = "options-prefix",
                ObservationWindowId = probeWindow,
                BootId = Boot,
                ProxyEndpointId = "proxy-prefix",
                ProxyIsLocal = true,
                TimingComparable = true,
            },
        },
        Records = new[]
        {
            new EvidenceRecord
            {
                EvidenceId = "record-prefix-1",
                Kind = "client_process",
                Summary = "合成进程观测",
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["snapshot_id"] = SnapshotId,
                    ["client"] = "nice",
                    ["pid"] = "1234",
                },
                ObservedAt = Origin,
                MonotonicMs = 0,
                BootId = Boot,
                ObservationWindowId = recordWindow,
                Source = "adapter.observation",
                Provenance = "adapter_read",
                Status = EventStatus.Ok,
                AddressFamily = "ipv4",
                ProxyEndpointId = "proxy-prefix",
            },
        },
    };
}
