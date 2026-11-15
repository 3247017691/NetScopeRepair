namespace NetScope.Maintenance;

/// <summary>
/// Pure planning logic: real observation in, typed findings and reversible actions out.
/// It has no I/O, so every branch can be unit-tested without touching the machine.
/// Rules never execute anything; the caller decides which selected actions to apply.
/// </summary>
public static class MaintenancePlanner
{
    public const int HighInterfaceMetric = 500;

    public static RepairPlan Build(MaintenanceSnapshot snapshot, bool deep = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var findings = new List<Finding>();
        var actions = new List<RepairAction>();
        var working = snapshot.WorkingProxyPort;
        var alivePorts = snapshot.ListeningProxyPorts;

        // ---- 1. Dead proxy: configuration points at a port nothing is listening on ----
        var deadLayers = snapshot.ProxyLayers
            .Where(layer => layer.Port > 0 && !layer.PortListening)
            .ToArray();
        if (deadLayers.Length > 0)
        {
            var evidence = deadLayers.Select(layer => $"{layer.Scope}\\{layer.Name}={layer.Value} → 端口 {layer.Port} 无监听").ToArray();
            findings.Add(new Finding(
                "DEAD_PROXY",
                "死代理：配置指向没有监听的端口",
                snapshot.AnyProxyAlive
                    ? "有可用代理端口，但部分配置仍指向旧端口；读环境变量的程序会连死端口。"
                    : "没有任何代理端口可用，环境变量却仍指向它：不开代理时国内软件/命令行工具会全部失败。",
                FindingSeverity.Critical,
                evidence));

            foreach (var layer in deadLayers)
            {
                var align = working is not null;
                var desired = align ? "http://127.0.0.1:" + working : null;
                actions.Add(new RepairAction(
                    (align ? "align-env:" : "clear-env:") + layer.Scope + ":" + layer.Name,
                    (align ? "对齐 " : "清除 ") + layer.Scope + "\\" + layer.Name,
                    (align ? "改为 " : "原值 ") + (layer.Value ?? "") + (align ? " → " + desired : "（端口无监听，删除后走直连）"),
                    ActionRisk.Safe,
                    align ? "AlignProxyEnv" : "ClearProxyEnv",
                    layer.Scope + "\\" + layer.Name,
                    layer.Value,
                    desired,
                    layer.Scope == "Machine",
                    true,
                    new Dictionary<string, string>
                    {
                        ["Scope"] = layer.Scope,
                        ["Name"] = layer.Name,
                        ["Value"] = desired ?? "",
                    }));
            }
        }

        if (snapshot.SystemProxyEnabled && snapshot.SystemProxyPort > 0
            && !alivePorts.Contains(snapshot.SystemProxyPort))
        {
            findings.Add(new Finding(
                "SYSTEM_PROXY_DEAD", "系统代理开着，却指向死端口",
                "ProxyEnable=1 但 127.0.0.1:" + snapshot.SystemProxyPort + " 没有监听；走 WinINET 的程序会全部失败。",
                FindingSeverity.Critical,
                new[] { "ProxyServer=" + snapshot.SystemProxyServer, "ProxyEnable=1" }));
            actions.Add(new RepairAction(
                "disable-system-proxy", "关闭系统代理", "把 ProxyEnable 置 0，浏览器等程序回到直连（ProxyServer 值保留）",
                ActionRisk.Safe, "DisableSystemProxy", "HKCU\\ProxyEnable", "1", "0", false, true,
                new Dictionary<string, string>()));
        }

        if (snapshot.WinHttpIsProxy && snapshot.WinHttpPort > 0)
        {
            var align = working is not null && working != snapshot.WinHttpPort;
            var dead = !alivePorts.Contains(snapshot.WinHttpPort);
            if (align || dead)
            {
                findings.Add(new Finding(
                    "WINHTTP_DRIFT", "WinHTTP 代理端口残留",
                    "WinHTTP 指向 " + snapshot.WinHttpPort + (dead ? "（无监听）" : "，与真实端口不一致") + "；Windows Update 等服务会走错。",
                    FindingSeverity.Warning,
                    new[] { "WinHTTP=127.0.0.1:" + snapshot.WinHttpPort }));
                actions.Add(new RepairAction(
                    "winhttp-proxy", dead ? "重置 WinHTTP 为直连" : "对齐 WinHTTP 代理端口",
                    dead ? "netsh winhttp reset proxy" : "netsh winhttp set proxy 127.0.0.1:" + working,
                    ActionRisk.Elevated, dead ? "ResetWinHttpProxy" : "SetWinHttpProxy",
                    "WinHTTP", snapshot.WinHttpPort.ToString(),
                    dead ? "direct" : "127.0.0.1:" + working, true, true,
                    new Dictionary<string, string> { ["Port"] = (working ?? 0).ToString() }));
            }
        }

        // ---- 2. Stale egress: old interface still holds a default route ----
        var defaultRoutes = snapshot.Interfaces.Where(item => item.HasDefaultRoute).ToArray();
        if (defaultRoutes.Length > 1)
        {
            findings.Add(new Finding(
                "MULTIPLE_DEFAULT_ROUTES", "多条默认路由并存",
                "换网后旧接口没退场，出站可能被分到没有 Internet 的链路。",
                FindingSeverity.Warning,
                defaultRoutes.Select(item => $"if={item.Index} {item.Alias} [{item.Connectivity}] metric={item.InterfaceMetric}").ToArray()));
        }

        foreach (var iface in snapshot.Interfaces)
        {
            if (!iface.HasDefaultRoute || iface.Connectivity == "Internet") continue;
            // Report the problem even when no trustworthy egress exists, but never act on it:
            // raising the only remaining interface's metric would make things worse.
            if (snapshot.ActiveEgressIndex > 0 && iface.Index == snapshot.ActiveEgressIndex) continue;
            if (!WindowsMaintenanceProbe.IsPhysical(iface.Alias)) continue;

            findings.Add(new Finding(
                "STALE_EGRESS", "旧出口接口：" + iface.Alias,
                "if=" + iface.Index + " [" + iface.Connectivity + "] 仍持有 0.0.0.0/0 默认路由，网关 " + iface.NextHop
                    + "，接口度量 " + iface.InterfaceMetric + "（与有网出口同权重时会抢走出站）。",
                FindingSeverity.Warning,
                new[] { "IP=" + string.Join(",", iface.Addresses), "网关=" + iface.NextHop }));

            if (snapshot.ActiveEgressIndex > 0 && iface.InterfaceMetric < HighInterfaceMetric)
            {
                actions.Add(new RepairAction(
                    "raise-metric:" + iface.Index, "调高 " + iface.Alias + " 接口度量",
                    "接口度量 " + iface.InterfaceMetric + " → " + HighInterfaceMetric + "（自动度量 Disabled），让有网出口胜出；不禁用网卡、不删路由",
                    ActionRisk.Elevated, "RaiseInterfaceMetric", "if=" + iface.Index,
                    iface.InterfaceMetric + "/" + iface.AutomaticMetric,
                    HighInterfaceMetric.ToString(), true, true,
                    new Dictionary<string, string>
                    {
                        ["Index"] = iface.Index.ToString(),
                        ["Alias"] = iface.Alias,
                        ["PreMetric"] = iface.InterfaceMetric.ToString(),
                        ["PreAuto"] = iface.AutomaticMetric,
                    }));
            }

            if (iface.DnsServers.Count > 0 && iface.Dhcp == "Enabled")
            {
                findings.Add(new Finding(
                    "STALE_DNS", "旧接口静态 DNS：" + iface.Alias,
                    "接口已不是上网出口，却仍静态指定 " + string.Join(", ", iface.DnsServers) + "；只剩这条链路时 DNS 会指向死路。",
                    FindingSeverity.Warning,
                    new[] { "DNS=" + string.Join(",", iface.DnsServers) }));
                if (deep)
                {
                    actions.Add(new RepairAction(
                        "reset-dns:" + iface.Index, "重置 " + iface.Alias + " 的 DNS 为自动获取",
                        "原静态 DNS " + string.Join(", ", iface.DnsServers) + " → DHCP 自动获取",
                        ActionRisk.Risky, "ResetInterfaceDns", "if=" + iface.Index,
                        string.Join(",", iface.DnsServers), "auto", true, false,
                        new Dictionary<string, string>
                        {
                            ["Index"] = iface.Index.ToString(),
                            ["Alias"] = iface.Alias,
                            ["PreDns"] = string.Join(",", iface.DnsServers),
                        }));
                }
            }
        }

        // ---- 3. Ghost devices (deep only) ----
        foreach (var ghost in snapshot.GhostDevices)
        {
            findings.Add(new Finding(
                "GHOST_DEVICE", "幽灵网卡：" + ghost.Name,
                "PnP 状态 " + ghost.Status + "，设备已不在但条目还在；插回设备时会自动重新枚举。",
                FindingSeverity.Info,
                new[] { ghost.InstanceId }));
            if (deep)
            {
                actions.Add(new RepairAction(
                    "remove-device:" + ghost.InstanceId, "移除幽灵网卡 " + ghost.Name,
                    "pnputil /remove-device；下次插回会自动重装驱动",
                    ActionRisk.Risky, "RemoveGhostDevice", ghost.InstanceId,
                    ghost.Status, "removed", true, false,
                    new Dictionary<string, string> { ["InstanceId"] = ghost.InstanceId, ["Name"] = ghost.Name }));
            }
        }

        // ---- 4. Cache refresh, only when something else actually changes ----
        if (actions.Count > 0)
        {
            actions.Add(new RepairAction(
                "flush-dns", "刷新 DNS / 邻居缓存",
                "Clear-DnsClientCache + ipconfig /flushdns，让新配置立刻生效",
                ActionRisk.Safe, "FlushDnsCache", "local", "", "", false, true,
                new Dictionary<string, string>()));
        }

        if (!snapshot.IsElevated && actions.Any(action => action.RequiresElevation))
        {
            findings.Add(new Finding(
                "ELEVATION_REQUIRED", "部分动作需要管理员权限",
                "当前进程不是管理员；接口度量 / WinHTTP / 机器级变量的动作会被跳过并如实报告，不会假装成功。",
                FindingSeverity.Warning,
                new[] { "IsElevated=false" }));
        }

        return new RepairPlan(snapshot.ObservedAt, snapshot, findings, actions);
    }
}
