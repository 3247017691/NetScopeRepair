# T09-R2：客户端身份、内核监听归属、TUN 候选与配置 Gap 分类

任务：T09-R2（原 T09 定向补修，不重做 M0/M1，不领取后续任务）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：原设计 02/03/07/08/09/11，T09 身份/端点/TUN/配置边界，QA-009/010/019/039

**结论：本轮只修只读适配与客户端页显示。实际行为测试和真实只读探针都已完成；由于 Nice 界面进程与 FlClashCore 在当前普通权限下都不暴露路径/启动时间，且核心由 `FlClashHelperService` 启动，本轮没有把 `127.0.0.1:7890` 归给 Nice。事实与缺口分别展示，未用名称/端口硬认领。T06-R3 的真实诊断 Records 和 T04-R2 的探测事实仍未实现。**

## 1. 实现了什么（用户可观察行为）

1. **进程身份与字段读取状态**：`ProcessObservation` 现在带父 PID、PID+启动时间，以及 `UnreadableFields`（`access_denied`、`process_exited`、`parent_table_unavailable` 等短原因码）。路径读不到不再与“不存在”混淆。
2. **界面/内核/监听分实体**：客户端卡片新增“进程”行，分别显示界面进程与内核候选的 PID、名称、启动时间、关联等级（父子/同安装目录/未确认）和依据；真实监听通过 PID 归属显示在“端点”，并带所有者 PID、名称、读取状态和归属依据。
3. **归属等级而不是猜测**：只有“父子 PID 且双方启动时间可验证”或“同一安装目录（并记录这是路径关联）”才关联客户端。同名核心来自不同目录、父子实例无法验证、多个客户端目录相同等情况保留在“未归属的内核候选进程”，不串到 Nice/Apex。
4. **未归属监听不丢事实**：没有可信归属的监听仍出现在“未识别的监听进程”，显示端点、地址族、所有者 PID、读取状态与原因；IPv6 所有者不支持时明确写 `unsupported_family`，不借用 IPv4 结果。
5. **系统级 TUN 与客户端 TUN 分开**：新增“系统 TUN 候选（不归属客户端）”观察行，逐候选显示启发式依据、操作状态、GUID/ifIndex/LUID 和分族地址；客户端 TUN 胶囊只显示“归属未确认/未知”，不再把同一个全局 TUN 复制给每个客户端。适配器列表 Partial 且无候选时系统行保持 null，不写 false。
6. **配置解析 Notice/Coverage**：白名单外的普通结构（proxies/rules/providers 等）只进入 Notice，不再让正常配置显示“已解析（存在缺口）”；缺必需字段、坏格式、读取失败继续进入 Gap。`ClientConfigEvidence` 增加 `Notices`/`Coverage`，`ClientStatus.IsComplete` 按每项事实是否为 `present` 计算，不靠改文案变绿。
7. **客户端页**：沿用现有 Magpie 主题、SectionCard、胶囊；“端点”显示实际监听与来源，“配置来源”证据块显示字段覆盖 N/M 和单独的配置提示；新增“系统观察（不归属客户端）”区块。磁盘里的 `mixed_port` 只作为配置事实，不当成实际监听。

## 2. 实际修改文件

适配与模型：

- `src/NetScope.Adapters/AdapterSeams.cs`：进程父 PID/字段原因、监听地址族/所有者读取状态/实例时间、网卡 GUID/ifIndex/LUID/分族地址；新增 `system_tun`、`unattributed_core` 观测 ID。
- `src/NetScope.Adapters/SystemSeams.cs`：toolhelp 父进程快照；进程字段原因码；IPv4 监听所有者表（owner-module listener 优先，PID 表回退，唯一端口匹配，重复投影去重）；网卡稳定身份与 LUID（`iphlpapi!ConvertInterfaceIndexToLuid`）。
- `src/NetScope.Adapters/ClientCatalog.cs`：界面名与内核候选名分离，身份版本 `client-identity/1.1.0`。
- `src/NetScope.Adapters/GenericAdapter.cs`：归属判定、监听归属、未归属内核/监听行、系统 TUN 行、配置 Notice/覆盖接入。
- `src/NetScope.Adapters/TunAdapterHeuristics.cs`：候选证据类型改名，避免与 Application 的 `TunCandidate` 冲突（逻辑未放宽）。
- `src/NetScope.Adapters/WhitelistParseResult.cs`、`NiceConfigParser.cs`、`ApexConfigParser.cs`、`NiceAdapter.cs`、`ApexAdapter.cs`：Notice/Coverage、必需/可选字段、解析器版本（Nice `1.0.2`、Apex `1.0.1`）。
- `src/NetScope.Application/Adapters.cs`：`ClientEndpoint` 所有者/来源字段、`ClientProcess`、`TunCandidate`、`ConfigFactCoverage`、`ClientConfigEvidence.Notices/Coverage`、`ClientStatus.Processes/TunOwnership/TunCandidates` 与 `IsComplete`。

