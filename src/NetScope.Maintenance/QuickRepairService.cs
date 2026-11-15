using System.Globalization;

namespace NetScope.Maintenance;

/// <summary>
/// Orchestrates real, local maintenance: inspect → plan → explicitly selected apply → restore.
/// No action runs without a plan and a caller-supplied selection; every applied change is journaled.
/// </summary>
public sealed class QuickRepairService
{
    private static readonly string[] StandardProxyNames =
        { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" };

    private readonly IMaintenanceProbe _probe;
    private readonly IMaintenanceExecutor _executor;
    private readonly MaintenanceJournalStore _journal;
    private readonly IPostCheckProbe _postCheck;
    private readonly IRepairEventSink? _sink;
    private string? _lastBatchId;

    public QuickRepairService(
        IMaintenanceProbe? probe = null,
        IMaintenanceExecutor? executor = null,
        MaintenanceJournalStore? journal = null,
        IPostCheckProbe? postCheck = null,
        IRepairEventSink? sink = null)
    {
        _probe = probe ?? new WindowsMaintenanceProbe();
        _executor = executor ?? new WindowsMaintenanceExecutor();
        _journal = journal ?? new MaintenanceJournalStore();
        _postCheck = postCheck ?? new WindowsPostCheckProbe(_probe);
        _sink = sink;
    }

    public MaintenanceJournalStore Journal => _journal;

    public Task<MaintenanceSnapshot> InspectAsync(CancellationToken cancellationToken)
        => _probe.CaptureAsync(cancellationToken);

    public async Task<RepairPlan> PlanAsync(bool deep, CancellationToken cancellationToken,
        IProgress<RepairProgress>? progress = null)
    {
        progress?.Report(new RepairProgress(RepairStepKind.Phase,
            "读取本机网络状态：网卡 · 路由 · DNS · 代理 · 端口存活…"));
        var snapshot = await InspectAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new RepairProgress(RepairStepKind.Phase, "分析观测，生成修复计划…"));
        var plan = MaintenancePlanner.Build(snapshot, deep);
        progress?.Report(new RepairProgress(RepairStepKind.Done,
            $"体检完成：发现 {plan.Findings.Count} 项问题 · {plan.Actions.Count} 项可执行动作。",
            Detail: $"有网出口 if={snapshot.ActiveEgressIndex} {snapshot.ActiveEgressAlias}"));
        return plan;
    }

