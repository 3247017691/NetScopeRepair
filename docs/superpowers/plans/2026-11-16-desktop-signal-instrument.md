# 「信号仪」桌面重构 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 NetScope.Desktop 重构为「修复一等公民」的信号仪界面：白底 Qoder-light 中性色 + IBM Plex + 实时网络路径仪器主角 + 双层左栏 + 分阶段修复流 + 克制且按耗时分级反馈的动效。

**Architecture:** 只改 UI 层（`src/NetScope.Desktop`）。保持 `Theme.xaml` 的 token *key* 稳定、只换 *值*，二级页面自动继承。仪器是 `MaintenanceSnapshot`/`PostCheckResult` 的只读投影（`NetworkPathModel`），修复阶段（`RepairStage`）与问题↔修复配对（`RepairItemRow`）都是 VM 派生状态。修复引擎/契约/采集/CLI 一律不动。

**Tech Stack:** .NET 10 (`net10.0-windows`)、WPF、xUnit（`tests/Desktop/NetScope.Desktop.Tests.csproj`）、IBM Plex Sans/Mono（OFL 内嵌）。

**Spec:** `docs/superpowers/specs/2026-11-16-desktop-signal-instrument-design.md`

## Global Constraints

- 只改 `src/NetScope.Desktop` 与 `tests/Desktop`；不动 Maintenance/Domain/Collectors/Probes/Storage/Diagnostics/Adapters/Cli。
- 安全语义逐字保留：勾选 + 二次确认才写入；`ApplyButtonText`/`RestoreButtonText` 的 pending 态文案不变；journal 回滚；非管理员如实跳过。
- 进度侧通道 `RepairProgress` 契约不变，不写入 NDJSON/SQLite。
- 所有动效闸门 = `SystemParameters.ClientAreaAnimation`（沿用 `Controls/Spinner.cs:68` 的判定）；关闭时静止且无内容隐藏。
- 未知保持未知：仪器未测得显示「—」，绝不暗示健康。
- 构建必须 `dotnet build NetScopeRepair.slnx` 0 警告 0 错误；测试全绿。
- 文案：大白话、主动语态、按钮=动词+对象；不用 → 装饰、· 元信息串、全大写眉标。
- 字体：UI=IBM Plex Sans（CJK 回退 Microsoft YaHei UI），数据=IBM Plex Mono；Logo/ico 不动。
- 信号色 `#0E7C66`；主底 `#FFFFFF`；导航栏 `#F7F8FA`；发丝线 `#E8EAED`；墨 `#1F2329`。

## File Structure

- `Resources/Theme.xaml` — 调色板 token 值、UiFont/MonoFont、PrimaryButton、新增 Unknown/Recess、字阶。（改）
- `Resources/Fonts/IBMPlexSans-*.ttf`、`IBMPlexMono-*.ttf`、`IBMPlex-LICENSE.txt` — 内嵌字体。（增；删 Inter-*）
- `NetScope.Desktop.csproj` — 字体资源。（改）
- `ViewModels/Rows.cs` — `UiText` 冻结画刷同步新值。（改）
- `ViewModels/NetworkPathModel.cs` — 仪器只读投影 + `PathStationState`/`PathStation`。（增）
- `ViewModels/RepairStreamRows.cs` — 新增 `RepairStage` 枚举。（改）
- `ViewModels/QuickRepairViewModel.cs` — `Stage`/`VerdictText`/`NetworkPath`/`RepairItems`；`ActionRow` 增 `Current`/`Desired`；`RepairItemRow`；handoff 反馈。（改）
- `ViewModels/MainViewModel.cs` — `NavSection.Group`、Sections 重排、默认修复、顶栏会话控制。（改）
- `Views/ShellView.xaml` — 双层左栏 + 简化顶栏。（改）
- `Controls/NetworkPathInstrument.xaml(.cs)` — 仪器迹线控件。（增）
- `Views/QuickRepairView.xaml` — 阶段流 + 仪器 + 配对列表 + 工具条。（改）
- `Services/ScreenRenderer.cs` — 适配 8 页渲染。（改，若硬编码页数）
- `tests/Desktop/*` — 新增投影/阶段/配对/IA 测试；更新旧色值/页数断言。（改/增）

---

### Task 1: 设计 token + UiText 画刷

**Files:**
- Modify: `src/NetScope.Desktop/Resources/Theme.xaml:4-78`（Color 值 + UiFont + 新增 token）
- Modify: `src/NetScope.Desktop/Resources/Theme.xaml:177-217`（PrimaryButton 背景）
- Modify: `src/NetScope.Desktop/ViewModels/Rows.cs:159-178`（UiText 画刷）
- Test: `tests/Desktop/DesignTokensTests.cs`

