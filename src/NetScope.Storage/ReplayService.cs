using NetScope.Domain;
using NetScope.Application;

namespace NetScope.Storage;

/// <summary>Aggregate view of one replayed session.</summary>
public sealed record ReplaySummary
{
    public required int EventCount { get; init; }

    public required int SyntheticCount { get; init; }

    public required int SuccessCount { get; init; }

    public required int PartialCount { get; init; }

    public required int ObservationGapCount { get; init; }

    public required IReadOnlyDictionary<EventType, int> ByType { get; init; }

    public required IReadOnlyDictionary<EventStatus, int> ByStatus { get; init; }

    public bool AllSynthetic => EventCount > 0 && SyntheticCount == EventCount;
}

/// <summary>
/// One snapshot and only the evidence explicitly scoped to it. The snapshot is the current
/// topology for that cycle; the probes and records are not borrowed from other cycles.
/// </summary>
public sealed record SnapshotEvidenceScope
{
    public required string SnapshotEventId { get; init; }

    public required int Sequence { get; init; }

    public required NetworkSnapshot Snapshot { get; init; }

    public required EvidenceSet Evidence { get; init; }

    /// <summary>Diagnoses saved in this cycle, including diagnoses that cite cross-cycle evidence.</summary>
    public IReadOnlyList<Diagnosis> Diagnoses { get; init; } = Array.Empty<Diagnosis>();
}

/// <summary>Replay output: the events plus everything that could not be read cleanly.</summary>
public sealed record ReplayResult
{
    public required EventLogReadResult Log { get; init; }

    public required ReplaySummary Summary { get; init; }

    /// <summary>
    /// Single-snapshot-compatible view. When the session has one snapshot this is exactly that
    /// snapshot's scoped evidence. When it has several snapshots this is the <b>latest</b>
    /// snapshot scope only, never one snapshot combined with probes or records from other cycles.
    /// When the session has no snapshot at all this is the unscoped evidence set. Callers that
    /// need every cycle must read <see cref="SnapshotScopes"/> instead.
    /// </summary>
    public EvidenceSet? Evidence { get; init; }

    /// <summary>Every snapshot in read order, each with only its own scoped evidence.</summary>
    public IReadOnlyList<SnapshotEvidenceScope> SnapshotScopes { get; init; } = Array.Empty<SnapshotEvidenceScope>();

    /// <summary>Typed evidence that has no snapshot to attach to; never mixed into a snapshot scope.</summary>
    public EvidenceSet? UnscopedEvidence { get; init; }

    public IReadOnlyList<Diagnosis> Diagnoses { get; init; } = Array.Empty<Diagnosis>();

    public IReadOnlyList<string> EvidenceProblems { get; init; } = Array.Empty<string>();

    public bool IsClean => Log.IsClean && EvidenceProblems.Count == 0;
}

/// <summary>
/// Offline replay of a captured session.
/// <para>
/// Replay only reads the file and applies the same strict reader the live store uses. It has no
/// network client and no repair capability, so replaying a captured outage can never reproduce
/// the outage or change the machine.
/// </para>
/// </summary>
public sealed class ReplayService
{
    public ReplayResult Replay(string path)
    {
        using var log = new NdjsonEventLog(path);
        var read = log.Read();
        var decoded = EvidenceEventCodec.Decode(read.Events);

        return new ReplayResult
        {
            Log = read,
            Summary = Summarize(read.Events),
            Evidence = decoded.Evidence,
            SnapshotScopes = decoded.SnapshotScopes,
            UnscopedEvidence = decoded.UnscopedEvidence,
            Diagnoses = decoded.Diagnoses,
            EvidenceProblems = decoded.Problems,
        };
    }

    /// <summary>Builds a deterministic summary; the same input always produces the same output.</summary>
    public static ReplaySummary Summarize(IReadOnlyList<EventEnvelope> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var byType = new SortedDictionary<EventType, int>();
        var byStatus = new SortedDictionary<EventStatus, int>();

        foreach (var envelope in events)
        {
            byType[envelope.EventType] = byType.GetValueOrDefault(envelope.EventType) + 1;
            byStatus[envelope.Status] = byStatus.GetValueOrDefault(envelope.Status) + 1;
        }

        return new ReplaySummary
        {
            EventCount = events.Count,
            SyntheticCount = events.Count(envelope => envelope.Synthetic),
            SuccessCount = events.Count(envelope => envelope.Status.IsSuccess()),
            PartialCount = events.Count(envelope => envelope.Status == EventStatus.Partial),
            ObservationGapCount = events.Count(envelope => envelope.Status.IsObservationGap()),
            ByType = byType,
            ByStatus = byStatus,
        };
    }
}
