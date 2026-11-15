using NetScope.Maintenance;
using NetScope.Storage;
using NetScope.Storage.Export;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class QuickRepairIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netscope-vm-test-" + Guid.NewGuid().ToString("N"));

    public QuickRepairIntegrationTests() => Directory.CreateDirectory(_dir);

    private string WriteSessionLog()
    {
        var path = Path.Combine(_dir, "session.ndjson");
        using var log = new NdjsonEventLog(path);
        var events = new[]
        {
            new RepairEvent("batch-x", "postcheck_failed", DateTimeOffset.Now, null, null,
                "修后自检未通过（原因 D）", "err", false, null, null, null),
        };
        foreach (var envelope in RepairSessionEvents.Create(events, "s", "b", false, 1.0))
            log.AppendAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
        return path;
    }

    [Fact]
    public async Task Handoff_command_exports_package_and_copies_prompt()
    {
        var sessionPath = WriteSessionLog();
        string? copied = null;
        var vm = new QuickRepairViewModel(exporter: new HandoffExporter(),
            sessionPath: () => sessionPath, copyToClipboard: text => copied = text);

        vm.HandoffCommand.Execute(null);
        for (var i = 0; i < 50 && copied is null; i++) await Task.Delay(100);

        Assert.NotNull(copied);
        Assert.Contains("handoff-", copied);
        Assert.Contains("交接包已生成", vm.StatusMessage);
    }

    [Fact]
    public void Mark_unresolved_without_batch_says_so()
    {
        var vm = new QuickRepairViewModel();

        vm.MarkUnresolvedCommand.Execute(null);

        Assert.Contains("没有可标记的修复批次", vm.StatusMessage);
    }

    [Fact]
    public void Diagnosis_map_covers_dead_proxy_and_stale_config_only()
    {
        Assert.Contains("ClearProxyEnv", DiagnosisRepairMap.ActionKindsFor("DX-001"));
        Assert.Contains("RaiseInterfaceMetric", DiagnosisRepairMap.ActionKindsFor("DX-006"));
        Assert.Empty(DiagnosisRepairMap.ActionKindsFor("DX-005"));
        Assert.Empty(DiagnosisRepairMap.ActionKindsFor("DX-999"));
    }

    [Fact]
    public async Task Preselect_for_dx001_selects_only_dead_proxy_actions()
    {
        var service = new QuickRepairService(new StaticProbe(DeadProxySnapshot()), new NoopExecutor(),
            new MaintenanceJournalStore(Path.Combine(_dir, "journal.json")),
            new GreenPostCheck());
        var vm = new QuickRepairViewModel(service);

        await vm.PreselectForDiagnosisAsync("DX-001");

        Assert.Contains(vm.Actions, a => a.Kind == "ClearProxyEnv");
        Assert.Contains(vm.Actions, a => a.Kind == "DisableSystemProxy");
        Assert.All(vm.Actions.Where(a => a.Kind is "ClearProxyEnv" or "DisableSystemProxy"),
            a => Assert.True(a.IsSelected));
        Assert.All(vm.Actions.Where(a => a.Kind is not ("ClearProxyEnv" or "DisableSystemProxy")),
            a => Assert.False(a.IsSelected));
    }

    [Fact]
    public async Task Preselect_while_busy_returns_without_waiting_for_the_inflight_refresh()
    {
        var gate = new TaskCompletionSource<MaintenanceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new QuickRepairService(new GatedProbe(gate.Task), new NoopExecutor(),
            new MaintenanceJournalStore(Path.Combine(_dir, "journal.json")),
            new GreenPostCheck());
        var vm = new QuickRepairViewModel(service);

        vm.RefreshCommand.Execute(null);
        Assert.True(vm.IsBusy);

        var preselect = vm.PreselectForDiagnosisAsync("DX-001");
        var finished = await Task.WhenAny(preselect, Task.Delay(2000));

        Assert.Same(preselect, finished);
        Assert.Empty(vm.Actions);

        gate.SetResult(DeadProxySnapshot());
        for (var i = 0; i < 100 && vm.IsBusy; i++) await Task.Delay(50);
        Assert.False(vm.IsBusy);
    }

    private static MaintenanceSnapshot DeadProxySnapshot() => new(
        DateTimeOffset.Now, true, 21, "WLAN", null, Array.Empty<int>(),
        new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        true, "127.0.0.1:7890", 7890, false, 0,
        Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class StaticProbe(MaintenanceSnapshot snapshot) : IMaintenanceProbe
    {
        public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class GatedProbe(Task<MaintenanceSnapshot> gate) : IMaintenanceProbe
    {
        public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => gate;
    }

    private sealed class NoopExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken ct)
            => Task.FromResult(new ActionResult(action.Id, action.Title, true, action.Current, action.Desired, null));
        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken ct)
            => Task.FromResult((true, "ok"));
        public Task BroadcastEnvironmentAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class GreenPostCheck : IPostCheckProbe
    {
        public Task<PostCheckObservation> ObserveAsync(CancellationToken ct)
            => Task.FromResult(new PostCheckObservation(21, true, true, true, true, false));
    }
}