**Interfaces:**
- Produces: token brush keys 不变（`Canvas/Rail/Surface/SubtleSurface/HoverSurface/Line/LineSubtle/Ink/Muted/SubtleText/Accent/AccentInteractive/AccentLight/AccentBorder/AccentDeep/Ok/OkBg/OkBorder/Warn*/Bad*/Gap*/HoverBorder/ScrollThumbHover`），新增 `Unknown` brush；`UiText.AccentBrush` 等为新值。后续所有任务依赖这些 key。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/Desktop/DesignTokensTests.cs
using System.Windows.Media;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class DesignTokensTests
{
    private static byte[] Rgb(Brush b) => new[]
    {
        ((SolidColorBrush)b).Color.R, ((SolidColorBrush)b).Color.G, ((SolidColorBrush)b).Color.B
    };

    [Fact]
    public void Signal_accent_is_teal()
    {
        Assert.Equal(new byte[] { 0x0E, 0x7C, 0x66 }, Rgb(UiText.AccentBrush));
    }

    [Fact]
    public void Status_brushes_are_aa_tuned()
    {
        Assert.Equal(new byte[] { 0x17, 0x7A, 0x4C }, Rgb(UiText.OkBrush));
        Assert.Equal(new byte[] { 0xB4, 0x53, 0x09 }, Rgb(UiText.WarnBrush));
        Assert.Equal(new byte[] { 0xC0, 0x39, 0x2B }, Rgb(UiText.BadBrush));
    }

    [Fact]
    public void Soft_background_mapping_still_resolves_by_reference()
    {
        Assert.Same(UiText.OkBgBrush, UiText.SoftBackground(UiText.OkBrush));
        Assert.Same(UiText.AccentBgBrush, UiText.SoftBackground(UiText.AccentBrush));
        Assert.Same(UiText.GapBgBrush, UiText.SoftBackground(UiText.GapBrush));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~DesignTokensTests"`
Expected: FAIL（AccentBrush 仍是 0x5E6AD2）。

- [ ] **Step 3: 替换 Theme.xaml 调色板值**

把 `Theme.xaml:4-39` 的 Color 值按 spec §4.1 表替换（key 不变）。关键值：
`CanvasColor #FFFFFF`、`RailColor #F7F8FA`、`SurfaceColor #FFFFFF`、`SubtleSurfaceColor #F8F9FB`、`HoverSurfaceColor #F0F2F5`、`LineColor #E8EAED`、`LineSubtleColor #EFF1F4`、`InkColor #1F2329`、`MutedColor #6B7280`、`SubtleTextColor #9AA0A8`、`AccentColor #0E7C66`、`AccentInteractiveColor #0C6B58`、`AccentLightColor #E6F2EE`、`AccentBorderColor #BFE0D6`、`AccentDeepColor #0A5A4A`、`OkColor #177A4C`/`OkBgColor #E7F6EE`/`OkBorderColor #BFE6D2`、`WarnColor #B45309`/`WarnBgColor #FBF0E2`/`WarnBorderColor #F0D3A8`、`BadColor #C0392B`/`BadBgColor #FBEAEA`/`BadBorderColor #F2C4C4`、`GapColor #6B7280`/`GapBgColor #F8F9FB`/`GapBorderColor #E8EAED`、`HoverBorderColor #D3D8DE`、`ScrollThumbHoverColor #B6BCC4`。

在 Gap brush 之后新增：
```xml
<Color x:Key="UnknownColor">#C4C9D0</Color>
<SolidColorBrush x:Key="Unknown" Color="{StaticResource UnknownColor}" />
<SolidColorBrush x:Key="Recess" Color="{StaticResource SubtleSurfaceColor}" />
```

- [ ] **Step 4: PrimaryButton 改信号青绿**

`Theme.xaml` PrimaryButton：`Background` `#151B21`→`{StaticResource Accent}`；hover `#282F35`→`{StaticResource AccentInteractive}`；pressed `#373E45`→`{StaticResource AccentDeep}`。

- [ ] **Step 5: 同步 UiText 画刷（Rows.cs:159-178）**

```csharp
public static readonly Brush OkBrush = Frozen(0x17, 0x7A, 0x4C);
public static readonly Brush WarnBrush = Frozen(0xB4, 0x53, 0x09);
public static readonly Brush BadBrush = Frozen(0xC0, 0x39, 0x2B);
public static readonly Brush GapBrush = Frozen(0x6B, 0x72, 0x80);
public static readonly Brush AccentBrush = Frozen(0x0E, 0x7C, 0x66);
public static readonly Brush OkBgBrush = Frozen(0xE7, 0xF6, 0xEE);
public static readonly Brush OkBorderBrush = Frozen(0xBF, 0xE6, 0xD2);
public static readonly Brush WarnBgBrush = Frozen(0xFB, 0xF0, 0xE2);
public static readonly Brush WarnBorderBrush = Frozen(0xF0, 0xD3, 0xA8);
public static readonly Brush BadBgBrush = Frozen(0xFB, 0xEA, 0xEA);
public static readonly Brush BadBorderBrush = Frozen(0xF2, 0xC4, 0xC4);
public static readonly Brush GapBgBrush = Frozen(0xF8, 0xF9, 0xFB);
public static readonly Brush GapBorderBrush = Frozen(0xE8, 0xEA, 0xED);
public static readonly Brush AccentBgBrush = Frozen(0xE6, 0xF2, 0xEE);
public static readonly Brush AccentBorderBrush = Frozen(0xBF, 0xE0, 0xD6);
```

- [ ] **Step 6: 运行测试通过 + 构建**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~DesignTokensTests"` → PASS
Run: `dotnet build NetScopeRepair.slnx` → 0 警告 0 错误（若有旧色值断言失败，见 Task 11 统一更新；此处先确保新测试绿）。

- [ ] **Step 7: Commit**

```bash
git add src/NetScope.Desktop/Resources/Theme.xaml src/NetScope.Desktop/ViewModels/Rows.cs tests/Desktop/DesignTokensTests.cs
git commit -m "feat(desktop-ui): signal-instrument palette tokens + UiText brushes"
```

---

### Task 2: IBM Plex 字体内嵌

**Files:**
- Create: `src/NetScope.Desktop/Resources/Fonts/IBMPlexSans-{Regular,Medium,SemiBold,Bold}.ttf`、`IBMPlexMono-{Regular,Medium}.ttf`、`IBMPlex-LICENSE.txt`
- Delete: `src/NetScope.Desktop/Resources/Fonts/Inter-*.ttf`、`Inter-LICENSE.txt`
- Modify: `src/NetScope.Desktop/NetScope.Desktop.csproj:21-25`
- Modify: `src/NetScope.Desktop/Resources/Theme.xaml:78`（UiFont）+ Mono 样式 `:135-141`

**Interfaces:**
- Produces: `UiFont` = Plex Sans pack URI；`MonoFont` key = Plex Mono pack URI。后续视图用 `{StaticResource UiFont}`/`{StaticResource MonoFont}`。

- [ ] **Step 1: 获取 OFL 字体**

Run（在仓库根，用 gh 下载官方 IBM Plex release 并解压所需 TTF）:
```bash
gh release download --repo IBM/plex --pattern "*.zip" --dir C:/Temp/plex --clobber
```
从解压包取 `ibm-plex-sans` 的 `IBMPlexSans-Regular.ttf`/`-Medium.ttf`/`-SemiBold.ttf`/`-Bold.ttf` 与 `ibm-plex-mono` 的 `IBMPlexMono-Regular.ttf`/`-Medium.ttf`，复制到 `src/NetScope.Desktop/Resources/Fonts/`；复制其 `LICENSE.txt`（OFL）为 `IBMPlex-LICENSE.txt`。若 release 资产名不同，用 `gh release view --repo IBM/plex` 查实际资产名再下载。

- [ ] **Step 2: 替换 csproj 资源**

`NetScope.Desktop.csproj` 把 Inter 四行 `<Resource Include="Resources\Fonts\Inter-*.ttf" />` 与 Inter-LICENSE 的 `<Content>` 替换为：
```xml
<Resource Include="Resources\Fonts\IBMPlexSans-Regular.ttf" />
<Resource Include="Resources\Fonts\IBMPlexSans-Medium.ttf" />
<Resource Include="Resources\Fonts\IBMPlexSans-SemiBold.ttf" />
<Resource Include="Resources\Fonts\IBMPlexSans-Bold.ttf" />
<Resource Include="Resources\Fonts\IBMPlexMono-Regular.ttf" />
<Resource Include="Resources\Fonts\IBMPlexMono-Medium.ttf" />
<Content Include="Resources\Fonts\IBMPlex-LICENSE.txt" Link="Plex-LICENSE.txt" CopyToOutputDirectory="PreserveNewest" />
```
删除磁盘上的 Inter-*.ttf 与 Inter-LICENSE.txt。

- [ ] **Step 3: 更新 FontFamily**

`Theme.xaml:78`：
```xml
<FontFamily x:Key="UiFont">pack://application:,,,/Resources/Fonts/#IBM Plex Sans, Microsoft YaHei UI</FontFamily>
<FontFamily x:Key="MonoFont">pack://application:,,,/Resources/Fonts/#IBM Plex Mono, Consolas</FontFamily>
```
`Mono` 样式（`:135-141`）的 `FontFamily` `Cascadia Mono, Consolas, Courier New` → `{StaticResource MonoFont}`。

- [ ] **Step 4: 构建 + 字重解析验证**

Run: `dotnet build NetScopeRepair.slnx` → 0 警告。
验证（临时 STA 控制台或测试）：`new FontFamily(new Uri("pack://application:,,,/Resources/Fonts/"), "#IBM Plex Sans").GetTypefaces()` 应含 Normal/Medium/SemiBold/Bold。把该断言加入 `tests/Desktop/DesignTokensTests.cs`：
```csharp
[Fact]
public void Plex_sans_resolves_four_weights()
{
    var fam = new System.Windows.Media.FontFamily(
        new Uri("pack://application:,,,/Resources/Fonts/"), "#IBM Plex Sans");
    var weights = fam.GetTypefaces().Select(t => t.Weight).ToHashSet();
    Assert.Contains(System.Windows.FontWeights.Normal, weights);
    Assert.Contains(System.Windows.FontWeights.Medium, weights);
    Assert.Contains(System.Windows.FontWeights.SemiBold, weights);
    Assert.Contains(System.Windows.FontWeights.Bold, weights);
}
```
Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~Plex_sans"` → PASS（需 STA；若测试程序集非 STA，用 `[StaFact]` 或在 STA 线程跑，参照现有 Desktop 测试约定）。

- [ ] **Step 5: Commit**

```bash
git add src/NetScope.Desktop/Resources/Fonts src/NetScope.Desktop/NetScope.Desktop.csproj src/NetScope.Desktop/Resources/Theme.xaml tests/Desktop/DesignTokensTests.cs
git commit -m "feat(desktop-ui): embed IBM Plex Sans/Mono, replace Inter"
```

---

### Task 3: NetworkPathModel 仪器投影（TDD）

**Files:**
- Create: `src/NetScope.Desktop/ViewModels/NetworkPathModel.cs`
- Test: `tests/Desktop/NetworkPathModelTests.cs`

**Interfaces:**
- Consumes: `MaintenanceSnapshot`（`MaintenanceModels.cs:58`，字段 `SystemProxyEnabled/SystemProxyPort/WorkingProxyPort/ActiveEgressIndex/ActiveEgressAlias`）、`PostCheckResult`（`PostCheck.cs:8`，`.Observation.PhysicalInternet`）。
- Produces:
  - `enum PathStationState { Ok, Fault, Unknown }`
  - `record PathStation(string Label, string ValueText, PathStationState State, string Note)`
  - `record NetworkPathModel(PathStation Local, PathStation Proxy, PathStation Egress, PathStation Internet, string PortReadout, string LatencyReadout)`
  - `static NetworkPathModel NetworkPathModel.FromSnapshot(MaintenanceSnapshot snapshot, PostCheckResult? postCheck)`
  - `static NetworkPathModel NetworkPathModel.Unmeasured { get; }`（全 Unknown，读数「—」）

- [ ] **Step 1: 写失败测试**

```csharp
// tests/Desktop/NetworkPathModelTests.cs
using NetScope.Maintenance;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class NetworkPathModelTests
{
    private static MaintenanceSnapshot Snap(int? workingPort, bool sysProxyEnabled, int sysPort,
        int egressIndex = 21, string egressAlias = "WLAN") => new(
        DateTimeOffset.Now, true, egressIndex, egressAlias, workingPort, Array.Empty<int>(),
        Array.Empty<ProxyLayerSnapshot>(), sysProxyEnabled, "127.0.0.1:" + sysPort, sysPort,
        false, 0, Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());

    [Fact]
    public void Dead_configured_port_is_fault()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(null, true, 7890), null);
        Assert.Equal(PathStationState.Fault, model.Proxy.State);
        Assert.Equal("7890", model.Proxy.ValueText);
        Assert.Equal("无监听", model.Proxy.Note);
        Assert.Equal("7890", model.PortReadout);
    }

    [Fact]
    public void Aligned_live_port_is_ok()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), null);
        Assert.Equal(PathStationState.Ok, model.Proxy.State);
        Assert.Equal("7897", model.Proxy.ValueText);
    }

    [Fact]
    public void No_proxy_configured_is_unknown()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(null, false, 0), null);
        Assert.Equal(PathStationState.Unknown, model.Proxy.State);
        Assert.Equal("—", model.PortReadout);
    }

    [Fact]
    public void Internet_unknown_without_postcheck()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), null);
        Assert.Equal(PathStationState.Unknown, model.Internet.State);
        Assert.Equal("—", model.LatencyReadout);
    }

    [Fact]
    public void Internet_reflects_postcheck_physical_internet()
    {
        var post = new PostCheckResult(true, "None", Array.Empty<string>(),
            new PostCheckObservation(21, true, true, true, true, true));
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), post);
        Assert.Equal(PathStationState.Ok, model.Internet.State);
    }

    [Fact]
    public void Missing_egress_is_unknown()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897, egressIndex: 0, egressAlias: ""), null);
        Assert.Equal(PathStationState.Unknown, model.Egress.State);
    }

    [Fact]
    public void Unmeasured_is_all_unknown()
    {
        var model = NetworkPathModel.Unmeasured;
        Assert.Equal(PathStationState.Unknown, model.Proxy.State);
        Assert.Equal(PathStationState.Unknown, model.Internet.State);
        Assert.Equal("—", model.PortReadout);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~NetworkPathModelTests"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现投影**

```csharp
// src/NetScope.Desktop/ViewModels/NetworkPathModel.cs
using NetScope.Maintenance;

namespace NetScope.Desktop.ViewModels;

public enum PathStationState { Ok, Fault, Unknown }

public sealed record PathStation(string Label, string ValueText, PathStationState State, string Note);

public sealed record NetworkPathModel(
    PathStation Local, PathStation Proxy, PathStation Egress, PathStation Internet,
    string PortReadout, string LatencyReadout)
{
    public static NetworkPathModel Unmeasured { get; } = new(
        new PathStation("本机", "本机", PathStationState.Unknown, ""),
        new PathStation("代理端口", "—", PathStationState.Unknown, "未测得"),
        new PathStation("出口", "—", PathStationState.Unknown, "未测得"),
        new PathStation("互联网", "—", PathStationState.Unknown, "未测得"),
        "—", "—");

    public static NetworkPathModel FromSnapshot(MaintenanceSnapshot snapshot, PostCheckResult? postCheck)
    {
        var local = new PathStation("本机", "本机", PathStationState.Ok, "");

        PathStation proxy;
        if (snapshot.WorkingProxyPort is int live && (!snapshot.SystemProxyEnabled || snapshot.SystemProxyPort == live))
            proxy = new PathStation("代理端口", live.ToString(), PathStationState.Ok, "监听中");
        else if (snapshot.SystemProxyEnabled && snapshot.SystemProxyPort > 0)
            proxy = new PathStation("代理端口", snapshot.SystemProxyPort.ToString(), PathStationState.Fault, "无监听");
        else
            proxy = new PathStation("代理端口", "—", PathStationState.Unknown, "未配置代理");

        var egress = snapshot.ActiveEgressIndex > 0 && !string.IsNullOrWhiteSpace(snapshot.ActiveEgressAlias)
            ? new PathStation("出口", snapshot.ActiveEgressAlias, PathStationState.Ok, "if=" + snapshot.ActiveEgressIndex)
            : new PathStation("出口", "—", PathStationState.Unknown, "未测得");

        PathStation internet;
        if (postCheck is null)
            internet = new PathStation("互联网", "—", PathStationState.Unknown, "未测得");
        else
            internet = postCheck.Observation.PhysicalInternet
                ? new PathStation("互联网", "可达", PathStationState.Ok, "实测")
                : new PathStation("互联网", "不可达", PathStationState.Fault, "实测");

        return new NetworkPathModel(local, proxy, egress, internet, proxy.ValueText, "—");
    }
}
```

- [ ] **Step 4: 运行通过**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~NetworkPathModelTests"` → PASS（7 项）。

- [ ] **Step 5: Commit**

```bash
git add src/NetScope.Desktop/ViewModels/NetworkPathModel.cs tests/Desktop/NetworkPathModelTests.cs
git commit -m "feat(desktop-ui): read-only network-path instrument projection"
```

---

### Task 4: 修复阶段 + 结论 + 问题↔修复配对（TDD）

**Files:**
- Modify: `src/NetScope.Desktop/ViewModels/RepairStreamRows.cs`（增 `RepairStage`）
- Modify: `src/NetScope.Desktop/ViewModels/QuickRepairViewModel.cs`（`ActionRow` 增 Current/Desired；`RepairItemRow`；`Stage`/`VerdictText`/`NetworkPath`/`RepairItems`）
- Test: `tests/Desktop/RepairStageAndPairingTests.cs`

**Interfaces:**
- Consumes: `NetworkPathModel.FromSnapshot`（Task 3）、`DiagnosisRepairMap.ActionKindsFor`（`DiagnosisRepairMap.cs:9`）、`FindingRow`/`ActionRow`（`QuickRepairViewModel.cs:14-59`）。
- Produces:
  - `enum RepairStage { Inspect, Review, Execute, Verify }`（RepairStreamRows.cs）
  - `ActionRow.Current`/`ActionRow.Desired`（`string?`）
  - `sealed class RepairItemRow : ViewModelBase`，ctor `(FindingRow? finding, ActionRow action)`；props `ProblemText`/`FixText`/`CodeRef`/`BeforeAfterText`/`RiskBadge`/`IsSelected`（双向委托 Action.IsSelected）/`HasFinding`。
  - `QuickRepairViewModel.Stage`（RepairStage，派生）、`VerdictText`（string）、`NetworkPath`（NetworkPathModel）、`RepairItems`（ObservableCollection<RepairItemRow>）。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/Desktop/RepairStageAndPairingTests.cs
using NetScope.Maintenance;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class RepairStageAndPairingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netscope-stage-" + Guid.NewGuid().ToString("N"));
    public RepairStageAndPairingTests() => Directory.CreateDirectory(_dir);

    private QuickRepairService Service(MaintenanceSnapshot snap) => new(
        new StaticProbe(snap), new NoopExecutor(),
        new MaintenanceJournalStore(Path.Combine(_dir, "journal.json")), new GreenPostCheck());

    private static MaintenanceSnapshot DeadProxy() => new(
        DateTimeOffset.Now, true, 21, "WLAN", null, Array.Empty<int>(),
        new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        true, "127.0.0.1:7890", 7890, false, 0,
        Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());

    [Fact]
    public void Stage_is_inspect_before_plan()
    {
        var vm = new QuickRepairViewModel();
        Assert.Equal(RepairStage.Inspect, vm.Stage);
        Assert.Equal(NetworkPathModel.Unmeasured.Proxy.State, vm.NetworkPath.Proxy.State);
    }

    [Fact]
    public async Task Stage_is_review_after_refresh_and_path_populated()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync(null);
        Assert.Equal(RepairStage.Review, vm.Stage);
        Assert.Equal(PathStationState.Fault, vm.NetworkPath.Proxy.State);
        Assert.NotEmpty(vm.VerdictText);
    }

    [Fact]
    public async Task Repair_items_pair_dead_proxy_finding_with_actions()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync("DX-001");
        Assert.NotEmpty(vm.RepairItems);
        Assert.Contains(vm.RepairItems, i => i.Action.Kind == "ClearProxyEnv");
        var paired = vm.RepairItems.First(i => i.Action.Kind == "ClearProxyEnv");
        Assert.True(paired.HasFinding);
        Assert.False(string.IsNullOrEmpty(paired.ProblemText));
    }

    [Fact]
    public async Task Action_row_exposes_before_after()
    {
        var vm = new QuickRepairViewModel(Service(DeadProxy()));
        await vm.PreselectForDiagnosisAsync(null);
        Assert.All(vm.Actions, a => Assert.NotNull(a.Title));
        // Current/Desired 来自 RepairAction，可能为 null，但属性必须存在
        _ = vm.Actions[0].Current;
        _ = vm.Actions[0].Desired;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class StaticProbe(MaintenanceSnapshot s) : IMaintenanceProbe
    { public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(s); }
    private sealed class NoopExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction a, CancellationToken ct)
            => Task.FromResult(new ActionResult(a.Id, a.Title, true, a.Current, a.Desired, null));
        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry e, CancellationToken ct) => Task.FromResult((true, "ok"));
        public Task BroadcastEnvironmentAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class GreenPostCheck : IPostCheckProbe
    { public Task<PostCheckObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(new PostCheckObservation(21, true, true, true, true, false)); }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~RepairStageAndPairingTests"` → FAIL。

