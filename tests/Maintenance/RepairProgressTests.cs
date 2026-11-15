using NetScope.Maintenance;

namespace NetScope.Maintenance.Tests;

/// <summary>
/// The live progress stream is a UI side-channel. These tests pin its shape (per-action
/// start/result, post-check attempts, a terminal Done) and prove it never alters the report.
/// </summary>
public sealed class RepairProgressTests
{
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-11-15T14:00:00+08:00");

    private static MaintenanceSnapshot Snapshot(bool elevated = true)
        => new(FixedTime, elevated, 21, "WLAN", null, Array.Empty<int>(),
            new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
            true, "127.0.0.1:7890", 7890, false, 0,
            Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());

    [Fact]
    public async Task Apply_streams_a_start_and_result_per_action_then_a_terminal_done()
    {
        var progress = new CollectProgress();
        var service = Service(Snapshot());
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None, progress);

        var starts = progress.Ticks.Where(t => t.Kind == RepairStepKind.ActionStart).ToArray();
        var results = progress.Ticks.Where(t => t.Kind == RepairStepKind.ActionResult).ToArray();
        Assert.Equal(plan.Actions.Count, starts.Length);
        Assert.Equal(plan.Actions.Count, results.Length);
        Assert.All(results, r => Assert.True(r.Applied));
        // Step advances monotonically and the scale matches the selected action count.
        Assert.Equal(plan.Actions.Count, results[0].TotalSteps);
        Assert.Equal(Enumerable.Range(1, plan.Actions.Count), results.Select(r => r.Step));
        var done = Assert.Single(progress.Ticks, t => t.Kind == RepairStepKind.Done);
        Assert.True(done.Applied);
        Assert.Contains("应用 " + report.Applied, done.Message);
        Assert.Same(progress.Ticks[^1], done);
    }

    [Fact]
    public async Task Apply_streams_postcheck_attempts_and_a_green_verdict()
    {
        var progress = new CollectProgress();
        var service = Service(Snapshot());
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, false, CancellationToken.None, progress);

        Assert.Contains(progress.Ticks, t => t.Kind == RepairStepKind.PostCheck && t.Message.Contains("第 1/3 次"));
        Assert.Contains(progress.Ticks, t => t.Kind == RepairStepKind.PostCheck && t.Applied == true);
    }

    [Fact]
    public async Task Plan_streams_a_phase_then_a_done_carrying_the_finding_count()
    {
        var progress = new CollectProgress();
        var service = Service(Snapshot());

        var plan = await service.PlanAsync(false, CancellationToken.None, progress);

        Assert.Equal(RepairStepKind.Phase, progress.Ticks[0].Kind);
        var done = Assert.Single(progress.Ticks, t => t.Kind == RepairStepKind.Done);
        Assert.Contains(plan.Findings.Count + " 项问题", done.Message);
    }

    [Fact]
    public async Task Elevation_skip_is_streamed_as_a_failed_result_not_a_success()
    {
        var progress = new CollectProgress();
        var snapshot = Snapshot(elevated: false) with
        {
            TunInterfaces = new[] { new TunInterfaceSnapshot(7, "Mihomo", 4225, "Enabled", "Disabled", 9000, new[] { "198.18.0.1" }) },
        };
        var service = Service(snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, false, CancellationToken.None, progress);

        var skip = Assert.Single(progress.Ticks,
            t => t.Kind == RepairStepKind.ActionResult && t.ActionId == "fix-tun-priority:7");
        Assert.False(skip.Applied);
        Assert.Contains("管理员", skip.Detail);
    }

    [Fact]
    public async Task Restore_streams_a_result_per_entry_then_a_terminal_done()
    {
        var progress = new CollectProgress();
        using var temp = new TempJournal();
        var service = Service(Snapshot(), temp.Store);
        var plan = await service.PlanAsync(false, CancellationToken.None);
        await service.ApplyAsync(plan, null, false, CancellationToken.None);
        var journaled = temp.Store.Load()!.Entries.Count;

        var restore = await service.RestoreAsync(false, CancellationToken.None, progress);

        var results = progress.Ticks.Where(t => t.Kind == RepairStepKind.ActionResult).ToArray();
        Assert.Equal(journaled, results.Length);
        Assert.Equal(journaled, restore.Restored);
        var done = Assert.Single(progress.Ticks, t => t.Kind == RepairStepKind.Done);
        Assert.True(done.Applied);
    }

    [Fact]
    public async Task Progress_is_a_side_channel_and_never_changes_the_report()
    {
        var withProgress = Service(Snapshot());
        var withoutProgress = Service(Snapshot());
        var planA = await withProgress.PlanAsync(false, CancellationToken.None);
        var planB = await withoutProgress.PlanAsync(false, CancellationToken.None);

        var a = await withProgress.ApplyAsync(planA, null, false, CancellationToken.None, new CollectProgress());
        var b = await withoutProgress.ApplyAsync(planB, null, false, CancellationToken.None);

        Assert.Equal(b.Applied, a.Applied);
        Assert.Equal(b.Skipped, a.Skipped);
        Assert.Equal(b.Failed, a.Failed);
        Assert.Equal(b.Results.Count, a.Results.Count);
    }

    private static QuickRepairService Service(MaintenanceSnapshot snapshot, MaintenanceJournalStore? store = null)
        => new(new FakeProbe(snapshot), new FakeExecutor(), store ?? new MaintenanceJournalStore(TempJournal.NewPath()),
            postCheck: new FakePostCheck(new PostCheckObservation(21, true, true, true, true, false)));

    private sealed class CollectProgress : IProgress<RepairProgress>
    {
        public List<RepairProgress> Ticks { get; } = new();
        public void Report(RepairProgress value) => Ticks.Add(value);
    }

    private sealed class FakeProbe(MaintenanceSnapshot snapshot) : IMaintenanceProbe
    {
        public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class FakePostCheck(params PostCheckObservation[] observations) : IPostCheckProbe
    {
        private readonly Queue<PostCheckObservation> _queue = new(observations);
        public Task<PostCheckObservation> ObserveAsync(CancellationToken ct)
            => Task.FromResult(_queue.Count > 1 ? _queue.Dequeue() : _queue.Peek());
    }

    private sealed class FakeExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken cancellationToken)
            => Task.FromResult(new ActionResult(action.Id, action.Title, true, action.Current, action.Desired, null));

        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken cancellationToken)
            => Task.FromResult((true, "已还原 " + entry.Name));

        public Task BroadcastEnvironmentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TempJournal : IDisposable
    {
        public TempJournal() => Store = new MaintenanceJournalStore(NewPath());
        public MaintenanceJournalStore Store { get; }
        public static string NewPath() => Path.Combine(Path.GetTempPath(),
            "netscope-progress-test-" + Guid.NewGuid().ToString("N") + ".json");
        public void Dispose() { if (File.Exists(Store.Path)) File.Delete(Store.Path); }
    }
}
