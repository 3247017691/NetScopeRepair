using System.IO.Compression;
using System.Text.Json;
using NetScope.Domain;
using NetScope.Maintenance;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

public sealed class HandoffExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netscope-handoff-test-" + Guid.NewGuid().ToString("N"));

    public HandoffExporterTests() => Directory.CreateDirectory(_dir);

    private string WriteLog(params RepairEvent[] events)
    {
        var path = Path.Combine(_dir, "session.ndjson");
        using var log = new NdjsonEventLog(path);
        var envelopes = RepairSessionEvents.Create(events, "session-t", "boot-t", false, 1.0);
        foreach (var envelope in envelopes) log.AppendAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
        return path;
    }

    private static RepairEvent Failed(string batch, string error) => new(
        batch, "postcheck_failed", DateTimeOffset.Now, null, null, "修后自检未通过（原因 D）",
        error, false, null, null, null);

    [Fact]
    public async Task Export_produces_a_zip_with_case_context_guide_and_manifest()
    {
        var logPath = WriteLog(Failed("batch-aaa", @"C:\Users\测试用户\AppData\config.yaml 读取失败"));
        var exporter = new HandoffExporter();

        var result = await exporter.ExportAsync(logPath, _dir, null, CancellationToken.None);

        Assert.True(File.Exists(result.PackagePath));
        Assert.Contains(result.PackagePath, result.Prompt);
        using var zip = ZipFile.OpenRead(result.PackagePath);
        Assert.NotNull(zip.GetEntry("case/case.json"));
        Assert.NotNull(zip.GetEntry("context/context.json"));
        Assert.NotNull(zip.GetEntry("developer/guide.md"));
        Assert.NotNull(zip.GetEntry("manifest.json"));
        using var reader = new StreamReader(zip.GetEntry("case/case.json")!.Open());
        var caseText = reader.ReadToEnd();
        Assert.DoesNotContain("测试用户", caseText);
        Assert.Contains("batch-aaa", caseText);
    }

    [Fact]
    public async Task Latest_failed_batch_is_chosen_when_no_id_given()
    {
        var logPath = WriteLog(
            Failed("batch-old", "err1"),
            new RepairEvent("batch-new", "repair_applied", DateTimeOffset.Now, null, null, "应用 1 项", null, false, null, null, null),
            Failed("batch-new", "err2"));
        var exporter = new HandoffExporter();

        var result = await exporter.ExportAsync(logPath, _dir, null, CancellationToken.None);

        using var zip = ZipFile.OpenRead(result.PackagePath);
        using var reader = new StreamReader(zip.GetEntry("case/case.json")!.Open());
        Assert.Contains("batch-new", reader.ReadToEnd());
    }

    [Fact]
    public async Task Explicit_batch_id_selects_that_batch()
    {
        var logPath = WriteLog(Failed("batch-old", "err1"), Failed("batch-new", "err2"));
        var exporter = new HandoffExporter();

        var result = await exporter.ExportAsync(logPath, _dir, "batch-old", CancellationToken.None);

        using var zip = ZipFile.OpenRead(result.PackagePath);
        using var reader = new StreamReader(zip.GetEntry("case/case.json")!.Open());
        var text = reader.ReadToEnd();
        Assert.Contains("batch-old", text);
        Assert.DoesNotContain("batch-new", text);
    }

    [Fact]
    public async Task No_repair_events_is_an_honest_error()
    {
        var path = Path.Combine(_dir, "empty.ndjson");
        File.WriteAllText(path, "");
        var exporter = new HandoffExporter();

        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(path, _dir, null, CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}