- [ ] **Step 3: 增 RepairStage 枚举（RepairStreamRows.cs，RepairRunState 之后）**

```csharp
public enum RepairStage { Inspect, Review, Execute, Verify }
```

- [ ] **Step 4: ActionRow 增 Current/Desired（QuickRepairViewModel.cs:18-42）**

ctor 内增 `Current = action.Current; Desired = action.Desired;`；增属性：
```csharp
public string? Current { get; }
public string? Desired { get; }
```

- [ ] **Step 5: 增 RepairItemRow（QuickRepairViewModel.cs，FindingRow 之后）**

```csharp
public sealed class RepairItemRow : ViewModelBase
{
    public RepairItemRow(FindingRow? finding, ActionRow action)
    {
        Finding = finding;
        Action = action;
    }
    public FindingRow? Finding { get; }
    public ActionRow Action { get; }
    public bool HasFinding => Finding is not null;
    public string ProblemText => Finding?.Title ?? Action.Title;
    public string CodeRef => Finding?.Code ?? "";
    public string FixText => Action.Title;
    public string BeforeAfterText =>
        Action.Current is null && Action.Desired is null ? ""
        : (Action.Current ?? "未设置") + " → " + (Action.Desired ?? "清除");
    public string RiskBadge => Action.Badge;
    public bool IsSelected
    {
        get => Action.IsSelected;
        set { if (Action.IsSelected != value) { Action.IsSelected = value; Raise(); } }
    }
}
```
（注：`BeforeAfterText` 的「→」是数据 before→after 连接符，属真实数据表达，非按钮装饰箭头，保留。）

