using System.Text;
using NetScope.Application;
using NetScope.Collectors.Windows;
using NetScope.Domain;
using NetScope.Diagnostics;
using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Cli;

/// <summary>
/// Command line entry point.
/// <para>
/// The M0 commands (<c>check</c>, <c>snapshot</c>, <c>demo</c>, <c>replay</c>, <c>case-replay</c>)
/// stay strictly read-only. The maintenance commands added later (<c>status</c>, <c>repair</c>,
/// <c>proxy</c>) do real work on the local machine: <c>status</c> only observes, while
/// <c>repair --apply</c> and <c>proxy on|off</c> write user-scope proxy settings, interface metrics
/// and DNS caches. Every applied change is journaled and can be reverted with
/// <c>repair --restore</c>. Nothing is written without an explicit --apply / on / off verb.
/// </para>
/// </summary>
internal static class Program
{
    private const int Success = 0;

    private const int ContractFailure = 1;

    private const int MissingInput = 2;

    private const int UnsupportedContract = 3;

    private const int BadArguments = 4;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A redirected or headless console may refuse the encoding change; output still works.
        }

        var command = args.Length == 0 ? "check" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "check" => RunCheck(rest),
                "snapshot" => await RunSnapshotAsync().ConfigureAwait(false),
                "demo" => await RunDemoAsync(rest).ConfigureAwait(false),
                "replay" => RunReplay(rest),
                "case-replay" or "casereplay" => CaseReplayCommand.Run(rest),
                "status" => await RunStatusAsync().ConfigureAwait(false),
                "repair" => await RunRepairAsync(rest).ConfigureAwait(false),
                "proxy" => await RunProxyAsync(rest).ConfigureAwait(false),
                "handoff" => await RunHandoffAsync(rest).ConfigureAwait(false),
                "help" or "--help" or "-h" => Usage(),
                _ => UnknownCommand(command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("CANCELLED: the operation was cancelled; no partial result is claimed.");
            return ContractFailure;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"FAILED: local input or persistence failed ({exception.GetType().Name}); no successful save is claimed.");
            return ContractFailure;
        }
    }

    private static int RunCheck(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var fixturePath = args.Length > 0
            ? args[0]
            : Path.Combine(baseDirectory, "fixtures", "synthetic-incident.ndjson");
        var schemaPath = Path.Combine(baseDirectory, "contracts", "event-envelope.schema.json");

        Console.WriteLine("NetScope Repair CLI - M0 / contract inspection");
        Console.WriteLine("DATA SOURCE: local NDJSON only. No network probe was performed and no system setting was read or changed.");
        Console.WriteLine($"fixture: {fixturePath}");
        Console.WriteLine($"schema : {schemaPath}");
        Console.WriteLine();

        if (!File.Exists(fixturePath) || !File.Exists(schemaPath))
        {
            Console.Error.WriteLine("FAILED: the fixture or the contract file is missing next to the executable.");
            return MissingInput;
        }

        var unsupportedKeywords = ContractInspection.FindUnsupportedKeywords(schemaPath);
        if (unsupportedKeywords.Count > 0)
        {
            Console.Error.WriteLine("FAILED: the contract uses keywords this build cannot enforce.");
            foreach (var keyword in unsupportedKeywords)
            {
                Console.Error.WriteLine($"  - {keyword}");
            }

            return UnsupportedContract;
        }

        var result = ContractInspection.Inspect(fixturePath, schemaPath, requireSynthetic: args.Length == 0);

        Console.WriteLine($"{"seq",3}  {"event_type",-16}  {"status",-14}  {"success",-7}  {"synthetic",-9}  event_id");

        foreach (var envelope in result.Accepted)
        {
            Console.WriteLine(
                $"{envelope.Sequence,3}  {envelope.EventType,-16}  {envelope.Status,-14}  "
                + $"{(envelope.Status.IsSuccess() ? "yes" : "no"),-7}  {envelope.Synthetic,-9}  {envelope.EventId}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"events={result.Accepted.Count} ok={result.SuccessCount} "
            + $"partial={result.PartialCount} observation_gap={result.ObservationGapCount} "
            + $"rejected_lines={result.RejectedCount}");
        Console.WriteLine("A partial result, timeout, refusal, unsupported call or unknown outcome is never counted as success.");
        Console.WriteLine("An observation gap is never rendered as an empty-but-healthy result.");
        Console.WriteLine("A line that fails the contract is reported and excluded from every total above.");

        if (!result.Passed)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"FAILED: {result.Problems.Count} problem(s) found.");
            foreach (var problem in result.Problems)
            {
                Console.Error.WriteLine($"  - {problem}");
            }

            return ContractFailure;
        }

        Console.WriteLine();
        Console.WriteLine("OK: every envelope was read and conforms to the published contract.");
        return Success;
    }

    private static async Task<int> RunSnapshotAsync()
    {
        var clock = new SystemClock();
        var now = clock.UtcNow;

        var context = new CollectionContext
        {
            SessionId = $"session-{now:yyyyMMddTHHmmss}",
            BootId = $"boot-{Environment.TickCount64}",
            SnapshotId = $"snapshot-{now:yyyyMMddTHHmmss}",
            ObservedAt = now,
            MonotonicMs = clock.MonotonicMs,
            Synthetic = false,
        };

        Console.WriteLine("NetScope Repair CLI - read-only snapshot");
        Console.WriteLine("READ ONLY: no route, interface, DNS, proxy or registry value is modified; no network request is sent.");
        Console.WriteLine();

        var snapshot = await new WindowsSnapshotCollector().CollectAsync(context, CancellationToken.None);

        Console.WriteLine("domain status");
        foreach (var pair in snapshot.Completeness())
        {
            Console.WriteLine($"  {pair.Key,-12} {pair.Value}");
        }

        Console.WriteLine();
        Console.WriteLine($"interfaces ({snapshot.Interfaces.Value?.Count ?? 0})");
        Console.WriteLine($"  {"idx",3}  {"family",-7}  {"kind",-9}  {"status",-10}  {"metric",6}  {"auto",-5}  {"noDefRt",-7}  name");

        foreach (var item in (snapshot.Interfaces.Value ?? Array.Empty<InterfaceState>()).OrderBy(entry => entry.IfIndex))
        {
            Console.WriteLine(
                $"  {item.IfIndex,3}  {item.Family,-7}  {item.Kind,-9}  {item.OperationalStatus,-10}  "
                + $"{item.Metric?.ToString() ?? "-",6}  {Format(item.AutomaticMetric),-5}  {Format(item.DisableDefaultRoutes),-7}  {item.Name}");
        }

        if (snapshot.Interfaces.Reason is { Length: > 0 } interfaceReason)
        {
            Console.WriteLine($"  note: {interfaceReason}");
        }

        Console.WriteLine();
        var routes = snapshot.Routes.Value ?? Array.Empty<RouteEntry>();
        Console.WriteLine($"routes ({routes.Count}): IPv4 default={routes.Count(route => route.Family == "ipv4" && route.DestinationPrefix == "0.0.0.0/0")}, "
            + $"IPv6 default={routes.Count(route => route.Family == "ipv6" && route.DestinationPrefix == "::/0")}");

        foreach (var route in routes
                     .Where(route => route.DestinationPrefix is "0.0.0.0/0" or "::/0")
                     .OrderBy(route => route.TotalMetric)
                     .Take(6))
        {
            Console.WriteLine(
                $"  {route.DestinationPrefix,-12} if={route.IfIndex,-3} route_metric={route.RouteMetric,-3} "
                + $"if_metric={route.InterfaceMetric?.ToString() ?? "-",-3} total={route.TotalMetric?.ToString() ?? "-",-3} next={route.NextHop} protocol={route.Protocol}");
        }

        Console.WriteLine();
        Console.WriteLine("proxy layers");
        var proxies = snapshot.Proxies.Value;
        if (proxies is null)
        {
            Console.WriteLine($"  unavailable: {snapshot.Proxies.Reason}");
        }
        else
        {
            foreach (var (name, layer) in new (string, Collected<ProxyLayer>)[]
                     {
                         ("tool_process_environment", proxies.ToolProcessEnvironment),
                         ("user_environment", proxies.UserEnvironment),
                         ("machine_environment", proxies.MachineEnvironment),
                         ("wininet", proxies.WinInet),
                         ("winhttp", proxies.WinHttp),
                     })
            {
                Console.WriteLine(
                    $"  {name,-24} {layer.Status,-12} enabled={Format(layer.Value?.Enabled)} "
                    + $"values={layer.Value?.Variables.Count ?? 0}{(layer.Reason is null ? string.Empty : "  reason=" + layer.Reason)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("A high metric, a reserved-range address or two clients running at once is a clue, not a root cause.");
        return Success;
    }

    private static async Task<int> RunDemoAsync(string[] args)
    {
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetScopeRepair",
            "demo");

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--out" or "-o" && index + 1 < args.Length)
            {
                outputDirectory = args[index + 1];
                index++;
            }
        }

        return await DemoPipeline.RunAsync(outputDirectory, Console.Out, CancellationToken.None).ConfigureAwait(false);
    }

    private static int RunReplay(string[] args)
    {
        var path = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-incident.ndjson");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAILED: no session log at {path}");
            return MissingInput;
        }

        Console.WriteLine("NetScope Repair CLI - offline replay");
        Console.WriteLine($"log: {path}");
        Console.WriteLine();

        var result = new ReplayService().Replay(path);
        var summary = result.Summary;

        Console.WriteLine($"events={summary.EventCount} synthetic={summary.SyntheticCount}");
        Console.WriteLine($"ok={summary.SuccessCount} partial={summary.PartialCount} observation_gap={summary.ObservationGapCount}");
        Console.WriteLine($"duplicates={result.Log.DuplicateCount} gaps={result.Log.SequenceGaps.Count} problems={result.Log.Problems.Count}");
        Console.WriteLine("by event type");

        foreach (var pair in summary.ByType)
        {
            Console.WriteLine($"  {WireNames.Of(pair.Key),-16} {pair.Value}");
        }

        foreach (var gap in result.Log.SequenceGaps)
        {
            Console.WriteLine($"  gap: sequences {gap.Expected}..{gap.Found - 1} are missing ({gap.Missing} event(s))");
        }

        foreach (var problem in result.Log.Problems)
        {
            Console.WriteLine($"  problem: line {problem.LineNumber}: {problem.Message}{(problem.IsTruncatedTail ? " [truncated tail]" : string.Empty)}");
        }

        foreach (var problem in result.EvidenceProblems) Console.WriteLine($"  evidence_problem: {problem}");
        var ruleFailures = 0;
        if (result.IsClean && result.Evidence is not null)
        {
            // Recompute separately per snapshot. A snapshot is never combined with another
            // cycle's probes; recorded diagnoses may still cite evidence across cycles.
            var scopes = result.SnapshotScopes.Count > 0
                ? result.SnapshotScopes.Select(scope => (SnapshotId: scope.Snapshot.SnapshotId, scope.Evidence)).ToArray()
                : new[] { (SnapshotId: "unscoped", Evidence: result.Evidence) };
            var evaluations = scopes
                .Select(scope => (scope.SnapshotId, Evaluation: DiagnosticEngine.CreateDefault().EvaluateDetailed(scope.Evidence)))
                .ToArray();
            ruleFailures = evaluations.Sum(item => item.Evaluation.Failures.Count);
            var recomputed = evaluations.Sum(item => item.Evaluation.Diagnoses.Count);

            Console.WriteLine(
                $"typed_evidence=yes recorded_diagnoses={result.Diagnoses.Count} "
                + $"recomputed_diagnoses={recomputed} rule_errors={ruleFailures}");
            if (result.SnapshotScopes.Count > 1)
            {
                Console.WriteLine($"snapshot_scopes={result.SnapshotScopes.Count} "
                    + $"unscoped_evidence={(result.UnscopedEvidence is null ? "no" : "yes")}");
                Console.WriteLine("note: 每个快照单独重算；已保存的跨周期诊断只作为记录展示，不并入单快照证据。");
            }

            foreach (var (snapshotId, evaluation) in evaluations)
            {
                var prefix = result.SnapshotScopes.Count > 1 ? $"  [{snapshotId}] " : "  ";
                foreach (var diagnosis in evaluation.Diagnoses)
                    Console.WriteLine($"{prefix}{diagnosis.RuleId} [{WireNames.Of(diagnosis.Level)}] {diagnosis.Summary}");
                foreach (var failure in evaluation.Failures)
                    Console.WriteLine($"{prefix}rule_error: {failure.RuleId} {failure.ErrorType}");
            }
        }
        else if (result.Evidence is null)
            Console.WriteLine("typed_evidence=no: legacy event summary only; diagnostic premises were not recorded by this format.");
        Console.WriteLine();
        Console.WriteLine("Replay reads saved evidence only; it cannot reproduce the outage or change the machine.");
        return result.IsClean && ruleFailures == 0 ? Success : ContractFailure;
    }

    private static string Format(bool? value) => value is null ? "-" : value.Value ? "yes" : "no";

    // ------------------------------------------------------------------ real maintenance

    private static QuickRepairService MaintenanceService() => new(sink: new NetScope.Storage.NdjsonRepairEventSink(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetScopeRepair", "maintenance", "repair-events.ndjson")));

    private static async Task<int> RunStatusAsync()
    {
        var service = MaintenanceService();
        Console.WriteLine("=== NetScope 本机体检（真实数据 · 只读）===");
        var plan = await service.PlanAsync(deep: false, CancellationToken.None).ConfigureAwait(false);
        var snapshot = plan.Snapshot;

        Console.WriteLine($"观测时间  {snapshot.ObservedAt:yyyy-MM-dd HH:mm:ss}   管理员权限={snapshot.IsElevated}");
        Console.WriteLine($"有网出口  if={snapshot.ActiveEgressIndex} {snapshot.ActiveEgressAlias}");
        Console.WriteLine($"代理可用端口  {(snapshot.WorkingProxyPort is null ? "(无)" : "127.0.0.1:" + snapshot.WorkingProxyPort)}"
            + $"   监听中的候选端口={string.Join(",", snapshot.ListeningProxyPorts)}");
        Console.WriteLine();

        Console.WriteLine("默认路由 / 接口");
        foreach (var item in snapshot.Interfaces.Where(item => item.HasDefaultRoute))
        {
            var tag = item.Connectivity == "Internet" ? "有网" : "无网";
            Console.WriteLine($"  if={item.Index,-3} {item.Alias,-16} 网关={item.NextHop,-16} 度量={item.InterfaceMetric,-4} [{tag}] DNS={(item.DnsServers.Count > 0 ? string.Join(",", item.DnsServers) : "自动")}");
        }

        Console.WriteLine();
        Console.WriteLine("代理配置");
        Console.WriteLine($"  系统代理  ProxyEnable={(snapshot.SystemProxyEnabled ? 1 : 0)}  ProxyServer={snapshot.SystemProxyServer}  端口={snapshot.SystemProxyPort}");
        Console.WriteLine($"  WinHTTP   {(snapshot.WinHttpIsProxy ? "127.0.0.1:" + snapshot.WinHttpPort : "直连")}");
        foreach (var layer in snapshot.ProxyLayers)
        {
            var state = layer.Port <= 0 ? "端口未解析" : layer.PortListening ? "端口在听" : "端口无监听 ← 死路";
            Console.WriteLine($"  {layer.Scope,-8}\\{layer.Name,-12} = {layer.Value,-32} [{state}]");
        }

        Console.WriteLine();
        Console.WriteLine($"发现 {plan.Findings.Count} 项");
        foreach (var finding in plan.Findings)
            Console.WriteLine($"  [{finding.Severity.ToString().ToUpperInvariant(),-8}] {finding.Code,-24} {finding.Title}");

        Console.WriteLine();
        Console.WriteLine($"可执行动作 {plan.Actions.Count} 项（默认选中 {plan.Actions.Count(action => action.SelectedByDefault)} 项）");
        foreach (var action in plan.Actions)
            Console.WriteLine($"  [{(action.SelectedByDefault ? "x" : " ")}] {action.Id,-40} {action.Title}");

        foreach (var note in snapshot.Notes) Console.WriteLine("  说明: " + note);
        Console.WriteLine();
        Console.WriteLine("status 只读取，不修改任何设置。执行：netscope repair --apply --yes");
        return Success;
    }

    private static async Task<int> RunRepairAsync(string[] args)
    {
        var service = MaintenanceService();
        var restore = args.Contains("--restore", StringComparer.OrdinalIgnoreCase);
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase)
            || !args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var deep = args.Contains("--deep", StringComparer.OrdinalIgnoreCase);
        var yes = args.Contains("--yes", StringComparer.OrdinalIgnoreCase) || args.Contains("-y", StringComparer.OrdinalIgnoreCase);

        if (restore)
        {
            Console.WriteLine("=== 回滚上一次修复 ===");
            var restoreReport = await service.RestoreAsync(false, CancellationToken.None).ConfigureAwait(false);
            foreach (var message in restoreReport.Messages) Console.WriteLine("  " + message);
            Console.WriteLine($"  还原 {restoreReport.Restored} 项，失败 {restoreReport.Failed} 项。");
            return restoreReport.Failed > 0 ? ContractFailure : Success;
        }

        var plan = await service.PlanAsync(deep, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("=== 网络残留修复计划（本机真实数据）===");
        Console.WriteLine($"发现 {plan.Findings.Count} 项，动作 {plan.Actions.Count} 项，高风险模式={deep}");
        foreach (var finding in plan.Findings)
            Console.WriteLine($"  [{finding.Severity.ToString().ToUpperInvariant(),-8}] {finding.Title} — {finding.Detail}");
        Console.WriteLine();
        foreach (var action in plan.Actions.Where(action => action.SelectedByDefault))
            Console.WriteLine($"  将执行 [{action.Risk}] {action.Title}  {action.Current} → {action.Desired ?? "(空)"}");
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("这是预览（DryRun），没有修改任何设置。执行：netscope repair --apply --yes");
            return Success;
        }

        if (!yes)
        {
            Console.Write("确认执行以上动作？[y/N] ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("已取消，未修改任何设置。");
                return Success;
            }
        }

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None).ConfigureAwait(false);
        foreach (var result in report.Results)
        {
            Console.WriteLine(result.Applied
                ? $"  ✔ {result.Title}  {result.Before} → {result.After}"
                : $"  · {result.Title}  跳过/失败：{result.Error ?? "未选中"}");
        }
        foreach (var message in report.Messages) Console.WriteLine("  " + message);
        Console.WriteLine($"  应用 {report.Applied} / 跳过 {report.Skipped} / 失败 {report.Failed}");
        return report.Failed > 0 ? ContractFailure : Success;
    }

    private static async Task<int> RunProxyAsync(string[] args)
    {
        var service = MaintenanceService();
        var verb = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "status";
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        if (verb.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await service.InspectAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("=== 代理状态（真实数据）===");
            Console.WriteLine($"  系统代理  ProxyEnable={(snapshot.SystemProxyEnabled ? 1 : 0)}  {snapshot.SystemProxyServer}");
            foreach (var layer in snapshot.ProxyLayers)
                Console.WriteLine($"  {layer.Scope}\\{layer.Name} = {layer.Value} [{(layer.PortListening ? "活" : "死" )}]");
            Console.WriteLine($"  WinHTTP  {(snapshot.WinHttpIsProxy ? "127.0.0.1:" + snapshot.WinHttpPort : "直连")}");
            Console.WriteLine($"  可用代理端口  {(snapshot.WorkingProxyPort is null ? "(无)" : snapshot.WorkingProxyPort.ToString())}");
            return Success;
        }

        if (verb.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("=== 关代理：回到纯直连 ===");
            var report = await service.ProxyOffAsync(dryRun, CancellationToken.None).ConfigureAwait(false);
            return PrintReport(report);
        }

        if (verb.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("=== 开代理：对齐到真实端口 ===");
            var report = await service.ProxyOnAsync(7890, dryRun, CancellationToken.None).ConfigureAwait(false);
            return PrintReport(report);
        }

        Console.Error.WriteLine("FAILED: 用法 netscope proxy status|on|off [--dry-run]");
        return BadArguments;
    }

    private static int PrintReport(RepairReport report)
    {
        foreach (var result in report.Results)
        {
            Console.WriteLine(result.Applied
                ? $"  ✔ {result.Title}  {result.Before} → {result.After}"
                : $"  · {result.Title}  {(result.Error ?? "预览，未修改")}");
        }
        foreach (var message in report.Messages) Console.WriteLine("  " + message);
        Console.WriteLine($"  应用 {report.Applied} / 跳过 {report.Skipped} / 失败 {report.Failed}"
            + (report.DryRun ? "   [DryRun 预览]" : ""));
        return report.Failed > 0 ? ContractFailure : Success;
    }

    private static async Task<int> RunHandoffAsync(string[] args)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logPath = Path.Combine(local, "NetScopeRepair", "maintenance", "repair-events.ndjson");
        var outDir = OptionValue(args, "--out") ?? Path.Combine(local, "NetScopeRepair", "handoff");
        var batchId = OptionValue(args, "--case");
        Console.WriteLine("=== 生成 AI 交接包 ===");
        var result = await new NetScope.Storage.Export.HandoffExporter()
            .ExportAsync(logPath, outDir, batchId, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("交接包：" + result.PackagePath);
        Console.WriteLine();
        Console.WriteLine("把下面这段粘贴给 AI 助手（Qoder 等，需打开 NetScopeRepair 仓库）：");
        Console.WriteLine(result.Prompt);
        return Success;
    }

    private static string? OptionValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Usage()
    {
        Console.WriteLine("netscope - NetScope Repair CLI");
        Console.WriteLine();
        Console.WriteLine("只读命令（M0/M1，永不修改系统）：");
        Console.WriteLine("  netscope check                 校验契约与合成 fixture");
        Console.WriteLine("  netscope snapshot              读一次本机网络状态");
        Console.WriteLine("  netscope demo [--out <dir>]    跑合成流水线，写本地演示日志");
        Console.WriteLine("  netscope replay [<file>]       离线回放已捕获会话");
        Console.WriteLine("  netscope case-replay <pkg.zip> 只读重放本地故障包");
        Console.WriteLine();
        Console.WriteLine("真实维护命令（本机真实数据；带写入能力，全部可回滚）：");
        Console.WriteLine("  netscope status                本机体检：死代理 / 残留接口 / 幽灵网卡（只读）");
        Console.WriteLine("  netscope repair                预览修复计划（等同 --dry-run）");
        Console.WriteLine("  netscope repair --apply --yes  真执行默认动作（加 --deep 含高风险项）");
        Console.WriteLine("  netscope repair --restore      回滚上一次/首次以来的全部改动");
        Console.WriteLine("  netscope proxy status          查看代理配置与端口存活（只读）");
        Console.WriteLine("  netscope proxy on|off          开/关代理（off = 纯直连）");
        Console.WriteLine("  netscope handoff [--case <id>] [--out <dir>]  生成 AI 失败案例交接包");
        Console.WriteLine();
        Console.WriteLine("只有 --apply / proxy on|off 会写系统；每次改动记入 %LOCALAPPDATA%\\NetScopeRepair\\maintenance\\journal.json。");
        Console.WriteLine("接口度量 / WinHTTP / 机器级变量需要管理员权限，权限不足会如实跳过而不会假装成功。");
        return Success;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"FAILED: unknown command '{command}'.");
        Console.Error.WriteLine("Run 'netscope help' for the supported commands.");
        return BadArguments;
    }
}