桌面展示（最小增量）：

- `src/NetScope.Desktop/ViewModels/ClientsViewModel.cs`：客户端行/系统观察行拆分、进程/端点/TUN/覆盖/提示文案。
- `src/NetScope.Desktop/ViewModels/Rows.cs`：`ClientRow` 增加进程字段，`ClientEvidenceRow` 增加提示字段，新增 `ObservationRow`。
- `src/NetScope.Desktop/Views/ClientsView.xaml`：加“进程”行、配置提示、系统观察区块；未改 Theme/Logo/Shell/关闭行为。
- `src/NetScope.Desktop/Services/DemoClientProvider.cs`：合成演示适配新模型（合成 TUN、内核进程、覆盖/提示）。

测试与证据：

- 新增 `tests/Adapters/T09R2PreFixReproducerTests.cs`（4 个修前失败反例，保留作证据）。
- 新增 `tests/Adapters/T09R2RegressionTests.cs`（14 个正反例）。
- 新增 `tests/Desktop/T09R2ClientViewTests.cs`（2 个展示映射回归）。
- 修改 `tests/Adapters/GenericAdapterTests.cs`、`NiceAdapterTests.cs`、`ApexAdapterTests.cs`、`TestSupport.cs`（语义变化与 mock 适配；未删除测试）。
- 新证据目录 `docs/reports/evidence/T09-R2/`（本地被 `.gitignore` 忽略；报告内链接指向本机文件）。

未修改：`ObservationSession`、调度、Probes、Repair/Broker、`Theme.xaml`、图标/Logo、`ShellView.xaml`、`MainWindow.xaml`、`CloseChoiceDialog.xaml`、`TrayController.cs`、启动入口、真实客户端配置与系统网络设置。

## 3. 修前失败证据（旧实现）

先写 4 个只使用现有 API 的反例，在未修改的 HEAD 上运行：

| 反例 | 旧行为 |
|---|---|
| `Nice_ui_only_process_leaves_its_real_core_listener_unattributed` | Nice 卡片端点为空，7890 被丢进 unknown |
| `One_global_tun_candidate_is_currently_copied_onto_both_clients` | Nice/Apex 都 `TunEnabled=true`、`TunAddress=198.18.0.1/16` |
| `Ordinary_ignored_structures_currently_become_a_parse_gap` | 6 个字段完整 + proxies 结构仍产生 Gap |
| `Partial_adapter_list_currently_reports_tun_false_for_everyone` | 适配器 Partial 且无候选时全员 `TunEnabled=false` |

运行结果：**0 通过 / 4 失败，退出码 1**。见 [prefix-failures.log](evidence/T09-R2/logs/prefix-failures.log)、[prefix-failures.trx](evidence/T09-R2/test-results-baseline/prefix-failures.trx)。

## 4. 修后验收与命令

环境：.NET SDK `10.0.403`（[dotnet-version.txt](evidence/T09-R2/logs/dotnet-version.txt)），Windows 普通权限；未提权、未启动客户端、未改网络。

| 命令 | 结果 |
|---|---|
| `dotnet build NetScopeRepair.slnx` | 退出码 0，**0 警告 / 0 错误**；[build-final.log](evidence/T09-R2/logs/build-final.log) |
| `dotnet test tests/Adapters/... --logger trx` | 99 通过 / 0 失败 / 0 跳过，退出码 0 |
| `dotnet test tests/Desktop/... --logger trx` | 35 通过 / 0 失败 / 0 跳过，退出码 0 |
| 其余 5 个测试工程 | Contract 80、Unit 243、Windows 79、Replay 58、Export 43，全部 0 失败 / 0 跳过，退出码 0 |
| **完整测试** | **637 通过 / 0 失败 / 0 跳过**（原 617 + 本轮新增 20） |
| 只读探针 | 退出码 0，[readonly-observation.json](evidence/T09-R2/readonly-observation.json) |
| 七页离屏渲染 | 退出码 0，[04-clients.png](evidence/T09-R2/screens/04-clients.png) 等 |
| 真实源包 SHA-256 | 仍为 `8cdbdc33…3a6`，未变 |

