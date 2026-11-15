using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>What the strict reader found in one stored session log, including crash evidence.</summary>
public sealed record SessionRecoveryInspection
{
    public required string SessionPath { get; init; }

    public required bool Exists { get; init; }

    public required long Bytes { get; init; }

    public required DateTimeOffset InspectedAt { get; init; }

    public required bool InvalidEncoding { get; init; }

    public required int LineCount { get; init; }

    public required int EventCount { get; init; }

    public required int InvalidLineCount { get; init; }

    public required int DuplicateCount { get; init; }

    public required IReadOnlyList<EventLogProblem> Problems { get; init; }

    public required IReadOnlyList<SequenceGap> SequenceGaps { get; init; }

    public required bool HasTruncatedTail { get; init; }

    public required bool HasMiddleCorruption { get; init; }

    public required long TruncatedTailBytes { get; init; }

    public required int? TruncatedTailLine { get; init; }

    /// <summary>
    /// The wall clock is earlier than the last recorded event. Duration math stays on the
    /// monotonic clock, and times read from this recovery are marked uncertain instead of negative.
    /// </summary>
    public required bool ClockRollbackSuspected { get; init; }

    public required IReadOnlyList<int> InvalidLineNumbers { get; init; }

    public required string Summary { get; init; }

    /// <summary>A truncated final line can be quarantined; middle damage is never silently rewritten.</summary>
    public bool IsRecoverableTruncatedTail =>
        Exists && !InvalidEncoding && HasTruncatedTail && !HasMiddleCorruption;

    public bool IsClean => Exists && !InvalidEncoding && Problems.Count == 0 && SequenceGaps.Count == 0
        && DuplicateCount == 0;
}

/// <summary>Result of recording one explicit gap after an append was rejected (for example a full disk).</summary>
public sealed record SessionGapRecordResult
{
    public required bool Recorded { get; init; }

    public required int? Sequence { get; init; }

    public required IReadOnlyList<string> NotPersisted { get; init; }

    public required string Summary { get; init; }
}

/// <summary>Outcome of quarantine/truncate/gap-marker recovery. Every partial result is explicit.</summary>
public sealed record SessionRecoveryResult
{
    public required SessionRecoveryInspection Inspection { get; init; }

    public required bool Modified { get; init; }

    public required bool QuarantineWritten { get; init; }

    public required string? QuarantinePath { get; init; }

    public required long QuarantinedBytes { get; init; }

    public required bool GapEventAppended { get; init; }

    public required IReadOnlyList<string> NotPersisted { get; init; }

    public required string Summary { get; init; }
}

/// <summary>Cursor/reason document written next to a rebuilt SQLite projection.</summary>
public sealed record IndexRebuildReport
{
    public required string SessionPath { get; init; }

    public required string DatabasePath { get; init; }

    public required string Reason { get; init; }

    public required bool Succeeded { get; init; }

    public required int IndexedEvents { get; init; }

    public required int SkippedDuplicates { get; init; }

    public required int InvalidLines { get; init; }

    public required int? LastSequence { get; init; }

    public required DateTimeOffset? LastObservedAt { get; init; }

    public required bool StateMarkerWritten { get; init; }

    public required bool CursorWritten { get; init; }

    public required DateTimeOffset RebuiltAt { get; init; }

    public required IReadOnlyList<string> NotPersisted { get; init; }

    public required string Summary { get; init; }
}

/// <summary>The durable state of a SQLite projection: a crash can leave it in "rebuilding".</summary>
public sealed record IndexState
{
    public required string Status { get; init; }

    public required DateTimeOffset ReadAt { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset? RebuiltAt { get; init; }

    public int? EventCount { get; init; }

    public int? LastSequence { get; init; }

    public DateTimeOffset? LastObservedAt { get; init; }

    public bool NeedsRebuild => Status is "missing" or "rebuilding" or "unknown";
}

/// <summary>One range/scope that was not persisted and must not be presented as retained.</summary>
public sealed record UnpersistedRange
{
    public required string Scope { get; init; }

    public required string? Path { get; init; }

    public required int? FirstSequence { get; init; }

    public required int? LastSequence { get; init; }

    public required string Reason { get; init; }

    public required bool FailureCountsRetained { get; init; }
}

/// <summary>Explicit degradation when local storage cannot be written.</summary>
public sealed record SessionPersistenceStatus
{
    public required bool SessionWritable { get; init; }

