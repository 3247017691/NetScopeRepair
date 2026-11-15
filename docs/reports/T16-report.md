# T16：修复计划、确认、事务与业务复测 UI（执行入口保持 Disabled）

任务：T16（M3 收尾：把既有新 UI 接到计划、确认、事务和业务复测；本轮不领取 T17/T18）
日期：2026-11-05（Asia/Shanghai）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：设计包 `docs/05`（事务与权限边界）、`docs/03 §8`（修复结果表达）、`docs/07` QA-023/025—031/036—039、`docs/08`/T16、`docs/09`；施工单 `docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T16 卡；`docs/reports/T13-report.md`、`T14-report.md`、`T15-report.md`、`T07-R2-report.md`
工作树基线：含 T04-R2、T06-R3、T07-R2、T08-R2、T09-R2、T10、T11、T12、T13、T14、T15 等并行未提交修改；T12 在任务期间新建 `CaseReplay*` 并两次短暂打断 Storage/Desktop 构建（详见 §4），本任务未修改任何 T12 文件。本任务只新增/修改 `src/NetScope.Application` 的 Repair 连接层、`src/NetScope.Desktop` 的修复页最小增量、`tests/Desktop` 与报告。

**结论：在现有 Shell/主题内新增“修复”页，完成计划预览、逐动作默认未选、显式确认、执行禁用原因、事务分层进度、恢复预览与十分钟/三轮业务观察；Application 的 Repair 连接层把 T13 typed plan、T15 事务状态与 broker 信任闸门以 seam 形式暴露给 UI，Desktop 不直接写网络。真实执行入口在本交付中保持 Disabled 并逐条写明原因（无可信签名部署、无平台实验、无 broker 进程、输入事实未验证），未启动 UAC/提权/服务/网络/注册表/真实客户端配置写入。** 50 个 T16 测试全部通过；Desktop 全量 135 个测试通过。视觉用既有 Shell/主题离屏渲染验证（8 页均成功，含 `04-repair.png`），未重做外壳、图标或关闭行为。

## 1. 实现了什么（用户可观察行为）

1. **修复页增量接入**：`MainViewModel` 新增“修复 / 计划、确认与恢复”导航项（复用既有 nav 模板与图标风格），`App.xaml` 增加 `RepairViewModel`→`RepairView` DataTemplate；未改 Theme、Logo、图标、关闭/隐藏行为；仅把左栏安全卡文案从“无修复入口”改为“修复入口禁用 · 不修改网络”，与真实状态一致。
2. **计划预览**：`RepairPlanView` 基于 T13 typed `RepairPlan`，逐动作展示接口身份（GUID/LUID/ifIndex/名称）、IPv4、`ActiveStore（当前生效）`、`before → desired`（InterfaceMetric 保留原始 uint 与手动态，AutomaticMetric/IgnoreDefaultRoutes 显示启用/忽略语义）、依赖动作、证据 ID、before TTL/来源、风险与回滚条件、资格/阻断原因；所有动作**默认未选中**。
3. **no_op 不计修复**：no-op 字段单独列出（字段/store/现值/跳过原因），`CountsAsRepair=false`；no_op 计划不产生可执行动作，也不会计入修复次数。
4. **执行门禁**：`RepairGateView` 显示 Disabled 状态与逐条原因：无可信签名部署（`BrokerTrustPolicy.DeliveryDefault` 关闭）、无平台实验（`PlatformCapabilityMatrix.UnverifiedDefault` 不可用）、无 broker 进程、输入事实/回放包未验证、synthetic 计划、计划未通过执行前置检查、不可授权、目标漂移、计划过期、no_op。**显式确认**（`ExecuteSelectedAsync(userConfirmed:true)`）之后也只会调用 gateway，不会在 Desktop 内直接写网络或启动 UAC；默认交付下按钮保持禁用。
5. **依赖组合授权**：动作依赖必须一并显式选择，否则 `CanExecute=false` 并显示缺少的依赖；满足时摘要写明“依赖已作为组合授权一并选择”。
6. **事务进度分层**：DisplayTotal 四层分开：命令/系统 API 调用、目标配置读回、技术探测、用户业务复测；`committed` 仅表示技术验收通过，业务结果独立。技术/观察失败、取消后已有变更、读回不符、冲突、回滚失败、恢复未决均按各自状态显示，**不会把 HTTP/技术探测失败改写为 committed**。
7. **RepairVerifier**：按计划允许的 delta 核对 before/after；同时要求目标指纹、方法、地址族、请求 options、实际选路接口与 protected uplink 未变，非计划字段不得变化；缺失观测判 insufficient，不判通过。
8. **DiagnosticComparabilityGate**：修复前后可比性显式检查 `TopologyChanged`、目标/方法/族/options、时间可比；`TopologyChanged` 直接判不可比，保留原标志，**不清零制造可比**。
9. **十分钟 / 三轮业务观察**：由低权主进程（Application 层）执行，DSH、Antigravity、Chrome（国内/国外）、TUN 应用分别记录 pass/fail/not_tested；未满 10 分钟窗口或不足 3 轮不宣称稳定，未测试不算通过，失败轮次进入失败统计且不可改写；不自动付费调用、不发起真实业务请求。
10. **恢复与收尾**：恢复卡片只做“扫描未终结事务”的预览（事务 id、状态、可恢复字段数、未决 intent 数、是否需要新授权、恢复计划 id），不自动执行；退出时 `MainViewModel.ExitAsync` 调 `Repair.PrepareExitAsync` 显示“已终结/尚未终结（保留 journal，下次需用户确认恢复，恢复不依赖互联网）”；断连通过 `ReportDisconnect` 显示当前事务状态；全程不启动新写入。
11. **QA-036 漏斗**：`RepairFunnelCounter` 把 committed/no_op/aborted/rolled_back/conflict/failed/recovery_required/cancelled/denied/disconnected/incomplete 全部计入分母，UI 显示各项计数，不只统计已提交事务。

## 2. 实际修改/新增文件

新增（Application，Repair 连接层，纯本地/无 I/O）：

- [Repairing.cs](../../src/NetScope.Application/Repairing.cs)：预览/动作/门禁/事务/恢复视图记录，`IRepairPreviewSource`、`IRepairGateway`，`UnavailableRepairGateway`（交付默认，永不执行）、`PreviewOnlyRepairGateway`（合成演示只预览），`RepairPreviewMapper`（typed plan→UI，含 TTL/资格/门禁映射）。
- [SyntheticRepairPreview.cs](../../src/NetScope.Application/SyntheticRepairPreview.cs)：用 T13 `RepairPlanBuilder` + synthetic preflight/matrix 构建演示计划（2 个变更动作 + 1 个 no-op 字段），不读本机、不写任何值。
- [RepairVerification.cs](../../src/NetScope.Application/RepairVerification.cs)：`RepairVerifier`（允许 delta + 目标/方法/族/options/路由/非目标核对）、`DiagnosticComparabilityGate`（拒绝 TopologyChanged，不清标志）。
- [RepairBusinessObservation.cs](../../src/NetScope.Application/RepairBusinessObservation.cs)：10 分钟/3 轮业务观察状态机、5 类目标 pass/fail/not_tested、`RepairFunnelCounter`（QA-036 分母）。

修改（Application）：

- [NetScope.Application.csproj](../../src/NetScope.Application/NetScope.Application.csproj)：增加 `NetScope.Repair` 项目引用（Application 连接 T13 typed plan；Repair 仍不引用 Application/Desktop）。

新增（Desktop 修复页）：

- [RepairViewModel.cs](../../src/NetScope.Desktop/ViewModels/RepairViewModel.cs)：动作行（默认未选/依赖/资格）、预览/确认/取消/恢复扫描/业务观察命令、分层结果、事务状态、退出/断连文案、漏斗。
- [RepairView.xaml](../../src/NetScope.Desktop/Views/RepairView.xaml)、[RepairView.xaml.cs](../../src/NetScope.Desktop/Views/RepairView.xaml.cs)：复用 `SectionCard/Card/Pill/PrimaryButton/QuietButton` 等既有样式，两列布局（计划/动作 + 确认/进度/业务/恢复）。

修改（Desktop 最小接入）：

- [MainViewModel.cs](../../src/NetScope.Desktop/ViewModels/MainViewModel.cs)：新增 `Repair` VM 与“修复”导航项；`ExitAsync` 先调用 `PrepareExitAsync` 收尾（不启动新写入）。
- [App.xaml](../../src/NetScope.Desktop/App.xaml)：新增 `RepairViewModel` DataTemplate。
- [Composition.cs](../../src/NetScope.Desktop/Services/Composition.cs)：`AppServices.Repair` + synthetic 时 `PreviewOnlyRepairGateway(SyntheticRepairPreviewSource)`，生产默认 `UnavailableRepairGateway`（无真实 broker/签名部署）。
- [ScreenRenderer.cs](../../src/NetScope.Desktop/Services/ScreenRenderer.cs)：渲染页序增加 `repair` slug（否则新页导致后续截图命名错位；只改 slug 一行）。
- [ShellView.xaml](../../src/NetScope.Desktop/Views/ShellView.xaml)：仅安全卡文案改为“修复入口禁用 · 不修改网络”；未改视觉/图标/关闭行为。
- `RepairView.xaml` 内所有只读 `Run.Text` 绑定显式 `Mode=OneWay`（WPF Run.Text 绑定默认模式导致的运行期异常；离屏渲染时发现并修复，非视觉重做）。

测试（tests/Desktop，全部 fake/合成，无网络）：

- [T16TestSupport.cs](../../tests/Desktop/T16TestSupport.cs)：假时钟、`FakeRepairGateway`（预览/执行/取消/恢复/退出记录）、预览/动作/结果/恢复 fixture。
- [T16RepairUiTests.cs](../../tests/Desktop/T16RepairUiTests.cs)：22 个用例（默认未选、字段展示、显式确认、依赖组合、no_op、过期/漂移/不可信禁用、门禁原因与拒绝计数、四层结果、非 committed 状态、取消/断连漏斗、退出/断连状态、恢复预览、漏斗分母）。
- [T16RepairVerificationTests.cs](../../tests/Desktop/T16RepairVerificationTests.cs)：17 个用例（delta 通过、目标/方法/族/options/路由/protected uplink/非目标变化失败、缺失观测 insufficient；可比性 TopologyChanged/维度变化/缺失/时间不可比，标志不清零）。
- [T16BusinessObservationTests.cs](../../tests/Desktop/T16BusinessObservationTests.cs)：8 个用例（未满窗口不宣称稳定、满窗口 3 轮通过、失败计数、未测试不算通过、窗口外/超轮拒绝、业务失败不改技术 committed、业务观察不调用 gateway、合成演示预览 2 变更 + 1 no-op 且默认未选/禁用）。
- [T16CompositionTests.cs](../../tests/Desktop/T16CompositionTests.cs)：3 个用例（synthetic Composition 的 PreviewOnly gateway + Shell 修复页、Shell 退出收尾、默认 gateway 永不执行/扫描）。

未修改：`Theme.xaml`、Logo/图标、Shell 视觉/关闭行为、`src/NetScope.Probes/**`、`src/NetScope.Export`/`src/NetScope.Storage/Export/**`、`ObservationSession`、真实客户端配置与系统网络设置；未新增服务/计划任务/启动项；未运行 UAC；未修改任何 T12 文件。

## 3. 关键设计与偏差

- **没有现成 `BrokerClient`**：T14/T15 交付的是 broker 服务端与 journal；本交付没有 UI 侧 broker 客户端或真实提权部署。因此 T16 在 Application 定义 `IRepairGateway` seam，交付默认 `UnavailableRepairGateway`，合成演示用 `PreviewOnlyRepairGateway`；真实 broker 接线留给后续任务。没有把 fake 通道包装成真实能力。
- **生产预览不可用是设计结果**：没有经 T13 平台实验导入的真实 capability，也没有真实 preflight 事实来源；生产组合只显示 Disabled 原因。合成演示用 T13 `RepairPlanBuilder` 生成真实 typed plan（synthetic 标记），UI/门禁/选择/映射代码与未来真实路径共用。
- **逐动作确认与 UAC 的关系**：UI 只表达“显式确认后请求执行”；UAC 由未来 broker 流程负责。本交付没有任何提权代码，`execute` 在默认 gateway 下直接返回带原因的拒绝。
- **业务观察低权**：业务复测只记录用户/本地输入，不发起真实请求、不自动付费调用；技术 committed 与业务结果分层，业务失败不回写 committed。
- **恢复只预览**：UI 不读 journal 文件、不自动恢复；`ScanRecoveryAsync` 由 gateway 实现；交付默认明确“恢复不可用/无权威 journal”，恢复不依赖互联网的文案随结果展示。
- **渲染 / Shell 的最小改动**：仅新增 nav 项、DataTemplate、页面文件、渲染 slug 与安全卡文案；未重做外壳。离屏渲染发现 `Run.Text` 默认 TwoWay 导致运行期异常，改为显式 OneWay，属于修复而非视觉调整。
- **T12 并行冲突处理**：任务期间 Storage（`CaseReplayService.cs`/`SessionRecovery.cs`）与 Desktop（`CaseReplayViewModel.cs`）的 T12 未完成改动两次使构建失败；按 Lead 指示未修改 T12 文件，等待其修复后重试；最终 Desktop/Storage/Tests 构建全部通过。T12 的 `CaseReplay*` 文件未被我覆盖。

## 4. 执行的命令、环境、退出码

环境：Windows、普通权限用户；.NET SDK `10.0.403`；未提权、未运行 UAC、未改系统/网络/注册表/服务/启动项。只构建 Desktop 与 Desktop 测试（未构建全解决方案）；`src/NetScope.Storage` 在 T12 修复前后各构建一次（用于确认阻塞来源，非本任务交付）。

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build src/NetScope.Storage/NetScope.Storage.csproj -v q`（T12 修复前） | 1 | T12 并行未完成：37 / 2 个错误（非本任务文件）；未修改 |
| `dotnet build src/NetScope.Storage/NetScope.Storage.csproj -v q`（T12 修复后） | 0 | 0 警告 / 0 错误 |
| `dotnet build src/NetScope.Desktop/NetScope.Desktop.csproj -v q` | 0 | 0 警告 / 0 错误 |
| `dotnet build tests/Desktop/NetScope.Desktop.Tests.csproj -v q` | 0 | 0 警告 / 0 错误 |
| `dotnet test tests/Desktop/... --filter "FullyQualifiedName~T16"` | 0 | **50 通过 / 0 失败 / 0 跳过**（22 UI + 17 verifier + 8 business + 3 composition） |
| `dotnet test tests/Desktop/...`（全量） | 0 | **135 通过 / 0 失败 / 0 跳过** |
| `dotnet run --project src/NetScope.Desktop --no-build -- --render-screens %TEMP%\NetScopeRepair-T16\screens2` | 0 | 8 页离屏渲染成功：`01-home`…`08-help`，含 `04-repair.png`；未显示窗口、未启动观察 |
| 临时 STA 渲染 harness（桌面页填充合成预览后离屏渲染，位于 `%TEMP%\t16render`，未入仓） | 0（输出 ok） | 生成 `%TEMP%\NetScopeRepair-T16\screens-populated\repair-populated.png`，验证动作卡/门禁/未选状态渲染 |

日志与 TRX 仅在本地 `%TEMP%\NetScopeRepair-T16\`（未入仓）：`t16-final2.log` + `t16-final2.trx`（T16 全绿）、`all-final3.log` + `all-final3.trx`（全量 135）、`screens2/` 与 `screens-populated/` 渲染 PNG。

## 5. 测试数量与覆盖（QA-023/025—031/036—039）

本轮新增 **50** 个测试用例（Theory 展开后实测：`T16RepairUiTests` 22、`T16RepairVerificationTests` 17、`T16BusinessObservationTests` 8、`T16CompositionTests` 3）。

- **QA-023 GUI 只读/取消**：修复页默认 Disabled；合成 Composition 走 PreviewOnly gateway；退出/断连不产生写入；业务观察不调用 gateway（0 次）。已有的探测取消测试继续通过（全量 135 绿）。
- **QA-025 授权绑定**：预览展示 plan digest/TTL；显式确认后把 plan id/digest/动作 id 传给 gateway；过期计划禁用并提示重新预览；未显式确认不发起执行。
- **QA-026 broker 边界**：Desktop 只通过 `IRepairGateway` 请求执行，不打开字段写入；fake 断言执行请求只含确认动作与摘要；默认交付拒绝执行并给原因。
- **QA-027 CAS/冲突**：`rollback_conflict`/`rollback_failed` 以独立状态与文案显示，不覆盖外部值（UI 层 + T15 coordinator 既有证据）。
- **QA-028 状态中断**：UI 显示 `recovery_required`、未终结事务与“退出保留 journal、需用户确认恢复”；恢复扫描只预览不执行。
- **QA-029 回滚失败**：`rollback_failed` 显示残留/离线恢复入口；不宣称网络已恢复。
- **QA-030 空操作/取消**：no_op 不计修复且不可执行；`aborted` 显示写前中止；取消/断连进入漏斗分母；已写后失败按 rolled_back 等状态显示。
- **QA-031 观察窗**：3 轮未满 10 分钟不宣称稳定；满窗 3 轮通过；失败轮次计入失败统计。
- **QA-036 产品成功漏斗**：`RepairFunnelCounter` 分母含 committed/no_op/aborted/rolled_back/rollback_conflict/rollback_failed/recovery_required/cancelled/denied/disconnected/incomplete，UI 显示逐项计数。
- **QA-037 托盘/生命周期**：未改托盘/关闭行为；Shell 退出调用 Repair 收尾且不写；现有生命周期测试全绿。
- **QA-038 四类业务**：DSH、Antigravity、Chrome（国内/国外）、TUN 应用分别 pass/fail/not_tested；未测不算通过；不自动付费调用。
- **QA-039 多客户端**：未改多客户端页，修复页没有停止/切换入口；全量 Desktop 测试通过说明既有客户端/依赖解释未回归。

## 6. 能力关闭矩阵（本轮实际状态）

| 能力 | 状态 | 说明 |
|---|---|---|
| 真实修复执行（Desktop→UAC→broker） | **关闭** | 无签名部署、无平台实验、无 broker 进程；默认 gateway 只返回 Disabled 与原因 |
| 真实 broker 客户端/提权 | **关闭** | 本交付无 UI 侧 broker client；T14/T15 服务端类型未被调用 |
| 生产修复预览（真实事实） | **关闭** | 生产组合 `UnavailableRepairGateway`；只有 synthetic 演示/测试可显示计划 |
| 权威 journal 恢复执行 | **关闭（无 journal 目录）** | UI 仅显示恢复不可用/扫描预览；恢复不依赖互联网 |
| 真实业务复测请求/付费调用 | **关闭** | 只记录本地结果；不发起请求、不自动付费 |
| UAC/服务/启动项/真实网络写入 | **关闭** | 未实现、未运行 |

## 7. 只读/权限/隐私边界检查

- 未调用真实网络、未写注册表、未改系统网络设置、未写真实客户端配置、未安装/启动服务、未运行 UAC、未提权。
- 测试与合成 Composition 只写测试自建临时目录（`TempAppPaths`）；离屏渲染只读合成会话并写 `%TEMP%` PNG；未在仓库内产生证据目录。
- 修复页不展示 nonce、凭据、真实客户端配置；业务目标只显示名称与本地记录结果。
- 未修改 `Probes/**`、`Export/**`、`ObservationSession`（工作树中这些文件的改动来自其他并行任务，不是本任务）。

## 8. 已知限制与未验证项

- **没有真实 UI 自动化点击**：验证是 VM 级 + 离屏渲染（合成 Composition），未用 UI 自动化执行真实鼠标/键盘流程；渲染证明 XAML 可加载与布局可用，但不替代人工视觉验收。
- **没有真实 broker/VM/签名部署**：默认 gateway 强制 Disabled；未来接线需要 T13 实验导入、签名 ACL 部署与 broker 进程，本轮未验证。`BrokerTrustPolicy.DeliveryDefault` 保持关闭（T14 既有测试证据，本轮未改）。
- **生产预览不可用**：真实 preflight 事实源缺失，生产组合只能显示原因；合成演示的 typed plan 来自 synthetic matrix，不能作为平台验证证据。
- **业务观察是记录器**：不自动探测 DSH/Antigravity/Chrome/TUN 应用，也不发起付费模型调用；结果需用户/本地低权检查提供。真实十分钟业务复测未在本机执行。
- **恢复 UI 仅扫描预览**：没有权威 journal 目录时只显示“恢复不可用”；真实恢复需要 T15 broker/服务路径与用户再次授权，未在本轮接线。
- **渲染截图**：仅本地 `%TEMP%`，未入仓；未对所有页面做逐像素视觉回归，只确认 8 页成功渲染且修复页布局正常。

## 9. 是否有真实网络操作

没有。修复执行、预览、业务观察均为合成/fake 或纯记录；未发起任何外部请求、未修改任何网络字段、未启动提权或服务。

## 10. 建议下一任务（不自动执行）

T17/T18（按施工单后续领取，本轮未开始）：在满足签名部署、ACL journal 目录与平台实验门槛后，把 `IRepairGateway` 接到真实 broker 客户端与恢复执行；补 UI 自动化与真实十分钟业务复测；G3 安全复核仍为开放写入前的前置条件。本轮完成即停。