- [ ] **Step 6: VM 增 Stage/VerdictText/NetworkPath/RepairItems**

字段：`private NetworkPathModel _networkPath = NetworkPathModel.Unmeasured;`
属性：
```csharp
public ObservableCollection<RepairItemRow> RepairItems { get; } = new();

public NetworkPathModel NetworkPath
{
    get => _networkPath;
    private set => Set(ref _networkPath, value);
}

public RepairStage Stage =>
    !HasPlan ? RepairStage.Inspect
    : IsBusy && _writeOperation ? RepairStage.Execute
    : RunState is RepairRunState.Success or RepairRunState.Failed ? RepairStage.Verify
    : RepairStage.Review;

public string VerdictText =>
    !HasPlan ? "尚未体检。点击「体检本机」读取真实网络状态（只读，不修改任何设置）。"
    : Findings.Count == 0 ? "本次体检未发现可修复问题。"
    : RunState is RepairRunState.Success ? "修复已执行，修后自检见下方实测结果。"
    : Findings[0].Title + "。" + Findings[0].Detail;
```
在 `HasPlan`/`RunState`/`IsBusy` 的 setter 里追加 `Raise(nameof(Stage)); Raise(nameof(VerdictText));`。
在 `LoadPlanAsync`（`:261-285`）末尾（`HasPlan = true;` 之前）增：
```csharp
NetworkPath = NetworkPathModel.FromSnapshot(snapshot, _lastPostCheck);
RepairItems.Clear();
foreach (var action in Actions)
{
    var finding = Findings.FirstOrDefault(f => DiagnosisRepairMap.ActionKindsFor(f.Code).Contains(action.Kind));
    RepairItems.Add(new RepairItemRow(finding, action));
}
Raise(nameof(VerdictText));
```
增字段 `private PostCheckResult? _lastPostCheck;`，在 `ApplyAsync` 拿到 `report` 后赋 `_lastPostCheck = report.PostCheck;`（`RepairReport.PostCheck` 存在，`MaintenanceModels.cs:123`）。