TRX 在 [test-results-final](evidence/T09-R2/test-results-final)；基线 81/33 通过见 [test-results-baseline](evidence/T09-R2/test-results-baseline) 与 [adapters-baseline.log](evidence/T09-R2/logs/adapters-baseline.log)、[desktop-baseline.log](evidence/T09-R2/logs/desktop-baseline.log)。这些是本次 HEAD 的实际计数，不复制历史 616/617。

### 覆盖的失败反例（新增测试）

1. UI/core 不同 PID + 可验证父子 + 核心拥有监听：Nice 卡片显示两条进程、7890 端点、所有者 PID 和父子依据。
2. 同安装目录、无父子的核心：以路径关联归属，并明确写“不是父子实例验证”。
3. 两个同名 core 来自无关目录：两条都不归属 Nice，分别保留在未归属内核行和未识别监听行。
4. 只有 UI：Nice 运行、无核心、无伪造端点；只有 core：Nice 未运行、核心未归属但事实保留。
5. 核心路径不可读但监听所有者可读：端点仍经父子归属显示，同时保留 `executable_path=access_denied` 缺口。
6. PID 复用（监听创建时间早于当前实例）：端点事实保留，归属被拒绝，unknown 行给出 PID 复用缺口。
7. 两个客户端和平并存：无“端点并存”、无冲突结论；同地址端口两个所有者时仍显示并存说明。
8. IPv6 所有者不支持：IPv6 端点保持未归属，Nice 只保留 IPv4，缺口写明不借用 IPv4。
9. 一台机器两个 TUN 候选（含 IPv6 首项 + IPv4 同时存在）：系统行逐候选分族显示，客户端只显示归属未确认；Partial 无候选为 null。
10. 完整白名单 + proxies/规则/provider 结构：有效、Gap 为空、Notice 非空、覆盖全 present；缺必需字段/坏格式/读取失败仍为 Gap；`IsComplete` 只在事实完整时为 true。

## 5. 真实只读核验（本机，普通权限）

探针源码：[readonly-probe](evidence/T09-R2/readonly-probe/Program.cs)。只调用进程/监听/网卡三个系统源，使用空代理层、无配置适配器；不连接任何端点、不读写配置、不提权。输出：[readonly-observation.json](evidence/T09-R2/readonly-observation.json)。

当次快照：进程 401（路径读 228、父进程读 401）；TCP 监听 7 个（全 IPv4；所有者读取 2、未定位 5、实例时间 2）；网卡 48（GUID 48、ifIndex 9、LUID 9）；系统 TUN 候选 8 个（含 Nice 的 Meta Tunnel、Teredo/IP-HTTPS/6to4、WAN Miniport；均只作候选）。

关键事实：

- Nice 界面 `Nice加速` PID 24452 运行中，但路径/启动时间 `access_denied`（进程受保护或提权），核心 `FlClashCore` PID 37176 同样路径/启动时间不可读，父进程为 `FlClashHelperService`(5052)，不是 Nice UI。
- `127.0.0.1:7890` 的实际监听所有者读出为 PID 37176（owner-module listener 表地址写作 `0.0.0.0`，通过唯一端口匹配补上，并在端点依据中注明）。
- 因此本轮真实页面的正确结果是：Nice 卡片“无本客户端可归属的实际监听”，未归属内核行显示 FlClashCore 37176，未识别监听行显示 7890 与所有者 PID 37176；**没有**把 7890 归给 Nice。这是有意保留边界，不是修复失败。

页面演示图：[04-clients.png](evidence/T09-R2/screens/04-clients.png)（合成数据）。截图证明卡片新增进程/端点/胶囊/提示的布局；系统观察区在 900px 视口下方，未出现在截图中，其绑定由 [T09R2ClientViewTests.cs](../../tests/Desktop/T09R2ClientViewTests.cs) 覆盖。渲染不等于鼠标交互验收。

