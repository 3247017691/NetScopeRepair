using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>Preserves source observations and their normalized evidence links in one local M0 session.</summary>
public static class EvidenceSessionEvents
{
    public static IReadOnlyList<EventEnvelope> Create(EvidenceSet evidence, IReadOnlyList<Diagnosis> diagnoses,
        string sessionId, string bootId, bool synthetic, DateTimeOffset diagnosisAt, double diagnosisMonotonicMs,
        string? batchId = null, IReadOnlyList<RuleEvaluationFailure>? failures = null,
        IReadOnlyList<ObservationEvidenceGap>? evidenceGaps = null, string? observationWindowId = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var expectedWindow = ResolveExpectedWindow(evidence, observationWindowId);
        var rows = new List<Row>();
        if (evidence.Snapshot is { } snapshot)
        {
            if (snapshot.Synthetic != synthetic) throw new JsonException("Snapshot synthetic marker disagrees with the session.");
            rows.Add(new(snapshot.ObservedAt, snapshot.MonotonicMs, EventType.Snapshot,
                snapshot.Completeness().Values.All(x => x == EventStatus.Ok) ? EventStatus.Ok : EventStatus.Partial,
                new EventRefs { SnapshotId = snapshot.SnapshotId, EvidenceId = snapshot.SnapshotId },
                synthetic ? "synthetic.snapshot-collector" : "snapshot-collector",
                id => EvidenceEventCodec.Snapshot(snapshot, id, evidence.ProposedChanges)));
        }
        else if (evidence.ProposedChanges.Count != 0) throw new JsonException("Proposed changes need their observed snapshot.");

        foreach (var probe in evidence.Probes)
        {
            if (probe.BootId != bootId || probe.StartedMonotonicMs is not { } monotonic)
                throw new JsonException("Probe boot or monotonic timestamp is missing; evidence cannot be restamped.");
            rows.Add(new(probe.StartedAt, monotonic, EventType.ProbeResult, probe.Status,
                new EventRefs { SnapshotId = evidence.Snapshot?.SnapshotId, ProbeGroupId = probe.ProbeGroupId,
                    ProbeId = probe.ProbeId, EvidenceId = probe.ProbeId },
                synthetic ? "synthetic.probe-runner" : "probe-runner", id => EvidenceEventCodec.Probe(probe, id)));
        }
        foreach (var record in evidence.Records)
        {
            if (record.BootId != bootId || record.ObservedAt is not { } at || record.MonotonicMs is not { } monotonic)
                throw new JsonException("Evidence record has no coherent boot or observation time; cannot persist it as current evidence.");
            RequireRecordScope(record, evidence.Snapshot, expectedWindow);
            rows.Add(new(at, monotonic, EventType.Snapshot, record.Status,
                // The reference is explicit: a consumer never has to guess which snapshot a record
                // belongs to, and a record without its own snapshot stays unscoped on purpose.
                new EventRefs { SnapshotId = evidence.Snapshot?.SnapshotId, EvidenceId = record.EvidenceId },
                record.Source ?? "evidence-record", id => EvidenceEventCodec.Record(record, id)));
        }
        foreach (var diagnosis in diagnoses)
        {
            rows.Add(new(diagnosisAt, diagnosisMonotonicMs, EventType.Diagnosis, EventStatus.Ok,
                new EventRefs { SnapshotId = evidence.Snapshot?.SnapshotId, DiagnosisId = diagnosis.DiagnosisId },
                "diagnostic-engine", _ => EvidenceEventCodec.Diagnosis(diagnosis)));
        }
        foreach (var failure in failures ?? Array.Empty<RuleEvaluationFailure>())
        {
            // A failed rule is not a diagnosis and must not disappear into memory-only state. It
            // is written as an untyped gap event so an old reader can still skip it safely.
            rows.Add(new(diagnosisAt, diagnosisMonotonicMs, EventType.Gap, EventStatus.Failed,
                new EventRefs { SnapshotId = evidence.Snapshot?.SnapshotId }, "diagnostic-engine",
                _ => EvidenceEventCodec.RuleFailure(failure)));
        }
        foreach (var gap in evidenceGaps ?? Array.Empty<ObservationEvidenceGap>())
        {
            rows.Add(new(diagnosisAt, diagnosisMonotonicMs, EventType.Gap, gap.Status,
                new EventRefs { SnapshotId = evidence.Snapshot?.SnapshotId }, gap.Source,
                _ => EvidenceEventCodec.ObservationSourceGap(gap)));
        }
        var sorted = rows.OrderBy(x => x.MonotonicMs).ToArray();
        var events = new List<EventEnvelope>();
        for (var index = 0; index < sorted.Length; index++)
        {
            var row = sorted[index];
            var id = synthetic ? $"event-{sessionId}{(batchId is null ? "" : "-" + batchId)}-{index + 1:000}" : Guid.NewGuid().ToString("D");
            var envelope = new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion, EventId = id, SessionId = sessionId,
                BootId = bootId, Sequence = index + 1, ObservedAt = row.ObservedAt, MonotonicMs = row.MonotonicMs,
                EventType = row.Type, Status = row.Status, Source = row.Source, SourceVersion = "1.1",
                Synthetic = synthetic, Refs = row.Refs, Payload = row.Payload(id),
            };
            _ = EnvelopeJson.Serialize(envelope);
            events.Add(envelope);
        }
        var decoded = EvidenceEventCodec.Decode(events);
        if (decoded.Problems.Count != 0)
            throw new JsonException("The session evidence is incomplete: " + string.Join("; ", decoded.Problems));
        return events;
    }

    /// <summary>
    /// The explicit window of this write. The caller may pass the session window; otherwise a
    /// single window declared by the probes of this cycle is used. Records from another window,
    /// or records without an explicit window when the cycle has one, are rejected.
    /// </summary>
    private static string? ResolveExpectedWindow(EvidenceSet evidence, string? observationWindowId)
    {
        if (!string.IsNullOrWhiteSpace(observationWindowId))
        {
            return observationWindowId;
        }

        var windows = evidence.Probes
            .Select(probe => probe.ObservationWindowId)
            .Where(window => !string.IsNullOrWhiteSpace(window))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return windows.Length == 1 ? windows[0] : null;
    }

    private static void RequireRecordScope(EvidenceRecord record, NetworkSnapshot? snapshot, string? expectedWindow)
    {
        var expectedSnapshot = snapshot?.SnapshotId;
        if (record.Facts.TryGetValue("snapshot_id", out var factSnapshot))
        {
            if (string.IsNullOrWhiteSpace(factSnapshot))
                throw new JsonException("Evidence record has an empty snapshot reference.");
            if (expectedSnapshot is null)
                throw new JsonException("Evidence record references a snapshot outside this evidence set; it is not restamped.");
            if (!string.Equals(factSnapshot, expectedSnapshot, StringComparison.Ordinal))
                throw new JsonException("Evidence record references a snapshot outside this evidence set; it is not restamped.");
        }

        if (expectedWindow is null)
        {
            return;
        }

        if (!string.Equals(record.ObservationWindowId, expectedWindow, StringComparison.Ordinal))
            throw new JsonException("Evidence record belongs to another observation window; this cycle cannot adopt it.");
    }

    private sealed record Row(DateTimeOffset ObservedAt, double MonotonicMs, EventType Type, EventStatus Status,
        EventRefs Refs, string Source, Func<string, JsonObject> Payload);
}
