using System.Text;
using System.Text.Json;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>Injectable local-file boundary for append failure tests. No network or platform configuration.</summary>
public interface IEventLogFileAccess
{
    Stream OpenForAppend(string path);
    void FlushToDisk(Stream stream);
}

internal sealed class EventLogFileAccess : IEventLogFileAccess
{
    public Stream OpenForAppend(string path) => new FileStream(path, FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);

    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
}

/// <summary>
/// Local append-only source of truth. Admission uses the same embedded core contract as check.
/// Existing damage is reported and blocks further appends; recovery is never silently invented.
/// </summary>
public sealed class NdjsonEventLog : IEventStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEventLogFileAccess _files;
    private bool _disposed;

    public NdjsonEventLog(string path, IEventLogFileAccess? files = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The log path must not be empty.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        _files = files ?? new EventLogFileAccess();
    }

    public string Path { get; }

    /// <summary>Returns successfully only after a complete validated event is flushed to disk.</summary>
    public async Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var line = EnvelopeJson.Serialize(envelope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Exclusive writer handle covers validation, duplicate detection, write and flush.
            // Re-read under this lease: an earlier failed append or another writer cannot leave a stale id cache.
            await using var stream = _files.OpenForAppend(Path);
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
                throw new IOException("The event log requires a readable, writable, seekable local stream.");
            RequireUtf8(stream);
            stream.Position = 0;
            string text;
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                       detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
            {
                try { text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
                catch (DecoderFallbackException) { throw new InvalidDataException("The event log is not valid UTF-8; append refused."); }
            }
            var existing = Parse(text);
            if (existing.Problems.Count != 0 || existing.DuplicateCount != 0)
                throw new IOException("The existing event log is damaged or ambiguous; append refused. Preserve and inspect the file before recovery.");
            if (existing.Events.Any(item => item.EventId == envelope.EventId))
                throw new DuplicateEventException(envelope.EventId);

            // A complete last JSON value without LF is safe; insert a separator before the next value.
            var separator = text.Length > 0 && !text.EndsWith('\n') && !text.EndsWith('\r') ? "\n" : string.Empty;
            var bytes = Encoding.UTF8.GetBytes(separator + line + "\n");
            stream.Position = stream.Length;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _files.FlushToDisk(stream);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<EventEnvelope>> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { cancellationToken.ThrowIfCancellationRequested(); return Read().Events; }
        finally { _gate.Release(); }
    }

    public EventLogReadResult Read()
    {
        if (!File.Exists(Path)) return Parse(string.Empty);
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            RequireUtf8(stream);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is DecoderFallbackException or InvalidDataException)
        {
            return Parse(string.Empty) with
            {
                Problems = new[] { new EventLogProblem(1, "Invalid event log encoding; UTF-8 is required.", false) },
            };
        }
    }

    private static void RequireUtf8(Stream stream)
    {
        stream.Position = 0;
        Span<byte> prefix = stackalloc byte[4];
        var count = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (count >= 2 && ((prefix[0] == 0xFF && prefix[1] == 0xFE) || (prefix[0] == 0xFE && prefix[1] == 0xFF))
            || count >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF)
            throw new InvalidDataException("Event logs must use UTF-8; append refused before changing the existing file.");
    }

    private static EventLogReadResult Parse(string text)
    {
        var events = new List<EventEnvelope>();
        var problems = new List<EventLogProblem>();
        var gaps = new List<SequenceGap>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        int? previousSequence = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var lastContentIndex = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var terminated = text.EndsWith('\n') || text.EndsWith('\r');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index].Trim();
            if (raw.Length == 0) continue;
            var syntaxValid = false;
            EventEnvelope envelope;
            try
            {
                using var document = JsonDocument.Parse(raw);
                syntaxValid = true;
                envelope = EnvelopeJson.Deserialize(raw);
            }
            catch (JsonException exception)
            {
                problems.Add(new EventLogProblem(index + 1,
                    "The event could not be read: " + exception.Message,
                    IsTruncatedTail: !syntaxValid && index == lastContentIndex && !terminated));
                continue;
            }
            if (!seen.Add(envelope.EventId)) { duplicates++; continue; }
            if (previousSequence is null && envelope.Sequence > 1)
                gaps.Add(new SequenceGap(1, envelope.Sequence));
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
        }
        return new EventLogReadResult { Events = events, Problems = problems, SequenceGaps = gaps, DuplicateCount = duplicates };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
