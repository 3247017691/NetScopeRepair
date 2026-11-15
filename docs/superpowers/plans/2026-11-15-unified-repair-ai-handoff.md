# 统一修复引擎与 AI 接力闭环 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 删除旧 TUN 链路，把桌面 5 脚本的全部修复能力原生并入 NetScope.Maintenance 唯一引擎，修复失败进现有日志管线并一键生成 AI 交接包。

**Architecture:** 体检 Probe → 计划 Planner(纯逻辑) → 执行 Executor(+journal) → 修后自检 PostCheck；失败事件经 IRepairEventSink 进 NDJSON/SQLite；HandoffExporter 复用脱敏组件产出交接包。零 .ps1 依赖，全部 C#。

**Tech Stack:** .NET 10 (net10.0-windows)、WPF、xUnit、System.Text.Json、SQLite 投影（现有）。

**Spec:** `docs/superpowers/specs/2026-11-15-unified-repair-ai-handoff-design.md`（已批准，commit 14b41c4）

## Global Constraints

- 构建 0 警告 0 错误；每个任务结束 `dotnet build NetScopeRepair.slnx` + `dotnet test` 全绿再 commit。
- 只有 `status=ok` 算成功；读不到就说读不到；失败/跳过如实报告，不假装成功。
- planner 纯逻辑无 I/O；executor 只跑固定命令，字符串值经 `WindowsCommandRunner.PsQuote`；不新增 fail-closed 闸门/签名 broker。
- 所有系统写入进 journal 可回滚；不自动申请 UAC；TUN 动作只动 TUN 网卡，绝不碰物理网卡。
- 零外部脚本依赖：不携带、不调用任何 .ps1 文件（netsh/ipconfig/pnputil/PowerShell cmdlet 是系统接口，允许）。
- 事件契约不变：复用 `EventType.RepairEvent`（线名 `repair_event`），不改 contracts schema。
- UI 文案中文；测试 xUnit，fake 注入，不在单测里读写真实机器/网络。
- 工作目录 `D:\Projects\NetScopeRepair`；Git Bash 下 dotnet 命令直接可用。

## 文件结构（新增/修改一览）

| 文件 | 责任 |
|---|---|
| `src/NetScope.Maintenance/MaintenanceModels.cs` | +TunInterfaceSnapshot/PortOwnerSnapshot；快照 +3 字段；RepairReport +PostCheck |
| `src/NetScope.Maintenance/ICommandRunner.cs`（新） | 命令运行抽象，测试可注入 |
| `src/NetScope.Maintenance/ProxyPortTester.cs`（新） | 真打判定：gstatic 204 返回 200/204 才算可用 |
| `src/NetScope.Maintenance/WindowsMaintenanceProbe.cs` | +TUN/端口归属/源地址/config 扫描/BestRouteIndex |
| `src/NetScope.Maintenance/MaintenancePlanner.cs` | +TUN_PRIORITY/PORT_CONFLICT/MULTIPLE_TUN |
| `src/NetScope.Maintenance/WindowsMaintenanceExecutor.cs` | +FixTunPriority 执行与回滚；FlushDns +ARP |
| `src/NetScope.Maintenance/MaintenanceJournalStore.cs` | +Remove(key)（自动回滚后摘除条目） |
| `src/NetScope.Maintenance/PostCheck.cs`（新） | PostCheckObservation/Result + 纯逻辑分类器 A~E |
| `src/NetScope.Maintenance/WindowsPostCheckProbe.cs`（新） | 真实自检探测（可注入） |
| `src/NetScope.Maintenance/RepairEvent.cs`（新） | RepairEvent 记录 + IRepairEventSink |
| `src/NetScope.Maintenance/QuickRepairService.cs` | +sink/postcheck 集成 + MarkUnresolvedAsync |
| `src/NetScope.Storage/RepairSessionEvents.cs`（新） | RepairEvent → EventEnvelope |
| `src/NetScope.Storage/NdjsonRepairEventSink.cs`（新） | CLI 用文件 sink |
| `src/NetScope.Storage/Export/HandoffExporter.cs`（新） | 交接包 zip + HandoffPrompt |
| `src/NetScope.Desktop/Services/SessionRepairEventSink.cs`（新） | 会话 sink |
| `src/NetScope.Desktop/Services/ObservationSession.cs` | +AppendRepairEventsAsync |
| `src/NetScope.Desktop/ViewModels/QuickRepairViewModel.cs` | +交给 AI/还是没好/诊断预选 |
| `src/NetScope.Desktop/ViewModels/DiagnosisRepairMap.cs`（新） | 规则→动作映射（UI 层） |
| `src/NetScope.Desktop/ViewModels/{Diagnoses,Home,Main}ViewModel.cs` | 联动接线 |
| `src/NetScope.Cli/Program.cs` | +handoff 命令；sink 接线 |
| `tests/Maintenance/{MaintenanceTests,PostCheckTests,RepairEventTests}.cs` | 引擎测试 |
| `tests/Export/HandoffExporterTests.cs`（新） | 交接包测试 |
| `tests/Desktop/QuickRepairIntegrationTests.cs`（新） | 联动/按钮测试 |

---

### Task 0: 基线 commit（用户确认门禁）

当前 M3 快速修复全部未提交。删除旧链路前必须先落基线，删错可整体找回。

**Files:** 无代码改动；仅 git。

- [ ] **Step 1: 审查待提交内容**

Run: `git -C "D:/Projects/NetScopeRepair" status --short`
逐条确认：src/ tests/ docs/ README.md NetScopeRepair.slnx 运行NetScope*.cmd 为 M3 工作；无 .env/密钥/凭据类文件。artifacts/ 与 bin/obj 由 .gitignore 处理，若出现在未跟踪列表则不 add。

- [ ] **Step 2: 请用户确认**

向用户展示待提交清单摘要，明确问：「是否把当前 M3 现状提交为基线 commit？」**得到明确同意前不执行 Step 3。**

- [ ] **Step 3: 提交基线**

```bash
git -C "D:/Projects/NetScopeRepair" add src tests docs README.md NetScopeRepair.slnx 运行NetScope.cmd 运行NetScope只读试用版.cmd
git -C "D:/Projects/NetScopeRepair" commit -m "feat(m3): quick repair baseline before unified rewrite"
git -C "D:/Projects/NetScopeRepair" status --short
```

Expected: commit 成功；status 只剩刻意不提交的项。

---

### Task 1: 删除旧 TUN 链路，套件恢复绿

**Files:**
- Delete: `src/NetScope.Repair/`（整个目录）、`src/NetScope.Broker/`（整个目录）
- Delete: `src/NetScope.Application/Repairing.cs`、`RepairVerification.cs`、`SyntheticRepairPreview.cs`
- Delete: `src/NetScope.Desktop/Views/RepairView.xaml`、`RepairView.xaml.cs`、`ViewModels/RepairViewModel.cs`
- Delete: `tests/WindowsIntegration/`（整个目录——21 个源文件全部属旧链路，含 experiments/platform-experiment-manifest.json）
- Delete: `tests/Desktop/T16RepairUiTests.cs`、`T16CompositionTests.cs`、`T16RepairVerificationTests.cs`
- Modify: `NetScopeRepair.slnx`（移除 Repair/Broker/WindowsIntegration 三个 Project 行及 WindowsIntegration Folder）
- Modify: `src/NetScope.Application/NetScope.Application.csproj:10`（移除 Repair 引用）
- Modify: `src/NetScope.Desktop/Services/Composition.cs`（移除 IRepairGateway 参数/属性/合成预览接线）
- Modify: `src/NetScope.Desktop/ViewModels/MainViewModel.cs:39,57-58,88`（移除 Repair 属性与导航项）
- Modify: `src/NetScope.Desktop/App.xaml:25-27`（移除 RepairViewModel DataTemplate）
- Modify: `tests/Desktop/T16BusinessObservationTests.cs`（删 2 个网关依赖测试，保留业务观察测试）
- Modify: `tests/Desktop/T16TestSupport.cs`（移除网关 fake/辅助，保留其余）

**Interfaces:**
- Consumes: 无（纯删除）。
- Produces: 解决方案少 3 个工程；`AppServices` 不再有 `Repair` 属性；`MainViewModel` 不再有 `Repair` 属性。后续任务不再引用任何 `NetScope.Repair`/`NetScope.Broker`/`IRepairGateway` 类型。

- [ ] **Step 1: 删除工程与文件**

```bash
cd "D:/Projects/NetScopeRepair"
git rm -r -q src/NetScope.Repair src/NetScope.Broker tests/WindowsIntegration
git rm -q src/NetScope.Application/Repairing.cs src/NetScope.Application/RepairVerification.cs src/NetScope.Application/SyntheticRepairPreview.cs
git rm -q src/NetScope.Desktop/Views/RepairView.xaml src/NetScope.Desktop/Views/RepairView.xaml.cs src/NetScope.Desktop/ViewModels/RepairViewModel.cs
git rm -q tests/Desktop/T16RepairUiTests.cs tests/Desktop/T16CompositionTests.cs tests/Desktop/T16RepairVerificationTests.cs
```

- [ ] **Step 2: 改 slnx 与 csproj**

`NetScopeRepair.slnx`：删除第 5 行（Broker）、第 13 行（Repair）、第 41-43 行（WindowsIntegration Folder 块）。
`src/NetScope.Application/NetScope.Application.csproj`：删除 `<ProjectReference Include="..\NetScope.Repair\NetScope.Repair.csproj" />`。

- [ ] **Step 3: 改 Composition.cs**