- [ ] **Step 7: 运行通过 + 构建**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~RepairStageAndPairingTests"` → PASS。
Run: `dotnet build NetScopeRepair.slnx` → 0 警告。

- [ ] **Step 8: Commit**

```bash
git add src/NetScope.Desktop/ViewModels/RepairStreamRows.cs src/NetScope.Desktop/ViewModels/QuickRepairViewModel.cs tests/Desktop/RepairStageAndPairingTests.cs
git commit -m "feat(desktop-ui): repair stage, verdict, problem-fix pairing, instrument binding"
```

---

### Task 5: 双层左栏 + 信息架构

**Files:**
- Modify: `src/NetScope.Desktop/ViewModels/MainViewModel.cs:8`（NavSection 增 Group）`:52-73`（Sections）
- Modify: `src/NetScope.Desktop/Views/ShellView.xaml:15-108`（左栏）`:118-139`（顶栏）
- Test: `tests/Desktop/ShellNavigationTests.cs`

**Interfaces:**
- Consumes: `QuickRepairViewModel`（Task 4）、`HomeViewModel`（会话控制）。
- Produces: `enum NavGroup { Primary, Secondary, Footer }`；`NavSection.Group`；`MainViewModel.Sections` 8 项首项为修复；`MainViewModel.PrimarySection`/`SecondarySections`/`FooterSections`。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/Desktop/ShellNavigationTests.cs
using NetScope.Desktop.ViewModels;
using NetScope.Desktop.Services;

namespace NetScope.Desktop.Tests;

public sealed class ShellNavigationTests
{
    // 构造方式镜像 ReviewRegressionTests.cs:103-107；TempAppPaths/TestClock/StubSnapshotCollector/
    // StubProbeRunner 来自 TestDoubles.cs；ObservationSession/HttpProbeTransport 命名空间同现有测试。
    [Fact]
    public void Repair_is_first_and_default()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-1", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        Assert.Equal(8, vm.Sections.Count);
        Assert.Same(vm.QuickRepair, vm.Sections[0].ViewModel);
        Assert.Same(vm.QuickRepair, vm.SelectedSection.ViewModel);
        Assert.Equal(NavGroup.Primary, vm.Sections[0].Group);
    }

    [Fact]
    public void Secondary_and_footer_groups_are_quiet()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-2", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        Assert.Equal(5, vm.SecondarySections.Count());
        Assert.Equal(2, vm.FooterSections.Count());
        Assert.DoesNotContain(vm.Sections, s => s.Title == "首页");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~ShellNavigationTests"` → FAIL。

- [ ] **Step 3: NavSection 增 Group + NavGroup 枚举（MainViewModel.cs:8）**

```csharp
public enum NavGroup { Primary, Secondary, Footer }
public sealed record NavSection(string Title, string Caption, ViewModelBase ViewModel, string IconGeometry = "", NavGroup Group = NavGroup.Secondary);
```

- [ ] **Step 4: 重排 Sections（MainViewModel.cs:52-73）**

修复为首项（Group=Primary，扳手 geometry），二级组 5 项（诊断与证据/时间线/多客户端/导出/案例回放，Group=Secondary），设置/帮助 Group=Footer。移除「首页」导航项。扳手 geometry：
`M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z`
`_selectedSection = Sections[0];`（修复）。增：
```csharp
public NavSection PrimarySection => Sections.First(s => s.Group == NavGroup.Primary);
public IEnumerable<NavSection> SecondarySections => Sections.Where(s => s.Group == NavGroup.Secondary);
public IEnumerable<NavSection> FooterSections => Sections.Where(s => s.Group == NavGroup.Footer);
```
`OpenRepairAsync`（`:220-231`）改用 `SelectedSection = PrimarySection;`。

- [ ] **Step 5: ShellView 双层左栏**

把 `ShellView.xaml:47-106` 的单一 ListBox 替换为：顶部主入口（绑定 `PrimarySection`，panel 底 + 发丝边 + 扳手图标 + live 点）→ 分隔线 + 「观察与工具」组标题 → 二级 ListBox（`SecondarySections`，安静样式）→ 栏底 footer ListBox（`FooterSections`）。主入口模板用 `Surface` 底 + `Line` 边 + `Accent` 图标；二级项沿用现有 ListBoxItem 模板但 Foreground=`Muted`、选中=`Accent`。品牌区 Logo 不动。

- [ ] **Step 6: 顶栏会话控制**

`ShellView.xaml:126-137` 右侧保留状态 pill + `开始观察`/`暂停`/`立即诊断`（绑定 Home 命令，全局可用）；把「载入合成演示」按钮移到设置页（Task 10）或顶栏 overflow，保留 `LoadDemoCommand` 绑定与演示横幅。

- [ ] **Step 7: 运行通过 + 构建 + 渲染**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~ShellNavigationTests"` → PASS。
Run: `dotnet build NetScopeRepair.slnx` → 0 警告。
渲染：`dotnet run --project src/NetScope.Desktop -- --render-screens C:/Temp/ns-shots`（先确认 ScreenRenderer 不硬编码 9 页；若硬编码改为遍历 `Sections`）。目视左栏双层。

- [ ] **Step 8: Commit**

```bash
git add src/NetScope.Desktop/ViewModels/MainViewModel.cs src/NetScope.Desktop/Views/ShellView.xaml src/NetScope.Desktop/Services/ScreenRenderer.cs tests/Desktop/ShellNavigationTests.cs
git commit -m "feat(desktop-ui): two-tier rail, repair-first navigation, repair default"
```

---

### Task 6: NetworkPathInstrument 控件

**Files:**
- Create: `src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml`
- Create: `src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml.cs`

**Interfaces:**
- Consumes: `NetworkPathModel`/`PathStationState`（Task 3）、token brushes（Task 1）、`SystemParameters.ClientAreaAnimation`。
- Produces: `UserControl NetworkPathInstrument`，`DataContext` 为 `NetworkPathModel`；依赖属性 `Model`（NetworkPathModel）；迹线 draw-in 在 Model 变化且动画开启时播放。

- [ ] **Step 1: XAML（Viewbox + Canvas，760×128 坐标，对齐 mockup）**

