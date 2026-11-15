using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// T08-R2 replay side: a synthetic 102-snapshot capture shape (every interfaces domain Partial,
/// WinHTTP Unsupported, zero probes) must keep one scope per cycle, stay deterministic and must
/// not collapse Partial into Ok or merge an independent gap event into snapshot data.
/// </summary>
public sealed class T08R2ReplayDeterminismTests
{
    private const int SnapshotCount = 102;
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T06:00:00Z");
    private const string Session = "session-t08r2-replay";
    private const string Boot = "boot-t08r2-replay";

    [Fact]
    public async Task One_hundred_two_partial_cycles_keep_one_scope_per_snapshot_and_replay_deterministically()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildAsync(workspace, "t08r2-replay-102.ndjson");

        var first = new ReplayService().Replay(path);
        var second = new ReplayService().Replay(path);

        Assert.True(first.Log.IsClean);
        Assert.Empty(first.Log.Problems);
        Assert.Empty(first.Log.SequenceGaps);
        Assert.Equal(0, first.Log.DuplicateCount);
        Assert.Equal(SnapshotCount, first.Log.Events.Count);
        Assert.Equal(SnapshotCount, first.Summary.EventCount);
        Assert.Equal(SnapshotCount, first.Summary.PartialCount);
        Assert.Equal(0, first.Summary.ObservationGapCount); // Partial is not an observation gap
        Assert.Equal(0, first.Summary.SuccessCount);
        Assert.Equal(SnapshotCount, first.Summary.ByType[EventType.Snapshot]);

        Assert.Empty(first.EvidenceProblems);
        Assert.Equal(SnapshotCount, first.SnapshotScopes.Count);
        Assert.All(first.SnapshotScopes, scope =>
        {
            Assert.Equal(EventStatus.Partial, scope.Snapshot.Interfaces.Status);
            Assert.NotNull(scope.Snapshot.Interfaces.Value);
            Assert.Empty(scope.Evidence.Probes);
            Assert.Empty(scope.Evidence.Records);
            Assert.Empty(scope.Diagnoses);
        });
        Assert.Equal("snapshot-t08r2-replay-102", first.SnapshotScopes[^1].Snapshot.SnapshotId);
        Assert.NotNull(first.Evidence);
        Assert.Equal(first.SnapshotScopes[^1].Snapshot.SnapshotId, first.Evidence!.Snapshot!.SnapshotId);
        Assert.Null(first.UnscopedEvidence);