`AppServices` 构造函数删去 `IRepairGateway? repair = null` 参数与 `Repair = repair ?? new UnavailableRepairGateway();`；删去 `public IRepairGateway Repair { get; }` 属性及其注释。`CreateDefault`（约 85-95 行）删去整个 `IRepairGateway repair = synthetic ? ... : ...;` 块，`new AppServices(...)` 调用去掉末尾 `repair` 实参。删除文件顶部对 Application repair 类型的 using（如有）。

- [ ] **Step 4: 改 MainViewModel.cs 与 App.xaml**

MainViewModel：删 `Repair = new RepairViewModel(services.Repair, services.Clock);`（39 行）、`public RepairViewModel Repair { get; }`（88 行）、Sections 里 `new NavSection("修复", "TUN 深度计划（门禁 Disabled）", Repair, ...)` 整项（57-58 行）。
App.xaml：删 `{x:Type vm:RepairViewModel}` 的 DataTemplate 块（25-27 行）。

- [ ] **Step 5: 修剪 T16 残留测试**

`T16BusinessObservationTests.cs`：删除 `A_business_observation_never_calls_the_repair_gateway` 与 `The_synthetic_demo_preview_has_selectable_changed_actions_and_a_no_op_field` 两个测试方法及其专属 using；保留业务观察窗口测试。
`T16TestSupport.cs`：删除引用 IRepairGateway/RepairGateView 的辅助成员；若整个文件只剩网关辅助则整文件删除（git rm）。

- [ ] **Step 6: 构建并修剩余引用**

Run: `dotnet build NetScopeRepair.slnx`
Expected: 首次可能报剩余引用错误（如 tests/Desktop 其他文件 using NetScope.Repair）。逐个删除/改写报错引用，直到 **0 警告 0 错误**。禁止用 `#if` 或注释屏蔽代码。

- [ ] **Step 7: 全套件测试**

Run: `dotnet test NetScopeRepair.slnx`
Expected: 全绿（旧链路测试已随工程删除；剩余测试不依赖旧类型）。

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor!: remove legacy TUN repair chain (Repair/Broker/gateway)

统一重写第一步：旧链路整体删除，Maintenance 成为唯一修复引擎。
基线 commit 可整体找回。G3 历史报告保留。"
```

---

### Task 2: 命令运行抽象 + 体检采集扩展（TUN/端口归属/源地址/config 扫描）

**Files:**
- Create: `src/NetScope.Maintenance/ICommandRunner.cs`
- Modify: `src/NetScope.Maintenance/WindowsCommandRunner.cs`（实现接口）
- Modify: `src/NetScope.Maintenance/MaintenanceModels.cs`（+2 记录类型；快照 +3 可选字段）
- Modify: `src/NetScope.Maintenance/WindowsMaintenanceProbe.cs`
- Test: `tests/Maintenance/MaintenanceTests.cs`（更新 Snapshot 辅助 + 新采集测试）

**Interfaces:**
- Consumes: Task 1 后的解决方案。
- Produces:
  - `public interface ICommandRunner { Task<CommandResult> RunPowerShellAsync(string script, CancellationToken ct); Task<CommandResult> RunAsync(string fileName, string[] arguments, CancellationToken ct); Task<CommandResult> RunJsonAsync(string script, CancellationToken ct); }`
  - `public sealed record TunInterfaceSnapshot(int Index, string Alias, int InterfaceMetric, string AutomaticMetric, string IgnoreDefaultRoutes, int NlMtu, IReadOnlyList<string> Addresses);`
  - `public sealed record PortOwnerSnapshot(int Port, int Pid, string ProcessName);`
  - `MaintenanceSnapshot` 末尾追加三个可选参数：`IReadOnlyList<TunInterfaceSnapshot>? TunInterfaces = null, IReadOnlyList<PortOwnerSnapshot>? PortOwners = null, int BestRouteIndex = 0`（现有 15 参调用全部保持编译）。
  - `WindowsMaintenanceProbe.TunAliasPattern` 常量（planner 复用）。
  - 测试 fake：`FakeCommandRunner`（本任务 Step 1 定义，后续任务复用）。

- [ ] **Step 1: 写失败测试**

在 `tests/Maintenance/MaintenanceTests.cs` 追加（Snapshot 辅助不变——新字段有默认值）：

```csharp
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
```

注意：`MachineFacts` 与 `InterfaceFact` 当前是 `internal`——本任务改为 `public`（探针内部类型，测试同程序集不可见，需 public 或 InternalsVisibleTo；选 public，与 `GhostDeviceSnapshot` 一致）。`InterfaceFact` 记录改为：
`public sealed record InterfaceFact(int Index, string Alias, string State, int Metric, string Auto, string Dhcp, string IgnoreDefaultRoutes, int NlMtu);`

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Maintenance --filter "Tun_interfaces_are_identified|Machine_facts_parse_tun"`
Expected: 编译失败（IsTun/IgnoreDefaultRoutes/Owners 不存在）。

- [ ] **Step 3: 实现**

`ICommandRunner.cs` 新文件：

```csharp
namespace NetScope.Maintenance;

/// <summary>Injectable seam for fixed local commands; tests never spawn a real process.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunPowerShellAsync(string script, CancellationToken cancellationToken);
    Task<CommandResult> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken);
    Task<CommandResult> RunJsonAsync(string script, CancellationToken cancellationToken);
}
```

`WindowsCommandRunner`：声明改 `public sealed class WindowsCommandRunner : ICommandRunner`（方法签名已匹配，无需改体）。
`WindowsMaintenanceProbe` 与 `WindowsMaintenanceExecutor` 的构造函数参数类型 `WindowsCommandRunner?` 改为 `ICommandRunner?`。

`MaintenanceModels.cs` 追加两个记录（放在 GhostDeviceSnapshot 后）：

```csharp
/// <summary>One connected TUN adapter; never a candidate for physical-egress metric raising.</summary>
public sealed record TunInterfaceSnapshot(
    int Index, string Alias, int InterfaceMetric, string AutomaticMetric,
    string IgnoreDefaultRoutes, int NlMtu, IReadOnlyList<string> Addresses);

/// <summary>Which process owns a listening candidate proxy port.</summary>
public sealed record PortOwnerSnapshot(int Port, int Pid, string ProcessName);
```

`MaintenanceSnapshot` 主构造函数末尾（`Notes` 之后）追加：

```csharp
    IReadOnlyList<TunInterfaceSnapshot>? TunInterfaces = null,
    IReadOnlyList<PortOwnerSnapshot>? PortOwners = null,
    int BestRouteIndex = 0)
```

`WindowsMaintenanceProbe.cs`：
1. 加常量与识别方法：

```csharp
/// <summary>Alias shapes of known proxy-client TUN adapters (same list as the verified desktop script).</summary>
public const string TunAliasPattern = "Mihomo|Nice|农夫|Meta Tunnel|FlClash|Clash|Wintun|TUN|Verge";

public static bool IsTun(string alias, IReadOnlyList<string> addresses, string adapterDescription)
    => !Regex.IsMatch(alias, "VMware|vEthernet|VirtualBox|Hyper-V|Loopback|Bluetooth|蓝牙", RegexOptions.IgnoreCase)
        && (Regex.IsMatch(alias, TunAliasPattern, RegexOptions.IgnoreCase)
            || addresses.Any(a => a.StartsWith("198.18.", StringComparison.Ordinal) || a.StartsWith("198.19.", StringComparison.Ordinal))
            || Regex.IsMatch(adapterDescription, "Wintun|Tunnel", RegexOptions.IgnoreCase));
```

2. `ReadMachineAsync` 的 PowerShell 脚本：`Get-NetIPInterface` 的 Select 追加 `@{n='Ignore';e={$_.IgnoreDefaultRoutes.ToString()}},@{n='Mtu';e={[int]$_.NlMtu}}`；脚本末尾输出对象前追加：

```powershell
$adapters=@(Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object @{n='Index';e={[int]$_.ifIndex}},@{n='Desc';e={$_.InterfaceDescription}})
$owners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $p=Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; [pscustomobject]@{Port=[int]$_.LocalPort;Pid=[int]$_.OwningProcess;Name=if($p){$p.ProcessName}else{''}} })
$src4=''; $src5=''
try { $src4=[string](Find-NetRoute -RemoteIPAddress '8.8.8.8' | Select-Object -First 1).IPAddress } catch {}
try { $src5=[string](Find-NetRoute -RemoteIPAddress '223.5.5.5' | Select-Object -First 1).IPAddress } catch {}
```

输出对象加 `Adapters=$adapters;Owners=$owners;Src4=$src4;Src5=$src5`。

3. `MachineFacts` 改 public 并加属性：`Adapters`(IReadOnlyList<AdapterFact>)、`Owners`(IReadOnlyList<PortOwnerSnapshot>)、`SourceAddress4`/`SourceAddress5`(string)；`Parse` 对应解析（Owners 的 Name 用 Str）。`InterfaceFact` 加 `IgnoreDefaultRoutes`/`NlMtu`（Parse 读 "Ignore"/"Mtu"）。新增 `public sealed record AdapterFact(int Index, string Desc);`

4. `CaptureAsync`：构建 TUN 列表与归属列表，写入快照新字段；`BestRouteIndex = machine.BestRouteIndex`：