```xml
<UserControl x:Class="NetScope.Desktop.Controls.NetworkPathInstrument"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Background="{StaticResource Recess}" BorderBrush="{StaticResource Line}" BorderThickness="1" CornerRadius="14" Padding="20,18,16,14">
        <StackPanel>
            <DockPanel Margin="0,0,0,6">
                <TextBlock Text="网络路径 · 实测信号" FontSize="12.5" Foreground="{StaticResource Muted}" FontWeight="Medium" DockPanel.Dock="Left"/>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" DockPanel.Dock="Right">
                    <StackPanel Margin="0,0,26,0">
                        <TextBlock x:Name="PortReadout" FontFamily="{StaticResource MonoFont}" FontSize="26" FontWeight="Medium" HorizontalAlignment="Right"/>
                        <TextBlock Text="代理端口" FontSize="10.5" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" Margin="0,5,0,0"/>
                    </StackPanel>
                    <StackPanel>
                        <TextBlock x:Name="LatencyReadout" Text="—" FontFamily="{StaticResource MonoFont}" FontSize="26" FontWeight="Medium" Foreground="{StaticResource SubtleText}" HorizontalAlignment="Right"/>
                        <TextBlock Text="出口延迟 ms · 未测得" FontSize="10.5" Foreground="{StaticResource Muted}" HorizontalAlignment="Right" Margin="0,5,0,0"/>
                    </StackPanel>
                </StackPanel>
            </DockPanel>
            <Viewbox Stretch="Uniform" Height="128">
                <Canvas Width="760" Height="128">
                    <Line X1="20" Y1="64" X2="740" Y2="64" Stroke="{StaticResource Line}" StrokeThickness="1" StrokeDashArray="2 5"/>
                    <Path x:Name="HealthyPath" Stroke="{StaticResource Accent}" StrokeThickness="2.4" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                          Data="M20 64 L70 64 L82 64 L90 30 L100 98 L110 64 L150 64 L162 64 L170 34 L180 94 L190 64 L232 64"/>
                    <Path x:Name="FaultPath" Stroke="{StaticResource Bad}" StrokeThickness="2.4" StrokeStartLineCap="Round" Opacity="0.85" Data="M232 64 L740 64"/>
                    <Ellipse x:Name="LocalNode" Canvas.Left="14" Canvas.Top="58" Width="12" Height="12" Fill="{StaticResource Ok}"/>
                    <Ellipse x:Name="ProxyNode" Canvas.Left="225" Canvas.Top="57" Width="14" Height="14" Fill="{StaticResource Bad}"/>
                    <Ellipse x:Name="EgressNode" Canvas.Left="494" Canvas.Top="58" Width="12" Height="12" Fill="{StaticResource Unknown}"/>
                    <Ellipse x:Name="InternetNode" Canvas.Left="734" Canvas.Top="58" Width="12" Height="12" Fill="{StaticResource Unknown}"/>
                    <TextBlock x:Name="ProxyLabel" Canvas.Left="232" Canvas.Top="86" Text="代理端口" FontSize="12" Foreground="{StaticResource Bad}" FontWeight="SemiBold"/>
                    <TextBlock x:Name="ProxyNote" Canvas.Left="232" Canvas.Top="102" FontSize="10.5" Foreground="{StaticResource Bad}"/>
                </Canvas>
            </Viewbox>
            <StackPanel Orientation="Horizontal" Margin="0,2,0,0">
                <TextBlock Text="实测信号" FontSize="11" Foreground="{StaticResource Muted}" Margin="0,0,18,0"/>
                <TextBlock Text="中断 / 故障" FontSize="11" Foreground="{StaticResource Muted}" Margin="0,0,18,0"/>
                <TextBlock Text="未测得（未知）" FontSize="11" Foreground="{StaticResource Muted}"/>
            </StackPanel>
        </StackPanel>
    </Border>
</UserControl>
```
（站点标签位置实现时按 Viewbox 居中微调；健康/故障 Path 的显隐与颜色由 code-behind 按 Model 设置。）

- [ ] **Step 2: code-behind（绑定 + 状态着色 + draw-in 闸门）**

```csharp
// src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Controls;

public partial class NetworkPathInstrument : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(NetworkPathModel), typeof(NetworkPathInstrument),
        new PropertyMetadata(null, OnModelChanged));

    public NetworkPathModel? Model
    {
        get => (NetworkPathModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public NetworkPathInstrument() => InitializeComponent();

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NetworkPathInstrument)d).Apply((NetworkPathModel?)e.NewValue);

    private void Apply(NetworkPathModel? model)
    {
        if (model is null) return;
        PortReadout.Text = model.PortReadout;
        PortReadout.Foreground = model.Proxy.State switch
        {
            PathStationState.Fault => (Brush)FindResource("Bad"),
            PathStationState.Unknown => (Brush)FindResource("SubtleText"),
            _ => (Brush)FindResource("Ink"),
        };
        ProxyNode.Fill = StationBrush(model.Proxy.State);
        EgressNode.Fill = StationBrush(model.Egress.State);
        InternetNode.Fill = StationBrush(model.Internet.State);
        ProxyNote.Text = model.Proxy.Note;
        // 故障平线仅在代理故障或下游未知时显示；健康脉冲在已测量时显示
        FaultPath.Visibility = model.Proxy.State == PathStationState.Fault ? Visibility.Visible : Visibility.Collapsed;
        HealthyPath.Visibility = model.Proxy.State == PathStationState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation && HealthyPath.Visibility == Visibility.Visible)
            DrawIn(HealthyPath);
    }

    private Brush StationBrush(PathStationState state) => state switch
    {
        PathStationState.Ok => (Brush)FindResource("Ok"),
        PathStationState.Fault => (Brush)FindResource("Bad"),
        _ => (Brush)FindResource("Unknown"),
    };

    private static void DrawIn(System.Windows.Shapes.Path path)
    {
        var len = path.Data.GetFlattenedPathGeometry().Bounds.Width > 0
            ? path.Data.GetFlattenedPathGeometry().Bounds.Width : 400;
        // 用 StrokeDashOffset 做左→右揭示
        path.StrokeDashArray = new DoubleCollection { len, len };
        path.StrokeDashOffset = len;
        var anim = new DoubleAnimation(len, 0, new Duration(TimeSpan.FromSeconds(0.9)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        path.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, anim);
    }
}
```
注：`GetFlattenedPathGeometry().Bounds.Width` 是近似弧长；实现时若揭示不自然，改用 `path.Data.GetFlattenedPathGeometry()` 逐段累加真实弧长。draw-in 结束后把 `StrokeDashArray` 置 null 恢复实线（动画 Completed 回调）。

- [ ] **Step 3: 构建**

Run: `dotnet build NetScopeRepair.slnx` → 0 警告。

- [ ] **Step 4: Commit**

```bash
git add src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml.cs
git commit -m "feat(desktop-ui): network-path instrument control with gated draw-in"
```

---

### Task 7: QuickRepairView 分阶段重构

**Files:**
- Modify: `src/NetScope.Desktop/Views/QuickRepairView.xaml`（整体重写）

**Interfaces:**
- Consumes: `QuickRepairViewModel.Stage/VerdictText/NetworkPath/RepairItems/Steps/LogLines`（Task 4）、`NetworkPathInstrument`（Task 6）、token styles（Task 1）。
- Produces: 阶段 stepper + 仪器主角 + verdict + 问题↔修复列表 + 上下文主按钮 + 安静工具条 + 步骤/日志。

