using NetScope.Maintenance;

namespace NetScope.Maintenance.Tests;

/// <summary>
/// Behaviour tests for the real maintenance subsystem. These use fake probe/executor so no test
/// ever reads or writes the machine; the machine path is verified separately by the CLI smoke run.
/// </summary>
public sealed class MaintenanceTests
{
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-11-15T14:00:00+08:00");

    private static MaintenanceSnapshot Snapshot(
        bool elevated = true,
        int activeEgress = 21,
        int? working = null,
        int[]? listening = null,
        ProxyLayerSnapshot[]? layers = null,
        bool systemEnabled = false,
        string systemServer = "",
        int systemPort = 0,
        InterfaceSnapshot[]? interfaces = null,
        GhostDeviceSnapshot[]? ghosts = null)
        => new(
            FixedTime, elevated, activeEgress, "WLAN", working,
            listening ?? Array.Empty<int>(),
            layers ?? Array.Empty<ProxyLayerSnapshot>(),
            systemEnabled, systemServer, systemPort,
            false, 0,
            interfaces ?? Array.Empty<InterfaceSnapshot>(),
            ghosts ?? Array.Empty<GhostDeviceSnapshot>(),
            Array.Empty<string>());

    private static InterfaceSnapshot Iface(int index, string alias, bool hasRoute,
        string connectivity, int metric, bool staleDns = false)
        => new(index, alias, hasRoute, "192.168.38.1", metric, "Enabled", "Enabled", connectivity,
            new[] { "192.168.38.238" },
            staleDns ? new[] { "119.29.29.29", "223.5.5.5" } : Array.Empty<string>());

    private static ProxyLayerSnapshot DeadEnv(string name = "HTTP_PROXY", string scope = "User", int port = 7890)
        => new(scope, name, "http://127.0.0.1:" + port, port, false);

    [Fact]
    public void Dead_proxy_with_no_working_port_clears_env_and_disables_system_proxy()
    {
        var snapshot = Snapshot(
            layers: new[] { DeadEnv() },
            systemEnabled: true, systemServer: "127.0.0.1:7890", systemPort: 7890);

        var plan = MaintenancePlanner.Build(snapshot);

        Assert.Contains(plan.Findings, finding => finding.Code == "DEAD_PROXY");
        Assert.Contains(plan.Findings, finding => finding.Code == "SYSTEM_PROXY_DEAD");
        Assert.Contains(plan.Actions, action => action.Kind == "ClearProxyEnv");
        Assert.Contains(plan.Actions, action => action.Kind == "DisableSystemProxy");
        Assert.Contains(plan.Actions, action => action.Kind == "FlushDnsCache");
        Assert.All(plan.Actions.Where(action => action.Kind != "FlushDnsCache"),
            action => Assert.Equal(ActionRisk.Safe, action.Risk));
    }

    [Fact]
    public void A_working_port_aligns_stale_layers_instead_of_clearing_them()
    {
        var snapshot = Snapshot(
            working: 7892, listening: new[] { 7892 },
            layers: new[] { DeadEnv() });

        var plan = MaintenancePlanner.Build(snapshot);

        var align = Assert.Single(plan.Actions, action => action.Kind == "AlignProxyEnv");
        Assert.Equal("http://127.0.0.1:7892", align.Desired);
        Assert.DoesNotContain(plan.Actions, action => action.Kind == "ClearProxyEnv");
    }

    [Fact]
    public void Stale_egress_is_raised_once_and_the_change_is_idempotent()
    {
        var first = MaintenancePlanner.Build(Snapshot(
            interfaces: new[] { Iface(18, "以太网", true, "LocalNetwork", 35) }));
        Assert.Contains(first.Actions, action => action.Kind == "RaiseInterfaceMetric");

        var second = MaintenancePlanner.Build(Snapshot(
            interfaces: new[] { Iface(18, "以太网", true, "LocalNetwork", MaintenancePlanner.HighInterfaceMetric) }));
        Assert.DoesNotContain(second.Actions, action => action.Kind == "RaiseInterfaceMetric");
    }