```csharp
var tuns = machine.Interfaces
    .Where(iface => iface.State == "Connected")
    .Where(iface => IsTun(iface.Alias,
        machine.Addrs.Where(a => a.Index == iface.Index).Select(a => a.Address).ToArray(),
        machine.Adapters.FirstOrDefault(a => a.Index == iface.Index)?.Desc ?? ""))
    .Select(iface => new TunInterfaceSnapshot(iface.Index, iface.Alias, iface.Metric, iface.Auto,
        iface.IgnoreDefaultRoutes, iface.NlMtu,
        machine.Addrs.Where(a => a.Index == iface.Index).Select(a => a.Address).ToArray()))
    .ToArray();
var owners = machine.Owners.Where(o => candidatePorts.Contains(o.Port)).ToArray();
```

（`candidatePorts` 已有；owners 过滤移到 candidatePorts 计算之后。）
源地址报告进 notes：`if (machine.SourceAddress4.Length > 0) notes.Add($"内核出站源地址 8.8.8.8 → {machine.SourceAddress4}（if={machine.BestRouteIndex}）");` 223.5.5.5 同理。

5. config 写死扫描（只报告，C# 原生，与脚本 Find-StaleClientConfig 同根目录）：

```csharp
private static void ReportPinnedClientConfig(List<string> notes)
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    foreach (var root in new[] { "io.github.clash-verge-rev.clash-verge-rev", "Clash Verge" })
    {
        var dir = Path.Combine(appData, root);
        if (!Directory.Exists(dir)) continue;
        var files = Directory.EnumerateFiles(dir, "*.y*ml", SearchOption.AllDirectories).Take(40);
        foreach (var file in files)
        {
            var hits = File.ReadLines(file)
                .Select((text, i) => (text, i))
                .Where(x => Regex.IsMatch(x.text, @"^\s*(interface-name|device)\s*:"))
                .Take(5).ToArray();
            foreach (var (text, i) in hits)
                notes.Add($"客户端配置写死网卡：{file}:{i + 1} {text.Trim()}（只报告，不改文件）");
        }
    }
}
```

在 `CaptureAsync` 末尾调用；IOException/UnauthorizedAccessException 时加 note「客户端配置扫描失败：…」，不抛出。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Maintenance`
Expected: 全绿（含既有测试——快照新字段有默认值，不破坏）。

- [ ] **Step 5: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src/NetScope.Maintenance tests/Maintenance
git commit -m "feat(maintenance): collect TUN adapters, port owners, egress source and pinned config"
```

---

### Task 3: planner 新规则（TUN_PRIORITY / PORT_CONFLICT / MULTIPLE_TUN）

**Files:**
- Modify: `src/NetScope.Maintenance/MaintenancePlanner.cs`
- Test: `tests/Maintenance/MaintenanceTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `TunInterfaceSnapshot`/`PortOwnerSnapshot`/快照新字段。
- Produces: finding codes `TUN_PRIORITY`、`PORT_CONFLICT`、`MULTIPLE_TUN`；动作 `Kind="FixTunPriority"`，`Id="fix-tun-priority:<ifIndex>"`，`Parameters` 含 `Index`/`PreMetric`/`PreAuto`/`PreIgnore`，`Risk=Elevated`、`RequiresElevation=true`、`SelectedByDefault=true`；`Current` 格式 `"{metric}/{auto}/{ignore}"`，`Desired="1/Disabled/Disabled"`。

- [ ] **Step 1: 写失败测试**

```csharp
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
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Maintenance --filter "Tun_with|Two_tuns|Candidate_port|Two_proxy"`
Expected: FAIL（finding/action 不存在）。

- [ ] **Step 3: 实现 planner**

在 `MaintenancePlanner.Build` 的「---- 4. Cache refresh ----」之前插入：

```csharp
// ---- 3.5 TUN priority (script fix-tun-priority semantics; TUN adapter only) ----
var tuns = snapshot.TunInterfaces ?? Array.Empty<TunInterfaceSnapshot>();
if (tuns.Count > 1)
{
    findings.Add(new Finding("MULTIPLE_TUN", "发现多个 TUN 网卡",
        "两个客户端同时开 TUN 会抢 127.0.0.1 端口和默认路由，建议只留一个：" +
        string.Join("、", tuns.Select(t => $"if={t.Index} {t.Alias}")),
        FindingSeverity.Warning,
        tuns.Select(t => $"if={t.Index} {t.Alias} metric={t.InterfaceMetric}/{t.AutomaticMetric}").ToArray()));
}
foreach (var tun in tuns)
{
    var needsMetric = tun.AutomaticMetric == "Enabled" || tun.InterfaceMetric > 1;
    var needsIgnore = tun.IgnoreDefaultRoutes == "Enabled";
    if (!needsMetric && !needsIgnore) continue;
    findings.Add(new Finding("TUN_PRIORITY", "TUN 优先级未生效：" + tun.Alias,
        $"if={tun.Index} 度量={tun.InterfaceMetric}（自动={tun.AutomaticMetric}）IgnoreDefaultRoutes={tun.IgnoreDefaultRoutes}；" +
        "默认路由可能没走 TUN，表现为 fake-ip DNS 正常但全部超时。",
        FindingSeverity.Critical,
        new[] { $"if={tun.Index} {tun.Alias}", $"metric={tun.InterfaceMetric}/{tun.AutomaticMetric}", $"ignore={tun.IgnoreDefaultRoutes}" }));
    actions.Add(new RepairAction(
        "fix-tun-priority:" + tun.Index, "修复 TUN 优先级（" + tun.Alias + "）",
        "接口度量 → 1（自动度量关闭）" + (needsIgnore ? "；IgnoreDefaultRoutes → disabled" : "") +
        "；只动 TUN 网卡，ActiveStore，重开 TUN/重启后可能重置",
        ActionRisk.Elevated, "FixTunPriority", "if=" + tun.Index,
        tun.InterfaceMetric + "/" + tun.AutomaticMetric + "/" + tun.IgnoreDefaultRoutes,
        "1/Disabled/Disabled", true, true,
        new Dictionary<string, string>
        {
            ["Index"] = tun.Index.ToString(),
            ["PreMetric"] = tun.InterfaceMetric.ToString(),
            ["PreAuto"] = tun.AutomaticMetric,
            ["PreIgnore"] = tun.IgnoreDefaultRoutes,
        }));
}

