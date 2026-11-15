using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Unit.Tests;

/// <summary>
/// T11 quota behaviour with an in-memory file seam: retention order, active-incident sealing,
/// separate pinned pool, protected journals/sidecars/exports, disabled-tier degradation and
/// explicit unpersisted scopes. No real disk is filled and no system clock is changed.
/// </summary>
public sealed class T11QuotaManagerTests
{
    private static readonly DateTimeOffset Now = T11Data.Origin.AddDays(30);

    [Fact]
    public void Expired_success_detail_is_removed_before_expired_failure_evidence_and_counts_survive()
    {
        var (layout, files, clock) = Create();
        AddSession(files, layout, "success-session", clock.UtcNow.AddDays(-10),
            T11Data.Event(1, EventStatus.Ok), T11Data.Event(2, EventStatus.Ok));
        AddSession(files, layout, "failure-session", clock.UtcNow.AddDays(-9),
            T11Data.Failure(1, EventStatus.Timeout), T11Data.Failure(2, EventStatus.AccessDenied));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var plan = manager.Plan();

        var deletes = plan.Actions.Where(action => action.Kind == QuotaActionKind.DeleteExpiredDetail).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.EndsWith("success-session.ndjson", deletes[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("failure-session.ndjson", deletes[1].Path, StringComparison.Ordinal);
        Assert.False(deletes[0].PreserveAggregate);
        Assert.True(deletes[1].PreserveAggregate);
        Assert.NotNull(deletes[1].OutputPath);

        var result = manager.Enforce();

        Assert.False(files.Exists(layout.SessionPath("success-session")));
        Assert.False(files.Exists(layout.SessionPath("failure-session")));
        Assert.False(files.Exists(StorageLayout.AggregatePathFor(layout.SessionPath("success-session"))));
        var aggregate = JsonNode.Parse(files.ReadText(deletes[1].OutputPath!))!.AsObject();
        Assert.Equal("failure_aggregate", aggregate["kind"]!.GetValue<string>());
        Assert.Equal(2, aggregate["failure_count"]!.GetValue<int>());
        Assert.Equal(1, aggregate["failure_counts_by_status"]!["timeout"]!.GetValue<int>());
        Assert.Equal(1, aggregate["failure_counts_by_status"]!["access_denied"]!.GetValue<int>());
        Assert.True(aggregate["first_failure_at"] is not null);
        Assert.True(aggregate["last_failure_at"] is not null);
        Assert.False(result.Degraded, result.Summary);
    }

    [Fact]
    public void Active_incident_is_sealed_at_the_warning_level_and_never_deleted()
    {
        var (layout, files, clock) = Create();
        AddSession(files, layout, "active-session", clock.UtcNow.AddDays(-2),
            T11Data.IncidentMarker(1, "incident-active", "incident_open"),
            T11Data.Event(2, EventStatus.Partial));
        files.AddSparse(Path.Combine(layout.LogsDirectory, "fill.log"), 420L * 1024 * 1024, clock.UtcNow.AddHours(-2));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var inspection = manager.Inspect();
        Assert.Equal(QuotaLevel.Warning, inspection.Evaluation.Level);

        var result = manager.Enforce(inspection);
        var seal = Assert.Single(result.Plan.Actions, action => action.Kind == QuotaActionKind.SealActiveIncident);
        Assert.Contains(result.Results, action => action.Action == seal && action.Succeeded);
        Assert.True(files.Exists(layout.SessionPath("active-session")), "the active log must not be deleted");
        Assert.True(files.Exists(StorageLayout.SealedPathFor(layout.SessionPath("active-session"))));
        var sealedJson = JsonNode.Parse(files.ReadText(StorageLayout.SealedPathFor(layout.SessionPath("active-session"))))!.AsObject();
        Assert.Equal("sealed_incident", sealedJson["kind"]!.GetValue<string>());
        Assert.True(sealedJson["active"]!.GetValue<bool>());
        Assert.Contains(result.Plan.Actions, action =>
            !string.IsNullOrEmpty(action.Reason) && action.Reason.Contains("Active incident", StringComparison.Ordinal));
    }

    [Fact]
    public void Pinned_incidents_have_their_own_limit_and_are_never_unpinned_automatically()
    {
        var (layout, files, clock) = Create();
        files.AddSparse(Path.Combine(layout.PinnedDirectory, "inc-1.zip"), 1024L * 1024 * 1024, clock.UtcNow.AddDays(-40));
        files.AddSparse(Path.Combine(layout.LogsDirectory, "fill.log"), 420L * 1024 * 1024, clock.UtcNow.AddHours(-1));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var admission = manager.EvaluatePinRequest(2L * 1024 * 1024);
        Assert.False(admission.Accepted);
        Assert.Contains("refused", admission.Reason, StringComparison.OrdinalIgnoreCase);

        var result = manager.Enforce();
        Assert.DoesNotContain(result.Plan.Actions, action => action.Path.Contains("pinned", StringComparison.OrdinalIgnoreCase));
        Assert.True(files.Exists(Path.Combine(layout.PinnedDirectory, "inc-1.zip")));
        Assert.Contains(result.Plan.BlockedReasons, reason => reason.Contains("Pinned", StringComparison.Ordinal));

        var removal = manager.RemovePinned("inc-1");
        Assert.True(removal.Removed);
        Assert.Equal(1024L * 1024 * 1024, removal.RemovedBytes);
        Assert.False(files.Exists(Path.Combine(layout.PinnedDirectory, "inc-1.zip")));
    }

    [Fact]
    public void Pending_journal_is_excluded_from_normal_cleanup_and_from_the_normal_pool()
    {
        var (layout, files, clock) = Create();
        files.AddSparse(Path.Combine(layout.JournalDirectory, "pending.journal"), 900L * 1024 * 1024, clock.UtcNow.AddDays(-30));
        files.AddSparse(Path.Combine(layout.LogsDirectory, "big.log"), 520L * 1024 * 1024, clock.UtcNow.AddHours(-1));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var inspection = manager.Inspect();
        Assert.Equal(520L * 1024 * 1024, inspection.NormalBytes);

        var result = manager.Enforce(inspection);
        Assert.DoesNotContain(result.Plan.Actions, action => action.Path.Contains("journal", StringComparison.OrdinalIgnoreCase));
        Assert.True(files.Exists(Path.Combine(layout.JournalDirectory, "pending.journal")));
        Assert.Contains("pending repair-journal", string.Join(" ", inspection.Observations), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exported_packages_are_not_deleted_by_default_but_can_be_included_explicitly()
    {
        var (layout, files, clock) = Create();
        var exportPath = Path.Combine(layout.ExportsDirectory, "incident-old.zip");
        files.AddSparse(exportPath, 10L * 1024 * 1024, clock.UtcNow.AddDays(-10));

        var defaults = new QuotaManager(layout, files: files, clock: clock);
        var defaultResult = defaults.Enforce();
        Assert.DoesNotContain(defaultResult.Plan.Actions, action => action.Path == exportPath);
        Assert.True(files.Exists(exportPath));

        var includingExports = new QuotaManager(layout,
            new RetentionPolicy { IncludeExportsInNormalQuota = true }, files, clock);
        var includedResult = includingExports.Enforce();
        Assert.Contains(includedResult.Plan.Actions, action =>
            action.Path == exportPath && action.Kind == QuotaActionKind.DeleteExpiredDetail);
        Assert.False(files.Exists(exportPath));
    }

    [Fact]
    public void Clock_rollback_never_deletes_fresh_looking_data_and_is_reported()
    {
        var (layout, files, clock) = Create();
        AddSession(files, layout, "future-session", clock.UtcNow.AddDays(2),
            T11Data.Event(1, EventStatus.Ok));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var inspection = manager.Inspect();
        var result = manager.Enforce(inspection);

        Assert.True(inspection.ClockRollbackSuspected);
        Assert.DoesNotContain(result.Plan.Actions, action => action.Kind == QuotaActionKind.DeleteExpiredDetail);
        Assert.True(files.Exists(layout.SessionPath("future-session")));
        Assert.Contains(inspection.Evaluation.Warnings,
            warning => warning.Contains("moved backwards", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delete_failure_keeps_the_file_reports_the_action_failed_and_degrades()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("stuck-session");
        AddSession(files, layout, "stuck-session", clock.UtcNow.AddDays(-10), T11Data.Event(1, EventStatus.Ok));
        files.FailDeletePaths.Add(Path.GetFullPath(path));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var result = manager.Enforce();

        Assert.True(files.Exists(path));
        var failed = Assert.Single(result.Results, action => !action.Succeeded);
        Assert.Contains("kept", failed.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Degraded);
        Assert.Contains(result.NotPersisted, item => item.Contains("retention delete", StringComparison.Ordinal));
    }

    [Fact]
    public void Over_hard_limit_removes_additional_unpinned_detail_but_keeps_protected_items()
    {
        var (layout, files, clock) = Create();
        var bigLog = Path.Combine(layout.LogsDirectory, "huge.log");
        files.AddSparse(bigLog, 620L * 1024 * 1024, clock.UtcNow.AddMinutes(-30));
        files.AddSparse(Path.Combine(layout.JournalDirectory, "pending.journal"), 10L * 1024 * 1024, clock.UtcNow.AddDays(-10));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var result = manager.Enforce();

        Assert.False(files.Exists(bigLog));
        Assert.True(files.Exists(Path.Combine(layout.JournalDirectory, "pending.journal")));
        Assert.False(result.Degraded, result.Summary);
        Assert.True(result.After.NormalBytes < result.Plan.Inspection.Evaluation.NormalLimit);
    }

    [Fact]
    public void Protected_sidecars_are_never_removed_by_quota_cleanup()
    {
        var (layout, files, clock) = Create();
        var sidecar = StorageLayout.AggregatePathFor(layout.SessionPath("old-session"));
        files.AddText(sidecar, "{\"kind\":\"failure_aggregate\"}\n", clock.UtcNow.AddDays(-30));
        files.AddSparse(Path.Combine(layout.LogsDirectory, "fill.log"), 520L * 1024 * 1024, clock.UtcNow.AddHours(-1));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var result = manager.Enforce();

        Assert.DoesNotContain(result.Plan.Actions, action => action.Path == sidecar);
        Assert.True(files.Exists(sidecar));
    }

    [Fact]
    public void Persistence_status_names_unpersisted_scopes_instead_of_claiming_lossless_retention()
    {
        var (layout, files, clock) = Create();
        files.UnwritableDirectories.Add(Path.GetFullPath(layout.SessionsDirectory));
        var manager = new QuotaManager(layout, files: files, clock: clock);

        var status = manager.CheckPersistence();

        Assert.False(status.SessionsWritable);
        Assert.False(status.CanRetainFailureCounts);
        Assert.False(status.IsLossless);
        Assert.Contains(status.Unpersisted, scope =>
            scope.Scope.Contains("failure counts", StringComparison.Ordinal) && !scope.FailureCountsRetained);
        Assert.Contains("NOT persisted", status.Message, StringComparison.Ordinal);

        var (layout2, files2, clock2) = Create();
        files2.UnwritableDirectories.Add(Path.GetFullPath(layout2.ExportsDirectory));
        var partial = new QuotaManager(layout2, files: files2, clock: clock2).CheckPersistence();
        Assert.True(partial.SessionsWritable);
        Assert.True(partial.CanRetainFailureCounts);
        Assert.False(partial.ExportsWritable);
        Assert.Contains(partial.Unpersisted, scope =>
            scope.Scope.Contains("export", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("storage is lossless", partial.Message.ToLowerInvariant());
    }

    [Fact]
    public void Daily_budget_produces_a_warning_action_without_deleting_fresh_detail()
    {
        var (layout, files, clock) = Create();
        var path = layout.SessionPath("fresh-session");
        AddSession(files, layout, "fresh-session", clock.UtcNow.AddHours(-2), T11Data.Event(1, EventStatus.Ok));
        // 120 MB written today: above the 100 MB tuning target but far below the 500 MB pool limit.
        files.AddSparse(Path.Combine(layout.LogsDirectory, "today.log"), 120L * 1024 * 1024, clock.UtcNow.AddHours(-1));

        var manager = new QuotaManager(layout, files: files, clock: clock);
        var inspection = manager.Inspect();
        Assert.True(inspection.Evaluation.DailyBudgetExceeded);
        Assert.Equal(QuotaLevel.Normal, inspection.Evaluation.Level);

        var result = manager.Enforce(inspection);
        Assert.Contains(result.Plan.Actions, action => action.Kind == QuotaActionKind.WarnDailyBudget);
        Assert.True(files.Exists(path));
    }

    private static (StorageLayout Layout, FakeStorageFileSystem Files, T11FakeClock Clock) Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "netscope-t11-quota-" + Guid.NewGuid().ToString("N"));
        var clock = new T11FakeClock(Now);
        var files = new FakeStorageFileSystem { LastKnownNow = clock.UtcNow };
        var layout = new StorageLayout(root);
        layout.EnsureCreated(files);
        return (layout, files, clock);
    }

    private static void AddSession(FakeStorageFileSystem files, StorageLayout layout, string sessionId,
        DateTimeOffset lastWrite, params EventEnvelope[] events) =>
        files.AddText(layout.SessionPath(sessionId), T11Data.LogText(events), lastWrite);
}
