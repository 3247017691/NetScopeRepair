using NetScope.Desktop.ViewModels;
using NetScope.Maintenance;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Desktop.Tests;

public sealed class HandoffFeedbackTests
{
    [Fact]
    public void Handoff_without_session_reports_no_log()
    {
        var vm = new QuickRepairViewModel(sessionPath: () => null!);
        vm.HandoffCommand.Execute(null);
        Assert.Contains("没有会话日志", vm.StatusMessage);
    }

    [Fact]
    public async Task Handoff_sets_busy_and_status_while_running()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netscope-hf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.ndjson");
        using (var log = new NdjsonEventLog(path))
        {
            var ev = new RepairEvent("b", "postcheck_failed", DateTimeOffset.Now, null, null, "x", "err", false, null, null, null);
            foreach (var env in RepairSessionEvents.Create(new[] { ev }, "s", "b", false, 1.0))
                await log.AppendAsync(env, CancellationToken.None);
        }
        var vm = new QuickRepairViewModel(exporter: new HandoffExporter(),
            sessionPath: () => path, copyToClipboard: _ => { });
        vm.HandoffCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.Contains("交接包", vm.StatusMessage);
        for (var i = 0; i < 100 && vm.IsBusy; i++) await Task.Delay(50);
        Assert.False(vm.IsBusy);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