// ---- 3.6 Port ownership: name the squatter, never kill it ----
var knownClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Nice加速", "FlClashCore", "FlClashHelperService", "Apex", "ApexCore", "Clash Verge", "verge-mihomo", "mihomo", "clash", "sing-box" };
var owners = snapshot.PortOwners ?? Array.Empty<PortOwnerSnapshot>();
foreach (var port in snapshot.ListeningProxyPorts)
{
    var owner = owners.FirstOrDefault(o => o.Port == port);
    if (owner is null || owner.ProcessName.Length == 0 || knownClients.Contains(owner.ProcessName)) continue;
    findings.Add(new Finding("PORT_CONFLICT", $"端口 {port} 被非代理进程占用",
        $"127.0.0.1:{port} 的监听进程是 {owner.ProcessName}（PID {owner.Pid}），不是已知代理客户端；只报告，不结束进程。",
        FindingSeverity.Warning, new[] { $"port={port} pid={owner.Pid} process={owner.ProcessName}" }));
}
var clientNames = owners.Where(o => knownClients.Contains(o.ProcessName))
    .Select(o => o.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
if (clientNames.Length > 1)
{
    findings.Add(new Finding("PORT_CONFLICT", "多个代理客户端同时监听候选端口",
        string.Join("、", clientNames) + " 同时在听；两个客户端一定会抢端口，建议只留一个。只报告，不结束进程。",
        FindingSeverity.Warning, owners.Select(o => $"{o.Port}→{o.ProcessName}({o.Pid})").ToArray()));
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Maintenance`
Expected: 全绿。

- [ ] **Step 5: Commit**

```bash
git add src/NetScope.Maintenance/MaintenancePlanner.cs tests/Maintenance
git commit -m "feat(maintenance): plan TUN priority fix and name port squatters"
```

---

### Task 4: executor 执行/回滚 FixTunPriority + ARP 刷新 + 真打判定对齐

**Files:**
- Create: `src/NetScope.Maintenance/ProxyPortTester.cs`
- Modify: `src/NetScope.Maintenance/WindowsMaintenanceExecutor.cs`、`WindowsMaintenanceProbe.cs`（IsWorkingProxyAsync 改委托）、`QuickRepairService.cs`（ToJournalEntry）
- Test: `tests/Maintenance/MaintenanceTests.cs`（+FakeCommandRunner 与执行器测试）

**Interfaces:**
- Consumes: Task 2 `ICommandRunner`；Task 3 `FixTunPriority` 动作与 Parameters。
- Produces:
  - `public static class ProxyPortTester { public static Task<bool> IsWorkingAsync(int port, HttpMessageHandler? handler, CancellationToken ct); }`（handler null = 真实 HttpClientHandler；200/204 才 true）
  - executor 支持 `Kind="FixTunPriority"` 的 Apply 与 Restore；journal key `"TunPriority|<index>"`，Old=`"{metric}/{auto}/{ignore}"`，Extra=index。
  - `FakeCommandRunner`（测试辅助，Step 1 定义）：记录脚本/参数，按队列返回 CommandResult。

- [ ] **Step 1: 写失败测试**

```csharp
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Maintenance --filter "Fix_tun|Restore_tun|Flush_dns_also|Only_200_204"`
Expected: 编译失败（ProxyPortTester 不存在；FixTunPriority 未知动作）。

- [ ] **Step 3: 实现**

`ProxyPortTester.cs`：

```csharp
using System.Net;
using System.Net.Http;

namespace NetScope.Maintenance;

/// <summary>
/// A port is a working proxy only if a real request through it returns 200/204 from
/// gstatic generate_204 (same bar as the verified desktop script). Listening alone is not enough.
/// </summary>
public static class ProxyPortTester
{
    public static async Task<bool> IsWorkingAsync(int port, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        var ownsHandler = handler is null;
        handler ??= new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + port), UseProxy = true };
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("https://www.gstatic.com/generate_204", cancellationToken).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ownsHandler) handler.Dispose();
        }
    }
}
```

`WindowsMaintenanceProbe.IsWorkingProxyAsync` 整方法替换为：
`private static Task<bool> IsWorkingProxyAsync(int port, CancellationToken ct) => ProxyPortTester.IsWorkingAsync(port, null, ct);`

`WindowsMaintenanceExecutor`：
1. Apply switch 加 `"FixTunPriority" => await FixTunPriorityAsync(action, cancellationToken).ConfigureAwait(false),`
2. 新方法：

```csharp
private async Task<ActionResult> FixTunPriorityAsync(RepairAction action, CancellationToken cancellationToken)
{
    var index = int.Parse(action.Parameters["Index"], CultureInfo.InvariantCulture);
    var script = "Set-NetIPInterface -InterfaceIndex " + index.ToString(CultureInfo.InvariantCulture)
        + " -AddressFamily IPv4 -InterfaceMetric 1 -AutomaticMetric Disabled -ErrorAction Stop; 'OK'";
    var result = await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    if (!result.Ok || !result.StandardOutput.Contains("OK", StringComparison.Ordinal))
        return new ActionResult(action.Id, action.Title, false, action.Current, null,
            "Set-NetIPInterface 失败（通常需要管理员）：" + result.StandardError);
    if (action.Parameters.TryGetValue("PreIgnore", out var preIgnore) && preIgnore == "Enabled")
    {
        var netsh = await _runner.RunAsync("netsh", new[] { "interface", "ipv4", "set", "interface",
            index.ToString(CultureInfo.InvariantCulture), "ignoredefaultroutes=disabled" }, cancellationToken).ConfigureAwait(false);
        if (!netsh.Ok)
            return new ActionResult(action.Id, action.Title, false, action.Current, null,
                "netsh ignoredefaultroutes 失败：" + netsh.StandardError);
    }
    return new ActionResult(action.Id, action.Title, true, action.Current, "1/Disabled/Disabled", null);
}
```

3. Restore switch 加 case：

```csharp
case "FixTunPriority":
{
    var parts = (entry.Old ?? "").Split('/');
    var index = int.Parse(entry.Extra ?? entry.Name, CultureInfo.InvariantCulture);
    var metric = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
    var auto = parts.Length > 1 ? parts[1] : "Enabled";
    var ignore = parts.Length > 2 ? parts[2] : "Disabled";
    var script = auto == "Enabled"
        ? "Set-NetIPInterface -InterfaceIndex " + index + " -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop; 'OK'"
        : "Set-NetIPInterface -InterfaceIndex " + index + " -AddressFamily IPv4 -InterfaceMetric " + metric
            + " -AutomaticMetric Disabled -ErrorAction Stop; 'OK'";
    var result = await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    if (!result.Ok || !result.StandardOutput.Contains("OK", StringComparison.Ordinal))
        return (false, "还原 TUN 度量失败：" + result.StandardError);
    if (ignore == "Enabled")
    {
        var netsh = await _runner.RunAsync("netsh", new[] { "interface", "ipv4", "set", "interface",
            index.ToString(CultureInfo.InvariantCulture), "ignoredefaultroutes=enabled" }, cancellationToken).ConfigureAwait(false);
        if (!netsh.Ok) return (false, "还原 IgnoreDefaultRoutes 失败：" + netsh.StandardError);
    }
    return (true, "已还原 if=" + index + " TUN 三字段");
}
```

4. `FlushDnsAsync` 在 ipconfig 行后加：
`await _runner.RunAsync("netsh", new[] { "interface", "ip", "delete", "arpcache" }, cancellationToken).ConfigureAwait(false);`

`QuickRepairService.ToJournalEntry`：extra switch 加 `"FixTunPriority" => action.Parameters.TryGetValue("Index", out var ti) ? ti : "",`；key switch 加 `"FixTunPriority" => "TunPriority|" + extra,`。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Maintenance`
Expected: 全绿。

- [ ] **Step 5: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src/NetScope.Maintenance tests/Maintenance
git commit -m "feat(maintenance): execute and roll back TUN priority fix; ARP flush; strict proxy port test"
```

---

### Task 5: 修后自检 PostCheck（A~E 分类 + 重试 + 仅 A/B 自动回滚）

**Files:**
- Create: `src/NetScope.Maintenance/PostCheck.cs`、`src/NetScope.Maintenance/WindowsPostCheckProbe.cs`
- Modify: `src/NetScope.Maintenance/MaintenanceModels.cs`（RepairReport +PostCheck）、`QuickRepairService.cs`、`MaintenanceJournalStore.cs`（+Remove）
- Test: `tests/Maintenance/PostCheckTests.cs`（新）、`MaintenanceTests.cs`（Service 辅助加 FakePostCheck）

**Interfaces:**
- Consumes: Task 2 快照 `BestRouteIndex`；Task 4 `ProxyPortTester`、executor Restore。
- Produces:
  - `public sealed record PostCheckObservation(int BestRouteIndex, bool PhysicalInternet, bool DnsOk, bool DirectOk, bool ProxyOk, bool ProxyAlive);`
  - `public sealed record PostCheckResult(bool Success, string CauseClass, IReadOnlyList<string> Details, PostCheckObservation Observation);`（CauseClass ∈ None/A/B/C/D/E，"A/B" 是字面三字符）
  - `public interface IPostCheckProbe { Task<PostCheckObservation> ObserveAsync(CancellationToken cancellationToken); }`
  - `public static PostCheckResult PostCheckClassifier.Classify(PostCheckObservation observation, int? expectedRouteIndex)`
  - `MaintenanceJournalStore.Remove(string key)`；`RepairReport` 末尾追加 `PostCheckResult? PostCheck = null`。
  - `QuickRepairService` 构造函数第 4 个可选参数 `IPostCheckProbe? postCheck = null`（null → `new WindowsPostCheckProbe(_probe)`）。

- [ ] **Step 1: 写失败测试**

`tests/Maintenance/PostCheckTests.cs`：

```csharp
using NetScope.Maintenance;

namespace NetScope.Maintenance.Tests;

public sealed class PostCheckTests
{
    private static PostCheckObservation Obs(int route = 7, bool internet = true, bool dns = true,
        bool direct = true, bool proxy = true, bool proxyAlive = true)
        => new(route, internet, dns, direct, proxy, proxyAlive);

    [Fact]
    public void All_green_is_success_with_cause_none()
    {
        var result = PostCheckClassifier.Classify(Obs(), expectedRouteIndex: 7);
        Assert.True(result.Success);
        Assert.Equal("None", result.CauseClass);
    }

    [Fact]
    public void Route_not_via_tun_is_cause_ab()
    {
        var result = PostCheckClassifier.Classify(Obs(route: 21), expectedRouteIndex: 7);
        Assert.False(result.Success);
        Assert.Equal("A/B", result.CauseClass);
    }

    [Fact]
    public void No_physical_internet_is_cause_c()
    {
        var result = PostCheckClassifier.Classify(Obs(internet: false, direct: false, proxy: false), expectedRouteIndex: null);
        Assert.Equal("C", result.CauseClass);
    }

    [Fact]
    public void Route_ok_but_everything_dead_is_cause_d()
    {
        var result = PostCheckClassifier.Classify(Obs(direct: false, proxy: false), expectedRouteIndex: 7);
        Assert.Equal("D", result.CauseClass);
    }

    [Fact]
    public void Partial_connectivity_is_cause_e()
    {
        var result = PostCheckClassifier.Classify(Obs(proxy: false), expectedRouteIndex: 7);
        Assert.Equal("E", result.CauseClass);
    }

    [Fact]
    public void Dead_proxy_port_is_not_tested_and_not_a_failure()
    {
        var result = PostCheckClassifier.Classify(Obs(proxy: false, proxyAlive: false), expectedRouteIndex: null);
        Assert.True(result.Success);
    }
}
```

`MaintenanceTests.cs` 追加（Service 辅助与 FakePostCheck 定义放本文件，供既有测试复用）：

```csharp
private sealed class FakePostCheck(params PostCheckObservation[] observations) : IPostCheckProbe
{
    private readonly Queue<PostCheckObservation> _queue = new(observations);
    public int Calls { get; private set; }
    public Task<PostCheckObservation> ObserveAsync(CancellationToken ct)
    { Calls++; return Task.FromResult(_queue.Count > 1 ? _queue.Dequeue() : _queue.Peek()); }
}

private static PostCheckObservation GreenObs(int route = 21) => new(route, true, true, true, true, false);

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
```

同时把既有 `Service(...)` 辅助改为传 `postCheck: new FakePostCheck(GreenObs())`（否则既有非 DryRun 测试会走真实探测）：

```csharp
private static QuickRepairService Service(MaintenanceSnapshot snapshot, FakeExecutor executor,
    MaintenanceJournalStore? store = null)
    => new(new FakeProbe(snapshot), executor, store ?? new MaintenanceJournalStore(TempJournal.NewPath()),
        postCheck: new FakePostCheck(GreenObs()));
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Maintenance --filter "PostCheck|postcheck|Cause_c"`
Expected: 编译失败（PostCheck 类型不存在）。

- [ ] **Step 3: 实现**

`PostCheck.cs`：

```csharp
namespace NetScope.Maintenance;

