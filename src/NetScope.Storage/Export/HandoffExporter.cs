using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;

namespace NetScope.Storage.Export;

public sealed record HandoffResult(string PackagePath, string Prompt);

/// <summary>
/// Builds the AI handoff package: redacted failure case + environment context + developer guide.
/// Reuses the incident anonymizer; the per-package salt never leaves memory.
/// </summary>
public sealed class HandoffExporter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<HandoffResult> ExportAsync(string ndjsonPath, string destinationDirectory,
        string? batchId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ndjsonPath)) throw new ArgumentException("日志路径不能为空。", nameof(ndjsonPath));
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("输出目录不能为空。", nameof(destinationDirectory));
        cancellationToken.ThrowIfCancellationRequested();

        using var log = new NdjsonEventLog(ndjsonPath);
        var repairEvents = log.Read().Events.Where(e => e.EventType == EventType.RepairEvent).ToArray();
        if (repairEvents.Length == 0) throw new IOException("日志里没有修复事件，无法生成交接包。");

        var chosenBatch = batchId
            ?? (repairEvents.LastOrDefault(e => e.Status != EventStatus.Ok) ?? repairEvents[^1]).Refs.RepairPlanId;
        var caseEvents = repairEvents.Where(e => e.Refs.RepairPlanId == chosenBatch).ToArray();
        if (caseEvents.Length == 0) throw new IOException("找不到修复批次 " + chosenBatch + "。");

        var salt = RandomNumberGenerator.GetBytes(32);
        var ledger = new RedactionLedger();
        var anonymizer = new PackageAnonymizer(ledger, salt);

        var caseJson = new JsonObject
        {
            ["batch_id"] = chosenBatch,
            ["events"] = new JsonArray(caseEvents.Select(e => (JsonNode)new JsonObject
            {
                ["observed_at"] = e.ObservedAt.ToString("O"),
                ["status"] = e.Status.ToString(),
                ["payload"] = e.Payload.DeepClone(),
            }).ToArray()),
        };
        anonymizer.Precollect(caseJson);
        var redactedCase = anonymizer.RedactObject(caseJson);

        var contextJson = new JsonObject
        {
            ["generated_at"] = DateTimeOffset.Now.ToString("O"),
            ["os"] = Environment.OSVersion.VersionString,
            ["app_version"] = typeof(HandoffExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["batch_id"] = chosenBatch,
            ["event_count"] = caseEvents.Length,
        };
        anonymizer.Precollect(contextJson);
        var redactedContext = anonymizer.RedactObject(contextJson);

        var manifest = new JsonObject
        {
            ["format"] = "netscope-handoff-package",
            ["format_version"] = "1",
            ["batch_id"] = chosenBatch,
            ["created_at"] = DateTimeOffset.Now.ToString("O"),
            ["redactions"] = JsonSerializer.SerializeToNode(ledger.Snapshot()),
        };

        var entries = new (string Name, byte[] Bytes)[]
        {
            ("case/case.json", Encoding.UTF8.GetBytes(redactedCase.ToJsonString(Indented))),
            ("context/context.json", Encoding.UTF8.GetBytes(redactedContext.ToJsonString(Indented))),
            ("developer/guide.md", Encoding.UTF8.GetBytes(HandoffGuide.Text)),
            ("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(Indented))),
        };

        Directory.CreateDirectory(destinationDirectory);
        var name = "handoff-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-"
            + (chosenBatch ?? "unknown")[..Math.Min(8, (chosenBatch ?? "unknown").Length)] + ".zip";
        var packagePath = Path.Combine(destinationDirectory, name);
        var temporary = packagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, CreateZip(entries), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, packagePath, overwrite: false);
        return new HandoffResult(packagePath, HandoffPrompt.Build(packagePath));
    }

    private static byte[] CreateZip((string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = entry.Open();
                writer.Write(bytes, 0, bytes.Length);
            }
        }
        return stream.ToArray();
    }
}

/// <summary>The developer context that teaches an AI assistant how to add a new repair capability.</summary>
internal static class HandoffGuide
{
    public const string Text = """
# NetScopeRepair 开发者指南（给 AI 助手）

你收到的是一个真实修复失败案例。目标：诊断根因，给仓库新增或修正修复能力，让下一次同类失败被修好。

## 仓库关键位置
- src/NetScope.Maintenance/MaintenancePlanner.cs —— 纯逻辑规则：快照进，finding + typed action 出（无 I/O）
- src/NetScope.Maintenance/WindowsMaintenanceExecutor.cs —— 唯一写系统的类：固定命令 + journal 回滚
- src/NetScope.Maintenance/WindowsMaintenanceProbe.cs —— 只读体检采集
- src/NetScope.Maintenance/PostCheck.cs —— 修后自检与 A~E 原因分类
- tests/Maintenance/ —— 行为测试（fake probe/executor/runner，不碰真机）

## 加一个新修复动作的四步
1. planner：加 finding code 与 RepairAction（Kind 唯一、Parameters 带原值、Risk 分级、可逆才默认选中）
2. executor：ApplyAsync switch 加 Kind 分支（固定命令，字符串经 PsQuote）；RestoreAsync 加对应回滚
3. journal：QuickRepairService.ToJournalEntry 加 key/extra 映射，保证 repair --restore 精确还原
4. 测试：planner 纯逻辑测试 + executor FakeCommandRunner 测试 + journal 往返测试

## 硬约束
- 零外部脚本依赖：全部 C#；不携带/调用 .ps1 文件
- 只有 status=ok 算成功；读不到就说读不到；不假装成功
- 不自动 UAC；需管理员的动作非管理员如实跳过
- TUN 动作只动 TUN 网卡，绝不碰物理网卡；所有写入必须可回滚
- 事件契约不变：复用 repair_event，不改 contracts schema

## 构建与测试
- dotnet build NetScopeRepair.slnx   （要求 0 警告 0 错误）
- dotnet test NetScopeRepair.slnx    （要求全绿）
- 实机验证：netscope status / netscope repair --apply --yes / netscope repair --restore

## 案例文件
- case/case.json：失败批次全量事件（含修前/修后快照、A~E 原因、错误消息；已脱敏，别名跨文件稳定）
- context/context.json：OS、app 版本、批次信息
""";
}

/// <summary>Clipboard prompt: paste into Qoder (or any AI assistant) with the repo open.</summary>
public static class HandoffPrompt
{
    public static string Build(string packagePath) =>
        "我在用 NetScopeRepair 修复网络时失败了。失败案例交接包：" + packagePath +
        "。请在 NetScopeRepair 仓库中：1) 解压并阅读 case/case.json 与 context/context.json；" +
        "2) 按 developer/guide.md 诊断根因；3) 按四步指南新增或修正修复能力（planner→executor→journal→单测）；" +
        "4) dotnet build 0 警告、dotnet test 全绿后，给我实机验收步骤。全部 C# 原生实现，不调用外部脚本。";
}