    public async Task<RepairReport> ApplyAsync(
        RepairPlan plan,
        IReadOnlyCollection<string>? selectedIds,
        bool dryRun,
        CancellationToken cancellationToken,
        IProgress<RepairProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var selected = selectedIds is null
            ? plan.Actions.Where(action => action.SelectedByDefault).Select(action => action.Id)
                .ToHashSet(StringComparer.Ordinal)
            : selectedIds.ToHashSet(StringComparer.Ordinal);
        var batchId = Guid.NewGuid().ToString("N");

        var results = new List<ActionResult>();
        var entries = new List<JournalEntry>();
        var messages = new List<string>();
        var skippedForElevation = 0;
        var total = selected.Count;
        var step = 0;
        progress?.Report(new RepairProgress(RepairStepKind.Phase,
            (dryRun ? "预览" : "准备执行") + $" {total} 项动作…", Step: 0, TotalSteps: total));

        foreach (var action in plan.Actions)
        {
            if (!selected.Contains(action.Id)) continue;
            step++;
            if (action.RequiresElevation && !plan.Snapshot.IsElevated)
            {
                skippedForElevation++;
                results.Add(new ActionResult(action.Id, action.Title, false, action.Current, null,
                    "需要管理员权限：已跳过，未假装成功"));
                progress?.Report(new RepairProgress(RepairStepKind.ActionResult, action.Title,
                    action.Id, action.Title, step, total, false, "需要管理员权限：已跳过，未假装成功"));
                continue;
            }
            if (dryRun)
            {
                results.Add(new ActionResult(action.Id, action.Title, false, action.Current, action.Desired, null));
                progress?.Report(new RepairProgress(RepairStepKind.ActionResult, action.Title,
                    action.Id, action.Title, step, total, null,
                    "预览：" + action.Current + " → " + (action.Desired ?? "(空)")));
                continue;
            }

            progress?.Report(new RepairProgress(RepairStepKind.ActionStart, action.Title,
                action.Id, action.Title, step, total, Detail: action.Detail));
            var result = await _executor.ApplyAsync(action, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new RepairProgress(RepairStepKind.ActionResult, action.Title,
                action.Id, action.Title, step, total, result.Applied,
                result.Applied ? (result.Before ?? "") + " → " + (result.After ?? "") : result.Error));
            // Cache refresh has no inverse; keeping it out of the journal keeps Restore precise.
            if (result.Applied && action.Kind != "FlushDnsCache")
                entries.Add(ToJournalEntry(action, result));
        }

        if (skippedForElevation > 0)
            messages.Add("有 " + skippedForElevation + " 个动作需要管理员权限，已跳过；请以管理员运行本程序后重试。");

        string? journalPath = null;
        if (!dryRun && entries.Count > 0)
        {
            journalPath = _journal.Save(entries);
            messages.Add("改动已记入回滚备份：" + journalPath);
            progress?.Report(new RepairProgress(RepairStepKind.Info,
                $" {entries.Count} 项改动已记入回滚备份，可一键还原。", Step: step, TotalSteps: total));
            if (entries.Any(entry => entry.Kind is "ClearProxyEnv" or "AlignProxyEnv"))
            {
                progress?.Report(new RepairProgress(RepairStepKind.Phase,
                    "广播环境变量变更，通知已打开的程序…", Step: step, TotalSteps: total));
                await _executor.BroadcastEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (dryRun) messages.Add("DryRun：以上为预览，没有修改任何设置。");
        if (!plan.Snapshot.AnyProxyAlive && !dryRun && results.Any(result => result.Applied))
            messages.Add("代理客户端未运行：当前为纯直连状态，国内软件不需要开代理即可使用。重启已打开的程序后才认新环境变量。");

        PostCheckResult? postCheck = null;
        if (!dryRun && results.Any(result => result.Applied))
        {
            var tunApplied = plan.Actions.FirstOrDefault(action => action.Kind == "FixTunPriority"
                && results.Any(result => result.ActionId == action.Id && result.Applied));
            int? expectedRoute = tunApplied is null ? null
                : int.Parse(tunApplied.Parameters["Index"], CultureInfo.InvariantCulture);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                progress?.Report(new RepairProgress(RepairStepKind.PostCheck,
                    $"修后自检 第 {attempt + 1}/3 次：复核出口路由与代理存活…", Step: step, TotalSteps: total));
                var observation = await _postCheck.ObserveAsync(cancellationToken).ConfigureAwait(false);
                postCheck = PostCheckClassifier.Classify(observation, expectedRoute);
                if (postCheck.Success) break;
                if (attempt < 2) await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            }
            foreach (var detail in postCheck!.Details)
            {
                messages.Add("自检：" + detail);
                progress?.Report(new RepairProgress(RepairStepKind.Info, "自检：" + detail, Step: step, TotalSteps: total));
            }
            if (postCheck.Success)
            {
                messages.Add("修后自检通过。");
                progress?.Report(new RepairProgress(RepairStepKind.PostCheck,
                    "修后自检通过：出口路由与代理存活均符合预期。", Step: step, TotalSteps: total, Applied: true));
            }
            else
            {
                messages.Add("修后自检未通过（原因 " + postCheck.CauseClass + "）。");
                progress?.Report(new RepairProgress(RepairStepKind.Warning,
                    "修后自检未通过（原因 " + postCheck.CauseClass + "）。", Step: step, TotalSteps: total, Applied: false));
                if (postCheck.CauseClass == "A/B" && tunApplied is not null)
                {
                    var entry = entries.FirstOrDefault(item => item.Kind == "FixTunPriority");
                    if (entry is not null)
                    {
                        progress?.Report(new RepairProgress(RepairStepKind.Phase,
                            "度量未生效（A/B）：自动还原 TUN 度量…", Step: step, TotalSteps: total));
                        var (ok, message) = await _executor.RestoreAsync(entry, cancellationToken).ConfigureAwait(false);
                        if (ok)
                        {
                            _journal.Remove(entry.Key);
                            messages.Add("度量改动未生效（原因 A/B）：已自动还原 TUN 度量。请在客户端关开 TUN / 打开 auto-route 后重新体检。");
                            progress?.Report(new RepairProgress(RepairStepKind.Warning,
                                "已自动还原 TUN 度量；请在客户端关开 TUN / 打开 auto-route 后重新体检。", Step: step, TotalSteps: total));
                        }
                        else
                        {
                            messages.Add("自动还原 TUN 度量失败：" + message);
                            progress?.Report(new RepairProgress(RepairStepKind.Error,
                                "自动还原 TUN 度量失败：" + message, Step: step, TotalSteps: total));
                        }
                    }
                }
            }
        }

        if (_sink is not null && !dryRun)
        {
            progress?.Report(new RepairProgress(RepairStepKind.Phase,
                "复测本机状态并写入修复事件日志…", Step: step, TotalSteps: total));
            var now = DateTimeOffset.Now;
            var events = new List<RepairEvent>();
            MaintenanceSnapshot? after = null;
            if (results.Any(result => result.Applied))
            {
                after = await _probe.CaptureAsync(cancellationToken).ConfigureAwait(false);
                events.Add(new RepairEvent(batchId, "repair_applied", now, null, null,
                    "应用 " + results.Count(result => result.Applied) + " 项", null, false, postCheck, plan.Snapshot, after));
            }
            foreach (var failed in results.Where(result => !result.Applied && result.Error is not null))
            {
                var action = plan.Actions.First(item => item.Id == failed.ActionId);
                events.Add(new RepairEvent(batchId, "action_failed", now, failed.ActionId, action.Kind, failed.Title,
                    failed.Error, failed.Error!.Contains("管理员"), null, null, null));
            }
            if (postCheck is { Success: false })
                events.Add(new RepairEvent(batchId, "postcheck_failed", now, null, null,
                    "修后自检未通过（原因 " + postCheck.CauseClass + "）", null, false, postCheck, plan.Snapshot, after));
            if (events.Count > 0) _lastBatchId = batchId;
            var sinkError = await EmitSafeAsync(events, cancellationToken).ConfigureAwait(false);
            if (sinkError is not null)
            {
                messages.Add(sinkError);
                progress?.Report(new RepairProgress(RepairStepKind.Warning, sinkError, Step: step, TotalSteps: total));
            }
        }

        var appliedCount = results.Count(result => result.Applied);
        var skippedCount = results.Count(result => !result.Applied && result.Error is null);
        var failedCount = results.Count(result => result.Error is not null);
        progress?.Report(new RepairProgress(RepairStepKind.Done,
            dryRun ? "预览完成：以上为预览，没有修改任何设置。"
                : $"执行完成：应用 {appliedCount} / 跳过 {skippedCount} / 失败 {failedCount}。",
            Step: total, TotalSteps: total,
            Applied: !dryRun && appliedCount > 0 && failedCount == 0 && (postCheck is null || postCheck.Success)));

        return new RepairReport(
            dryRun,
            appliedCount,
            skippedCount,
            failedCount,
            results,
            journalPath,
            messages,
            PostCheck: postCheck);
    }

    /// <summary>User says "still broken": persist a user_unresolved event for the last batch.</summary>
    public async Task<bool> MarkUnresolvedAsync(CancellationToken cancellationToken)
    {
        if (_sink is null || _lastBatchId is null) return false;
        var error = await EmitSafeAsync(new[]
        {
            new RepairEvent(_lastBatchId, "user_unresolved", DateTimeOffset.Now, null, null,
                "用户标记：还是没好", null, false, null, null, null),
        }, cancellationToken).ConfigureAwait(false);
        return error is null;
    }

    private async Task<string?> EmitSafeAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken)
    {
        if (_sink is null || events.Count == 0) return null;
        try
        {
            await _sink.EmitAsync(events, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return "修复事件写入失败：" + exception.Message + "（修复本身不受影响）";
        }
    }

    public async Task<RestoreReport> RestoreAsync(bool dryRun, CancellationToken cancellationToken,
        IProgress<RepairProgress>? progress = null)
    {
        progress?.Report(new RepairProgress(RepairStepKind.Phase, "读取回滚备份…"));
        var document = _journal.Load();
        if (document is null || document.Entries.Count == 0)
        {
            progress?.Report(new RepairProgress(RepairStepKind.Done, "没有找到可回滚的备份。", Applied: false));
            return new RestoreReport(0, 0, new[] { "没有找到可回滚的备份：" + _journal.Path }, false);
        }

        var restored = 0;
        var failed = 0;
        var total = document.Entries.Count;
        var step = 0;
        var messages = new List<string>
        {
            "备份时间：" + document.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + "（最近更新 " + document.UpdatedAt.ToString("HH:mm:ss") + "）",
        };
        progress?.Report(new RepairProgress(RepairStepKind.Phase,
            (dryRun ? "预览还原" : "开始还原") + $" {total} 项改动…", Step: 0, TotalSteps: total));

        foreach (var entry in document.Entries.AsEnumerable().Reverse())
        {
            step++;
            var label = entry.Kind + " " + entry.Scope + "\\" + entry.Name;
            if (dryRun)
            {
                messages.Add("[预览] 会还原 " + label + " → " + (entry.Old ?? "(空)"));
                progress?.Report(new RepairProgress(RepairStepKind.ActionResult, label,
                    Step: step, TotalSteps: total, Detail: "预览 → " + (entry.Old ?? "(空)")));
                continue;
            }
            progress?.Report(new RepairProgress(RepairStepKind.ActionStart, "还原 " + label,
                Step: step, TotalSteps: total));
            var (ok, message) = await _executor.RestoreAsync(entry, cancellationToken).ConfigureAwait(false);
            if (ok) restored++; else failed++;
            messages.Add((ok ? "✔ " : "X ") + message);
            progress?.Report(new RepairProgress(RepairStepKind.ActionResult, message,
                Step: step, TotalSteps: total, Applied: ok, Detail: ok ? null : message));
        }

        if (!dryRun && failed == 0)
        {
            _journal.Clear();
            messages.Add("✔ 回滚完成，备份文件已清除。");
        }
        progress?.Report(new RepairProgress(RepairStepKind.Done,
            dryRun ? "预览完成：未修改任何设置。" : $"回滚完成：还原 {restored} 项 / 失败 {failed} 项。",
            Step: total, TotalSteps: total, Applied: !dryRun && failed == 0));
        return new RestoreReport(restored, failed, messages, true);
    }

    /// <summary>Turn the proxy fully off: system proxy, every proxy environment variable, WinHTTP, caches.</summary>
    public async Task<RepairReport> ProxyOffAsync(bool dryRun, CancellationToken cancellationToken,
        IProgress<RepairProgress>? progress = null)
    {
        progress?.Report(new RepairProgress(RepairStepKind.Phase, "读取代理状态，准备关闭代理并清理残留…"));
        var snapshot = await InspectAsync(cancellationToken).ConfigureAwait(false);
        var actions = new List<RepairAction>();

        if (snapshot.SystemProxyEnabled)
            actions.Add(SimpleAction("DisableSystemProxy", "关闭系统代理", "HKCU\\ProxyEnable", "1", "0",
                false, new Dictionary<string, string>()));

        foreach (var layer in snapshot.ProxyLayers)
        {
            if (!StandardProxyNames.Contains(layer.Name) && !layer.Name.StartsWith("ALL_PROXY", StringComparison.OrdinalIgnoreCase)
                && !layer.Name.StartsWith("all_proxy", StringComparison.OrdinalIgnoreCase)) continue;
            actions.Add(SimpleAction("ClearProxyEnv", "清除 " + layer.Scope + "\\" + layer.Name,
                layer.Scope + "\\" + layer.Name, layer.Value, null, layer.Scope == "Machine",
                new Dictionary<string, string>
                {
                    ["Scope"] = layer.Scope,
                    ["Name"] = layer.Name,
                    ["Value"] = "",
                }));
        }

        if (snapshot.WinHttpIsProxy)
            actions.Add(SimpleAction("ResetWinHttpProxy", "重置 WinHTTP 为直连", "WinHTTP",
                "127.0.0.1:" + snapshot.WinHttpPort, "direct", true,
                new Dictionary<string, string> { ["Port"] = "0" }));

        actions.Add(SimpleAction("FlushDnsCache", "刷新 DNS / 邻居缓存", "local", "", "flushed",
            false, new Dictionary<string, string>()));

        var plan = new RepairPlan(snapshot.ObservedAt, snapshot, Array.Empty<Finding>(), actions);
        return await ApplyAsync(plan, actions.Select(action => action.Id).ToArray(), dryRun, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    /// <summary>Turn the proxy on after the client is running: align everything to a real, tested port.</summary>
    public async Task<RepairReport> ProxyOnAsync(int preferredPort, bool dryRun, CancellationToken cancellationToken,
        IProgress<RepairProgress>? progress = null)
    {
        progress?.Report(new RepairProgress(RepairStepKind.Phase, "检测可用代理端口，准备对齐系统代理与环境变量…"));
        var snapshot = await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.WorkingProxyPort is null)
        {
            progress?.Report(new RepairProgress(RepairStepKind.Done,
                "没有任何端口能当代理用：请先在代理客户端里连接成功，再执行开代理。", Applied: false));
            return new RepairReport(dryRun, 0, 0, 1, Array.Empty<ActionResult>(), null,
                new[] { "没有任何端口能当代理用：请先在代理客户端里连接成功，再执行开代理。" });
        }

        var port = preferredPort > 0 && snapshot.ListeningProxyPorts.Contains(preferredPort)
            ? preferredPort : snapshot.WorkingProxyPort.Value;
        var target = "http://127.0.0.1:" + port;
        var actions = new List<RepairAction>
        {
            SimpleAction("SetSystemProxyServer", "设置系统代理端口", "HKCU\\ProxyServer",
                snapshot.SystemProxyServer, "127.0.0.1:" + port, false, new Dictionary<string, string>()),
            SimpleAction("EnableSystemProxy", "开启系统代理", "HKCU\\ProxyEnable",
                snapshot.SystemProxyEnabled ? "1" : "0", "1", false, new Dictionary<string, string>()),
        };

        foreach (var name in StandardProxyNames)
        {
            actions.Add(SimpleAction("AlignProxyEnv", "设置 User\\" + name, "User\\" + name,
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User), target, false,
                new Dictionary<string, string> { ["Scope"] = "User", ["Name"] = name, ["Value"] = target }));
        }

        foreach (var layer in snapshot.ProxyLayers.Where(layer =>
            layer.Name.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase)))
        {
            actions.Add(SimpleAction("ClearProxyEnv", "删除 " + layer.Scope + "\\" + layer.Name,
                layer.Scope + "\\" + layer.Name, layer.Value, null, layer.Scope == "Machine",
                new Dictionary<string, string> { ["Scope"] = layer.Scope, ["Name"] = layer.Name, ["Value"] = "" }));
        }

        actions.Add(SimpleAction("FlushDnsCache", "刷新 DNS / 邻居缓存", "local", "", "flushed",
            false, new Dictionary<string, string>()));

        var plan = new RepairPlan(snapshot.ObservedAt, snapshot, Array.Empty<Finding>(), actions);
        var report = await ApplyAsync(plan, actions.Select(action => action.Id).ToArray(), dryRun, cancellationToken, progress)
            .ConfigureAwait(false);
        return report with
        {
            Messages = report.Messages.Append("已对齐到真实端口 127.0.0.1:" + port + "；已打开的程序需要重启才认新环境变量。").ToArray(),
        };
    }