/// <summary>One post-repair measurement round. Every flag is measured, never assumed.</summary>
public sealed record PostCheckObservation(
    int BestRouteIndex, bool PhysicalInternet, bool DnsOk, bool DirectOk, bool ProxyOk, bool ProxyAlive);

/// <summary>Classified post-check outcome. CauseClass: None / A/B / C / D / E (desktop script semantics).</summary>
public sealed record PostCheckResult(
    bool Success, string CauseClass, IReadOnlyList<string> Details, PostCheckObservation Observation);

public interface IPostCheckProbe
{
    Task<PostCheckObservation> ObserveAsync(CancellationToken cancellationToken);
}

/// <summary>Pure classification; no I/O. Only "route still not via TUN" (A/B) may trigger auto-rollback.</summary>
public static class PostCheckClassifier
{
    public static PostCheckResult Classify(PostCheckObservation observation, int? expectedRouteIndex)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var routeOk = expectedRouteIndex is null || observation.BestRouteIndex == expectedRouteIndex;
        var proxyOk = !observation.ProxyAlive || observation.ProxyOk;
        var success = routeOk && observation.DnsOk && observation.DirectOk && proxyOk;
        var cause = success ? "None"
            : expectedRouteIndex is not null && !routeOk ? "A/B"
            : !observation.PhysicalInternet ? "C"
            : !observation.DirectOk && !observation.ProxyOk ? "D"
            : "E";
        var details = new List<string>
        {
            "默认路由 if=" + observation.BestRouteIndex
                + (expectedRouteIndex is null ? "" : "（期望 if=" + expectedRouteIndex + "）")
                + (routeOk ? " ✔" : " X"),
            "物理出口 Internet：" + (observation.PhysicalInternet ? "有" : "无"),
            "DNS 解析：" + (observation.DnsOk ? "通" : "失败"),
            "直连（不走系统/环境变量代理；TUN 开启时仍可能经 TUN）：" + (observation.DirectOk ? "通" : "失败"),
            observation.ProxyAlive ? "经代理：" + (observation.ProxyOk ? "通" : "失败") : "经代理：未测（无可用代理端口）",
        };
        return new PostCheckResult(success, cause, details, observation);
    }
}
```

`WindowsPostCheckProbe.cs`：

```csharp
using System.Net.Http;
using System.Net.Sockets;

namespace NetScope.Maintenance;

/// <summary>Real post-repair measurement: fresh snapshot + DNS + direct HTTP + proxy HTTP.</summary>
public sealed class WindowsPostCheckProbe : IPostCheckProbe
{
    private readonly IMaintenanceProbe _probe;

    public WindowsPostCheckProbe(IMaintenanceProbe? probe = null)
        => _probe = probe ?? new WindowsMaintenanceProbe();

    public async Task<PostCheckObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _probe.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var dnsOk = await DnsResolvesAsync(cancellationToken).ConfigureAwait(false);
        var directOk = await DirectHttpAsync(cancellationToken).ConfigureAwait(false);
        var proxyAlive = snapshot.WorkingProxyPort is not null;
        var proxyOk = proxyAlive
            && await ProxyPortTester.IsWorkingAsync(snapshot.WorkingProxyPort!.Value, null, cancellationToken).ConfigureAwait(false);
        return new PostCheckObservation(
            snapshot.BestRouteIndex,
            snapshot.Interfaces.Any(item => item.Connectivity == "Internet"),
            dnsOk, directOk, proxyOk, proxyAlive);
    }

    private static async Task<bool> DnsResolvesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync("www.baidu.com", cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> DirectHttpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("http://www.baidu.com/", cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode > 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}
```

`MaintenanceJournalStore` 加方法：

```csharp
/// <summary>Removes one entry after an auto-rollback so Restore never replays it twice.</summary>
public void Remove(string key)
{
    var document = Load();
    if (document is null) return;
    document.Entries.RemoveAll(entry => entry.Key == key);
    if (document.Entries.Count == 0) { Clear(); return; }
    document.UpdatedAt = DateTimeOffset.Now;
    File.WriteAllText(Path, JsonSerializer.Serialize(document, Options));
}
```

`MaintenanceModels.cs`：`RepairReport` 主构造函数末尾追加 `PostCheckResult? PostCheck = null`。

`QuickRepairService.cs`：
1. 加 `using System.Globalization;`；字段 `private readonly IPostCheckProbe _postCheck;`；构造函数加第 4 参 `IPostCheckProbe? postCheck = null`，体内 `_postCheck = postCheck ?? new WindowsPostCheckProbe(_probe);`。
2. `ApplyAsync` 在 journal 保存块之后、return 之前插入：

```csharp
PostCheckResult? postCheck = null;
if (!dryRun && results.Any(result => result.Applied))
{
    var tunApplied = plan.Actions.FirstOrDefault(action => action.Kind == "FixTunPriority"
        && results.Any(result => result.ActionId == action.Id && result.Applied));
    int? expectedRoute = tunApplied is null ? null
        : int.Parse(tunApplied.Parameters["Index"], CultureInfo.InvariantCulture);
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var observation = await _postCheck.ObserveAsync(cancellationToken).ConfigureAwait(false);
        postCheck = PostCheckClassifier.Classify(observation, expectedRoute);
        if (postCheck.Success) break;
        if (attempt < 2) await Task.Delay(800, cancellationToken).ConfigureAwait(false);
    }
    foreach (var detail in postCheck!.Details) messages.Add("自检：" + detail);
    if (postCheck.Success)
    {
        messages.Add("修后自检通过。");
    }
    else
    {
        messages.Add("修后自检未通过（原因 " + postCheck.CauseClass + "）。");
        if (postCheck.CauseClass == "A/B" && tunApplied is not null)
        {
            var entry = entries.FirstOrDefault(item => item.Kind == "FixTunPriority");
            if (entry is not null)
            {
                var (ok, message) = await _executor.RestoreAsync(entry, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    _journal.Remove(entry.Key);
                    messages.Add("度量改动未生效（原因 A/B）：已自动还原 TUN 度量。请在客户端关开 TUN / 打开 auto-route 后重新体检。");
                }
                else messages.Add("自动还原 TUN 度量失败：" + message);
            }
        }
    }
}
```

3. return 的 `new RepairReport(...)` 末尾加 `postCheck` 实参（用命名实参 `PostCheck: postCheck` 防位置错位）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Maintenance`
Expected: 全绿（含既有测试）。

- [ ] **Step 5: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src/NetScope.Maintenance tests/Maintenance
git commit -m "feat(maintenance): post-repair self-check with A-E causes and A/B-only auto-rollback"
```

---

### Task 6: 失败事件进现有日志管线（三类 + 应用批次）

**Files:**
- Create: `src/NetScope.Maintenance/RepairEvent.cs`、`src/NetScope.Storage/RepairSessionEvents.cs`、`src/NetScope.Storage/NdjsonRepairEventSink.cs`、`src/NetScope.Desktop/Services/SessionRepairEventSink.cs`
- Modify: `src/NetScope.Maintenance/QuickRepairService.cs`、`src/NetScope.Storage/NetScope.Storage.csproj`（+Maintenance 引用）、`src/NetScope.Desktop/Services/ObservationSession.cs`、`src/NetScope.Desktop/ViewModels/MainViewModel.cs:40`、`src/NetScope.Cli/Program.cs:335`
- Test: `tests/Maintenance/RepairEventTests.cs`（新）、`tests/Export/RepairSessionEventsTests.cs`（新）

**Interfaces:**
- Consumes: Task 5 `PostCheckResult`；现有 `NdjsonEventLog`/`EventEnvelope`/`EventType.RepairEvent`。
- Produces:
  - `public sealed record RepairEvent(string BatchId, string Kind, DateTimeOffset At, string? ActionId, string? ActionKind, string? Title, string? Error, bool ElevationSkipped, PostCheckResult? PostCheck, MaintenanceSnapshot? Before, MaintenanceSnapshot? After);`（Kind ∈ repair_applied/action_failed/postcheck_failed/user_unresolved）
  - `public interface IRepairEventSink { Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken); }`
  - `QuickRepairService` 第 5 个可选构造参数 `IRepairEventSink? sink = null`；`Task<bool> MarkUnresolvedAsync(CancellationToken)`。
  - `RepairSessionEvents.Create(IReadOnlyList<RepairEvent> events, string sessionId, string bootId, bool synthetic, double monotonicMs)` → `IReadOnlyList<EventEnvelope>`。
  - `NdjsonRepairEventSink(string path)`；`SessionRepairEventSink(ObservationSession session, IClock clock)`。
  - `ObservationSession.AppendRepairEventsAsync(IReadOnlyList<EventEnvelope> events, CancellationToken)` → `Task<int>`。

- [ ] **Step 1: 写失败测试**

`tests/Maintenance/RepairEventTests.cs`：

```csharp
using NetScope.Maintenance;

namespace NetScope.Maintenance.Tests;

public sealed class RepairEventTests
{
    private sealed class FakeSink : IRepairEventSink
    {
        public List<RepairEvent> Events { get; } = new();
        public bool Throw { get; set; }
        public Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken ct)
        {
            if (Throw) throw new IOException("disk full");
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe(MaintenanceSnapshot snapshot) : IMaintenanceProbe
    {
        public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FakeExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken ct)
            => Task.FromResult(new ActionResult(action.Id, action.Title, true, action.Current, action.Desired, null));
        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken ct)
            => Task.FromResult((true, "ok"));
        public Task BroadcastEnvironmentAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakePostCheck(PostCheckObservation obs) : IPostCheckProbe
    {
        public Task<PostCheckObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(obs);
    }