## 6. UI 保护检查

对 [ui-baseline.json](../plans/evidence/20261105/ui-baseline.json) 的 20 个文件逐一重算 SHA-256：**18 个未变，2 个为本任务允许的最小增量**（`ViewModels/Rows.cs`、`Views/ClientsView.xaml`），**0 个意外改动**。Theme、Logo/ico、Shell、关闭对话框、MainWindow、TrayController、启动入口全部未变。明细：[ui-protection.json](evidence/T09-R2/ui-protection.json)。用户三次 UI 提交（`b348491`、`196301f`、`a4b07a0`）保留。

## 7. 只读/权限/隐私边界

- 产品适配器新增代码只有进程/监听/网卡/PID 表读取；无 connect、无 DNS 查询、无配置写入、无提权、无启动/停止客户端。
- 探针未读取任何客户端配置文件、环境代理值、注册表代理设置；真实客户端配置仅由既有适配器在正常试用时读取，且仍为 `disk_only`。
- 新证据只保存在本地被忽略的 `docs/reports/evidence/`；未上传源包、未把真实包加入 fixture、未创建提交/远程推送。
- 输出中仅含本机 PID/路径/端点等本地事实；没有凭据、订阅、配置全文。真实探针 JSON 仅本地保留。

## 8. 已知限制与未实现项

- **核心归属**：当前机器的 Nice UI 与 FlClashCore 都拒绝路径/启动时间，且核心由独立 helper 服务启动，故无可验证关联。产品不会仅凭 `FlClashCore` 名称或 7890 端口归属；因此 Nice 卡片端点仍为空，而系统观察区保留 7890 的所有者事实。获得可信归属需要管理员权限下的路径/启动参数证据或客户端运行时 API，不在本轮范围。
- **TUN 归属**：没有可信 owner 来源，本轮只展示系统候选和“归属未确认”；未证明核心↔TUN 强归属，也未实施修复资格判定（DX-003/004 仍待 T13）。
- **PID 复用**：监听实例时间依赖 owner-module 表；回退 PID 表时该字段未知，复用检测只能保留“未验证”，不能保证发现所有复用。
- **监听采样竞态**：枚举与所有者表之间仍可能有瞬时端口未定位（本机 5/7），已记录 `owner_row_not_found`，未当作端口关闭；未做二次补采。
- **IPv6 所有者表**：仍不支持，IPv6 端点只保留事实与 `unsupported_family`；不借用 IPv4。
- **配置“必需字段”**：本轮将 Nice `mixed_port/mode/tun_enable`、Apex `mixed_port/mode/tun_auto_route` 视为必需；其余缺失为 Notice。这是本任务的可解释产品选择，不改原设计配额/窗口/阈值。
- **系统 TUN 候选噪声**：启发式会把 Teredo/IP-HTTPS/6to4/WAN Miniport 等列入候选；全部只作系统候选，不归客户端。后续如需收敛，应作为独立任务调整启发式并补反例。
- **未实现**：真实诊断 Records 接线（T06-R3）、真实 DNS/连接探测事实（T04-R2）、故障前后窗（T10）、TUN 修复/事务（T13—T16）。本轮不能声称 M1 或 M2 完成，也不能声称真实桌面诊断链已接通。
- 网格/卡片的系统观察区只在滚动的下方出现；900px 截图不含该区，鼠标交互与滚动验收仍需人工试用。

## 9. 设计/ADR 偏差

- `ClientStatus.TunEnabled` 在客户端行改为 null（归属未知），机器级 TUN 事实移到 `system_tun` 观测行；这是为满足“不得把全局 TunView 赋给每个客户端”的模型语义修正。
- 新增 `system_tun`、`unattributed_core` 两个稳定观测 ID 和对应 UI 区块；没有新增阶段、门禁或修复能力。
- 解析器版本因输出语义变化升级；原设计默认值、QA 矩阵、M1/M2/M3 顺序未改。

## 10. 建议下一任务（不自动执行）

建议复核通过后领取 **T04-R2**（真实 DNS/连接事实，让 T06-R3 的规则有输入）；本轮完成即停，不执行 T04-R2/T06-R3，不创建提交或推送。