        // Same file, same read: identical counters, ids and scopes. No clock or network input.
        Assert.Equal(first.Summary.EventCount, second.Summary.EventCount);
        Assert.Equal(first.Summary.PartialCount, second.Summary.PartialCount);
        Assert.Equal(first.Summary.ObservationGapCount, second.Summary.ObservationGapCount);
        Assert.Equal(first.Summary.ByStatus.OrderBy(item => item.Key), second.Summary.ByStatus.OrderBy(item => item.Key));
        Assert.Equal(first.Summary.ByType.OrderBy(item => item.Key), second.Summary.ByType.OrderBy(item => item.Key));
        Assert.Equal(first.Log.Events.Select(envelope => envelope.EventId),
            second.Log.Events.Select(envelope => envelope.EventId));
        Assert.Equal(first.SnapshotScopes.Select(scope => scope.Snapshot.SnapshotId),
            second.SnapshotScopes.Select(scope => scope.Snapshot.SnapshotId));
    }

    [Fact]
    public async Task An_independent_gap_event_is_counted_separately_from_partial_domains()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildAsync(workspace, "t08r2-replay-gap.ndjson");

        using (var log = new NdjsonEventLog(path))
        {
            await log.AppendAsync(new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion,
                EventId = "event-t08r2-gap-001",
                SessionId = Session,
                BootId = Boot,
                Sequence = SnapshotCount + 1,
                ObservedAt = Origin.AddSeconds(2000),
                MonotonicMs = 1_000_000,
                EventType = EventType.Gap,
                Status = EventStatus.Failed,
                Source = "synthetic.gap",
                SourceVersion = "1.0",
                Synthetic = true,
                Refs = new EventRefs(),
                Payload = new JsonObject { ["reason"] = "synthetic gap fixture" },
            }, CancellationToken.None);
        }

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.Log.IsClean);
        Assert.Empty(replay.Log.SequenceGaps); // the gap is an event, not a sequence discontinuity
        Assert.Equal(1, replay.Summary.ByType[EventType.Gap]);
        Assert.Equal(1, replay.Summary.ObservationGapCount);
        Assert.Equal(SnapshotCount, replay.Summary.PartialCount); // Partial snapshots are untouched
        Assert.Equal(SnapshotCount, replay.SnapshotScopes.Count);
        Assert.Equal(SnapshotCount + 1, replay.Log.Events.Count);
        Assert.NotNull(replay.Evidence);
        Assert.NotNull(replay.Evidence!.Snapshot);
        Assert.Equal("snapshot-t08r2-replay-102", replay.Evidence.Snapshot!.SnapshotId);
    }

    private static async Task<string> BuildAsync(TempWorkspace workspace, string name)
    {
        var path = workspace.File(name);
        using var log = new NdjsonEventLog(path);
        var sequence = 0;
        for (var index = 1; index <= SnapshotCount; index++)
        {
            var at = Origin.AddSeconds((index - 1) * 10);
            var snapshot = new NetworkSnapshot
            {
                SnapshotId = "snapshot-t08r2-replay-" + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                ObservedAt = at,
                MonotonicMs = (index - 1) * 10_000d,
                Synthetic = true,
                Interfaces = Collected<IReadOnlyList<InterfaceState>>.Partial(new[]
                {
                    new InterfaceState
                    {
                        InterfaceGuid = "interface-t08r2-replay-12",
                        Name = "Synthetic-Uplink-A",
                        IfIndex = 12,
                        Family = "ipv4",
                        Kind = "ethernet",
                        OperationalStatus = "up",
                        Addresses = new[] { "192.168.7.10/24" },
                        Metric = 25,
                        AutomaticMetric = true,
                        DisableDefaultRoutes = false,
                        Luid = "synthetic-luid-12",
                    },
                }, "Synthetic fixture: interfaces domain partially readable.",
                    new[] { "GetIfEntry2 failed for adapter \"vEthernet (Default Switch)\"" }),
                Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(new[]
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
                }),
                Dns = Collected<DnsState>.Ok(new DnsState
                {
                    ServersByInterface = new Dictionary<int, IReadOnlyList<string>> { [12] = new[] { "192.168.7.1" } },
                    SuffixSearchList = new[] { "synthetic.invalid" },
                }),
                Proxies = Collected<ProxyState>.Ok(new ProxyState
                {
                    ToolProcessEnvironment = OkLayer("tool_process_environment"),
                    UserEnvironment = OkLayer("user_environment"),
                    MachineEnvironment = OkLayer("machine_environment"),
                    WinInet = OkLayer("wininet"),
                    WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported,
                        "Synthetic fixture does not read the WinHTTP store."),
                }),
            };

            var events = EvidenceSessionEvents.Create(
                new EvidenceSet { Snapshot = snapshot, Probes = Array.Empty<ProbeResult>(), Records = Array.Empty<EvidenceRecord>() },
                Array.Empty<Diagnosis>(),
                Session,
                Boot,
                synthetic: true,
                at.AddSeconds(1),
                snapshot.MonotonicMs + 1000,
                batchId: index.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var envelope in events)
            {
                await log.AppendAsync(envelope with { Sequence = ++sequence }, CancellationToken.None);
            }
        }

        return path;
    }

    private static Collected<ProxyLayer> OkLayer(string layer) => Collected<ProxyLayer>.Ok(new ProxyLayer
    {
        Layer = layer,
        Provenance = "synthetic",
        Enabled = false,
    });
}