    private static MaintenanceSnapshot EmptySnapshot() => new(
        DateTimeOffset.Now, true, 21, "WLAN", null, Array.Empty<int>(), Array.Empty<ProxyLayerSnapshot>(),
        false, "", 0, false, 0, Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(),
        Array.Empty<string>());

    private static QuickRepairService Service(FakeSink sink, MaintenanceSnapshot? snapshot = null,
        PostCheckObservation? obs = null)
        => new(new FakeProbe(snapshot ?? EmptySnapshot()), new FakeExecutor(),
            new MaintenanceJournalStore(Path.Combine(Path.GetTempPath(), "netscope-event-test-" + Guid.NewGuid().ToString("N") + ".json")),
            new FakePostCheck(obs ?? new PostCheckObservation(21, true, true, true, true, false)), sink);

    [Fact]
    public async Task Applied_batch_emits_repair_applied_with_before_and_after_snapshots()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, false, CancellationToken.None);

        var applied = Assert.Single(sink.Events, e => e.Kind == "repair_applied");
        Assert.NotNull(applied.Before);
        Assert.NotNull(applied.After);
        Assert.NotEmpty(applied.BatchId);
    }

    [Fact]
    public async Task Failed_postcheck_emits_postcheck_failed_with_cause()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot, new PostCheckObservation(21, false, true, false, false, false));
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, false, CancellationToken.None);

        var failed = Assert.Single(sink.Events, e => e.Kind == "postcheck_failed");
        Assert.Equal("C", failed.PostCheck!.CauseClass);
    }

    [Fact]
    public async Task Dry_run_emits_nothing()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, true, CancellationToken.None);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task User_unresolved_references_the_last_batch()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);
        await service.ApplyAsync(plan, null, false, CancellationToken.None);

        var marked = await service.MarkUnresolvedAsync(CancellationToken.None);

        Assert.True(marked);
        var report = Assert.Single(sink.Events, e => e.Kind == "user_unresolved");
        Assert.Equal(sink.Events[0].BatchId, report.BatchId);
    }

    [Fact]
    public async Task Mark_unresolved_without_a_batch_returns_false()
        => Assert.False(await Service(new FakeSink()).MarkUnresolvedAsync(CancellationToken.None));

    [Fact]
    public async Task Sink_failure_does_not_fail_the_repair()
    {
        var sink = new FakeSink { Throw = true };
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.Equal(0, report.Failed);
        Assert.Contains(report.Messages, m => m.Contains("事件写入失败"));
    }
}
```

`tests/Export/RepairSessionEventsTests.cs`（Export 测试工程已引用 Storage；Maintenance 类型经传递引用可用，若编译缺引用则在 csproj 加 `<ProjectReference Include="..\..\src\NetScope.Maintenance\NetScope.Maintenance.csproj" />`）：

```csharp
using NetScope.Domain;
using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Export.Tests;

public sealed class RepairSessionEventsTests
{
    [Fact]
    public void Repair_events_become_valid_repair_event_envelopes()
    {
        var events = new[]
        {
            new RepairEvent("batch1", "action_failed", DateTimeOffset.Now, "clear-env:User:HTTP_PROXY",
                "ClearProxyEnv", "清除 User\\HTTP_PROXY", "UnauthorizedAccessException", false, null, null, null),
            new RepairEvent("batch1", "repair_applied", DateTimeOffset.Now, null, null, "应用 1 项",
                null, false, null, null, null),
        };

        var envelopes = RepairSessionEvents.Create(events, "session-x", "boot-x", synthetic: false, 123.0);

        Assert.Equal(2, envelopes.Count);
        Assert.All(envelopes, e =>
        {
            Assert.Equal(EventType.RepairEvent, e.EventType);
            Assert.Equal("batch1", e.Refs.RepairPlanId);
            Assert.Equal("maintenance.quick-repair", e.Source);
            _ = EnvelopeJson.Serialize(e); // 契约校验不抛
        });
        Assert.Equal(EventStatus.Failed, envelopes[0].Status);
        Assert.Equal(EventStatus.Ok, envelopes[1].Status);
    }

    [Fact]
    public void Elevation_skip_maps_to_access_denied()
    {
        var events = new[]
        {
            new RepairEvent("b", "action_failed", DateTimeOffset.Now, "fix-tun-priority:7", "FixTunPriority",
                "修复 TUN", "需要管理员权限：已跳过", true, null, null, null),
        };
        var envelope = Assert.Single(RepairSessionEvents.Create(events, "s", "b", false, 1.0));
        Assert.Equal(EventStatus.AccessDenied, envelope.Status);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Maintenance --filter "RepairEventTests"` 与 `dotnet test tests/Export --filter "RepairSessionEvents"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现**

`RepairEvent.cs`（Maintenance）：

```csharp
namespace NetScope.Maintenance;

/// <summary>
/// One repair outcome worth persisting. Kinds: repair_applied / action_failed /
/// postcheck_failed / user_unresolved. Carries before/after snapshots when available.
/// </summary>
public sealed record RepairEvent(
    string BatchId,
    string Kind,
    DateTimeOffset At,
    string? ActionId,
    string? ActionKind,
    string? Title,
    string? Error,
    bool ElevationSkipped,
    PostCheckResult? PostCheck,
    MaintenanceSnapshot? Before,
    MaintenanceSnapshot? After);

/// <summary>Persistence port for repair outcomes; null sink keeps the service purely local.</summary>
public interface IRepairEventSink
{
    Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken);
}
```

`QuickRepairService.cs`：
1. 字段 `private readonly IRepairEventSink? _sink; private string? _lastBatchId;`；构造函数加第 5 参 `IRepairEventSink? sink = null`，体内 `_sink = sink;`。
2. `ApplyAsync` 开头（selected 计算后）加 `var batchId = Guid.NewGuid().ToString("N");`；在 PostCheck 块之后、return 之前加：

```csharp
if (_sink is not null && !dryRun)
{
    _lastBatchId = batchId;
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
    var sinkError = await EmitSafeAsync(events, cancellationToken).ConfigureAwait(false);
    if (sinkError is not null) messages.Add(sinkError);
}
```

3. 新方法：

```csharp
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
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return "修复事件写入失败：" + exception.Message + "（修复本身不受影响）";
    }
}
```

`NetScope.Storage.csproj`：加 `<ProjectReference Include="..\NetScope.Maintenance\NetScope.Maintenance.csproj" />`（Maintenance 只引用 Domain，无环）。

`RepairSessionEvents.cs`（Storage）：

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Maintenance;

namespace NetScope.Storage;

/// <summary>Builds repair_event envelopes; the writer rebases sequence numbers.</summary>
public static class RepairSessionEvents
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IReadOnlyList<EventEnvelope> Create(IReadOnlyList<RepairEvent> events,
        string sessionId, string bootId, bool synthetic, double monotonicMs)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = new List<EventEnvelope>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var repair = events[index];
            var status = repair.Kind switch
            {
                "repair_applied" => EventStatus.Ok,
                "action_failed" when repair.ElevationSkipped => EventStatus.AccessDenied,
                _ => EventStatus.Failed,
            };
            var envelope = new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion,
                EventId = Guid.NewGuid().ToString("D"),
                SessionId = sessionId,
                BootId = bootId,
                Sequence = index + 1,
                ObservedAt = repair.At,
                MonotonicMs = monotonicMs,
                EventType = EventType.RepairEvent,
                Status = status,
                Source = "maintenance.quick-repair",
                SourceVersion = "1.1",
                Synthetic = synthetic,
                Refs = new EventRefs { RepairPlanId = repair.BatchId },
                Payload = JsonSerializer.SerializeToNode(repair, PayloadOptions)!.AsObject(),
            };
            _ = EnvelopeJson.Serialize(envelope);
            result.Add(envelope);
        }
        return result;
    }
}
```

`NdjsonRepairEventSink.cs`（Storage）：

```csharp
using System.Diagnostics;
using NetScope.Maintenance;

namespace NetScope.Storage;

/// <summary>CLI-side sink: repair events append to their own NDJSON log (no observation session).</summary>
public sealed class NdjsonRepairEventSink : IRepairEventSink
{
    private readonly NdjsonEventLog _log;
    private readonly string _sessionId = "maintenance-" + DateTime.Now.ToString("yyyyMMdd");
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private int _nextSequence = 1;

    public NdjsonRepairEventSink(string path) => _log = new NdjsonEventLog(path);

    public async Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken)
    {
        var envelopes = RepairSessionEvents.Create(events, _sessionId, _bootId, synthetic: false,
            Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        foreach (var envelope in envelopes)
            await _log.AppendAsync(envelope with { Sequence = _nextSequence++ }, cancellationToken).ConfigureAwait(false);
    }
}
```

`ObservationSession.cs` 加公共方法（放在 PersistTransitionsWithGateAsync 附近）：

```csharp
/// <summary>Appends repair events to the session log; refuses after the session has stopped.</summary>
public async Task<int> AppendRepairEventsAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
{
    await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        lock (_stateLock)
        {
            if (_stopTask is not null)
                throw new InvalidOperationException("The session has been stopped; repair events are not written after exit.");
        }
        var outcome = await AppendEventsCoreAsync(events, cancellationToken).ConfigureAwait(false);
        return outcome.Count;
    }
    finally { _writeGate.Release(); }
}
```

`SessionRepairEventSink.cs`（Desktop/Services）：

```csharp
using NetScope.Application;
using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Desktop.Services;