- [ ] **Step 1: 重写视图结构**

按 spec §6 与 mockup `signal-instrument-final.html` 重写 `QuickRepairView.xaml`，自上而下：
1. **阶段 stepper**：4 个编号 chip（体检/看清问题/执行/自检），用 `DataTrigger` 绑 `Stage` 高亮当前、完成打勾。编号是真实顺序。
2. **仪器主角**：`<controls:NetworkPathInstrument Model="{Binding NetworkPath}"/>`。
3. **Verdict**：`TextBlock Text="{Binding VerdictText}"` FontSize=20 SemiBold。
4. **问题↔修复列表**：`ItemsControl ItemsSource="{Binding RepairItems}"`，每行 checkbox 绑 `IsSelected` + `ProblemText`（+`CodeRef` MonoSubtle）+ `FixText` + `BeforeAfterText` + `RiskBadge` + journal 注记。`Visibility` 绑 `HasPlan`。
5. **上下文主按钮 + 工具条**：主按钮用 `DataTrigger` 按 `Stage` 切换 Content/Command——Inspect→「体检本机（只读）」`RefreshCommand`；Review→`ApplyButtonText`/`ApplyCommand`；Execute→busy 禁用；Verify→「重新体检」`RefreshCommand`。工具条 QuietButton：开代理/关代理/回滚改动（`RestoreButtonText`）/交给 AI/还是没好 + 高风险 CheckBox。
6. **执行步骤 + 实时日志 + 进度条**：保留现有 `Steps`/`LogLines`/`LinearProgressBar`/`ElapsedText` 模板（现视图 `:157-259`），日志 Expander 默认 Execute 展开。
7. **边界注记**：保留现边界文案。

主按钮二次确认文案绑定 `ApplyButtonText`/`RestoreButtonText` 不变。

- [ ] **Step 2: 构建 + 渲染目视**

Run: `dotnet build NetScopeRepair.slnx` → 0 警告。
Run: `dotnet run --project src/NetScope.Desktop -- --render-screens C:/Temp/ns-shots` → 目视修复页对齐 mockup。

- [ ] **Step 3: 实机走查（用户常驻环境）**

运行 app：体检→看清（仪器填充、问题↔修复配对）→勾选→执行（二次确认）→自检；验证回滚、开/关代理、交给 AI。确认非管理员动作如实跳过。

- [ ] **Step 4: Commit**

```bash
git add src/NetScope.Desktop/Views/QuickRepairView.xaml
git commit -m "feat(desktop-ui): stage-guided repair page with instrument hero"
```

---

### Task 8: 动效（克制 · 分级 · 闸门）

**Files:**
- Modify: `src/NetScope.Desktop/Resources/Theme.xaml`（按钮 hover/press 过渡、stage chip 过渡）
- Modify: `src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml.cs`（呼吸 + 扫描点，闸门）
- Modify: `src/NetScope.Desktop/Views/ShellView.xaml`（页面切换轻淡入）
- Modify: `src/NetScope.Desktop/Views/QuickRepairView.xaml`（verdict/列表 reveal、读数落定）

**Interfaces:**
- Consumes: `SystemParameters.ClientAreaAnimation`（全局闸门）。
- Produces: 动效对齐 spec §7.1–7.2 时序；关闭时静止无隐藏。

- [ ] **Step 1: 仪器呼吸 + 扫描点（code-behind，闸门）**

在 `NetworkPathInstrument.Apply` 末尾，若 `SystemParameters.ClientAreaAnimation`：对 `HealthyPath` 加 opacity 呼吸 `DoubleAnimation(1,0.5,0:0:1.6){AutoReverse=true,RepeatBehavior=Forever}`；可选扫描点（Canvas 上 Ellipse + `PointAnimationUsingPath` 沿 HealthyPath，dur 3.2s Repeat）。`Unloaded` 停止动画。关闭时不加。

- [ ] **Step 2: 按钮微交互（Theme.xaml）**

PrimaryButton/QuietButton 模板加 `RenderTransform` TranslateTransform，hover `Y=-1`（~0.14s）、press `Y=0`；用 `EventTrigger` MouseEnter/MouseLeave/PreviewMouseDown 或 `VisualStateManager`。所有按钮 press 反馈 <0.3s。

- [ ] **Step 3: 页面切换轻淡入（ShellView）**

`ContentControl Content="{Binding SelectedSection.ViewModel}"` 加 `ContentTemplate` 切换时的 opacity 0→1（~0.14s）；内容立即出现，淡入只是叠加。闸门关闭时直接显示。

- [ ] **Step 4: verdict/列表 reveal + 读数落定（QuickRepairView）**

verdict 与 RepairItems 行用 `Loaded` EventTrigger opacity 0→1 + TranslateY 6→0（~0.4s，回应测量结果）。读数落定：`NetworkPath` 变化时 PortReadout opacity 淡入（~0.3s）。reduced-motion 时 `Visibility`/Opacity 直接为 1，不隐藏。

- [ ] **Step 5: 构建 + 渲染 + 开关验证**

Run: `dotnet build NetScopeRepair.slnx` → 0 警告。
实机：系统「显示动画」开/关各走一遍——开时迹线 draw-in/呼吸、按钮微交互；关时一切静止、无内容隐藏、无 >1s 静默（长操作仍有 spinner/进度）。

- [ ] **Step 6: Commit**

```bash
git add src/NetScope.Desktop/Resources/Theme.xaml src/NetScope.Desktop/Controls/NetworkPathInstrument.xaml.cs src/NetScope.Desktop/Views/ShellView.xaml src/NetScope.Desktop/Views/QuickRepairView.xaml
git commit -m "feat(desktop-ui): purposeful motion gated by system animation setting"
```

---

### Task 9: 交接包分级反馈（TDD）

**Files:**
- Modify: `src/NetScope.Desktop/ViewModels/QuickRepairViewModel.cs:394-419`（HandoffAsync）
- Test: `tests/Desktop/HandoffFeedbackTests.cs`

