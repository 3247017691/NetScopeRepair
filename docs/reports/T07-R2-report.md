# T07-R2：在用户新 UI 上显示“发生什么、还缺什么”

任务：T07-R2（原施工单定向补修，不重做 M0/M1，不领取 T16）
日期：2026-11-05（Asia/Shanghai）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送、未 reset/clean/checkout）
设计依据：设计包 02/03/07/09/11；`docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T07-R2 卡；QA-002/003/006/007/011/020/021/023/037/039
工作树基线：包含队友正在进行的 T06-R3（ObservationSession/Composition/证据与 Storage codec）、T04-R2、T08-R2、T09-R2、T10 修改；本任务只改 Desktop 的 ViewModels/Views、Desktop/Services 中**新增**的纯本地变化解释文件、`tests/Desktop`，未覆盖队友文件。

**结论：首页已拆成“采集健康 / 网络验证 / 最近诊断时间”三个独立问题，0 条探测结果固定显示“尚未进行外部验证”且不会转绿；诊断详情把 Partial/未支持原因聚合为可展开列表，按生产方 `RequestOptionsKey` 与窗口/启动跨度判断三路径是否可比，并把 `FailedStage`、连接/解析事实与 T04-R2 的地址族缺口码按原义显示；时间线按接口 GUID→LUID→ifIndex+family 的稳定身份比较相邻观察，显示 before/after、出现/未观测到与时间范围，文本不推断操作者或因果；设置页更新族能力文案并提供可编辑目标档案，保存/打开只写本机设置文件，不发请求。客户端页继续复用 T09-R2 展示。**

## 1. 实现了什么（用户可观察行为）

1. **首页三分**：Hero 卡片右侧改为“采集健康（本机） / 网络验证（外部探测） / 最近诊断时间”三个 KPI；采集健康只描述本机数据域是否读到，网络验证只描述最近一次显式探测，最近诊断时间只描述规则何时运行。三者互不替代。
2. **0 probe 不转绿**：没有外部探测结果时，网络验证块与总状态都显示“尚未进行外部验证”（灰），并写明“空诊断不代表全网正常”；只有“三路径同目标/方法/地址族/窗口、全部成功且生产方确认可比、且当前这轮就带探测”时总状态才转绿。
3. **过期验证不冒充当前**：上一轮通过的验证遇到后续本地采样时显示“外部验证需复测”（warn），明确“当前采样没有复测目标连通性”。
4. **未配目标仍看本地诊断**：未配置目标时，网络验证文案说明仍会运行本地规则诊断，只是不能发起外部验证；`ProbeTargetId`、URL、期望状态、地址族与显式代理全部可在设置里编辑。
5. **三路径同口径比较**：诊断详情新增“三路径对比（同目标 / 同方法 / 同地址族 / 同窗口）”。可比性由生产方 `RequestOptionsKey`、`ObservationWindowId`、`BootId`、启动时间跨度（≤2000 ms）、三路径齐全、状态可比、无 `TopologyChanged` 与生产方 `TimingComparable` 共同决定；任一不满足就显示“三路径不可比”并逐条列出未满足项，不合并成网络结论。
6. **阶段与事实准确**：每条路径显示状态、分类、失败阶段（`resolve/connect/proxy_connect/tls/http/unknown`，`null` 显示“未记录（不能归因）”）、失败端点（target/proxy/unknown）、请求族与失败归属、连接事实（角色/远端/是否复用）、解析事实（位置/来源/时间/地址）与耗时；成功路径写“失败归属：无（该路径成功）”。
7. **IPv4/IPv6 能力**：按 T04-R2 的事实显示三种路径的族能力——
   - `no-explicit-proxy`：能真实强制时显示“已强制 ipv4/ipv6（本路径真实拨号目标）”；
   - `explicit-http-proxy`：缺口码 `address_family_not_enforced_through_proxy` → “Unsupported 缺口”；
   - `system-default`：缺口码 `address_family_unknown_for_system_default` → “未知”；
   - `unsupported_address_family`、`no_address_for_requested_family` 也分别显示为 Unsupported/失败，不伪造分族成功。
8. **Partial 原因可展开**：诊断详情新增“部分 / 未支持原因”卡片，按所选结论引用的 evidence_id 聚合（相同原因只合并计数），每条显示来源、状态、摘要（分类/HTTP/错误码）与细节（阶段/失败端点/地址族/解析位置/提示）；默认展开、可一键收起，不把 Partial 改 Ok、不隐藏错误来降噪。
9. **相邻观察对比**：时间线新增“相邻观察变化”。接口身份优先 GUID，其次 LUID，最后 ifIndex+family（明确标注“仅定位线索”）；显示参数 before/after、出现（此前未观测到）与未观测到（不表示已删除或关闭）、时间范围、每对间隔与“有差异对数/总对数”。同一稳定身份跨地址族翻转时，metric/automatic_metric/disable_default_routes/ifIndex 标为“不可比”而不做数值对比；数据域状态与代理层读取也单列，明确属于采集健康而非网络路径。文本统一声明“不推断操作者、操作时间或因果”。
10. **边界措辞**：首页与诊断页都写明 .NET 探测不是 DSH/Antigravity/Chrome 的实际业务结果；无用户复测不能声称业务恢复。
11. **设置文案**：`FamilyNotice` 不再写“不强制地址族”。跟随系统=未指定族、三路径都不产生族结论；IPv4/IPv6=无显式代理路径可强制（真实 socket），代理路径返回 `address_family_not_enforced_through_proxy`，系统默认路径记为 `address_family_unknown_for_system_default`。
12. **客户端页**：未改布局与语义，继续复用 T09-R2 的进程/端点/TUN/覆盖/提示展示（`04-clients.png`）。

## 2. 实际修改文件

新增（纯本地变化解释，无 I/O、无网络、无系统调用）：

- `src/NetScope.Desktop/Services/ObservationChangeExplainer.cs`：相邻快照对比（接口/代理层/数据域）、GUID→LUID→ifIndex+family 匹配、before/after/出现/未观测到/不可比、时间范围与间隔说明。
- `src/NetScope.Desktop/Services/ProbeComparisonExplainer.cs`：探测组可比性、逐路径族能力、Partial/未支持原因聚合、边界文案常量。

修改（Desktop 展示层）：

- `src/NetScope.Desktop/ViewModels/HomeViewModel.cs`：三分健康模型、探测状态保留与过期检测、诊断时间/引用计数。
- `src/NetScope.Desktop/ViewModels/DiagnosesViewModel.cs`：三路径对比、族能力、原因聚合与展开、结论引用组扩展。
- `src/NetScope.Desktop/ViewModels/TimelineViewModel.cs`：载入回放后生成相邻观察对比。
- `src/NetScope.Desktop/ViewModels/SettingsViewModel.cs`：`FamilyNotice` 新语义、目标档案 ID、固定 GET 说明、保存不请求文案。
- `src/NetScope.Desktop/ViewModels/Rows.cs`：`DiagnosisRow` 增加证据 ID 列表；新增路径/族能力/原因/变化行与展示映射；`PathText` 改为设计名称“无显式代理（可能经 TUN）”。
- `src/NetScope.Desktop/Views/HomeView.xaml`、`DiagnosesView.xaml`、`TimelineView.xaml`、`SettingsView.xaml`：增量区块；未改 Theme/Logo/Shell/关闭行为。

测试：

- 新增 `tests/Desktop/T07R2TestSupport.cs`、`T07R2ChangeComparisonTests.cs`、`T07R2HomeVerificationTests.cs`、`T07R2DiagnosisComparisonTests.cs`、`T07R2SettingsTests.cs`（24 个用例，全部合成 fixture）。
- 修改 `tests/Desktop/SessionLifecycleTests.cs` 两处旧期望：原“完整样本 + 0 探测 = 当前样本正常”改为“采集完整 + 尚未进行外部验证且不绿”。这是 T07-R2 卡的明确要求，不是放宽断言。

未修改：`ObservationSession.cs`、`Composition.cs`、`Probes/**`、`Storage/**`（含 Export）、Repair/Broker、`Theme.xaml`、图标/Logo、`ShellView.xaml`、`MainWindow.xaml`、`CloseChoiceDialog.xaml`、`TrayController.cs`、启动入口、真实客户端配置与系统网络设置。

## 3. 关键设计与实现要点（含偏差）

1. **“绿”的语义收紧**：以前“本机数据域完整且诊断为空”即可显示“当前样本正常”；现在该路径改为“尚未进行外部验证”（灰）。只有最近一轮探测三路径成功、可比且生产方确认时间可比时才绿。空诊断不再是健康证据，符合本任务卡与 03 的“保留不确定性”。
2. **验证单位是探测组**：规则可能只引用其中一条路径（例如 DX-002 引用 no-explicit-proxy 探针），但三路径对比以“所选结论引用到的 `ProbeGroupId`”为单位展开该组全部路径；不相关周期的组不会混入。Partial 原因仍严格限定在结论直接引用的 evidence_id。
3. **地址族必须是显式比较条件**：生产方在未指定 `AddressFamily` 时不会确认 `TimingComparable`；UI 如实显示“不可比：地址族未指定”，并提示在设置中选择 IPv4/IPv6 后再诊断。没有为了变绿而放宽生产方或 UI 判据。
4. **Partial 原因默认展开**：允许收起，但默认不隐藏。理由是本任务明确禁止“隐藏错误来降低噪声”。
5. **UILayout 修正**：新增右侧详情卡后，左侧诊断列表的父 `Border` 被拉高，WPF 对 `VerticalAlignment=Stretch + MaxHeight` 的子元素做垂直居中，导致列表被排到可视区外（离屏渲染时表现为空白）。已在 `DiagnosesView.xaml` 给左列与 ListBox 显式 `VerticalAlignment="Top"`；有渲染截图前后对照，未改主题或外壳。
6. **目标方法固定 GET**：目标档案可编辑 ID/URL/期望状态/族/代理，但方法固定 GET 并在 UI 明示；本轮不引入可改变目标状态的方法，也不新增任何请求能力。
7. **无 ADR/阈值偏差**：未改前窗/后窗、3 次失败/3 组成功、采样/探测预算、规则版本或证据等级；未新增探测/规则/修复入口。

## 4. 命令、环境、退出码

环境：Windows，普通用户；.NET SDK `10.0.403`（`dotnet --version`，见 `test-results` 同级 `dotnet-version.txt`）；未提权、未改系统/网络/环境变量、未启动客户端。按要求只构建 Desktop 与 Desktop 测试，不构建全解决方案。

| 命令 | 结果 |
|---|---|
| `dotnet build src/NetScope.Desktop/NetScope.Desktop.csproj -v m`（基线） | 退出码 0，0 警告 / 0 错误 |
| `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj`（基线） | 退出码 0，**50 通过 / 0 失败 / 0 跳过** |
| `dotnet build src/NetScope.Desktop/NetScope.Desktop.csproj -v m`（最终） | 退出码 0，**0 警告 / 0 错误**（[build-desktop.log](evidence/T07-R2/build-desktop.log)） |
| `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --filter "FullyQualifiedName~T07R2"` | 退出码 0，**24 通过 / 0 失败 / 0 跳过**（[t07r2.trx](evidence/T07-R2/test-results/t07r2.trx)） |
| `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj`（最终全量） | 退出码 0，**81 通过 / 0 失败 / 0 跳过**（[desktop-full.trx](evidence/T07-R2/test-results/desktop-full.trx)） |
| `NetScope.Desktop.exe --render-screens docs\reports\evidence\T07-R2\screens` | 退出码 0，生成七页 PNG（[screens](evidence/T07-R2/screens)） |

过程说明（透明记录）：任务进行中队友正在编辑 `ObservationSession.cs`，20:38 左右曾出现其新增代码的 nullable 编译错误（`CS8604` ×2）。为不越权改队友文件，当时仅用 `-p:WarningsAsErrors=` 完成本任务测试验证；队友随后修复，最终构建与测试均在默认设置下重跑并通过（上表为最终结果）。基线 50 个测试与最终 81 个测试的差额包含本任务新增 24 个和队友在此期间新增的用例。

## 5. 测试数量、通过/失败/跳过

- 本任务新增 **24** 个桌面回归，全部通过；`tests/Desktop` 全量 **81 通过 / 0 失败 / 0 跳过**（退出码 0）。
- 新增用例分布：
  - `T07R2ChangeComparisonTests`（8）：metric 0→4225 与 disable_default_routes false→true 的 before/after、GUID 身份、ifIndex 变化不新增接口、LUID 回退、族翻转不可比、单次观察不可比、interfaces 未读取不当作消失、由真实会话日志驱动的时间线对比。
  - `T07R2HomeVerificationTests`（6）：0 探测=尚未验证且不绿、可比的通过只让验证块变绿、后续采样=需复测、失败路径=未通过且 bad、地址族未指定=不可比、最近诊断时间与采集/验证分离。
  - `T07R2DiagnosisComparisonTests`（7）：三路径可比、缺族不可比、T04 两条族缺口码按 Unsupported/未知显示、阶段与未知归因、Partial 原因聚合 ×2 与展开/收起、无探测引用=仅本机、多组只展开最近一组。
  - `T07R2SettingsTests`（3）：`FamilyNotice` 新文案与两条缺口码、保存目标档案不发探测/不启动会话、空目标 ID 被拒绝且不落盘。
- 未跳过任何用例；没有用“构建成功”替代行为断言。

## 6. 行为证据与 fixture 路径

- 合成 fixture：`tests/Desktop/T07R2TestSupport.cs`（接口/代理层/探测/诊断/快照构造器；真实会话用例使用 `VaryingInterfaceCollector` 写入两次快照，不读真实网卡）。
- 回归：上节四个测试文件。
- 七页离屏渲染（合成演示）：[01-home.png](evidence/T07-R2/screens/01-home.png)、[02-timeline.png](evidence/T07-R2/screens/02-timeline.png)、[03-diagnoses.png](evidence/T07-R2/screens/03-diagnoses.png)、[04-clients.png](evidence/T07-R2/screens/04-clients.png)、[05-export.png](evidence/T07-R2/screens/05-export.png)、[06-settings.png](evidence/T07-R2/screens/06-settings.png)、[07-help.png](evidence/T07-R2/screens/07-help.png)。
- 命令输出与 TRX：`docs/reports/evidence/T07-R2/test-results/`；构建日志同目录。
- 变更文件哈希：[files-changed.json](evidence/T07-R2/files-changed.json)；UI 基线比对：[ui-protection.json](evidence/T07-R2/ui-protection.json)。
- 未读取/引用任何真实故障包、真实客户端配置或真实目标。

## 7. UI 保护检查

对 `docs/plans/evidence/20261105/ui-baseline.json` 的 20 个文件重新计算 SHA-256：**10 个完全未变、7 个为本任务允许的展示增量、3 个为队友未提交修改**（[ui-protection.json](evidence/T07-R2/ui-protection.json)）。

- 未变：`CloseChoiceDialog.xaml`、`MainWindow.xaml`、`NetScope.Desktop.csproj`、`Theme.xaml`、`netscope-logo.png`、`netscope.ico`、`MainViewModel.cs`、`HelpView.xaml`、`ShellView.xaml`、`运行NetScope只读试用版.cmd`。
- 本任务增量：`Rows.cs`、`SettingsViewModel.cs`、`HomeView.xaml`、`DiagnosesView.xaml`、`SettingsView.xaml`、`TimelineView.xaml`、`SessionLifecycleTests.cs`。
- 队友工作树：`TrayController.cs`（T10）、`ClientsView.xaml`（T09/T06）、`ExportView.xaml`（T08）；本任务未触碰。

## 8. 只读/权限/隐私边界检查

- 新增两个服务均为纯本地函数：不读文件、不读注册表、不联网、不启动进程、不写系统；输入只来自已有 `NetworkSnapshot`/`ProbeResult`/`Diagnosis`。
- 未新增探测、DNS、socket 或代理能力；未修改 `HttpProbeTransport`/`HttpProbeRunner`。
- 未修改环境变量、系统代理、防火墙、路由、接口、DNS、证书或任何客户端配置；无提权入口。
- 测试只用合成数据；真实会话集成测试只写入临时 NDJSON（临时目录），不读本机网卡/配置。
- 证据目录 `docs/reports/evidence/` 已被 `.gitignore` 忽略；未上传源包、未提交真实日志或配置、未创建提交/推送。

## 9. 已知限制与未验证项

1. **未做真实外部验证**：本轮没有用户启用的目标，没有对任何外部站点发请求。真实 DNS/连接事实、代理鉴权、IPv4/IPv6 外网路径仍属 T04-R2 的待验项；本任务只验证“结果进入 UI 时的表达”。
2. **未做真实双栈实验**：族能力行按生产方结果与缺口码显示，但没在本机/外部目标上制造 IPv4-only / IPv6-only 的真实差异；该实验仍需 VM 或用户授权目标。
3. **演示会话只有 1 次快照**：`SyntheticShowcase` 不在本任务写范围，因此 `02-timeline.png` 只能展示“只有 1 次观察，至少需要 2 次”的诚实提示；相邻变化行由“真实会话日志 + 合成快照”测试证明（`Timeline_page_shows_adjacent_changes_from_a_saved_synthetic_session`），截图中未出现具体 before/after 行。
4. **截图只证明外观**：`--render-screens` 是离屏渲染，未点击、未滚动、未验证鼠标悬停/键盘；Partial 原因卡在 900px 视口下方，截图未包含，行为由测试覆盖。
5. **变化对比范围**：当前比较接口（GUID/LUID/family/参数）、五个代理层与数据域状态；尚未逐条比较路由条目、DNS 服务器和记录级证据（可作为后续增量，不在本卡）。
6. **可比性依赖生产方字段**：`RequestOptionsKey`/`TimingComparable` 缺失时只能显示“不可比”；这是故意保守，不推断可比。默认未选地址族时用户会看到“不可比”，需要先在设置里选 IPv4/IPv6。
7. **目标档案方法固定 GET**：没有提供其他 HTTP 方法，避免引入会改变目标状态的能力；若未来需要，应作为独立任务并重新评审。
8. **客户端页未新增检查**：直接复用 T09-R2，本任务没有重验真实客户端识别；队友的 T06-R3/T10 仍在进行。
9. **未构建全解决方案**：按任务要求只构建 Desktop 与 Desktop 测试；未声称其他工程的最终状态。
10. **无预修复失败测试**：T07-R2 是展示语义增量，未新增“旧实现失败”的复现测试；旧语义证据是 `SessionLifecycleTests` 原断言（0 探测仍期望“当前样本正常”），已在实现中按任务卡改掉并保留说明。

## 10. 是否有真实网络操作

**无。** 未启动真实探测、未连接外部目标、未做真实 DNS 查询、未读写真实客户端配置、未修改系统网络设置。渲染使用合成演示；全部测试使用合成 fixture 或临时本机会话文件，不读真实网卡、真实客户端配置或外部网络。

## 11. 验收映射（本任务负责的展示部分）

| 验收 | 本任务落点 | 状态 |
|---|---|---|
| QA-002 三路径参数可比、缺失保留 | 三路径对比卡：OptionsKey/窗口/启动跨度/缺失路径逐项判定 | 合成通过；真实外部待验 |
| QA-003 不误标 physical-direct | 路径名“无显式代理（可能经 TUN）”；探测 Notice 原样显示 | 通过（展示层） |
| QA-006 metric 不覆盖失败 | 首页采集健康与验证分开；诊断异常优先显示；变化行只陈述值 | 通过（合成） |
| QA-007 路径差异不判全网健康 | 逐路径状态/阶段/失败；总状态不因本地诊断为空转绿 | 通过（合成） |
| QA-011 DOM 口径 | 未改探测；边界文案强调 HTTP 204 不等于页面可用 | 不涉及（T04） |
| QA-020 IPv6 分族 | 族能力行 + T04 缺口码；IPv4 成功不掩盖 IPv6 失败 | 展示通过；真实双栈待验 |
| QA-021 代理鉴权/阶段区分 | 阶段、失败端点、分类、解析位置逐项显示 | 展示通过；真实外部待验 |
| QA-023 只读 GUI | 无写网入口；保存设置不发请求；按钮仅在观察/目标条件下启用 | 通过 |
| QA-037 托盘生命周期 | 未改关闭/托盘逻辑；只读页面 | 未回归（队友 T10） |
| QA-039 多客户端 | 客户端页复用 T09-R2 | 未改，沿用 T09-R2 结果 |

## 12. 建议下一任务（不自动执行）

按施工顺序下一张为 **T08-R2**（脱敏、缺口统计与版本溯源）；本任务完成即停，不领取 T16/G1-R2，不创建提交或推送。
