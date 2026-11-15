using System.Text.Json;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>One physical line of an NDJSON log and what the strict reader made of it.</summary>
internal sealed record ParsedLine(int LineNumber, string Text, bool IsValid, EventEnvelope? Envelope,
    bool IsTruncatedTail, string? ProblemMessage);

/// <summary>Line-preserving parse output used by recovery in addition to the replay summary.</summary>
internal sealed record ParsedEventLog(
    IReadOnlyList<EventEnvelope> Events,
    IReadOnlyList<EventLogProblem> Problems,
    IReadOnlyList<SequenceGap> SequenceGaps,
    int DuplicateCount,
    IReadOnlyList<ParsedLine> Lines,
    bool Terminated);

/// <summary>
/// The single strict line parser shared by <see cref="NdjsonEventLog"/> and crash recovery. A
/// malformed line is reported and skipped; later valid lines are still read, so a middle problem
/// never silently becomes "the valid prefix is all there is". A malformed final line without a
/// trailing newline is what an interrupted append looks like and is marked as a truncated tail.
/// </summary>
internal static class EventLogParser
{
    internal static EventLogReadResult Parse(string text) => ToReadResult(ParseDetailed(text));

    internal static EventLogReadResult ToReadResult(ParsedEventLog parsed) => new()
    {
        Events = parsed.Events,
        Problems = parsed.Problems,
        SequenceGaps = parsed.SequenceGaps,
        DuplicateCount = parsed.DuplicateCount,
    };

    internal static ParsedEventLog ParseDetailed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var events = new List<EventEnvelope>();
        var problems = new List<EventLogProblem>();
        var gaps = new List<SequenceGap>();
        var lines = new List<ParsedLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        int? previousSequence = null;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var rawLines = normalized.Split('\n');
        var lastContentIndex = Array.FindLastIndex(rawLines, line => !string.IsNullOrWhiteSpace(line));
        var terminated = text.EndsWith('\n') || text.EndsWith('\r');
        for (var index = 0; index < rawLines.Length; index++)
        {
            var raw = rawLines[index].Trim();
            if (raw.Length == 0) continue;
            var syntaxValid = false;
            EventEnvelope? envelope = null;
            string? problem = null;
            try
            {
                using var document = JsonDocument.Parse(raw);
                syntaxValid = true;
                envelope = EnvelopeJson.Deserialize(raw);
            }
            catch (JsonException exception)
            {
                var truncatedTail = !syntaxValid && index == lastContentIndex && !terminated;
                problem = "The event could not be read: " + exception.Message;
                problems.Add(new EventLogProblem(index + 1, problem, IsTruncatedTail: truncatedTail));
                lines.Add(new ParsedLine(index + 1, raw, false, null, truncatedTail, problem));
                continue;
            }

            if (!seen.Add(envelope.EventId))
            {
                duplicates++;
                lines.Add(new ParsedLine(index + 1, raw, true, envelope, false, "duplicate event id"));
                continue;
            }

            if (previousSequence is null && envelope.Sequence > 1)
            {
                gaps.Add(new SequenceGap(1, envelope.Sequence));
            }
            else if (previousSequence is not null)
            {
                if ((long)envelope.Sequence > (long)previousSequence.Value + 1)
                    gaps.Add(new SequenceGap(previousSequence.Value + 1, envelope.Sequence));
                else if (envelope.Sequence <= previousSequence.Value)
                    problems.Add(new EventLogProblem(index + 1,
                        $"sequence {envelope.Sequence} is not greater than the previous {previousSequence.Value}", false));
            }

            previousSequence = envelope.Sequence;
            events.Add(envelope);
            lines.Add(new ParsedLine(index + 1, raw, true, envelope, false, null));
        }

        return new ParsedEventLog(events, problems, gaps, duplicates, lines, terminated);
    }
}
