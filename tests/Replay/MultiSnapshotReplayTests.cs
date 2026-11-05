using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Application.Mock;
using NetScope.Domain;
using NetScope.Diagnostics;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// A normal observation session appends one snapshot per collection cycle. Replay must keep each
/// cycle separate instead of rejecting the second snapshot or merging all probes into one view.
/// </summary>
public sealed class MultiSnapshotReplayTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T03:00:00Z");
    private const string Boot = "boot-multi";
    private const string Session = "session-multi";

    [Fact]
    public async Task Multiple_snapshots_keep_per_cycle_evidence_and_a_latest_current_view()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildSessionAsync(workspace, "multi.ndjson");

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.Log.IsClean);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.SnapshotScopes.Count);

        var first = replay.SnapshotScopes[0];
        var second = replay.SnapshotScopes[1];
        Assert.Equal("snapshot-cycle-1", first.Snapshot.SnapshotId);
        Assert.Equal("snapshot-cycle-2", second.Snapshot.SnapshotId);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(5, second.Sequence);

        Assert.Equal(new[] { "probe-cycle-1" }, first.Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.Equal(new[] { "record-cycle-1" }, first.Evidence.Records.Select(record => record.EvidenceId));
        Assert.Equal(new[] { "probe-cycle-2" }, second.Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.Equal(new[] { "record-cycle-2" }, second.Evidence.Records.Select(record => record.EvidenceId));
        Assert.Equal(new[] { "diagnosis-cycle-1" }, first.Diagnoses.Select(diagnosis => diagnosis.DiagnosisId));
        Assert.Equal(new[] { "diagnosis-cycle-2" }, second.Diagnoses.Select(diagnosis => diagnosis.DiagnosisId));

        // No cycle borrows evidence from the other cycle.
        Assert.DoesNotContain(first.Evidence.Probes, probe => probe.ProbeId == "probe-cycle-2");
        Assert.DoesNotContain(first.Evidence.Records, record => record.EvidenceId == "record-cycle-2");
        Assert.DoesNotContain(second.Evidence.Probes, probe => probe.ProbeId == "probe-cycle-1");
        Assert.DoesNotContain(second.Evidence.Records, record => record.EvidenceId == "record-cycle-1");

        // The compatibility property is deliberately the latest snapshot scope only.
        Assert.NotNull(replay.Evidence);
        Assert.Equal("snapshot-cycle-2", replay.Evidence!.Snapshot!.SnapshotId);
        Assert.Equal(second.Snapshot.SnapshotId, replay.Evidence.Snapshot.SnapshotId);
        Assert.Equal(new[] { "probe-cycle-2" }, replay.Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.Null(replay.UnscopedEvidence);

        // A recorded diagnosis may cite evidence from an earlier cycle, and every citation still
        // resolves to a canonical definition.
        Assert.Equal(2, replay.Diagnoses.Count);
        var crossCycle = Assert.Single(replay.Diagnoses, diagnosis => diagnosis.DiagnosisId == "diagnosis-cycle-2");
        Assert.Contains("probe-cycle-2", crossCycle.EvidenceIds);
        Assert.Contains("record-cycle-1", crossCycle.EvidenceIds);
        var definitions = replay.Log.Events
            .Where(envelope => envelope.Refs.EvidenceId is not null)
            .Select(envelope => envelope.Refs.EvidenceId!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(crossCycle.EvidenceIds, id => Assert.Contains(id, definitions));

        // Each scope can be fed to the rules independently.
        var firstEvaluation = DiagnosticEngine.CreateDefault().EvaluateDetailed(first.Evidence);
        var secondEvaluation = DiagnosticEngine.CreateDefault().EvaluateDetailed(second.Evidence);
        Assert.Empty(firstEvaluation.Failures);
        Assert.Empty(secondEvaluation.Failures);
    }

    [Fact]
    public async Task An_unknown_snapshot_reference_is_reported_and_the_probe_is_not_mixed_in()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildSessionAsync(workspace, "unknown-ref.ndjson", unknownSnapshotReference: true);

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.Log.IsClean);
        Assert.False(replay.IsClean);
        Assert.Contains(replay.EvidenceProblems, problem =>
            problem.Contains("references unknown snapshot", StringComparison.Ordinal));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.Empty(replay.SnapshotScopes[1].Evidence.Probes);
        Assert.DoesNotContain(replay.SnapshotScopes[1].Evidence.Probes,
            probe => probe.ProbeId == "probe-cycle-2");
        Assert.DoesNotContain(replay.SnapshotScopes[0].Evidence.Probes,
            probe => probe.ProbeId == "probe-cycle-2");
    }

    [Fact]
    public async Task Duplicate_evidence_definitions_across_cycles_are_rejected_without_mixing()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildSessionAsync(workspace, "duplicate-definition.ndjson", duplicateProbeId: true);

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.Log.IsClean);
        Assert.False(replay.IsClean);
        Assert.Contains(replay.EvidenceProblems,
            problem => problem.Contains("duplicate evidence definition", StringComparison.Ordinal));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.Empty(replay.SnapshotScopes[1].Evidence.Probes);
    }

    [Fact]
    public async Task A_bad_later_snapshot_is_rejected_without_discarding_the_first_scope()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildSessionAsync(workspace, "bad-second-snapshot.ndjson", corruptSecondSnapshot: true);

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.Log.IsClean); // Envelope validation alone cannot establish semantics.
        Assert.False(replay.IsClean);
        Assert.NotEmpty(replay.EvidenceProblems);
        var scope = Assert.Single(replay.SnapshotScopes);
        Assert.Equal("snapshot-cycle-1", scope.Snapshot.SnapshotId);
        Assert.Equal("snapshot-cycle-1", replay.Evidence!.Snapshot!.SnapshotId);
    }

    [Fact]
    public async Task Evidence_before_the_first_snapshot_stays_unscoped_and_is_never_mixed_in()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildSessionAsync(workspace, "leading-probe.ndjson", leadingUnscopedProbe: true);

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.NotNull(replay.UnscopedEvidence);
        Assert.Equal(new[] { "probe-leading" }, replay.UnscopedEvidence!.Probes.Select(probe => probe.ProbeId));
        Assert.DoesNotContain(replay.SnapshotScopes.SelectMany(scope => scope.Evidence.Probes),
            probe => probe.ProbeId == "probe-leading");
        Assert.DoesNotContain(replay.Evidence!.Probes, probe => probe.ProbeId == "probe-leading");
    }

    [Fact]
    public async Task Probes_without_an_explicit_snapshot_reference_follow_the_preceding_cycle_only()
    {
        using var workspace = new TempWorkspace();
        var path = await BuildSessionAsync(workspace, "positional-probes.ndjson", stripProbeSnapshotRefs: true);

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.Equal(new[] { "probe-cycle-1" }, replay.SnapshotScopes[0].Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.Equal(new[] { "probe-cycle-2" }, replay.SnapshotScopes[1].Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.DoesNotContain(replay.SnapshotScopes[0].Evidence.Probes, probe => probe.ProbeId == "probe-cycle-2");
        Assert.DoesNotContain(replay.SnapshotScopes[1].Evidence.Probes, probe => probe.ProbeId == "probe-cycle-1");
    }

    private static async Task<string> BuildSessionAsync(TempWorkspace workspace, string name,
        bool unknownSnapshotReference = false, bool duplicateProbeId = false, bool corruptSecondSnapshot = false,
        bool leadingUnscopedProbe = false, bool stripProbeSnapshotRefs = false)
    {
        var path = workspace.File(name);
        using var log = new NdjsonEventLog(path);
        var nextSequence = 1;
        var first = await CycleAsync(1, "probe-cycle-1", "record-cycle-1", "diagnosis-cycle-1");
        var secondProbeId = duplicateProbeId ? "probe-cycle-1" : "probe-cycle-2";
        var second = AddCrossCycleEvidence(
            await CycleAsync(2, secondProbeId, "record-cycle-2", "diagnosis-cycle-2"),
            "diagnosis-cycle-2", "record-cycle-1");

        var events = first.Concat(second);
        if (stripProbeSnapshotRefs)
        {
            events = events.Select(envelope => envelope.EventType == EventType.ProbeResult
                ? envelope with { Refs = envelope.Refs with { SnapshotId = null } }
                : envelope);
        }

        if (leadingUnscopedProbe)
        {
            var leadingAt = Origin.AddSeconds(-5);
            var leadingProbe = new ProbeResult
            {
                ProbeId = "probe-leading",
                ProbeGroupId = "group-leading",
                TargetId = "target-multi",
                Path = ProbePath.NoExplicitProxy,
                Status = EventStatus.Ok,
                StartedAt = leadingAt,
                DurationMs = 10,
                HttpStatus = 204,
                Classification = ProbeClassification.ReachableExpected,
                AddressFamily = "ipv4",
                Method = "GET",
                RequestOptionsKey = "options-leading",
                ObservationWindowId = "window-leading",
                BootId = Boot,
                StartedMonotonicMs = 0,
                TimingComparable = true,
                ResolvedAddresses = new[] { "198.18.0.11" },
            };
            var leading = EvidenceSessionEvents.Create(new EvidenceSet
            {
                Probes = new[] { leadingProbe },
                Records = Array.Empty<EvidenceRecord>(),
            }, Array.Empty<Diagnosis>(), Session, Boot, synthetic: false, leadingAt.AddSeconds(1), 1000);
            events = leading.Concat(events);
        }
        if (unknownSnapshotReference)
        {
            events = events.Select(envelope => envelope.Refs.ProbeId == secondProbeId
                ? envelope with { Refs = envelope.Refs with { SnapshotId = "snapshot-missing" } }
                : envelope).ToArray();
        }

        if (corruptSecondSnapshot)
        {
            events = events.Select(envelope =>
            {
                if (envelope.Refs.SnapshotId != "snapshot-cycle-2" || !envelope.Payload.ContainsKey("snapshot"))
                    return envelope;
                var payload = envelope.Payload.DeepClone().AsObject();
                payload["snapshot"]!["interfaces"]!["status"] = "failed";
                return envelope with { Payload = payload };
            }).ToArray();
        }

        foreach (var envelope in events)
            await log.AppendAsync(envelope with { Sequence = nextSequence++ }, CancellationToken.None);
        return path;
    }

    private static EventEnvelope[] AddCrossCycleEvidence(EventEnvelope[] events, string diagnosisId, string evidenceId) =>
        events.Select(envelope =>
        {
            if (envelope.Refs.DiagnosisId != diagnosisId) return envelope;
            var payload = envelope.Payload.DeepClone().AsObject();
            ((JsonArray)payload["diagnosis"]!["evidence_ids"]!).Add(evidenceId);
            return envelope with { Payload = payload };
        }).ToArray();

    private static async Task<EventEnvelope[]> CycleAsync(int index, string probeId, string recordId,
        string diagnosisId)
    {
        var at = Origin.AddSeconds((index - 1) * 10);
        var monotonic = (index - 1) * 10_000d;
        var snapshotId = "snapshot-cycle-" + index;
        var snapshot = await new SyntheticSnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = Session,
            BootId = Boot,
            SnapshotId = snapshotId,
            ObservedAt = at,
            MonotonicMs = monotonic,
            Synthetic = false,
        }, CancellationToken.None);

        var probe = new ProbeResult
        {
            ProbeId = probeId,
            ProbeGroupId = "group-cycle-" + index,
            TargetId = "target-multi",
            Path = ProbePath.NoExplicitProxy,
            Status = EventStatus.Ok,
            StartedAt = at.AddSeconds(1),
            DurationMs = 20,
            HttpStatus = 204,
            Classification = ProbeClassification.ReachableExpected,
            AddressFamily = "ipv4",
            Method = "GET",
            RequestOptionsKey = "options-" + index,
            ObservationWindowId = "window-" + index,
            BootId = Boot,
            StartedMonotonicMs = monotonic + 1000,
            TimingComparable = true,
            ResolvedAddresses = new[] { "198.18.0.11" },
        };
        var record = new EvidenceRecord
        {
            EvidenceId = recordId,
            Kind = "client_runtime_config",
            Summary = "Synthetic cycle runtime evidence.",
            ObservedAt = at,
            MonotonicMs = monotonic,
            BootId = Boot,
            Source = "synthetic.runtime",
            Provenance = "runtime_api",
            Status = EventStatus.Ok,
            TargetId = "target-multi",
            AddressFamily = "ipv4",
            ObservationWindowId = "window-" + index,
            Facts = new Dictionary<string, string>
            {
                ["snapshot_id"] = snapshotId,
                ["client"] = "synthetic",
                ["running"] = "true",
                ["dns_mode"] = "fake-ip",
            },
        };
        var diagnosis = new Diagnosis
        {
            DiagnosisId = diagnosisId,
            RuleId = "DX-MULTI",
            RuleVersion = "1.0.0",
            Level = EvidenceGrade.Supported,
            Summary = "Synthetic cross-cycle diagnosis fixture.",
            EvidenceIds = new[] { probeId },
            CounterEvidenceIds = Array.Empty<string>(),
            AllowedRepairActions = Array.Empty<string>(),
            SuggestedNextStep = "Read-only fixture; no repair.",
            Scope = new DiagnosisScope
            {
                TargetId = "target-multi",
                AddressFamily = "ipv4",
                StartedAt = at,
                EndedAt = at.AddSeconds(30),
                ValidUntil = at.AddSeconds(30),
                BootId = Boot,
                ObservationWindowId = "window-" + index,
            },
        };

        var events = EvidenceSessionEvents.Create(new EvidenceSet
        {
            Snapshot = snapshot,
            Probes = new[] { probe },
            Records = new[] { record },
        }, new[] { diagnosis }, Session, Boot, synthetic: false, at.AddSeconds(30), monotonic + 30_000);
        return events.ToArray();
    }
}
