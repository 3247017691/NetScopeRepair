using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>
/// Failure/gap facts that must survive when a detail file is removed by retention. It contains
/// counts, first/last times and incident identifiers only; no event bodies, values or paths.
/// </summary>
public sealed record SessionEvidenceFacts
{
    public required int EventCount { get; init; }

    public required int FailureCount { get; init; }

    public required int PartialCount { get; init; }

    public required int GapEventCount { get; init; }

    public required int SequenceGapCount { get; init; }

    public required int MissingSequenceNumbers { get; init; }

    public required int ProblemLineCount { get; init; }

    public required DateTimeOffset? FirstFailureAt { get; init; }

    public required DateTimeOffset? LastFailureAt { get; init; }

    public required DateTimeOffset? FirstEventAt { get; init; }

    public required DateTimeOffset? LastEventAt { get; init; }

    public required IReadOnlyDictionary<string, int> FailureCountsByStatus { get; init; }

    public required IReadOnlyList<string> ActiveIncidentIds { get; init; }

    public required bool HasTruncatedTail { get; init; }

    /// <summary>Failure, gap or corruption evidence. Such a detail is never treated as disposable success detail.</summary>
    public bool HasEvidence => FailureCount > 0 || GapEventCount > 0 || SequenceGapCount > 0
        || ProblemLineCount > 0 || HasTruncatedTail;

    public bool HasActiveIncident => ActiveIncidentIds.Count > 0;
}

/// <summary>
/// Derives the retention-relevant facts from a strict read of one session log. It never repairs,
/// restamps or invents an event; a malformed line only makes the detail evidence-bearing.
/// </summary>
public static class SessionLogFacts
{
    public static SessionEvidenceFacts Read(EventLogReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var failures = read.Events
            .Where(envelope => envelope.Status.IsObservationGap())
            .ToArray();
        var firstEvent = read.Events.Count == 0
            ? (DateTimeOffset?)null
            : read.Events.Min(envelope => envelope.ObservedAt);
        var lastEvent = read.Events.Count == 0
            ? (DateTimeOffset?)null
            : read.Events.Max(envelope => envelope.ObservedAt);

        var failureCounts = failures
            .GroupBy(envelope => WireNames.Of(envelope.Status), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new SessionEvidenceFacts
        {
            EventCount = read.Events.Count,
            FailureCount = failures.Length,
            PartialCount = read.Events.Count(envelope => envelope.Status == EventStatus.Partial),
            GapEventCount = read.Events.Count(envelope => envelope.EventType == EventType.Gap),
            SequenceGapCount = read.SequenceGaps.Count,
            MissingSequenceNumbers = read.SequenceGaps.Sum(gap => gap.Missing),
            ProblemLineCount = read.Problems.Count,
            FirstFailureAt = failures.Length == 0 ? null : failures.Min(envelope => envelope.ObservedAt),
            LastFailureAt = failures.Length == 0 ? null : failures.Max(envelope => envelope.ObservedAt),
            FirstEventAt = firstEvent,
            LastEventAt = lastEvent,
            FailureCountsByStatus = failureCounts,
            ActiveIncidentIds = ActiveIncidentIds(read.Events),
            HasTruncatedTail = read.HasTruncatedTail,
        };
    }

    /// <summary>An incident is active only while its latest recorded state is one of the open states.</summary>
    private static IReadOnlyList<string> ActiveIncidentIds(IReadOnlyList<EventEnvelope> events)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var envelope in events)
        {
            if (envelope.EventType != EventType.IncidentMarker) continue;
            if (envelope.Payload["kind"]?.GetValue<string>() != "state_change") continue;
            var state = envelope.Payload["state"]?.GetValue<string>();
            var incidentId = envelope.Payload["incident"]?["incident_id"]?.GetValue<string>()
                ?? envelope.Refs.IncidentId;
            if (string.IsNullOrWhiteSpace(incidentId) || string.IsNullOrWhiteSpace(state)) continue;
            states[incidentId] = state;
        }

        return states
            .Where(item => item.Value is "observing" or "suspected" or "incident_open" or "recovering")
            .Select(item => item.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
