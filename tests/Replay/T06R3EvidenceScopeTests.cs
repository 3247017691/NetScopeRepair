using System.Text.Json;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// T06-R3 storage boundary: one cycle writes one explicit scope, and a mismatched boot, window,
/// snapshot reference or duplicate id is rejected instead of being restamped or merged.
/// </summary>
public sealed class T06R3EvidenceScopeTests
{
    private const string Boot = "boot-t06r3-scope";
    private const string Session = "session-t06r3-scope";
    private const string SnapshotId = "snapshot-t06r3-scope";
    private const string Window = "window-t06r3-scope";
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T01:00:00Z");

    [Fact]
    public void Evidence_record_from_another_boot_is_rejected()
    {
        var evidence = Evidence(Record() with { BootId = "boot-other" });

        var exception = Assert.Throws<JsonException>(() => Create(evidence));
        Assert.Contains("boot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_record_from_another_observation_window_is_rejected()
    {
        var evidence = Evidence(Record() with { ObservationWindowId = "window-other" });

        var exception = Assert.Throws<JsonException>(() => Create(evidence));
        Assert.Contains("window", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_record_without_an_explicit_window_is_rejected_when_the_cycle_has_one()
    {
        var evidence = Evidence(Record() with { ObservationWindowId = null });

        var exception = Assert.Throws<JsonException>(() => Create(evidence, observationWindowId: Window));
        Assert.Contains("window", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_record_for_another_snapshot_is_rejected()
    {
        var facts = new Dictionary<string, string>(Record().Facts, StringComparer.Ordinal)
        {
            ["snapshot_id"] = "snapshot-elsewhere",
        };
        var evidence = Evidence(Record() with { Facts = facts });

        var exception = Assert.Throws<JsonException>(() => Create(evidence));
        Assert.Contains("snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_evidence_record_ids_are_rejected()
    {
        var evidence = Evidence(Record("record-duplicate"), Record("record-duplicate"));

        var exception = Assert.Throws<JsonException>(() => Create(evidence));
        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule_failures_and_source_gaps_are_persisted_as_untyped_gap_events()
    {
        var evidence = Evidence(Record());
        var failures = new[]
        {
            new RuleEvaluationFailure
            {
                RuleId = "DX-THROW",
                RuleVersion = "1.0.0",
                ErrorType = "InvalidOperationException",
                Summary = "规则 DX-THROW 执行失败（InvalidOperationException），本轮没有得到该规则的结论。",
            },
        };
        var gaps = new[]
        {
            new ObservationEvidenceGap
            {
                Source = "processes",
                Status = EventStatus.AccessDenied,
                Reason = "进程列表不可读",
            },
        };

        var events = EvidenceSessionEvents.Create(evidence, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true,
            Origin.AddSeconds(30), 30000, failures: failures, evidenceGaps: gaps);

        var failureEvent = Assert.Single(events, item => item.EventType == EventType.Gap && item.Source == "diagnostic-engine");
        Assert.Equal(EventStatus.Failed, failureEvent.Status);
        Assert.Equal("DX-THROW", failureEvent.Payload["rule_id"]!.GetValue<string>());
        Assert.False(failureEvent.Payload.ContainsKey("payload_version"));
        var gapEvent = Assert.Single(events, item => item.EventType == EventType.Gap && item.Source == "processes");
        Assert.Equal(EventStatus.AccessDenied, gapEvent.Status);
        Assert.Equal("进程列表不可读", gapEvent.Payload["reason"]!.GetValue<string>());
        Assert.Equal(SnapshotId, failureEvent.Refs.SnapshotId);

        using var workspace = new TempWorkspace();
        var path = Write(workspace, events);
        var replay = new ReplayService().Replay(path);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Empty(replay.Diagnoses);
        Assert.Equal(2, replay.Summary.ByType[EventType.Gap]);
        Assert.Equal(2, replay.Summary.ObservationGapCount);
    }

    [Fact]
    public void Old_snapshot_only_session_keeps_an_empty_record_set_without_backfilling()
    {
        var evidence = new EvidenceSet
        {
            Snapshot = Snapshot(),
            Probes = Array.Empty<ProbeResult>(),
            Records = Array.Empty<EvidenceRecord>(),
        };
        var events = EvidenceSessionEvents.Create(evidence, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true,
            Origin.AddSeconds(30), 30000);
        using var workspace = new TempWorkspace();
        var path = Write(workspace, events);

        var replay = new ReplayService().Replay(path);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var scope = Assert.Single(replay.SnapshotScopes);
        Assert.Empty(scope.Evidence.Records);
        Assert.DoesNotContain(replay.Log.Events, item => item.Payload.ContainsKey("evidence_record"));
    }

    [Fact]
    public void Records_keep_their_explicit_scope_across_replay()
    {
        var events = Create(Evidence(Record()));
        using var workspace = new TempWorkspace();
        var replay = new ReplayService().Replay(Write(workspace, events));

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var record = Assert.Single(replay.Evidence!.Records);
        Assert.Equal("record-t06r3", record.EvidenceId);
        Assert.Equal(SnapshotId, record.Facts["snapshot_id"]);
        Assert.Equal(Boot, record.BootId);
        Assert.Equal(Window, record.ObservationWindowId);
        var eventItem = Assert.Single(replay.Log.Events, item => item.Payload.ContainsKey("evidence_record"));
        Assert.Equal(SnapshotId, eventItem.Refs.SnapshotId);
    }

    private static IReadOnlyList<EventEnvelope> Create(EvidenceSet evidence, string? observationWindowId = null) =>
        EvidenceSessionEvents.Create(evidence, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true,
            Origin.AddSeconds(30), 30000, observationWindowId: observationWindowId);

    private static string Write(TempWorkspace workspace, IReadOnlyList<EventEnvelope> events)
    {
        var path = workspace.File("t06r3-scope-" + Guid.NewGuid().ToString("N") + ".ndjson");
        using (var log = new NdjsonEventLog(path))
        {
            foreach (var item in events)
            {
                log.AppendAsync(item, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        return path;
    }

    private static EvidenceSet Evidence(params EvidenceRecord[] records) => new()
    {
        Snapshot = Snapshot(),
        Probes = new[]
        {
            new ProbeResult
            {
                ProbeId = "probe-t06r3",
                ProbeGroupId = "group-t06r3",
                TargetId = "target-t06r3",
                Path = ProbePath.ExplicitHttpProxy,
                Status = EventStatus.Ok,
                StartedAt = Origin.AddSeconds(1),
                StartedMonotonicMs = 1000,
                DurationMs = 50,
                HttpStatus = 204,
                Classification = ProbeClassification.ReachableExpected,
                AddressFamily = "ipv4",
                Method = "GET",
                RequestOptionsKey = "options-t06r3",
                ObservationWindowId = Window,
                BootId = Boot,
                ProxyEndpointId = "proxy-t06r3",
                ProxyIsLocal = true,
                TimingComparable = true,
            },
        },
        Records = records,
    };

    private static EvidenceRecord Record(string id = "record-t06r3") => new()
    {
        EvidenceId = id,
        Kind = "client_process",
        Summary = "合成进程观测",
        Facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["snapshot_id"] = SnapshotId,
            ["client"] = "nice",
            ["pid"] = "100",
        },
        ObservedAt = Origin,
        MonotonicMs = 0,
        BootId = Boot,
        ObservationWindowId = Window,
        Source = "adapter.observation",
        Provenance = "adapter_read",
        Status = EventStatus.Ok,
        AddressFamily = "ipv4",
        ProxyEndpointId = "proxy-t06r3",
    };

    private static NetworkSnapshot Snapshot() => new()
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
    };
}
