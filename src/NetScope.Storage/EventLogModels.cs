using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>One problem found while reading an event log, always tied to its line number.</summary>
/// <param name="IsTruncatedTail">
/// True when the unreadable line is the last one, which is what an interrupted append looks
/// like. Earlier valid events stay readable; only the tail is discarded.
/// </param>
public sealed record EventLogProblem(int LineNumber, string Message, bool IsTruncatedTail);

/// <summary>A range of sequence numbers that should exist but does not.</summary>
public sealed record SequenceGap(int Expected, int Found)
{
    public int Missing => Found - Expected;
}

/// <summary>Result of reading one NDJSON session log.</summary>
public sealed record EventLogReadResult
{
    public required IReadOnlyList<EventEnvelope> Events { get; init; }

    public required IReadOnlyList<EventLogProblem> Problems { get; init; }

    public required IReadOnlyList<SequenceGap> SequenceGaps { get; init; }

    /// <summary>Lines whose event_id had already been seen; the first occurrence is kept.</summary>
    public required int DuplicateCount { get; init; }

    public bool HasTruncatedTail => Problems.Any(problem => problem.IsTruncatedTail);

    public bool IsClean => Problems.Count == 0 && SequenceGaps.Count == 0 && DuplicateCount == 0;
}

/// <summary>Raised when an event id is appended twice into the same session log.</summary>
public sealed class DuplicateEventException : Exception
{
    public DuplicateEventException(string eventId)
        : base($"Event '{eventId}' is already present in this session log; appending it again would create ambiguity.")
    {
        EventId = eventId;
    }

    public string EventId { get; }
}