/// <summary>Repair events join the observing session's timeline (same NDJSON + SQLite projection).</summary>
public sealed class SessionRepairEventSink(ObservationSession session, IClock clock) : IRepairEventSink
{
    public Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken)
        => session.AppendRepairEventsAsync(
            RepairSessionEvents.Create(events, session.SessionId, session.BootId, session.Synthetic, clock.MonotonicMs),
            cancellationToken);
}
```

接线：
- `MainViewModel.cs:40`：`QuickRepair = new QuickRepairViewModel(new QuickRepairService(sink: new SessionRepairEventSink(services.Session, services.Clock)));`
- `Program.cs:335`：

```csharp
private static QuickRepairService MaintenanceService() => new(sink: new NetScope.Storage.NdjsonRepairEventSink(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetScopeRepair", "maintenance", "repair-events.ndjson")));
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Maintenance` 与 `dotnet test tests/Export`
Expected: 全绿。

- [ ] **Step 5: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src tests
git commit -m "feat(maintenance): repair outcomes flow into the existing event log pipeline"
```

---

### Task 7: AI 交接包导出器（HandoffExporter + HandoffPrompt）

**Files:**
- Create: `src/NetScope.Storage/Export/HandoffExporter.cs`
- Test: `tests/Export/HandoffExporterTests.cs`（新）

**Interfaces:**
- Consumes: Task 6 `RepairSessionEvents`；现有 `NdjsonEventLog.Read()`、`PackageAnonymizer(RedactionLedger, byte[])`（internal，同程序集可用）、`RedactionLedger.Snapshot()`。
- Produces:
  - `public sealed record HandoffResult(string PackagePath, string Prompt);`
  - `public sealed class HandoffExporter { public Task<HandoffResult> ExportAsync(string ndjsonPath, string destinationDirectory, string? batchId, CancellationToken cancellationToken); }`
  - `public static class HandoffPrompt { public static string Build(string packagePath); }`
  - 包结构：`case/case.json`、`context/context.json`、`developer/guide.md`、`manifest.json`；文件名 `handoff-<yyyyMMdd-HHmmss>-<batch前8位>.zip`。

- [ ] **Step 1: 写失败测试**

```csharp
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Export --filter "HandoffExporter"`
Expected: 编译失败（HandoffExporter 不存在）。

- [ ] **Step 3: 实现**

`HandoffExporter.cs`：

```csharp
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
"""
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
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Export`
Expected: 全绿。

- [ ] **Step 5: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src/NetScope.Storage tests/Export
git commit -m "feat(export): AI handoff package with redacted case and developer guide"
```

---

### Task 8: CLI handoff 命令 + 桌面「交给 AI」「还是没好」

**Files:**
- Modify: `src/NetScope.Cli/Program.cs`（dispatch + RunHandoffAsync + Usage）
- Modify: `src/NetScope.Desktop/ViewModels/QuickRepairViewModel.cs`、`Views/QuickRepairView.xaml`、`ViewModels/MainViewModel.cs:40`
- Test: `tests/Desktop/QuickRepairIntegrationTests.cs`（新）

**Interfaces:**
- Consumes: Task 6 sink 接线、Task 7 `HandoffExporter`/`HandoffPrompt`。
- Produces:
  - CLI：`netscope handoff [--case <batchId>] [--out <dir>]`（默认日志 `%LOCALAPPDATA%\NetScopeRepair\maintenance\repair-events.ndjson`，默认输出 `%LOCALAPPDATA%\NetScopeRepair\handoff`）。
  - `QuickRepairViewModel` 构造函数扩展为 `(QuickRepairService? service = null, HandoffExporter? exporter = null, Func<string>? sessionPath = null, Action<string>? copyToClipboard = null)`；新命令 `HandoffCommand`、`MarkUnresolvedCommand`。
  - `ActionRow` 增加 `public string Kind { get; }`（Task 9 预选用）。

- [ ] **Step 1: 写失败测试**

`tests/Desktop/QuickRepairIntegrationTests.cs`（Desktop 测试工程若缺 Maintenance/Storage 引用则在 csproj 补齐）：

```csharp
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
        await Task.Delay(500); // AsyncRelayCommand 异步执行

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
```

注意：`vm.StatusMessage` 来自 ViewModelBase（现有公共属性）；`HandoffCommand.Execute` 的异步完成用轮询等待替代固定 Delay 更稳：执行后 `for (var i = 0; i < 50 && copied is null; i++) await Task.Delay(100);`。实现测试时用轮询。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Desktop --filter "QuickRepairIntegration"`
Expected: 编译失败（HandoffCommand 不存在）。

- [ ] **Step 3: 实现 CLI**

`Program.cs`：
1. dispatch switch 加 `"handoff" => await RunHandoffAsync(rest).ConfigureAwait(false),`
2. Usage 维护命令区加一行：`Console.WriteLine("  netscope handoff [--case <id>] [--out <dir>]  生成 AI 失败案例交接包");`
3. 新方法（先 grep 确认无现有 OptionValue 辅助；有则复用）：

```csharp
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
```

- [ ] **Step 4: 实现桌面**

`QuickRepairViewModel.cs`：
1. 字段 `private readonly HandoffExporter? _exporter; private readonly Func<string>? _sessionPath; private readonly Action<string>? _copyToClipboard;`
2. 构造函数改签名并赋值；注册 `HandoffCommand = new AsyncRelayCommand(HandoffAsync, null, OnError); MarkUnresolvedCommand = new AsyncRelayCommand(MarkUnresolvedAsync, null, OnError);`
3. `ActionRow` 加 `public string Kind { get; }`（构造函数 `Kind = action.Kind;`）。
4. 新方法：

```csharp
private async Task HandoffAsync()
{
    if (IsBusy) return;
    IsBusy = true;
    try
    {
        var path = _sessionPath?.Invoke();
        if (path is null || !File.Exists(path))
        {
            StatusMessage = "没有会话日志，无法生成交接包。";
            return;
        }
        var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetScopeRepair", "handoff");
        var result = await (_exporter ?? new HandoffExporter())
            .ExportAsync(path, outDir, null, CancellationToken.None).ConfigureAwait(true);
        (_copyToClipboard ?? System.Windows.Clipboard.SetText)(result.Prompt);
        Messages.Add("交给 AI：" + result.PackagePath);
        StatusMessage = "交接包已生成，提示词已复制到剪贴板：" + result.PackagePath;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        StatusMessage = "交接包生成失败：" + exception.Message;
    }
    finally { IsBusy = false; }
}

private async Task MarkUnresolvedAsync()
{
    var ok = await _service.MarkUnresolvedAsync(CancellationToken.None).ConfigureAwait(true);
    StatusMessage = ok
        ? "已记录「还是没好」。点「交给 AI」生成失败案例包。"
        : "没有可标记的修复批次（先执行一次修复）。";
}
```

文件顶部加 `using NetScope.Storage.Export;` 与 `using System.IO;`。
5. `MainViewModel.cs:40` 改：

```csharp
QuickRepair = new QuickRepairViewModel(
    new QuickRepairService(sink: new SessionRepairEventSink(services.Session, services.Clock)),
    sessionPath: () => services.Session.SessionPath,
    copyToClipboard: System.Windows.Clipboard.SetText);
```

6. `QuickRepairView.xaml`：在顶部按钮行（开代理/关代理/回滚/高风险模式 所在面板）追加两个按钮：

```xml
<Button Content="交给 AI" Command="{Binding HandoffCommand}" />
<Button Content="还是没好" Command="{Binding MarkUnresolvedCommand}" />
```

样式与同排按钮一致（沿用现有 Style/间距）。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/Desktop --filter "QuickRepairIntegration"` 然后 `dotnet test tests/Maintenance`
Expected: 全绿。

- [ ] **Step 6: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src tests
git commit -m "feat: one-click AI handoff from CLI and repair page; user-unresolved marker"
```

---

### Task 9: 深度融合（修复主页 / 诊断联动 / 首页入口）

**Files:**
- Create: `src/NetScope.Desktop/ViewModels/DiagnosisRepairMap.cs`
- Modify: `src/NetScope.Desktop/ViewModels/MainViewModel.cs`（导航改名 + 联动接线）、`QuickRepairViewModel.cs`（PreselectForDiagnosisAsync）、`DiagnosesViewModel.cs`（GoRepairCommand）、`HomeViewModel.cs`（RepairHintText + OpenRepairCommand）、`Views/DiagnosesView.xaml`、`Views/HomeView.xaml`
- Test: `tests/Desktop/QuickRepairIntegrationTests.cs`（追加）

**Interfaces:**
- Consumes: Task 8 `ActionRow.Kind`、QuickRepairViewModel 命令。
- Produces:
  - `public static class DiagnosisRepairMap { public static IReadOnlyList<string> ActionKindsFor(string ruleId); }`（DX-001→ClearProxyEnv/AlignProxyEnv/DisableSystemProxy；DX-006→RaiseInterfaceMetric/ResetInterfaceDns/RemoveGhostDevice；其余→空）
  - `QuickRepairViewModel.PreselectForDiagnosisAsync(string? ruleId)`（null=只体检；有值=体检+按映射预选）
  - `DiagnosesViewModel.GoRepairCommand`；构造函数加 `Func<string, Task>? openRepair = null`。
  - `HomeViewModel.RepairHintText`（string 属性）、`OpenRepairCommand`；构造函数加 `Func<Task>? openRepair = null`。
  - 导航：唯一「修复」页（标题从「快速修复」改为「修复」，副标题「本机真实体检 · TUN/残留/代理 · 可回滚」）。

- [ ] **Step 1: 写失败测试**

追加到 `QuickRepairIntegrationTests.cs`：

```csharp
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
    var snapshot = new MaintenanceSnapshot(
        DateTimeOffset.Now, true, 21, "WLAN", null, Array.Empty<int>(),
        new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        true, "127.0.0.1:7890", 7890, false, 0,
        Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());
    var service = new QuickRepairService(new StaticProbe(snapshot), new NoopExecutor(),
        new MaintenanceJournalStore(Path.Combine(_dir, "journal.json")),
        new GreenPostCheck());
    var vm = new QuickRepairViewModel(service);

    await vm.PreselectForDiagnosisAsync("DX-001");

    Assert.All(vm.Actions.Where(a => a.Kind is "ClearProxyEnv" or "DisableSystemProxy"),
        a => Assert.True(a.IsSelected));
    Assert.All(vm.Actions.Where(a => a.Kind is not ("ClearProxyEnv" or "DisableSystemProxy")),
        a => Assert.False(a.IsSelected));
}

private sealed class StaticProbe(MaintenanceSnapshot snapshot) : IMaintenanceProbe
{
    public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(snapshot);
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Desktop --filter "Diagnosis_map|Preselect_for"`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`DiagnosisRepairMap.cs`：

```csharp
namespace NetScope.Desktop.ViewModels;

/// <summary>
/// UI-layer mapping from diagnosis rule to repair action kinds. Diagnostic rules themselves
/// never carry actions; this only drives navigation and pre-selection.
/// </summary>
public static class DiagnosisRepairMap
{
    public static IReadOnlyList<string> ActionKindsFor(string ruleId) => ruleId switch
    {
        "DX-001" => new[] { "ClearProxyEnv", "AlignProxyEnv", "DisableSystemProxy" },
        "DX-006" => new[] { "RaiseInterfaceMetric", "ResetInterfaceDns", "RemoveGhostDevice" },
        _ => Array.Empty<string>(),
    };
}
```

`QuickRepairViewModel.cs` 加：

```csharp
/// <summary>Entry from diagnosis/home: refresh, then pre-select the actions mapped to the rule.</summary>
public async Task PreselectForDiagnosisAsync(string? ruleId)
{
    await RefreshAsync().ConfigureAwait(true);
    if (ruleId is null) return;
    var kinds = DiagnosisRepairMap.ActionKindsFor(ruleId);
    if (kinds.Count == 0)
    {
        StatusMessage = "诊断 " + ruleId + " 暂无对应修复动作（只报告）。";
        return;
    }
    foreach (var row in Actions) row.IsSelected = kinds.Contains(row.Kind);
    StatusMessage = "已按诊断 " + ruleId + " 预选修复动作；确认后执行。";
}
```

`DiagnosesViewModel.cs`：构造函数加 `Func<string, Task>? openRepair = null`，字段保存；加：

```csharp
public ICommand GoRepairCommand { get; }

private async Task GoRepairAsync()
{
    if (Selected is not null && _openRepair is not null)
        await _openRepair(Selected.RuleId).ConfigureAwait(true);
}
```

构造函数注册 `GoRepairCommand = new AsyncRelayCommand(GoRepairAsync, null, null);`。

`HomeViewModel.cs`：构造函数加 `Func<Task>? openRepair = null`；加：

```csharp
public ICommand OpenRepairCommand { get; }

private string _repairHintText = "修复：体检本机，发现可修复问题";
public string RepairHintText
{
    get => _repairHintText;
    set => Set(ref _repairHintText, value);
}
```

构造函数注册 `OpenRepairCommand = new AsyncRelayCommand(async () => { if (_openRepair is not null) await _openRepair().ConfigureAwait(true); }, null, null);`。

`MainViewModel.cs`：
1. Sections 里「快速修复」项改（图标几何沿用原项）：`new NavSection("修复", "本机真实体检 · TUN/残留/代理 · 可回滚", QuickRepair, "M12 2v4M12 18v4M4.9 4.9l2.8 2.8M16.3 16.3l2.8 2.8M2 12h4M18 12h4M4.9 19.1l2.8-2.8M16.3 7.7l2.8-2.8M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z")`。
2. 调整创建顺序：QuickRepair 先于 Diagnoses/Home 创建；接线：

```csharp
Home = new HomeViewModel(services.Session, () => _settings, DiagnoseAsync, HandleErrorAsync, CollectAsync,
    openRepair: () => OpenRepairAsync(null));
Diagnoses = new DiagnosesViewModel(ruleId => OpenRepairAsync(ruleId));
```

```csharp
private async Task OpenRepairAsync(string? ruleId)
{
    try
    {
        SelectedSection = Sections.First(section => ReferenceEquals(section.ViewModel, QuickRepair));
        await QuickRepair.PreselectForDiagnosisAsync(ruleId);
    }
    catch (Exception exception)
    {
        await HandleErrorAsync(exception);
    }
}
```

3. 构造函数末尾订阅（首页计数）：

```csharp
QuickRepair.PropertyChanged += (_, e) =>
{
    if (e.PropertyName is nameof(QuickRepairViewModel.HasPlan) or nameof(QuickRepairViewModel.SummaryText))
        _dispatcher.Post(() => Home.RepairHintText = QuickRepair.HasPlan
            ? "发现 " + QuickRepair.Actions.Count + " 项可执行修复动作 → 去修复"
            : "修复：体检本机，发现可修复问题");
};
```

4. `DiagnosesView.xaml`：在选中诊断的理由/下一步面板加按钮（沿用现有按钮样式）：

```xml
<Button Content="去修复" Command="{Binding GoRepairCommand}" />
```

5. `HomeView.xaml`：在操作区加：

```xml
<Button Content="修复网络" Command="{Binding OpenRepairCommand}" />
<TextBlock Text="{Binding RepairHintText}" />
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Desktop`
Expected: 全绿。

- [ ] **Step 5: 全构建 + Commit**

Run: `dotnet build NetScopeRepair.slnx`（0 警告）

```bash
git add src tests
git commit -m "feat(desktop): repair as first-class page with diagnosis linkage and home entry"
```

---

### Task 10: README 重写 + 发布 + 实机验收

**Files:**
- Modify: `README.md`
- 发布: `artifacts/M4-UnifiedRepair/`

**Interfaces:**
- Consumes: 全部前序任务。
- Produces: 与代码一致的 README；可双击的发布版；实机验收记录。

- [ ] **Step 1: 重写 README**

更新「当前状态」：统一修复引擎（旧 TUN 链路已删除，G3 历史报告保留链接）；能力清单 = 死代理/端口对齐/旧出口/幽灵网卡/静态 DNS/代理开关 + TUN 三字段 + 端口归属点名 + 修后自检 A~E + 失败事件进日志 + AI 交接包。「使用」：CLI 加 `netscope handoff`；页面列表 10→9（修复页唯一）。「边界」：TUN 只动 TUN 网卡、ActiveStore 重启重置、不自动 UAC、零脚本依赖。「后续」：内置 AI 助手为下一里程碑。测试数字以实际 `dotnet test` 输出为准。

- [ ] **Step 2: 全套件 + 发布**

```bash
dotnet build NetScopeRepair.slnx
dotnet test NetScopeRepair.slnx
dotnet publish src/NetScope.Desktop -c Release -o artifacts/M4-UnifiedRepair
dotnet publish src/NetScope.Cli -c Release -o artifacts/M4-UnifiedRepair/cli
```

Expected: 0 警告；测试全绿；发布产物生成。

- [ ] **Step 3: 离屏自检**

```bash
artifacts/M4-UnifiedRepair/NetScope.Desktop.exe --render-screens artifacts/M4-UnifiedRepair/screens
artifacts/M4-UnifiedRepair/NetScope.Desktop.exe --smoke-test artifacts/M4-UnifiedRepair/smoke.json
artifacts/M4-UnifiedRepair/cli/netscope.exe status
```

Expected: 9 页 PNG 渲染成功；smoke 真实只读采集成功；status 输出真实体检。

- [ ] **Step 4: 实机验收（需用户配合，逐条确认）**

1. 开客户端 TUN → app「修复」页体检 → 出现 TUN_PRIORITY → 执行（管理员）→ 自检通过；`netscope repair --restore` 还原三字段。
2. 制造一次真实失败（如关客户端后执行开代理）→ 事件进日志 → 「交给 AI」生成交接包 + 剪贴板 prompt → 粘贴给 Qoder → AI 按 guide 加新能力 → 闭环走通。
3. 非管理员启动 → TUN/度量动作如实跳过不假装成功。

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs: unified repair engine + AI handoff loop (M4)"
```

---

## 自审记录（写完计划后已核对）

- **Spec 覆盖**：删除范围→Task 1；TUN 采集/修复→Task 2/3/4；端口归属→Task 2/3；PostCheck→Task 5；真打/ARP/PAC/源地址/config→Task 2/4（PAC 报告 M3 已有，probe 88-89 行，无需新增）；失败日志→Task 6；交接包→Task 7/8；深度融合→Task 9；验收→Task 10。
- **类型一致性**：`TunInterfaceSnapshot`/`PortOwnerSnapshot`/`PostCheckObservation`/`PostCheckResult`/`RepairEvent`/`IRepairEventSink`/`HandoffResult` 在各任务间签名一致；`RepairReport.PostCheck` 与 `MaintenanceSnapshot` 新字段均为末尾可选参，不破坏既有调用。
- **已知陷阱**：既有 MaintenanceTests 的 Service 辅助必须在 Task 5 改传 FakePostCheck，否则非 DryRun 测试走真实网络；Storage→Maintenance 新引用无环（Maintenance 仅引用 Domain）；MachineFacts/InterfaceFact 需改 public 供测试。