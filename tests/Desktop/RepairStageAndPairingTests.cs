using NetScope.Maintenance;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class RepairStageAndPairingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netscope-stage-" + Guid.NewGuid().ToString("N"));
    public RepairStageAndPairingTests() => Directory.CreateDirectory(_dir);

    private QuickRepairService Service(MaintenanceSnapshot snap, IMaintenanceExecutor? executor = null, IPostCheckProbe? postCheck = null) => new(
        new StaticProbe(snap), executor ?? new NoopExecutor(),
        new MaintenanceJournalStore(Path.Combine(_dir, "journal.json")), postCheck ?? new GreenPostCheck());

    private static MaintenanceSnapshot DeadProxy() => new(
        DateTimeOffset.Now, true, 21, "WLAN", null, Array.Empty<int>(),
        new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        true, "127.0.0.1:7890", 7890, false, 0,
        Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());

    [Fact]
    public void Stage_is_inspect_before_plan()
    {
        var vm = new QuickRepairViewModel();
        Assert.Equal(RepairStage.Inspect, vm.Stage);
        Assert.Equal(NetworkPathModel.Unmeasured.Proxy.State, vm.NetworkPath.Proxy.State);
    }

    [Fact]
    public async Task Stage_is_review_after_refresh_and_path_populated()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync(null);
        Assert.Equal(RepairStage.Review, vm.Stage);
        Assert.Equal(PathStationState.Fault, vm.NetworkPath.Proxy.State);
        Assert.NotEmpty(vm.VerdictText);
    }

    [Fact]
    public async Task Repair_items_pair_dead_proxy_finding_with_actions()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        Assert.NotEmpty(vm.RepairItems);
        Assert.Contains(vm.RepairItems, i => i.Action.Kind == "ClearProxyEnv");
        var paired = vm.RepairItems.First(i => i.Action.Kind == "ClearProxyEnv");
        Assert.True(paired.HasFinding);
        Assert.False(string.IsNullOrEmpty(paired.ProblemText));
    }

    [Fact]
    public async Task Action_row_exposes_before_after()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync(null);
        Assert.All(vm.Actions, a => Assert.NotNull(a.Title));
        // Current/Desired 来自 RepairAction，可能为 null，但属性必须存在
        _ = vm.Actions[0].Current;
        _ = vm.Actions[0].Desired;
    }

    private static RepairItemRow ItemRow(string? current, string? desired,
        ActionRisk risk = ActionRisk.Safe, bool requiresElevation = false) =>
        new(null, new ActionRow(new RepairAction("id", "标题", "详情", risk, "Kind", "target",
            current, desired, requiresElevation, true, new Dictionary<string, string>())));

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("", "", "")]
    [InlineData("http://127.0.0.1:7890", null, "http://127.0.0.1:7890 → 清除")]
    [InlineData(null, "http://127.0.0.1:7897", "未设置 → http://127.0.0.1:7897")]
    [InlineData("1", "0", "1 → 0")]
    [InlineData("", "0", "未设置 → 0")]
    [InlineData(" ", null, "  → 清除")]
    public void BeforeAfterText_normalizes_degenerate_pairs(string? current, string? desired, string expected)
    {
        // "" is a real planner output (FlushDnsCache); it must collapse to "" not a bare " → ".
        // Whitespace (" ") is real data and must survive — IsNullOrEmpty, never IsNullOrWhiteSpace.
        Assert.Equal(expected, ItemRow(current, desired).BeforeAfterText);
    }

    [Fact]
    public void BeforeAfterText_normalization_is_display_only()
    {
        var row = ItemRow("", "");
        Assert.Equal("", row.Action.Current);
        Assert.Equal("", row.Action.Desired);
        Assert.Equal("", row.BeforeAfterText);
    }

    [Fact]
    public void Risk_badge_marks_elevation()
    {
        var row = ItemRow("1", "0", ActionRisk.Elevated, requiresElevation: true);
        Assert.Equal("Elevated · 需管理员", row.RiskBadge);
        Assert.Equal(row.Action.Badge, row.RiskBadge);
    }

    [Fact]
    public async Task Repair_item_values_pin_finding_and_action()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        var paired = vm.RepairItems.First(i => i.Action.Kind == "ClearProxyEnv");
        Assert.True(paired.HasFinding);
        Assert.Equal("DEAD_PROXY", paired.CodeRef);
        Assert.Equal(paired.Finding!.Title, paired.ProblemText);
        Assert.Equal(paired.Action.Title, paired.FixText);
        Assert.Equal(paired.Action.Badge, paired.RiskBadge);
    }

    [Fact]
    public async Task Flush_dns_cache_item_has_no_finding_and_empty_before_after()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync(null);
        var flush = vm.RepairItems.First(i => i.Action.Kind == "FlushDnsCache");
        Assert.False(flush.HasFinding);
        Assert.Equal("", flush.CodeRef);
        Assert.Equal("刷新 DNS / 邻居缓存", flush.Action.Title);
        Assert.Equal(flush.Action.Title, flush.ProblemText);
        Assert.Equal("", flush.BeforeAfterText);
    }

    [Fact]
    public async Task Repair_item_row_notifies_when_action_selection_changes_directly()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        var item = vm.RepairItems.First(i => i.Action.Kind == "ClearProxyEnv");
        var notifications = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionRow.IsSelected)) notifications++;
        };

        // PreselectForDiagnosisAsync sets ActionRow.IsSelected directly; the paired row must still notify.
        var before = item.Action.IsSelected;
        item.Action.IsSelected = !before;
        Assert.Equal(1, notifications);
        Assert.Equal(!before, item.IsSelected);

        // Setting through the row is pure delegation: the subscription notifies exactly once, no double-raise.
        item.IsSelected = before;
        Assert.Equal(2, notifications);
        Assert.Equal(item.Action.IsSelected, item.IsSelected);
    }

    [Fact]
    public async Task Apply_shows_fresh_postcheck_but_restore_invalidates_it()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");

        await ApplySelectedAsync(vm);
        Assert.Equal(PathStationState.Ok, vm.NetworkPath.Internet.State);
        Assert.Equal("可达", vm.NetworkPath.Internet.ValueText);

        vm.RestoreCommand.Execute(null);
        Assert.Contains("再次点击确认回滚", vm.RestoreButtonText);
        vm.RestoreCommand.Execute(null);
        await WaitIdleAsync(vm, (AsyncRelayCommand)vm.RestoreCommand);

        // Rollback restored the broken pre-repair state; the apply-time measurement must not imply health.
        Assert.Equal(PathStationState.Unknown, vm.NetworkPath.Internet.State);
        Assert.Equal("—", vm.NetworkPath.Internet.ValueText);
    }

    [Fact]
    public async Task Manual_refresh_after_apply_invalidates_postcheck()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        await ApplySelectedAsync(vm);
        Assert.Equal(PathStationState.Ok, vm.NetworkPath.Internet.State);

        vm.RefreshCommand.Execute(null);
        await WaitIdleAsync(vm, (AsyncRelayCommand)vm.RefreshCommand);

        Assert.Equal(PathStationState.Unknown, vm.NetworkPath.Internet.State);
        Assert.Equal("—", vm.NetworkPath.Internet.ValueText);
    }

    [Fact]
    public async Task Proxy_off_and_on_after_apply_invalidate_postcheck()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        await ApplySelectedAsync(vm);
        Assert.Equal(PathStationState.Ok, vm.NetworkPath.Internet.State);

        vm.ProxyOffCommand.Execute(null);
        await WaitIdleAsync(vm, (AsyncRelayCommand)vm.ProxyOffCommand);
        Assert.Equal(PathStationState.Unknown, vm.NetworkPath.Internet.State);

        // A fresh apply re-measures; proxy on (no live port in this fixture) must invalidate again.
        await ApplySelectedAsync(vm);
        Assert.Equal(PathStationState.Ok, vm.NetworkPath.Internet.State);

        vm.ProxyOnCommand.Execute(null);
        await WaitIdleAsync(vm, (AsyncRelayCommand)vm.ProxyOnCommand);
        Assert.Equal(PathStationState.Unknown, vm.NetworkPath.Internet.State);
        Assert.Equal("—", vm.NetworkPath.Internet.ValueText);
    }

    [Fact]
    public void IsUnresolved_is_false_before_any_run()
    {
        var vm = new QuickRepairViewModel();
        Assert.False(vm.IsUnresolved);
    }

    [Fact]
    public async Task Failed_apply_sets_unresolved_at_verify()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy(), new FailExecutor()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        await ApplySelectedAsync(vm);
        Assert.Equal(RepairRunState.Failed, vm.RunState);
        Assert.Equal(RepairStage.Verify, vm.Stage);
        Assert.True(vm.IsUnresolved);
    }

    [Fact]
    public async Task Postcheck_unresolved_sets_unresolved_at_verify()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy(), postCheck: new RedPostCheck()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        await ApplySelectedAsync(vm);
        Assert.Equal(RepairRunState.Failed, vm.RunState);
        Assert.Equal(RepairStage.Verify, vm.Stage);
        Assert.True(vm.IsUnresolved);
    }

    [Fact]
    public async Task Refresh_clears_unresolved()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy(), new FailExecutor()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        await ApplySelectedAsync(vm);
        Assert.True(vm.IsUnresolved);

        vm.RefreshCommand.Execute(null);
        await WaitIdleAsync(vm, (AsyncRelayCommand)vm.RefreshCommand);
        Assert.False(vm.IsUnresolved);
    }

    [Fact]
    public async Task IsUnresolved_raises_property_changed()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy(), new FailExecutor()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickRepairViewModel.IsUnresolved)) raised = true;
        };
        await ApplySelectedAsync(vm);
        Assert.True(raised);
    }

    private static async Task ApplySelectedAsync(QuickRepairViewModel vm)
    {
        vm.ApplyCommand.Execute(null);
        Assert.Contains("再次点击确认执行", vm.ApplyButtonText);
        vm.ApplyCommand.Execute(null);
        await WaitIdleAsync(vm, (AsyncRelayCommand)vm.ApplyCommand);
    }

    private static async Task WaitIdleAsync(QuickRepairViewModel vm, AsyncRelayCommand command)
    {
        for (var i = 0; i < 400 && (vm.IsBusy || command.IsRunning); i++) await Task.Delay(25);
        Assert.False(vm.IsBusy);
        Assert.False(command.IsRunning);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class StaticProbe(MaintenanceSnapshot s) : IMaintenanceProbe
    { public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(s); }
    private sealed class NoopExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction a, CancellationToken ct)
            => Task.FromResult(new ActionResult(a.Id, a.Title, true, a.Current, a.Desired, null));
        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry e, CancellationToken ct) => Task.FromResult((true, "ok"));
        public Task BroadcastEnvironmentAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class GreenPostCheck : IPostCheckProbe
    { public Task<PostCheckObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(new PostCheckObservation(21, true, true, true, true, false)); }
    private sealed class FailExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction a, CancellationToken ct)
            => Task.FromResult(new ActionResult(a.Id, a.Title, false, a.Current, a.Desired, "x"));
        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry e, CancellationToken ct) => Task.FromResult((true, "ok"));
        public Task BroadcastEnvironmentAsync(CancellationToken ct) => Task.CompletedTask;
    }
    // PhysicalInternet=false + DnsOk=false → PostCheckClassifier.Success=false → Done.Applied=false.
    private sealed class RedPostCheck : IPostCheckProbe
    { public Task<PostCheckObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(new PostCheckObservation(21, false, false, true, true, false)); }
}