    public required bool IndexWritable { get; init; }

    public required bool SidecarWritable { get; init; }

    public required IReadOnlyList<UnpersistedRange> Unpersisted { get; init; }

    public required string Message { get; init; }

    public bool CanRetainFailureCounts => SessionWritable;

    public bool IsLossless => SessionWritable && IndexWritable && SidecarWritable && Unpersisted.Count == 0;
}

/// <summary>
/// Crash recovery and index rebuild for the local source of truth. It never invents an event:
/// a truncated final line is copied to a quarantine file, removed from the log and recorded as one
/// explicit gap event; middle damage is reported and left untouched, while later valid lines stay
/// readable. SQLite is always rebuilt from the NDJSON log with duplicate suppression and a cursor.
/// </summary>
public sealed class SessionRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly IStorageFileSystem _files;
    private readonly IClock _clock;

    public SessionRecovery(IStorageFileSystem? files = null, IClock? clock = null)
    {
        _files = files ?? StorageFileSystem.Instance;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Reads the session through the same seam as recovery; useful for status views and tests.</summary>
    public EventLogReadResult Read(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) throw new ArgumentException("A session path is required.", nameof(sessionPath));
        var fullPath = Path.GetFullPath(sessionPath);
        if (!_files.FileExists(fullPath)) return EventLogParser.Parse(string.Empty);
        return EventLogParser.Parse(Decode(ReadAllBytes(fullPath)));
    }

    public SessionRecoveryInspection Inspect(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) throw new ArgumentException("A session path is required.", nameof(sessionPath));
        var fullPath = Path.GetFullPath(sessionPath);
        var inspectedAt = _clock.UtcNow;
        if (!_files.FileExists(fullPath))
        {
            return Empty(fullPath, inspectedAt, "The session log does not exist.");
        }

        var bytes = _files.GetFileLength(fullPath);
        byte[] content;
        try
        {
            content = ReadAllBytes(fullPath);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return Empty(fullPath, inspectedAt, "The session log could not be read: " + exception.GetType().Name, bytes);
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return new SessionRecoveryInspection
            {
                SessionPath = fullPath,
                Exists = true,
                Bytes = bytes,
                InspectedAt = inspectedAt,
                InvalidEncoding = true,
                LineCount = 0,
                EventCount = 0,
                InvalidLineCount = 0,
                DuplicateCount = 0,
                Problems = Array.Empty<EventLogProblem>(),
                SequenceGaps = Array.Empty<SequenceGap>(),
                HasTruncatedTail = false,
                HasMiddleCorruption = false,
                TruncatedTailBytes = 0,
                TruncatedTailLine = null,
                ClockRollbackSuspected = false,
                InvalidLineNumbers = Array.Empty<int>(),
                Summary = "The session log is not valid UTF-8; it is preserved for manual inspection and cannot be recovered automatically.",
            };
        }

        var parsed = EventLogParser.ParseDetailed(text);
        var read = EventLogParser.ToReadResult(parsed);
        var tailProblem = read.Problems.FirstOrDefault(problem => problem.IsTruncatedTail);
        var middleCorruption = read.Problems.Any(problem => !problem.IsTruncatedTail);
        var tailBytes = tailProblem is null ? 0 : TailByteLength(content);
        var invalidLines = read.Problems.Select(problem => problem.LineNumber).ToArray();
        var nonEmptyLines = parsed.Lines.Count;
        var clockRollback = read.Events.Count > 0
            && read.Events[^1].ObservedAt > inspectedAt + TimeSpan.FromMinutes(1);

        var summary = read.IsClean
            ? $"Clean session log: {read.Events.Count} event(s), {nonEmptyLines} line(s)."
            : tailProblem is not null
                ? $"Interrupted final line detected ({tailBytes} bytes, line {tailProblem.LineNumber}); {read.Events.Count} earlier event(s) are valid. The tail can be quarantined and recorded as a gap."
                : $"Session log has {read.Problems.Count} damaged line(s) and {read.SequenceGaps.Count} sequence gap(s); middle damage is not treated as a valid prefix and is left untouched.";
        if (clockRollback)
        {
            summary += " The wall clock is earlier than the last recorded event: times are marked uncertain and no negative duration is produced.";
        }

