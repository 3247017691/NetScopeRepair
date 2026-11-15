using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Unit.Tests;

/// <summary>
/// T11 crash recovery: a truncated final line is quarantined and recorded as one explicit gap;
/// middle damage is never rewritten into a "valid prefix"; SQLite is rebuilt from NDJSON with
/// duplicate suppression and a traceable cursor. Failures use the file seam, so no real disk is
/// filled and no system clock is changed.
/// </summary>
public sealed class T11SessionRecoveryTests
{
    private static readonly DateTimeOffset Now = T11Data.Origin.AddDays(1);

    [Fact]
    public void Truncated_tail_is_quarantined_removed_and_recorded_as_one_gap_event()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("crash-session");
        var valid = new[] { T11Data.Event(1, EventStatus.Ok), T11Data.Event(2, EventStatus.Partial) };
        var truncated = T11Data.TruncatedLogText(valid);
        files.AddText(path, truncated, clock.UtcNow.AddMinutes(-5));

        var recovery = new SessionRecovery(files, clock);
        var inspection = recovery.Inspect(path);
        Assert.True(inspection.Exists);
        Assert.True(inspection.HasTruncatedTail);
        Assert.False(inspection.HasMiddleCorruption);
        Assert.True(inspection.IsRecoverableTruncatedTail);
        Assert.Equal(2, inspection.EventCount);
        Assert.Equal(T11Data.Bytes("{\"schema_version\":").LongLength, inspection.TruncatedTailBytes);

        var result = recovery.QuarantineTruncatedTail(path);
        Assert.True(result.Modified);
        Assert.True(result.QuarantineWritten);
        Assert.True(result.GapEventAppended);
        Assert.Empty(result.NotPersisted);
        Assert.NotNull(result.QuarantinePath);
        Assert.Equal("{\"schema_version\":", files.ReadText(result.QuarantinePath!));

        var read = recovery.Read(path);
        Assert.True(read.IsClean, string.Join("; ", read.Problems.Select(problem => problem.Message)));
        Assert.Empty(read.SequenceGaps);
        Assert.Equal(3, read.Events.Count);
        var gap = read.Events[^1];
        Assert.Equal(EventType.Gap, gap.EventType);
        Assert.Equal(EventStatus.Failed, gap.Status);
        Assert.Equal(3, gap.Sequence);
        Assert.Equal("truncated_tail_quarantined", gap.Payload["kind"]!.GetValue<string>());
        Assert.Equal(2, gap.Payload["last_sequence"]!.GetValue<int>());
        Assert.True(gap.Payload["sequence_unknown"]!.GetValue<bool>());
        Assert.Equal("storage.recovery", gap.Source);
        Assert.True(gap.MonotonicMs >= 0);
        Assert.True(gap.MonotonicMs >= read.Events[^2].MonotonicMs);

