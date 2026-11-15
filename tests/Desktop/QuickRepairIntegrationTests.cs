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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}