**Interfaces:**
- Consumes: `HandoffExporter.ExportAsync`（无 IProgress，Storage 范围外）、`Stopwatch`/`DispatcherTimer`（已有）。
- Produces: handoff 期间 busy + 状态文字 + 计时；>8s 安抚文案。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/Desktop/HandoffFeedbackTests.cs
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class HandoffFeedbackTests
{
    [Fact]
    public void Handoff_without_session_reports_no_log()
    {
        var vm = new QuickRepairViewModel(sessionPath: () => null);
        vm.HandoffCommand.Execute(null);
        Assert.Contains("没有会话日志", vm.StatusMessage);
    }

    [Fact]
    public async Task Handoff_sets_busy_and_status_while_running()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netscope-hf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.ndjson");
        using (var log = new NetScope.Storage.NdjsonEventLog(path))
        {
            var ev = new NetScope.Domain.RepairEvent("b", "postcheck_failed", DateTimeOffset.Now, null, null, "x", "err", false, null, null, null);
            foreach (var env in NetScope.Storage.RepairSessionEvents.Create(new[] { ev }, "s", "b", false, 1.0))
                log.AppendAsync(env, CancellationToken.None).GetAwaiter().GetResult();
        }
        var vm = new QuickRepairViewModel(exporter: new NetScope.Storage.Export.HandoffExporter(),
            sessionPath: () => path, copyToClipboard: _ => { });
        vm.HandoffCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.Contains("交接包", vm.StatusMessage);
        for (var i = 0; i < 100 && vm.IsBusy; i++) await Task.Delay(50);
        Assert.False(vm.IsBusy);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~HandoffFeedbackTests"` → FAIL（busy 期间状态文案不符）。

- [ ] **Step 3: HandoffAsync 增分级反馈**

`HandoffAsync`（`:394-419`）开头 `IsBusy = true;` 后立即 `StatusMessage = "正在生成交接包（脱敏 + 打包），请稍候…";` 并启动计时（复用 `_stopwatch`/`_timer` 或独立计时）；用 `IProgress`-free 方式：导出任务完成后清 busy。增一个后台计时 tick：若 elapsed > 8s 且仍在跑，`StatusMessage = "日志较大，仍在打包…已耗时 " + elapsed + "s";`。完成沿用现「交接包已生成…」文案。

- [ ] **Step 4: 运行通过 + 构建**

Run: `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~HandoffFeedbackTests"` → PASS。
Run: `dotnet build NetScopeRepair.slnx` → 0 警告。

- [ ] **Step 5: Commit**

```bash
git add src/NetScope.Desktop/ViewModels/QuickRepairViewModel.cs tests/Desktop/HandoffFeedbackTests.cs
git commit -m "feat(desktop-ui): latency-tiered feedback for AI handoff export"
```

---

### Task 10: 首页降级 plumbing + 二级页面一致性

**Files:**
- Modify: `src/NetScope.Desktop/ViewModels/MainViewModel.cs`（Home 会话控制保留为管线，不暴露导航）
- Modify: `src/NetScope.Desktop/Views/SettingsView.xaml`（接收「载入合成演示」按钮）
- Verify: `src/NetScope.Desktop/Views/{Timeline,Diagnoses,CaseReplay,Clients,Export,Settings,Help}View.xaml`

**Interfaces:**
- Consumes: Task 5 的 8 项 Sections。
- Produces: 首页功能（会话控制）经顶栏可达；演示入口在设置；二级页面全部继承新 token。

- [ ] **Step 1: 确认 Home 管线不丢功能**

`MainViewModel` 仍构造 `Home`（会话状态/失败上报），顶栏 `开始观察/暂停/立即诊断` 绑 `Home.StartCommand/PauseCommand/DiagnoseCommand`。`HomeViewModel` 不再作为导航页；其健康概览由修复仪器取代。确认 `OnCycle`/`UpdateSessionState` 仍调 `Home.ApplyCycle/ApplySessionState`（不删）。

- [ ] **Step 2: 演示入口移设置**

把「载入合成演示」按钮（`LoadDemoCommand`）加入 `SettingsView.xaml` 一个「演示」分区；从顶栏移除（Task 5 已留绑定）。保留演示横幅与合成标记逻辑。

- [ ] **Step 3: 二级页面 token 一致性核查**

逐页渲染目视：视图已用 token（无硬编码 hex，已确认）。检查是否残留旧靛蓝语义（如 `AccentLight` 选中态现在为青绿浅底，确认对比度）。必要时把个别 `FontWeight Bold` 调为 SemiBold（字阶 §4.3）。

- [ ] **Step 4: 构建 + 全页渲染**

Run: `dotnet build NetScopeRepair.slnx` → 0 警告。
Run: `dotnet run --project src/NetScope.Desktop -- --render-screens C:/Temp/ns-shots` → 8 页全部目视。

- [ ] **Step 5: Commit**

```bash
git add src/NetScope.Desktop/ViewModels/MainViewModel.cs src/NetScope.Desktop/Views/SettingsView.xaml
git commit -m "feat(desktop-ui): demote home to session plumbing, move demo entry to settings"
```

---

### Task 11: 全量验证 + 文档同步

**Files:**
- Modify: `tests/Desktop/*`（更新旧色值/页数断言，如 ReviewRegressionTests/T07R2*）
- Modify: `AGENTS.md`（页面数 9→8、修复一等公民描述、字体 IBM Plex）
- Verify: 全解决方案

**Interfaces:**
- Consumes: 全部前置任务。
- Produces: 0 警告构建 + 全绿测试 + 渲染/冒烟/实机通过 + 文档一致。

- [ ] **Step 1: 更新受影响断言**

Run: `dotnet test NetScopeRepair.slnx` → 列出失败。凡断言旧靛蓝 `#5E6AD2`/旧灰/9 页/首页导航者，更新为新值/8 页/修复默认。不改引擎测试语义。

- [ ] **Step 2: 全量构建 + 测试**

Run: `dotnet build NetScopeRepair.slnx` → 0 警告 0 错误。
Run: `dotnet test NetScopeRepair.slnx` → 全绿（记录通过数）。

- [ ] **Step 3: 渲染 + 冒烟**

Run: `dotnet run --project src/NetScope.Desktop -- --render-screens C:/Temp/ns-final` → 8 页 PNG 逐张对照 mockup。
Run: `dotnet run --project src/NetScope.Desktop -- --smoke-test C:/Temp/ns-smoke.json` → persisted=true、非合成、可导出。

- [ ] **Step 4: 实机验收（用户常驻环境，需管理员配合）**

修复全流程 + 二次确认 + 回滚 + TUN 三字段（管理员）+ 非管理员跳过 + 动画开关 + reduced-motion + 交接包。反馈时序：按钮 press <0.3s、长操作进度/步骤、无 >1s 静默。

- [ ] **Step 5: 文档同步**

`AGENTS.md`：页面 9→8、修复一等公民、双层左栏、IBM Plex、信号仪仪器、动效闸门。README 截图如需刷新另起任务（本计划不含发布）。

- [ ] **Step 6: Commit**

```bash
git add tests/Desktop AGENTS.md
git commit -m "test(desktop-ui): align assertions with signal-instrument system; sync docs"
```

---

## Self-Review

**Spec coverage:** §4.1 token→Task1；§4.2 字体→Task2；§4.3 字阶→Task1/2/10；§5 IA→Task5/10；§6.1 阶段→Task4/7；§6.2 仪器→Task3/6；§6.3 配对→Task4/7；§6.4 主按钮/工具条→Task7；§6.5 步骤/日志/handoff→Task7/9；§7 动效→Task8；§7.1–7.2 分级反馈→Task8/9；§8 二级继承→Task1/10；§9 字体资源→Task2；§11 验证→Task11；§12 风险（handoff 钩子范围外）→Task9 注明。无遗漏。

**Placeholder scan:** 无 TBD/TODO；各代码步含实际代码。Task5 测试已用真实 `AppServices` 构造（镜像 `ReviewRegressionTests.cs:103-107`，非假设工厂）。Task6 的 draw-in 弧长用 `Bounds.Width` 近似并已标注「若揭示不自然改用逐段真实弧长」，属实现微调指引而非占位。

**Type consistency:** `NetworkPathModel.FromSnapshot/Unmeasured`、`PathStationState`、`RepairStage`、`RepairItemRow`、`ActionRow.Current/Desired`、`NavGroup`/`NavSection.Group`、`PrimarySection/SecondarySections/FooterSections` 在定义任务与消费任务间名称一致。