        return new SessionRecoveryInspection
        {
            SessionPath = fullPath,
            Exists = true,
            Bytes = bytes,
            InspectedAt = inspectedAt,
            InvalidEncoding = false,
            LineCount = nonEmptyLines,
            EventCount = read.Events.Count,
            InvalidLineCount = read.Problems.Count,
            DuplicateCount = read.DuplicateCount,
            Problems = read.Problems,
            SequenceGaps = read.SequenceGaps,
            HasTruncatedTail = tailProblem is not null,
            HasMiddleCorruption = middleCorruption,
            TruncatedTailBytes = tailBytes,
            TruncatedTailLine = tailProblem?.LineNumber,
            ClockRollbackSuspected = clockRollback,
            InvalidLineNumbers = invalidLines,
            Summary = summary,
        };
    }

    /// <summary>
    /// Quarantines a truncated final line, cuts the log to the last complete line and appends one
    /// explicit gap event. A crash between these steps is reported as a partial result; the original
    /// bytes are never destroyed before the quarantine copy is written.
    /// </summary>
    public SessionRecoveryResult QuarantineTruncatedTail(string sessionPath)
    {
        var inspection = Inspect(sessionPath);
        if (!inspection.IsRecoverableTruncatedTail)
        {
            return Result(inspection, modified: false, quarantineWritten: false, quarantinePath: null,
                quarantinedBytes: 0, gapAppended: false, notPersisted: Array.Empty<string>(),
                summary: inspection.HasMiddleCorruption
                    ? "Recovery refused: the log has middle damage, so cutting it would treat a corrupt file as a valid prefix. Export/preserve it for manual review."
                    : inspection.HasTruncatedTail
                        ? "Recovery refused: the session log is not a recoverable truncated tail."
                        : "Recovery not needed: no truncated final line was found.");
        }

        var content = ReadAllBytes(inspection.SessionPath);
        var tailStart = LastLineStart(content);
        var tail = content[tailStart..];
        var quarantinePath = StorageLayout.QuarantinePathFor(inspection.SessionPath, inspection.InspectedAt);

        // 1. Persist the original tail before touching the log. A crash here loses nothing.
        try
        {
            using var stream = _files.OpenWrite(quarantinePath);
            stream.Write(tail);
            stream.Flush();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return Result(inspection, modified: false, quarantineWritten: false, quarantinePath,
                quarantinedBytes: 0, gapAppended: false,
                notPersisted: new[] { "quarantine copy of the truncated tail (" + tail.Length + " bytes)" },
                summary: "Not recovered: the quarantine copy could not be written, so the log was left untouched.");
        }

        // 2. Cut only complete lines. A crash after quarantine leaves a safe duplicate copy.
        try
        {
            using var stream = _files.OpenReadWrite(inspection.SessionPath);
            stream.SetLength(tailStart);
            stream.Flush();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return Result(inspection, modified: false, quarantineWritten: true, quarantinePath,
                quarantinedBytes: tail.Length, gapAppended: false,
                notPersisted: new[] { "removal of the truncated tail from the session log" },
                summary: "Partially recovered: the original tail is quarantined, but the log still contains it. No event was removed.");
        }

        // 3. Record the loss as an explicit gap event.
        var notPersisted = new List<string>();
        var gapAppended = false;
        var payload = new JsonObject
        {
            ["kind"] = "truncated_tail_quarantined",
            ["reason"] = "an interrupted final line was quarantined",
            ["quarantined_bytes"] = tail.Length,
            ["line"] = inspection.TruncatedTailLine,
            ["sequence_unknown"] = true,
            ["clock_rollback_suspected"] = inspection.ClockRollbackSuspected,
            ["recorded_at"] = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = "The lost line's sequence number is unknown; this gap records the loss without inventing one.",
        };
        try
        {
            gapAppended = TryAppendGap(inspection.SessionPath, payload, out _);
            if (!gapAppended) notPersisted.Add("gap event for the quarantined tail (no valid prior event identity)");
        }
        catch (Exception exception) when (IsIoFailure(exception) || exception is InvalidDataException)
        {
            notPersisted.Add("gap event for the quarantined tail (" + exception.GetType().Name + ")");
        }

        return Result(inspection, modified: true, quarantineWritten: true, quarantinePath,
            quarantinedBytes: tail.Length, gapAppended: gapAppended, notPersisted: notPersisted,
            summary: gapAppended
                ? $"Recovered: {tail.Length} truncated byte(s) quarantined to the named file, complete events kept and one gap event appended."
                : $"Partially recovered: {tail.Length} truncated byte(s) quarantined and removed from the log, but the gap marker was not persisted. Do not claim the gap was recorded.");
    }

    /// <summary>
    /// Records one explicit gap event after an append was rejected (for example a full disk). It
    /// is the caller's way to keep a gap marker once storage is writable again, without pretending
    /// the rejected events existed. Failure is reported, never silently ignored.
    /// </summary>
    public SessionGapRecordResult RecordGap(string sessionPath, int? firstAttemptedSequence,
        int? lastAttemptedSequence, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) throw new ArgumentException("A session path is required.", nameof(sessionPath));
        var inspection = Inspect(sessionPath);
        if (!inspection.Exists)
        {
            return new SessionGapRecordResult
            {
                Recorded = false,
                Sequence = null,
                NotPersisted = new[] { "gap marker (session log missing)" },
                Summary = "Gap not recorded: the session log does not exist.",
            };
        }

        var payload = new JsonObject
        {
            ["kind"] = "append_failure",
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? "append was rejected" : reason,
            ["first_attempted_sequence"] = firstAttemptedSequence,
            ["last_attempted_sequence"] = lastAttemptedSequence,
            ["attempted_count"] = firstAttemptedSequence is { } first && lastAttemptedSequence is { } last
                ? Math.Max(0, last - first + 1)
                : (int?)null,
            ["sequence_unknown"] = firstAttemptedSequence is null || lastAttemptedSequence is null,
            ["clock_rollback_suspected"] = inspection.ClockRollbackSuspected,
            ["recorded_at"] = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = "These events were not persisted. This gap event preserves the missing range; it does not recreate the events or their failure counts.",
        };
        try
        {
            var recorded = TryAppendGap(inspection.SessionPath, payload, out var sequence);
            return new SessionGapRecordResult
            {
                Recorded = recorded,
                Sequence = recorded ? sequence : null,
                NotPersisted = recorded ? Array.Empty<string>() : new[] { "gap marker event (no valid prior event identity)" },
                Summary = recorded
                    ? $"Gap recorded at sequence {sequence} for the unpersisted range; prior events and failure counts remain readable."
                    : "Gap not recorded: the session has no valid prior event identity.",
            };
        }
        catch (Exception exception) when (IsIoFailure(exception) || exception is InvalidDataException)
        {
            return new SessionGapRecordResult
            {
                Recorded = false,
                Sequence = null,
                NotPersisted = new[] { "gap marker event (" + exception.GetType().Name + ")" },
                Summary = "Gap not recorded: storage still cannot be written. Do not claim the missing range was preserved.",
            };
        }
    }

    /// <summary>
    /// Rebuilds the SQLite projection from the NDJSON source. It is idempotent, suppresses
    /// duplicate event ids (first occurrence wins) and writes a state/cursor sidecar with reason
    /// and last indexed position so a crash at the commit point is visible, not silent.
    /// </summary>
    public IndexRebuildReport RebuildIndex(string sessionPath, string databasePath, string reason = "manual")
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) throw new ArgumentException("A session path is required.", nameof(sessionPath));
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        var fullSession = Path.GetFullPath(sessionPath);
        var fullDatabase = Path.GetFullPath(databasePath);
        var rebuiltAt = _clock.UtcNow;
        var notPersisted = new List<string>();
        var inspection = Inspect(fullSession);
        if (!inspection.Exists || inspection.InvalidEncoding)
        {
            return new IndexRebuildReport
            {
                SessionPath = fullSession,
                DatabasePath = fullDatabase,
                Reason = reason,
                Succeeded = false,
                IndexedEvents = 0,
                SkippedDuplicates = 0,
                InvalidLines = inspection.InvalidLineCount,
                LastSequence = null,
                LastObservedAt = null,
                StateMarkerWritten = false,
                CursorWritten = false,
                RebuiltAt = rebuiltAt,
                NotPersisted = new[] { "index rebuild (" + inspection.Summary + ")" },
                Summary = "Index rebuild refused: " + inspection.Summary,
            };
        }

        var text = Decode(ReadAllBytes(fullSession));
        var parsed = EventLogParser.ParseDetailed(text);
        var read = EventLogParser.ToReadResult(parsed);
        var statePath = StorageLayout.IndexStatePathFor(fullDatabase);
        var cursorPath = StorageLayout.IndexCursorPathFor(fullDatabase);
        var stateWritten = TryWriteJson(statePath, new JsonObject
        {
            ["status"] = "rebuilding",
            ["reason"] = reason,
            ["started_at"] = rebuiltAt.ToString("O", CultureInfo.InvariantCulture),
            ["session"] = Path.GetFileName(fullSession),
            ["source_bytes"] = inspection.Bytes,
        }, notPersisted, "index rebuilding marker");

        var indexed = -1;
        try
        {
            var directory = Path.GetDirectoryName(fullDatabase);
            if (!string.IsNullOrEmpty(directory)) _files.CreateDirectory(directory);
            using var index = SqliteEventIndex.Open(fullDatabase);
            indexed = index.Rebuild(read.Events);
        }
        catch (Exception exception) when (IsIoFailure(exception) || exception is InvalidOperationException)
        {
            if (stateWritten) TryWriteJson(statePath, new JsonObject
            {
                ["status"] = "rebuilding",
                ["reason"] = reason,
                ["failure"] = exception.GetType().Name,
                ["failed_at"] = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["session"] = Path.GetFileName(fullSession),
            }, notPersisted, "index failure marker");
            return new IndexRebuildReport
            {
                SessionPath = fullSession,
                DatabasePath = fullDatabase,
                Reason = reason,
                Succeeded = false,
                IndexedEvents = 0,
                SkippedDuplicates = read.DuplicateCount,
                InvalidLines = read.Problems.Count,
                LastSequence = read.Events.Count == 0 ? null : read.Events[^1].Sequence,
                LastObservedAt = read.Events.Count == 0 ? null : read.Events[^1].ObservedAt,
                StateMarkerWritten = stateWritten,
                CursorWritten = false,
                RebuiltAt = rebuiltAt,
                NotPersisted = new[] { "SQLite projection rows for " + Path.GetFileName(fullDatabase) }
                    .Concat(notPersisted).ToArray(),
                Summary = "Index rebuild failed; the NDJSON source of truth was not modified. " + exception.GetType().Name,
            };
        }

        var last = read.Events.Count == 0 ? null : read.Events[^1];
        var cursorDocument = new JsonObject
        {
            ["schema_version"] = "1",
            ["status"] = "ready",
            ["reason"] = reason,
            ["session"] = Path.GetFileName(fullSession),
            ["source_bytes"] = inspection.Bytes,
            ["source_lines"] = inspection.LineCount,
            ["source_events"] = read.Events.Count,
            ["invalid_lines"] = read.Problems.Count,
            ["duplicate_events_ignored"] = read.DuplicateCount,
            ["sequence_gaps"] = read.SequenceGaps.Count,
            ["truncated_tail_present"] = read.HasTruncatedTail,
            ["indexed_rows"] = indexed,
            ["rebuilt_at"] = rebuiltAt.ToString("O", CultureInfo.InvariantCulture),
            ["last_sequence"] = last?.Sequence,
            ["last_observed_at"] = last?.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = "Rebuilt from the NDJSON source of truth. Duplicate event ids keep their first occurrence; invalid middle lines do not hide later valid events.",
        };
        var cursorWritten = TryWriteJson(cursorPath, cursorDocument, notPersisted, "index cursor");
        var readyWritten = TryWriteJson(statePath, new JsonObject
        {
            ["status"] = "ready",
            ["reason"] = reason,
            ["rebuilt_at"] = rebuiltAt.ToString("O", CultureInfo.InvariantCulture),
            ["indexed_rows"] = indexed,
            ["session"] = Path.GetFileName(fullSession),
        }, notPersisted, "index ready marker");

        var summary = read.Problems.Count == 0 && read.DuplicateCount == 0
            ? $"Index rebuilt: {indexed} row(s) from {read.Events.Count} event(s), cursor at sequence "
              + $"{(last is null ? "none" : last.Sequence.ToString(CultureInfo.InvariantCulture))}."
            : $"Index rebuilt from a damaged log: {indexed} row(s), {read.Problems.Count} invalid line(s), "
              + $"{read.DuplicateCount} duplicate event(s) ignored; later valid events were still indexed.";

        return new IndexRebuildReport
        {
            SessionPath = fullSession,
            DatabasePath = fullDatabase,
            Reason = reason,
            Succeeded = true,
            IndexedEvents = indexed,
            SkippedDuplicates = read.DuplicateCount,
            InvalidLines = read.Problems.Count,
            LastSequence = last?.Sequence,
            LastObservedAt = last?.ObservedAt,
            StateMarkerWritten = stateWritten && readyWritten,
            CursorWritten = cursorWritten,
            RebuiltAt = rebuiltAt,
            NotPersisted = notPersisted,
            Summary = summary,
        };
    }

    /// <summary>Reads the projection state; "missing"/"rebuilding" means the index must be rebuilt.</summary>
    public IndexState ReadIndexState(string databasePath)
    {
        var fullDatabase = Path.GetFullPath(databasePath);
        var cursorPath = StorageLayout.IndexCursorPathFor(fullDatabase);
        var statePath = StorageLayout.IndexStatePathFor(fullDatabase);
        var now = _clock.UtcNow;
        foreach (var (path, defaultStatus) in new[] { (statePath, "missing"), (cursorPath, "unknown") })
        {
            try
            {
                if (!_files.FileExists(path)) continue;
                var document = JsonNode.Parse(Decode(ReadAllBytes(path)));
                if (document is not JsonObject state) continue;
                var status = state["status"]?.GetValue<string>() ?? defaultStatus;
                return new IndexState
                {
                    Status = status,
                    ReadAt = now,
                    Reason = state["reason"]?.GetValue<string>(),
                    RebuiltAt = ParseTime(state["rebuilt_at"]),
                    EventCount = state["indexed_rows"]?.GetValue<int>(),
                    LastSequence = state["last_sequence"]?.GetValue<int>(),
                    LastObservedAt = ParseTime(state["last_observed_at"]),
                };
            }
            catch (Exception exception) when (IsIoFailure(exception) || exception is JsonException
                                                  or InvalidOperationException or FormatException)
            {
                return new IndexState { Status = "unknown", ReadAt = now, Reason = exception.GetType().Name };
            }
        }

        return new IndexState { Status = "missing", ReadAt = now };
    }

    /// <summary>
    /// Reports exactly which scopes are currently writable. When the session log cannot be written
    /// the failure counts of this cycle cannot be promised either; the message says so explicitly.
    /// </summary>
    public SessionPersistenceStatus CheckPersistence(string sessionPath, int? attemptedFirstSequence = null,
        int? attemptedLastSequence = null)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) throw new ArgumentException("A session path is required.", nameof(sessionPath));
        var fullPath = Path.GetFullPath(sessionPath);
        var sessionsDirectory = Path.GetDirectoryName(fullPath) ?? fullPath;
        var sessionWritable = _files.ProbeWritable(sessionsDirectory);
        var indexWritable = _files.ProbeWritable(sessionsDirectory);
        var sidecarWritable = _files.ProbeWritable(sessionsDirectory);
        var unpersisted = new List<UnpersistedRange>();

        if (!sessionWritable)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "session events (this cycle, including sequence gap markers)",
                Path = fullPath,
                FirstSequence = attemptedFirstSequence,
                LastSequence = attemptedLastSequence,
                Reason = "the session directory is not writable",
                FailureCountsRetained = false,
            });
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "failure counts and observation gap events for this cycle",
                Path = fullPath,
                FirstSequence = attemptedFirstSequence,
                LastSequence = attemptedLastSequence,
                Reason = "failure counters live in the same log, so they are not persisted either",
                FailureCountsRetained = false,
            });
        }

        if (!indexWritable)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "SQLite query projection rows and cursor",
                Path = StorageLayout.IndexPathFor(fullPath),
                FirstSequence = attemptedFirstSequence,
                LastSequence = attemptedLastSequence,
                Reason = "the index directory is not writable",
                FailureCountsRetained = sessionWritable,
            });
        }

        if (!sidecarWritable)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "sealed incident and aggregate sidecars",
                Path = Path.GetDirectoryName(fullPath),
                FirstSequence = null,
                LastSequence = null,
                Reason = "the sidecar directory is not writable",
                FailureCountsRetained = sessionWritable,
            });
        }

        var message = sessionWritable
            ? indexWritable && sidecarWritable
                ? "Session log, SQLite projection and sidecars are writable; persisted events are retained."
                : "Session events are writable, but the query projection or sidecars are not. Events are retained; index/sidecar scopes are listed as not persisted."
            : "Log write failed. This cycle's events, failure counts and sequence-gap markers were NOT persisted. Do not claim they were retained or that storage is lossless.";

        return new SessionPersistenceStatus
        {
            SessionWritable = sessionWritable,
            IndexWritable = indexWritable,
            SidecarWritable = sidecarWritable,
            Unpersisted = unpersisted,
            Message = message,
        };
    }

    private bool TryAppendGap(string sessionPath, JsonObject payload, out int sequence)
    {
        sequence = 0;
        var text = Decode(ReadAllBytes(sessionPath));
        var parsed = EventLogParser.ParseDetailed(text);
        if (parsed.Events.Count == 0) return false;
        var last = parsed.Events[^1];
        sequence = last.Sequence + 1;
        payload["last_sequence"] = last.Sequence;
        var envelope = new EventEnvelope
        {
            SchemaVersion = EventEnvelope.SupportedSchemaVersion,
            EventId = "gap-" + Guid.NewGuid().ToString("N"),
            SessionId = last.SessionId,
            BootId = last.BootId,
            Sequence = sequence,
            ObservedAt = _clock.UtcNow,
            // The monotonic clock never decreases: a wall-clock rollback must not create a negative duration.
            MonotonicMs = Math.Max(last.MonotonicMs, 0),
            EventType = EventType.Gap,
            Status = EventStatus.Failed,
            Source = "storage.recovery",
            SourceVersion = "1.0",
            Synthetic = last.Synthetic,
            Refs = new EventRefs { SnapshotId = last.Refs.SnapshotId },
            Payload = payload,
        };
        var serialized = EnvelopeJson.Serialize(envelope);
        var separator = text.Length > 0 && !text.EndsWith('\n') && !text.EndsWith('\r') ? "\n" : string.Empty;
        using var stream = _files.OpenReadWrite(sessionPath);
        stream.Seek(0, SeekOrigin.End);
        var bytes = Encoding.UTF8.GetBytes(separator + serialized + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        _files.FlushToDisk(stream);
        return true;
    }

    private static SessionRecoveryResult Result(SessionRecoveryInspection inspection, bool modified,
        bool quarantineWritten, string? quarantinePath, long quarantinedBytes, bool gapAppended,
        IReadOnlyList<string> notPersisted, string summary) => new()
    {
        Inspection = inspection,
        Modified = modified,
        QuarantineWritten = quarantineWritten,
        QuarantinePath = quarantinePath,
        QuarantinedBytes = quarantinedBytes,
        GapEventAppended = gapAppended,
        NotPersisted = notPersisted,
        Summary = summary,
    };

    private byte[] ReadAllBytes(string path)
    {
        using var stream = _files.OpenRead(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private bool TryWriteJson(string path, JsonObject document, List<string> notPersisted, string scope)
    {
        try
        {
            var text = document.ToJsonString(JsonOptions) + "\n";
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = _files.OpenWrite(temporary))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush();
            }

            _files.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            notPersisted.Add(scope + " (" + exception.GetType().Name + ")");
            return false;
        }
    }

    private static int LastLineStart(byte[] content)
    {
        for (var index = content.Length - 1; index >= 0; index--)
        {
            if (content[index] == (byte)'\n') return index + 1;
        }

        return 0;
    }

    private static long TailByteLength(byte[] content)
    {
        var start = LastLineStart(content);
        return content.Length - start;
    }

    private static string Decode(byte[] content) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static bool IsIoFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or NotSupportedException or System.Security.SecurityException or ArgumentException or InvalidDataException;

    private static SessionRecoveryInspection Empty(string path, DateTimeOffset at, string summary, long bytes = 0) => new()
    {
        SessionPath = path,
        Exists = false,
        Bytes = bytes,
        InspectedAt = at,
        InvalidEncoding = false,
        LineCount = 0,
        EventCount = 0,
        InvalidLineCount = 0,
        DuplicateCount = 0,
        Problems = Array.Empty<EventLogProblem>(),
        SequenceGaps = Array.Empty<SequenceGap>(),
        HasTruncatedTail = false,
        HasMiddleCorruption = false,
        TruncatedTailBytes = 0,
        TruncatedTailLine = null,
        ClockRollbackSuspected = false,
        InvalidLineNumbers = Array.Empty<int>(),
        Summary = summary,
    };
}