    [Fact]
    public void No_known_internet_egress_never_changes_any_interface_metric()
    {
        var plan = MaintenancePlanner.Build(Snapshot(
            activeEgress: 0,
            interfaces: new[] { Iface(18, "以太网", true, "LocalNetwork", 35) }));

        Assert.DoesNotContain(plan.Actions, action => action.Kind == "RaiseInterfaceMetric");
        Assert.Contains(plan.Findings, finding => finding.Code == "STALE_EGRESS");
    }

    [Fact]
    public void Risky_actions_only_appear_with_deep_and_are_never_selected_by_default()
    {
        var snapshot = Snapshot(
            ghosts: new[] { new GhostDeviceSnapshot("USB\\VID_1234", "Remote NDIS Device", "Unknown") },
            interfaces: new[] { Iface(18, "以太网", true, "LocalNetwork", 35, staleDns: true) });

        var shallow = MaintenancePlanner.Build(snapshot, deep: false);
        Assert.DoesNotContain(shallow.Actions, action => action.Risk == ActionRisk.Risky);
        Assert.Contains(shallow.Findings, finding => finding.Code == "STALE_DNS");
        Assert.Contains(shallow.Findings, finding => finding.Code == "GHOST_DEVICE");

        var deep = MaintenancePlanner.Build(snapshot, deep: true);
        var risky = deep.Actions.Where(action => action.Risk == ActionRisk.Risky).ToArray();
        Assert.Equal(2, risky.Length);
        Assert.All(risky, action => Assert.False(action.SelectedByDefault));
    }

    [Fact]
    public async Task Dry_run_never_invokes_the_executor()
    {
        var executor = new FakeExecutor();
        var service = Service(Snapshot(layers: new[] { DeadEnv() }), executor);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, true, CancellationToken.None);