        var after = recovery.Inspect(path);
        Assert.True(after.IsClean);
        Assert.False(after.HasTruncatedTail);
        Assert.Equal(3, after.EventCount);
    }

    [Fact]
    public void Middle_corruption_is_reported_and_later_valid_lines_survive_without_rewrite()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("middle-damage");
        var first = EnvelopeJson.Serialize(T11Data.Event(1, EventStatus.Ok));
        var third = EnvelopeJson.Serialize(T11Data.Event(3, EventStatus.Ok));
        var text = first + "\nthis is not json\n" + third + "\n";
        files.AddText(path, text, clock.UtcNow.AddDays(-1));

        var recovery = new SessionRecovery(files, clock);
        var inspection = recovery.Inspect(path);

        Assert.True(inspection.HasMiddleCorruption);
        Assert.False(inspection.HasTruncatedTail);
        Assert.False(inspection.IsRecoverableTruncatedTail);
        Assert.Equal(new[] { 2 }, inspection.InvalidLineNumbers);
        Assert.Equal(2, inspection.EventCount); // the line after the damage is still valid evidence
        var gap = Assert.Single(inspection.SequenceGaps);
        Assert.Equal(2, gap.Expected);
        Assert.Equal(3, gap.Found);

        var result = recovery.QuarantineTruncatedTail(path);
        Assert.False(result.Modified);
        Assert.False(result.QuarantineWritten);
        Assert.Empty(result.NotPersisted);
        Assert.Equal(text, files.ReadText(path)); // never rewritten into a "valid prefix"
    }

    [Fact]
    public void Quarantine_write_failure_leaves_the_log_untouched()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("no-space");
        var text = T11Data.TruncatedLogText(new[] { T11Data.Event(1, EventStatus.Ok) });
        files.AddText(path, text, clock.UtcNow.AddDays(-1));
        files.FailAllWrites = true;

        var result = new SessionRecovery(files, clock).QuarantineTruncatedTail(path);

        Assert.False(result.Modified);
        Assert.False(result.QuarantineWritten);
        Assert.False(result.GapEventAppended);
        Assert.Contains(result.NotPersisted, item => item.Contains("quarantine", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(text, files.ReadText(path));
    }

    [Fact]
    public void Truncate_failure_after_the_quarantine_copy_keeps_the_original_and_reports_partial()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("truncate-fails");
        var text = T11Data.TruncatedLogText(new[] { T11Data.Event(1, EventStatus.Ok) });
        files.AddText(path, text, clock.UtcNow.AddDays(-1));
        files.FailOpenReadWritePaths.Add(Path.GetFullPath(path));

        var result = new SessionRecovery(files, clock).QuarantineTruncatedTail(path);

        Assert.False(result.Modified);
        Assert.True(result.QuarantineWritten);
        Assert.True(files.Exists(result.QuarantinePath!));
        Assert.Contains(result.NotPersisted, item => item.Contains("removal", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(text, files.ReadText(path));
    }

    [Fact]
    public void Gap_marker_failure_after_the_cut_is_reported_and_not_claimed()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("gap-fails");
        files.AddText(path, T11Data.TruncatedLogText(new[] { T11Data.Event(1, EventStatus.Ok) }),
            clock.UtcNow.AddDays(-1));
        files.OpenReadWriteAllowed = 1; // the cut is allowed; the gap append is the simulated crash point

        var result = new SessionRecovery(files, clock).QuarantineTruncatedTail(path);

        Assert.True(result.Modified);
        Assert.True(result.QuarantineWritten);
        Assert.False(result.GapEventAppended);
        Assert.Contains(result.NotPersisted, item => item.Contains("gap event", StringComparison.OrdinalIgnoreCase));
        var read = new SessionRecovery(files, clock).Read(path);
        Assert.Single(read.Events);
        Assert.DoesNotContain(read.Events, envelope => envelope.EventType == EventType.Gap);
    }

    [Fact]
    public void Index_rebuild_suppresses_duplicates_and_writes_a_traceable_cursor()
    {
        using var temp = new T11TempDirectory();
        var clock = new T11FakeClock(Now);
        var files = new FakeStorageFileSystem { LastKnownNow = clock.UtcNow };
        var sessionPath = temp.File("session.ndjson");
        var dbPath = temp.File("session.db");
        var first = T11Data.Event(1, EventStatus.Ok);
        var second = T11Data.Event(2, EventStatus.Failed);
        var third = T11Data.Event(3, EventStatus.Ok);
        var text = EnvelopeJson.Serialize(first) + "\n"
            + EnvelopeJson.Serialize(second) + "\n"
            + EnvelopeJson.Serialize(first) + "\n" // duplicate event id; first occurrence wins
            + EnvelopeJson.Serialize(third) + "\n";
        File.WriteAllText(sessionPath, text);

        // Read/Inspect must see the real file, so this rebuild test uses the real file system seam.
        var real = new SessionRecovery(clock: clock);
        var report = real.RebuildIndex(sessionPath, dbPath, "test_rebuild");

        Assert.True(report.Succeeded, report.Summary);
        Assert.Equal(3, report.IndexedEvents);
        Assert.Equal(1, report.SkippedDuplicates);
        Assert.Equal(0, report.InvalidLines);
        Assert.Equal(3, report.LastSequence);
        Assert.True(report.CursorWritten);
        Assert.True(report.StateMarkerWritten);
        Assert.True(File.Exists(dbPath));
        var cursor = JsonNode.Parse(File.ReadAllText(StorageLayout.IndexCursorPathFor(dbPath)))!.AsObject();
        Assert.Equal("ready", cursor["status"]!.GetValue<string>());
        Assert.Equal("test_rebuild", cursor["reason"]!.GetValue<string>());
        Assert.Equal(3, cursor["indexed_rows"]!.GetValue<int>());
        Assert.Equal(1, cursor["duplicate_events_ignored"]!.GetValue<int>());
        Assert.Equal(3, cursor["last_sequence"]!.GetValue<int>());
        var state = real.ReadIndexState(dbPath);
        Assert.Equal("ready", state.Status);
        Assert.Equal(3, state.EventCount);
        Assert.False(state.NeedsRebuild);

        var again = real.RebuildIndex(sessionPath, dbPath, "test_rebuild_again");
        Assert.True(again.Succeeded);
        Assert.Equal(3, again.IndexedEvents); // idempotent, no duplicated rows
        using var index = SqliteEventIndex.Open(dbPath);
        Assert.Equal(3, index.Count());
    }

    [Fact]
    public void Index_rebuild_refuses_invalid_encoding_without_writing_a_projection()
    {
        using var temp = new T11TempDirectory();
        var clock = new T11FakeClock(Now);
        var sessionPath = temp.File("invalid.ndjson");
        var dbPath = temp.File("invalid.db");
        File.WriteAllBytes(sessionPath, new byte[] { 0xFF, 0xFE, 0x00, 0x61 });

        var report = new SessionRecovery(clock: clock).RebuildIndex(sessionPath, dbPath, "encoding_test");

        Assert.False(report.Succeeded);
        Assert.False(File.Exists(dbPath));
        Assert.NotEmpty(report.NotPersisted);
        Assert.Contains("not valid UTF-8", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_crash_at_the_index_commit_point_leaves_a_rebuilding_state_that_needs_rebuild()
    {
        using var temp = new T11TempDirectory();
        var clock = new T11FakeClock(Now);
        var sessionPath = temp.File("session.ndjson");
        var dbPath = temp.File("session.db");
        File.WriteAllText(sessionPath, T11Data.LogText(new[] { T11Data.Event(1, EventStatus.Ok) }));
        File.WriteAllText(StorageLayout.IndexStatePathFor(dbPath),
            "{\"status\":\"rebuilding\",\"reason\":\"simulated_crash\"}");

        var recovery = new SessionRecovery(clock: clock);
        var state = recovery.ReadIndexState(dbPath);
        Assert.Equal("rebuilding", state.Status);
        Assert.True(state.NeedsRebuild);

        var report = recovery.RebuildIndex(sessionPath, dbPath, "recover_after_crash");
        Assert.True(report.Succeeded);
        Assert.False(recovery.ReadIndexState(dbPath).NeedsRebuild);
    }

    [Fact]
    public void Session_persistence_status_names_the_unpersisted_ranges()
    {
        var (layout, files, clock) = Create();
        files.UnwritableDirectories.Add(Path.GetFullPath(layout.SessionsDirectory));

        var status = new SessionRecovery(files, clock)
            .CheckPersistence(layout.SessionPath("write-failed"), attemptedFirstSequence: 10,
                attemptedLastSequence: 12);

        Assert.False(status.SessionWritable);
        Assert.False(status.CanRetainFailureCounts);
        Assert.False(status.IsLossless);
        var sessionScope = Assert.Single(status.Unpersisted,
            scope => scope.Scope.Contains("session events", StringComparison.Ordinal));
        Assert.Equal(10, sessionScope.FirstSequence);
        Assert.Equal(12, sessionScope.LastSequence);
        Assert.False(sessionScope.FailureCountsRetained);
        Assert.Contains(status.Unpersisted, scope =>
            scope.Scope.Contains("failure counts", StringComparison.Ordinal) && !scope.FailureCountsRetained);
        Assert.Contains("NOT persisted", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disk_full_append_failure_keeps_existing_events_readable()
    {
        using var temp = new T11TempDirectory();
        var path = temp.File("disk-full.ndjson");
        File.WriteAllText(path, T11Data.LogText(new[] { T11Data.Event(1, EventStatus.Ok) }));

        using (var log = new NdjsonEventLog(path, new FailingEventLogFileAccess()))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                log.AppendAsync(T11Data.Event(2, EventStatus.Failed), CancellationToken.None));
        }

        var replay = new ReplayService().Replay(path);
        Assert.Single(replay.Log.Events);
        Assert.True(replay.Log.IsClean);
    }

    [Fact]
    public void Clock_rollback_is_marked_uncertain_and_the_gap_event_keeps_a_non_negative_monotonic_value()
    {
        var (layout, files, _) = Create();
        var path = layout.SessionPath("clock-rollback");
        var events = new[]
        {
            T11Data.Event(1, EventStatus.Ok, observedAt: T11Data.Origin.AddMinutes(10)),
            T11Data.Event(2, EventStatus.Timeout, observedAt: T11Data.Origin.AddMinutes(11)),
        };
        files.AddText(path, T11Data.TruncatedLogText(events), T11Data.Origin.AddMinutes(11));
        // The wall clock is before the recorded events, as after a manual rollback.
        var clock = new T11FakeClock(T11Data.Origin);

        var recovery = new SessionRecovery(files, clock);
        var inspection = recovery.Inspect(path);
        Assert.True(inspection.ClockRollbackSuspected);
        Assert.Contains("uncertain", inspection.Summary, StringComparison.OrdinalIgnoreCase);

        var result = recovery.QuarantineTruncatedTail(path);
        Assert.True(result.GapEventAppended);
        var gap = recovery.Read(path).Events[^1];
        Assert.Equal(2000d, gap.MonotonicMs);
        Assert.True(gap.MonotonicMs >= 0);
        Assert.True(gap.Payload["clock_rollback_suspected"]!.GetValue<bool>());
        Assert.Equal(T11Data.Origin, gap.ObservedAt); // recorded as observed; never used to compute a negative duration
    }

    [Fact]
    public async Task Append_failure_can_be_recorded_as_an_explicit_gap_once_storage_is_writable()
    {
        using var temp = new T11TempDirectory();
        var clock = new T11FakeClock(Now);
        var path = temp.File("append-failure.ndjson");
        File.WriteAllText(path, T11Data.LogText(new[] { T11Data.Failure(1, EventStatus.Timeout) }));

        using (var log = new NdjsonEventLog(path, new FailingEventLogFileAccess()))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                log.AppendAsync(T11Data.Event(2, EventStatus.Ok), CancellationToken.None));
        }

        var recovery = new SessionRecovery(clock: clock);
        var gap = recovery.RecordGap(path, firstAttemptedSequence: 2, lastAttemptedSequence: 2,
            reason: "disk full during append");

        Assert.True(gap.Recorded, gap.Summary);
        Assert.Equal(2, gap.Sequence);
        Assert.Empty(gap.NotPersisted);
        var replay = new ReplayService().Replay(path);
        Assert.True(replay.Log.IsClean);
        Assert.Equal(2, replay.Log.Events.Count);
        Assert.Equal(EventType.Gap, replay.Log.Events[^1].EventType);
        Assert.Equal("append_failure", replay.Log.Events[^1].Payload["kind"]!.GetValue<string>());
        Assert.Equal(1, replay.Log.Events[^1].Payload["attempted_count"]!.GetValue<int>());
        Assert.Equal(1, replay.Summary.ByType[EventType.Gap]);
        Assert.True(replay.Summary.ObservationGapCount >= 2); // the original timeout stays counted
        Assert.True(replay.Log.Events[^1].MonotonicMs >= 0);
    }

    [Fact]
    public void Record_gap_reports_not_persisted_when_storage_is_still_unwritable()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("still-broken");
        files.AddText(path, T11Data.LogText(new[] { T11Data.Event(1, EventStatus.Ok) }), clock.UtcNow.AddMinutes(-1));
        files.FailAllWrites = true;

        var result = new SessionRecovery(files, clock)
            .RecordGap(path, 2, 2, "disk still full");

        Assert.False(result.Recorded);
        Assert.Null(result.Sequence);
        Assert.NotEmpty(result.NotPersisted);
        Assert.Contains("cannot be written", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static (StorageLayout Layout, FakeStorageFileSystem Files, T11FakeClock Clock) Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "netscope-t11-recovery-" + Guid.NewGuid().ToString("N"));
        var clock = new T11FakeClock(Now);
        var files = new FakeStorageFileSystem { LastKnownNow = clock.UtcNow };
        var layout = new StorageLayout(root);
        layout.EnsureCreated(files);
        return (layout, files, clock);
    }
}