    private static RepairAction SimpleAction(string kind, string title, string target,
        string? current, string? desired, bool elevated, IReadOnlyDictionary<string, string> parameters)
        => new(kind + ":" + target, title, current + " → " + (desired ?? "(空)"),
            elevated ? ActionRisk.Elevated : ActionRisk.Safe, kind, target, current, desired,
            elevated, true, parameters);

    private static JournalEntry ToJournalEntry(RepairAction action, ActionResult result)
    {
        var scope = action.Parameters.TryGetValue("Scope", out var s) ? s : "";
        var name = action.Parameters.TryGetValue("Name", out var n) ? n : action.Target;
        var extra = action.Kind switch
        {
            "RaiseInterfaceMetric" or "ResetInterfaceDns" => action.Parameters.TryGetValue("Index", out var i) ? i : "",
            "FixTunPriority" => action.Parameters.TryGetValue("Index", out var ti) ? ti : "",
            "RemoveGhostDevice" => action.Parameters.TryGetValue("InstanceId", out var id) ? id : "",
            _ => "",
        };
        // One stable key per real target. Environment variables are case-insensitive on Windows,
        // and disable/enable both address ProxyEnable: without normalisation Restore would replay
        // the same value twice and briefly leave an intermediate state.
        var key = action.Kind switch
        {
            "ClearProxyEnv" or "AlignProxyEnv" => "EnvVar|" + scope + "|" + name.ToUpperInvariant(),
            "DisableSystemProxy" or "EnableSystemProxy" or "SetSystemProxyServer" => "Registry|" + action.Target,
            "ResetWinHttpProxy" or "SetWinHttpProxy" => "WinHttp|proxy",
            "RaiseInterfaceMetric" => "InterfaceMetric|" + extra,
            "FixTunPriority" => "TunPriority|" + extra,
            "ResetInterfaceDns" => "InterfaceDns|" + extra,
            "RemoveGhostDevice" => "Device|" + extra,
            _ => string.Join("|", action.Kind, scope, name, extra),
        };
        return new JournalEntry(key, action.Kind, scope, name, result.Before, result.After, extra, DateTimeOffset.Now);
    }
}