        Assert.True(report.DryRun);
        Assert.Equal(0, report.Applied);
        Assert.Empty(executor.Applied);
        Assert.Null(report.JournalPath);
    }

    [Fact]
    public async Task Actions_requiring_elevation_are_skipped_not_faked_when_not_admin()
    {
        var executor = new FakeExecutor();
        var service = Service(Snapshot(
            elevated: false,
            interfaces: new[] { Iface(18, "以太网", true, "LocalNetwork", 35) }), executor);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.DoesNotContain(executor.Applied, action => action.Kind == "RaiseInterfaceMetric");
        var skipped = Assert.Single(report.Results,
            result => result.ActionId.StartsWith("raise-metric", StringComparison.Ordinal));
        Assert.Contains("管理员", skipped.Error);
        Assert.Contains(report.Messages, message => message.Contains("管理员"));
    }

    [Fact]
    public async Task Applied_changes_are_journaled_and_restore_replays_the_oldest_values()
    {
        var executor = new FakeExecutor();
        using var temp = new TempJournal();
        var service = Service(Snapshot(
            layers: new[] { DeadEnv("HTTP_PROXY"), DeadEnv("HTTPS_PROXY") },
            systemEnabled: true, systemServer: "127.0.0.1:7890", systemPort: 7890), executor, temp.Store);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.True(report.Applied > 0);
        Assert.Equal(0, report.Failed);
        Assert.NotNull(report.JournalPath);
        Assert.True(temp.Store.HasBackup);
        Assert.Equal(1, executor.Broadcasts);
        var journaled = temp.Store.Load()!.Entries.Count;

        var restore = await service.RestoreAsync(false, CancellationToken.None);
        Assert.Equal(0, restore.Failed);
        Assert.Equal(journaled, restore.Restored);
        Assert.Equal(journaled, executor.Restored.Count); // every journaled entry replayed once
        Assert.False(temp.Store.HasBackup);
    }

    [Fact]
    public void Journal_merge_keeps_the_earliest_old_value_for_the_same_target()
    {
        using var temp = new TempJournal();
        var now = FixedTime;
        temp.Store.Save(new[]
        {
            new JournalEntry("EnvVar|User|HTTP_PROXY", "ClearProxyEnv", "User", "HTTP_PROXY",
                "http://127.0.0.1:7890", null, "", now),
        });
        temp.Store.Save(new[]
        {
            new JournalEntry("EnvVar|User|HTTP_PROXY", "AlignProxyEnv", "User", "HTTP_PROXY",
                "http://127.0.0.1:7892", "http://127.0.0.1:7893", "", now.AddMinutes(1)),
        });

        var entry = Assert.Single(temp.Store.Load()!.Entries);
        Assert.Equal("http://127.0.0.1:7890", entry.Old);
    }

    [Fact]
    public async Task Proxy_off_previews_clear_and_disable_without_writing()
    {
        var executor = new FakeExecutor();
        var service = Service(Snapshot(
            working: 7890, listening: new[] { 7890 },
            layers: new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, true) },
            systemEnabled: true, systemServer: "127.0.0.1:7890", systemPort: 7890), executor);

        var report = await service.ProxyOffAsync(true, CancellationToken.None);

        Assert.Empty(executor.Applied);
        Assert.Contains(report.Results, result => result.Title.Contains("关闭系统代理"));
        Assert.Contains(report.Results, result => result.Title.Contains("清除"));
        Assert.True(report.DryRun);
    }

    [Fact]
    public async Task Proxy_on_refuses_when_no_port_can_serve_as_a_proxy()
    {
        var executor = new FakeExecutor();
        var service = Service(Snapshot(), executor);

        var report = await service.ProxyOnAsync(7890, false, CancellationToken.None);

        Assert.Equal(1, report.Failed);
        Assert.Empty(executor.Applied);
        Assert.Contains(report.Messages, message => message.Contains("没有任何端口"));
    }

    [Fact]
    public async Task Failed_tun_postcheck_auto_restores_metric_and_removes_journal_entry()
    {
        var executor = new FakeExecutor();
        using var temp = new TempJournal();
        var snapshot = Snapshot() with { TunInterfaces = new[] { Tun() } };
        var service = new QuickRepairService(new FakeProbe(snapshot), executor, temp.Store,
            postCheck: new FakePostCheck(new PostCheckObservation(21, true, true, true, true, false)));
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.NotNull(report.PostCheck);
        Assert.Equal("A/B", report.PostCheck!.CauseClass);
        Assert.Contains(executor.Restored, e => e.Kind == "FixTunPriority");
        Assert.False(temp.Store.HasBackup);
        Assert.Contains(report.Messages, m => m.Contains("自动还原"));
    }

    [Fact]
    public async Task Postcheck_retries_up_to_three_times_then_succeeds()
    {
        var executor = new FakeExecutor();
        using var temp = new TempJournal();
        var postCheck = new FakePostCheck(
            new PostCheckObservation(21, true, false, true, true, false),
            new PostCheckObservation(21, true, false, true, true, false),
            GreenObs());
        var service = new QuickRepairService(new FakeProbe(Snapshot(layers: new[] { DeadEnv() })), executor, temp.Store, postCheck);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.Equal(3, postCheck.Calls);
        Assert.True(report.PostCheck!.Success);
    }

    [Fact]
    public async Task Cause_c_never_rolls_back_the_tun_metric()
    {
        var executor = new FakeExecutor();
        using var temp = new TempJournal();
        var snapshot = Snapshot() with { TunInterfaces = new[] { Tun() } };
        var service = new QuickRepairService(new FakeProbe(snapshot), executor, temp.Store,
            postCheck: new FakePostCheck(new PostCheckObservation(7, false, true, false, false, false)));
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.Equal("C", report.PostCheck!.CauseClass);
        Assert.DoesNotContain(executor.Restored, e => e.Kind == "FixTunPriority");
        Assert.True(temp.Store.HasBackup);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7890", 7890)]
    [InlineData("127.0.0.1:10809/", 10809)]
    [InlineData("socks5://127.0.0.1:1080", 1080)]
    [InlineData("direct", 0)]
    [InlineData("", 0)]
    public void Port_parsing_handles_the_shapes_seen_on_this_machine(string value, int expected)
        => Assert.Equal(expected, WindowsMaintenanceProbe.ParsePort(value));

    [Fact]
    public void Tun_interfaces_are_identified_by_alias_address_or_wintun_description()
    {
        // 直接测静态识别逻辑：别名命中 / 198.18 地址命中 / Wintun 描述命中；物理网卡不误判。
        Assert.True(WindowsMaintenanceProbe.IsTun("Mihomo", Array.Empty<string>(), ""));
        Assert.True(WindowsMaintenanceProbe.IsTun("以太网 2", new[] { "198.18.0.1" }, ""));
        Assert.True(WindowsMaintenanceProbe.IsTun("本地连接* 9", Array.Empty<string>(), "Wintun Userspace Tunnel"));
        Assert.False(WindowsMaintenanceProbe.IsTun("WLAN", new[] { "192.168.1.5" }, "Intel Wi-Fi 6"));
        Assert.False(WindowsMaintenanceProbe.IsTun("VMware Network Adapter", new[] { "198.18.0.9" }, ""));
    }

    [Fact]
    public void Machine_facts_parse_tun_fields_and_port_owners()
    {
        const string json = """
        {"Interfaces":[{"Index":7,"Alias":"Mihomo","State":"Connected","Metric":4225,"Auto":"Enabled","Dhcp":"Disabled","Ignore":"Enabled","Mtu":9000}],
         "Routes":[{"Index":7,"NextHop":"0.0.0.0"}],"Profiles":[],"Addrs":[{"Index":7,"Address":"198.18.0.1"}],
         "Dns":[],"Ghosts":[],"BestRouteIndex":7,
         "Adapters":[{"Index":7,"Desc":"Wintun Userspace Tunnel"}],
         "Owners":[{"Port":7890,"Pid":1234,"Name":"verge-mihomo"}],
         "Src4":"192.168.1.5","Src5":"192.168.1.5"}
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var facts = WindowsMaintenanceProbe.MachineFacts.Parse(doc.RootElement);
        var iface = Assert.Single(facts.Interfaces);
        Assert.Equal("Enabled", iface.IgnoreDefaultRoutes);
        Assert.Equal(9000, iface.NlMtu);
        var owner = Assert.Single(facts.Owners);
        Assert.Equal(7890, owner.Port);
        Assert.Equal("verge-mihomo", owner.ProcessName);
        Assert.Equal("192.168.1.5", facts.SourceAddress4);
    }

    private static TunInterfaceSnapshot Tun(int index = 7, int metric = 4225, string auto = "Enabled", string ignore = "Disabled")
        => new(index, "Mihomo", metric, auto, ignore, 9000, new[] { "198.18.0.1" });

    [Fact]
    public void Tun_with_automatic_metric_gets_a_fix_action_with_three_field_backup()
    {
        var snapshot = Snapshot() with { TunInterfaces = new[] { Tun() } };
        var plan = MaintenancePlanner.Build(snapshot);
        Assert.Contains(plan.Findings, f => f.Code == "TUN_PRIORITY");
        var action = Assert.Single(plan.Actions, a => a.Kind == "FixTunPriority");
        Assert.Equal("fix-tun-priority:7", action.Id);
        Assert.Equal("4225/Enabled/Disabled", action.Current);
        Assert.Equal("1/Disabled/Disabled", action.Desired);
        Assert.True(action.RequiresElevation);
        Assert.True(action.SelectedByDefault);
        Assert.Equal("Enabled", action.Parameters["PreAuto"]);
    }

    [Fact]
    public void Tun_already_at_metric_one_with_ignore_off_gets_no_action()
    {
        var snapshot = Snapshot() with { TunInterfaces = new[] { Tun(metric: 1, auto: "Disabled") } };
        var plan = MaintenancePlanner.Build(snapshot);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == "FixTunPriority");
        Assert.DoesNotContain(plan.Findings, f => f.Code == "TUN_PRIORITY");
    }

    [Fact]
    public void Tun_with_ignore_default_routes_enabled_is_fixed_even_at_metric_one()
    {
        var snapshot = Snapshot() with { TunInterfaces = new[] { Tun(metric: 1, auto: "Disabled", ignore: "Enabled") } };
        var plan = MaintenancePlanner.Build(snapshot);
        var action = Assert.Single(plan.Actions, a => a.Kind == "FixTunPriority");
        Assert.Equal("Enabled", action.Parameters["PreIgnore"]);
    }

    [Fact]
    public void Two_tuns_are_reported_as_conflict()
    {
        var snapshot = Snapshot() with { TunInterfaces = new[] { Tun(7), Tun(9) } };
        var plan = MaintenancePlanner.Build(snapshot);
        Assert.Contains(plan.Findings, f => f.Code == "MULTIPLE_TUN");
    }

    [Fact]
    public void Candidate_port_owned_by_non_client_process_is_named()
    {
        var snapshot = Snapshot(listening: new[] { 7890 }) with
        {
            PortOwners = new[] { new PortOwnerSnapshot(7890, 4321, "SomeUpdater") },
        };
        var plan = MaintenancePlanner.Build(snapshot);
        var finding = Assert.Single(plan.Findings, f => f.Code == "PORT_CONFLICT");
        Assert.Contains("SomeUpdater", finding.Detail);
        Assert.DoesNotContain(plan.Actions, a => a.Id.Contains("port", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Two_proxy_clients_listening_are_named_without_action()
    {
        var snapshot = Snapshot(listening: new[] { 7890, 7892 }) with
        {
            PortOwners = new[] { new PortOwnerSnapshot(7890, 1, "verge-mihomo"), new PortOwnerSnapshot(7892, 2, "Nice加速") },
        };
        var plan = MaintenancePlanner.Build(snapshot);
        Assert.Contains(plan.Findings, f => f.Code == "PORT_CONFLICT" && f.Detail.Contains("verge-mihomo") && f.Detail.Contains("Nice加速"));
        Assert.Empty(plan.Actions);
    }

    private static RepairAction TunAction(string preIgnore = "Disabled")
        => new("fix-tun-priority:7", "修复 TUN 优先级（Mihomo）", "", ActionRisk.Elevated, "FixTunPriority",
            "if=7", "4225/Enabled/" + preIgnore, "1/Disabled/Disabled", true, true,
            new Dictionary<string, string> { ["Index"] = "7", ["PreMetric"] = "4225", ["PreAuto"] = "Enabled", ["PreIgnore"] = preIgnore });

    [Fact]
    public async Task Fix_tun_priority_sets_metric_one_and_disables_ignore_only_when_enabled()
    {
        var runner = new FakeCommandRunner();
        var executor = new WindowsMaintenanceExecutor(runner);

        var result = await executor.ApplyAsync(TunAction(preIgnore: "Enabled"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Contains(runner.Scripts, s => s.Contains("-InterfaceMetric 1") && s.Contains("-AutomaticMetric Disabled"));
        Assert.Contains(runner.Commands, c => c.File == "netsh" && c.Args.Contains("ignoredefaultroutes=disabled"));
    }

    [Fact]
    public async Task Fix_tun_priority_skips_netsh_when_ignore_already_disabled()
    {
        var runner = new FakeCommandRunner();
        var executor = new WindowsMaintenanceExecutor(runner);

        var result = await executor.ApplyAsync(TunAction(), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.DoesNotContain(runner.Commands, c => c.Args.Contains("ignoredefaultroutes=disabled"));
    }

    [Fact]
    public async Task Fix_tun_priority_rolls_back_metric_when_netsh_fails()
    {
        var runner = new FakeCommandRunner();
        runner.Results.Enqueue(new CommandResult(0, "OK", ""));
        runner.Results.Enqueue(new CommandResult(1, "", "需要管理员"));
        runner.Results.Enqueue(new CommandResult(0, "OK", ""));
        var executor = new WindowsMaintenanceExecutor(runner);

        var result = await executor.ApplyAsync(TunAction(preIgnore: "Enabled"), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("netsh", result.Error);
        Assert.Contains("已还原度量", result.Error);
        Assert.Contains(runner.Scripts, s => s.Contains("-InterfaceIndex 7") && s.Contains("-AutomaticMetric Enabled"));
        Assert.Equal(2, runner.Scripts.Count);
    }

    [Fact]
    public async Task Fix_tun_priority_rolls_back_fixed_metric_when_pre_auto_was_disabled()
    {
        var runner = new FakeCommandRunner();
        runner.Results.Enqueue(new CommandResult(0, "OK", ""));
        runner.Results.Enqueue(new CommandResult(1, "", "拒绝访问"));
        runner.Results.Enqueue(new CommandResult(0, "OK", ""));
        var executor = new WindowsMaintenanceExecutor(runner);
        var action = TunAction(preIgnore: "Enabled") with
        {
            Parameters = new Dictionary<string, string>
            {
                ["Index"] = "7", ["PreMetric"] = "4225", ["PreAuto"] = "Disabled", ["PreIgnore"] = "Enabled",
            },
        };

        var result = await executor.ApplyAsync(action, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains(runner.Scripts, s => s.Contains("-InterfaceMetric 4225") && s.Contains("-AutomaticMetric Disabled"));
    }

    [Fact]
    public async Task Restore_tun_priority_replays_the_original_three_fields()
    {
        var runner = new FakeCommandRunner();
        var executor = new WindowsMaintenanceExecutor(runner);
        var entry = new JournalEntry("TunPriority|7", "FixTunPriority", "", "if=7",
            "4225/Enabled/Enabled", "1/Disabled/Disabled", "7", DateTimeOffset.Now);

        var (ok, _) = await executor.RestoreAsync(entry, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(runner.Scripts, s => s.Contains("-AutomaticMetric Enabled"));
        Assert.Contains(runner.Commands, c => c.File == "netsh" && c.Args.Contains("ignoredefaultroutes=enabled"));
    }

    [Fact]
    public async Task Flush_dns_also_clears_arp_cache()
    {
        var runner = new FakeCommandRunner();
        var executor = new WindowsMaintenanceExecutor(runner);
        var action = new RepairAction("flush-dns", "刷新 DNS / 邻居缓存", "", ActionRisk.Safe, "FlushDnsCache",
            "local", "", "", false, true, new Dictionary<string, string>());

        await executor.ApplyAsync(action, CancellationToken.None);

        Assert.Contains(runner.Commands, c => c.File == "netsh" && c.Args.SequenceEqual(new[] { "interface", "ip", "delete", "arpcache" }));
    }

    private sealed class StubHandler(System.Net.HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.OK, true)]
    [InlineData(System.Net.HttpStatusCode.NoContent, true)]
    [InlineData(System.Net.HttpStatusCode.BadGateway, false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, false)]
    public async Task Only_200_204_from_gstatic_counts_as_a_working_proxy(System.Net.HttpStatusCode code, bool expected)
        => Assert.Equal(expected, await ProxyPortTester.IsWorkingAsync(7890, new StubHandler(code), CancellationToken.None));

    [Fact]
    public async Task Canned_machine_json_from_the_runner_wires_tun_owners_and_best_route()
    {
        // CaptureAsync 还会读注册表/环境变量并做真实 TCP/HTTP 探测，无法完全 fake；
        // 这里钉住 runner JSON → MachineFacts.Parse → TUN 识别投影这段纯接线，全链路留给 Task 10 smoke。
        const string json = """
        {"Interfaces":[{"Index":7,"Alias":"Mihomo","State":"Connected","Metric":4225,"Auto":"Enabled","Dhcp":"Disabled","Ignore":"Enabled","Mtu":9000},
                       {"Index":21,"Alias":"WLAN","State":"Connected","Metric":35,"Auto":"Enabled","Dhcp":"Enabled","Ignore":"Disabled","Mtu":1500}],
         "Routes":[{"Index":21,"NextHop":"192.168.1.1"}],
         "Profiles":[{"Index":21,"Connectivity":"Internet"}],
         "Addrs":[{"Index":7,"Address":"198.18.0.1"},{"Index":21,"Address":"192.168.1.5"}],
         "Dns":[],"Ghosts":[],"BestRouteIndex":21,
         "Adapters":[{"Index":7,"Desc":"Wintun Userspace Tunnel"}],
         "Owners":[{"Port":7890,"Pid":1234,"Name":"verge-mihomo"}],
         "Src4":"192.168.1.5","Src5":""}
        """;
        var runner = new FakeCommandRunner();
        runner.Results.Enqueue(new CommandResult(0, json, ""));

        var read = await runner.RunJsonAsync("capture", CancellationToken.None);
        using var doc = System.Text.Json.JsonDocument.Parse(read.StandardOutput);
        var facts = WindowsMaintenanceProbe.MachineFacts.Parse(doc.RootElement);

        Assert.Equal(21, facts.BestRouteIndex);
        var tun = Assert.Single(facts.Interfaces, i => i.Index == 7);
        Assert.Equal(4225, tun.Metric);
        Assert.Equal("Enabled", tun.Auto);
        Assert.Equal("Enabled", tun.IgnoreDefaultRoutes);
        Assert.Equal(9000, tun.NlMtu);
        Assert.True(WindowsMaintenanceProbe.IsTun(tun.Alias,
            facts.Addrs.Where(a => a.Index == tun.Index).Select(a => a.Address).ToArray(),
            facts.Adapters.First(a => a.Index == tun.Index).Desc));
        Assert.False(WindowsMaintenanceProbe.IsTun("WLAN",
            facts.Addrs.Where(a => a.Index == 21).Select(a => a.Address).ToArray(), ""));
        var owner = Assert.Single(facts.Owners);
        Assert.Equal(7890, owner.Port);
        Assert.Equal(1234, owner.Pid);
        Assert.Equal("verge-mihomo", owner.ProcessName);
        Assert.Equal("192.168.1.5", facts.SourceAddress4);
    }

    private static PostCheckObservation GreenObs(int route = 21) => new(route, true, true, true, true, false);

    private static QuickRepairService Service(MaintenanceSnapshot snapshot, FakeExecutor executor,
        MaintenanceJournalStore? store = null)
        => new(new FakeProbe(snapshot), executor, store ?? new MaintenanceJournalStore(TempJournal.NewPath()),
            postCheck: new FakePostCheck(GreenObs()));

    private sealed class FakeProbe(MaintenanceSnapshot snapshot) : IMaintenanceProbe
    {
        public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken cancellationToken)
            => Task.FromResult(snapshot);
    }

    private sealed class FakePostCheck(params PostCheckObservation[] observations) : IPostCheckProbe
    {
        private readonly Queue<PostCheckObservation> _queue = new(observations);
        public int Calls { get; private set; }
        public Task<PostCheckObservation> ObserveAsync(CancellationToken ct)
        { Calls++; return Task.FromResult(_queue.Count > 1 ? _queue.Dequeue() : _queue.Peek()); }
    }

    private sealed class FakeExecutor : IMaintenanceExecutor
    {
        public List<RepairAction> Applied { get; } = new();
        public List<JournalEntry> Restored { get; } = new();
        public int Broadcasts { get; private set; }

        public Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken cancellationToken)
        {
            Applied.Add(action);
            return Task.FromResult(new ActionResult(action.Id, action.Title, true,
                action.Current, action.Desired, null));
        }

        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken cancellationToken)
        {
            Restored.Add(entry);
            return Task.FromResult((true, "ok"));
        }

        public Task BroadcastEnvironmentAsync(CancellationToken cancellationToken)
        {
            Broadcasts++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public List<string> Scripts { get; } = new();
        public List<(string File, string[] Args)> Commands { get; } = new();
        public Queue<CommandResult> Results { get; } = new();

        public Task<CommandResult> RunPowerShellAsync(string script, CancellationToken ct)
        { Scripts.Add(script); return Task.FromResult(Next()); }
        public Task<CommandResult> RunAsync(string fileName, string[] arguments, CancellationToken ct)
        { Commands.Add((fileName, arguments)); return Task.FromResult(Next()); }
        public Task<CommandResult> RunJsonAsync(string script, CancellationToken ct) => RunPowerShellAsync(script, ct);
        private CommandResult Next() => Results.Count > 0 ? Results.Dequeue() : new CommandResult(0, "OK", "");
    }

    private sealed class TempJournal : IDisposable
    {
        public TempJournal() => Store = new MaintenanceJournalStore(NewPath());

        public MaintenanceJournalStore Store { get; }

        public static string NewPath() => Path.Combine(Path.GetTempPath(),
            "netscope-maintenance-test-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            if (File.Exists(Store.Path)) File.Delete(Store.Path);
        }
    }
}
