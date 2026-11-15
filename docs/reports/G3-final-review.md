# G3 门禁处置记录：外部复核转录与不通过结论（M1—M3）

> 本文件是**外部独立复核结论的转录与门禁处置记录**，不是施工方自行完成的一次 M3 独立高权限复审；
> 文件名沿用 README 既有链接目标，结论以外部复核原件为准。

复核日期：2026-11-06（Asia/Shanghai）。复核对象：当前完整工作树（含全部未提交/未跟踪的 M1—M3 代码），
分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`。
复核承接：[施工总指令](../plans/DeepSeek-V4.1-Flash-construction-plan.md)、
[设计包里程碑](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/08-里程碑与施工任务.md>) 与
[T04-R2](T04-R2-report.md)、[T06-R3](T06-R3-report.md)、[T07-R2](T07-R2-report.md)、[T08-R2](T08-R2-report.md)、
[T09-R2](T09-R2-report.md)、[T10](T10-report.md)、[T11](T11-report.md)、[T12](T12-report.md)、
[T13](T13-report.md)、[T14](T14-report.md)、[T15](T15-report.md)、[T16](T16-report.md) 各任务报告。

## 0. 本文件性质与结论

**本文件不是通过证明，是 G3 门禁的失败结论与本轮整改入口。**

- **G3：不通过。** M1 不通过本次复核；M2 不通过；M3 未完成且不通过开放门禁。
- 记录 **20 条代码缺陷（P1 11 条 / P2 9 条）与 5 项交付缺口（G01—G05）**；13 条已动态复现，7 条静态控制流确认。
- 可以保留并确认可用的部分：全解决方案构建通过、1143 项既有测试通过、9 页离屏渲染通过、真实只读采集与本地脱敏导出通过、
  CLI 三项通过、有效故障包离线重放通过。
- **不能宣称**：M1 真实三路径与规则供数已可用；M2 观察闭环/T11 配额恢复/T12 回放已闭环；M3 受控修复已完成或可开放。
- **M3 真实执行入口保持关闭是正确的，但关闭入口不等于完成受控修复**：生产组合仍为 `UnavailableRepairGateway`，
  M3 当前是模型 / 协议 / 模拟测试 / 预览资产。不能靠改开关补完成度。

本轮由外部独立复核代理执行（M1、M2 两个独立审查者编写隔离反例，由主导复核者实际代跑并逐项读回；
M3 独立子代理被平台内容风险检查中断，改为静态核读，未形成动态 M3 反例与最终独立高权限结论）。
仓库内的自助复核工作流在外部复核交付后已终止，以外部复核结论为准；**本轮未修改任何产品源码**，
仅新增本报告与证据副本、更正 README 的完成口径（见 §9）。

收尾核查（2026-11-06 外部代理）已确认：与 312 项源码/配置 SHA256 基线比较**只有 README 变化**，
其余 311 项不变；九份证据副本与复核原件逐字节一致；本报告链接均可解析。
**F01—F20 尚未修复，G01—G04 尚未关闭，G1/G2/G3 没有因此通过。**

## 1. 复核基线与限制

| 项 | 内容 |
|---|---|
| 源码基线 | `D:/Projects/NetScopeRepair`，`main`，HEAD `a4b07a0`；审查对象是**全部当前未提交/未跟踪代码**，不能只用 HEAD 标识 |
| 固定摘要 | 312 项源码与配置摘要：外部复核目录 `evidence/source-manifest.json`（未复制进仓库，见 §10） |
| 设计基线 | 设计包 docs 01—11，重点 [07-测试验收与故障实验室](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/07-测试验收与故障实验室.md>)、[05-修复事务与权限边界](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/05-修复事务与权限边界.md>)、[11-用户需求确认与首版验收](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/11-用户需求确认与首版验收.md>) |
| 未做 | 隔离 VM 真实断网/平台矩阵、UAC 提权与原生写入、真实签名部署、真实断电/多实例/并发漂移、真实托盘鼠标交互、24 小时资源测试、DSH/Antigravity/Chrome/TUN 业务复测、发布与历史秘密扫描 |
| 真实网络写入 | 无。唯一真实探测 socket 是只返回 407 且不转发的 `127.0.0.1` 假代理；真实只读 smoke 中 `interfaces`/`proxy` 为 Partial，如实保留，未改写为正常 |

## 2. 重新运行的实际证据

| 项目 | 结果 | 证据 |
|---|---|---|
| 全解决方案构建 | 退出 0，0 警告 / 0 错误 | 外部复核 `evidence/build.log` |
| 八个原有测试工程 | **1143 通过 / 0 失败 / 0 跳过**（Adapters 99、Contract 88、Windows 79、WindowsIntegration 273、Desktop 135、Unit 320、Replay 97、Export 52） | [test-summary.json](evidence/G3-review-20261106/test-summary.json) |
| 桌面九页离屏渲染 | 退出 0（home/timeline/diagnoses/case-replay/repair/clients/export/settings/help） | 外部复核 `evidence/screens-verified/` |
| 真实只读 smoke | 3 快照、38 事件；`routes`/`dns` Ok，`interfaces`/`proxy` Partial；本地脱敏导出 35,155 字节 | [desktop-smoke.json](evidence/G3-review-20261106/desktop-smoke.json) |
| CLI check / demo / case-replay | 三项均退出 0 | 外部复核 `evidence/cli-exits.json`、`evidence/cli-case-replay.utf8.log` |
| M1 隔离反例 | 4 组，无 harness 异常；本机 CONNECT 407 不转发 | [repro-m1-results.json](evidence/G3-review-20261106/repro-m1-results.json) |
| M2 隔离反例 | 4/4 复现 | [repro-m2-results.json](evidence/G3-review-20261106/repro-m2-results.json) |
| UI / 验证器隔离反例 | 5/5 复现 | [repro-ui-results.json](evidence/G3-review-20261106/repro-ui-results.json) |

> 计数属实不等于覆盖充分：现有套件主要验证局部模型 / fake，遗漏真实组合合同、重复生命周期与部分失败后的重试。
> **13 条反例是用于确认当前错误的审查资产，返回 0 表示成功观察到该错误，不能算作 13 项功能通过。**

## 3. 按里程碑的实际交付判定

| 范围 | 判定 | 依据 |
|---|---|---|
| M1 / T07—T09 及定向补修 | **不通过本次复核** | 基础 UI / 只读采集 / 导出可运行；真实三路径与规则供数、407 与 IPv6 有缺陷（F06—F09） |
| M2 / T10 | **未完成且有缺陷** | 外部持续调度未接；后窗可误判完整；跨周期诊断与业务记录入口不完整（F08、F10、F19、G01） |
| M2 / T11 | **服务资产完成，产品未完成** | 配额 / 恢复 / 钉住未接会话；独立清理服务还会丢失失败汇总（F12、G02） |
| M2 / T12 | **可回放有效包，但不通过** | 当前规则离线链可运行；多标记合并、类型错误拒绝、引用检查不完整（F11、F13、F20） |
| M3 / T13 | **真实能力未完成** | 无 VM 平台动作矩阵及可信运行事实；原生平台适配器不可用（F04、G03） |
| M3 / T14 | **协议 / 库资产，未完成真实 broker** | 无真实 UI 客户端 / UAC / 可信签名部署；独立安全审查中断（F04、F05） |
| M3 / T15 | **不通过开放门禁** | 恢复耐久、权威目标、未知步骤与授权绑定存在静态缺陷（F01—F03、F05） |
| M3 / T16 | **预览 / 模型资产，未完成闭环** | 默认 Unavailable；命令 / 取消 / 过期 / 验证器有反例；业务录入与持久化缺失（F14—F18、G04） |
| G1-R2 / G2 / G3 | **不得视作通过** | 本轮门禁报告此前不存在（G05）；旧 G1 / G1-R1 历史报告不能替代当前工作树验收 |

## 4. 代码缺陷总览

| ID | 级别 | 范围 | 问题 | 核验 | 结论 |
|---|---|---|---|---|---|
| F01 | P1 | M3 | 恢复路径吞掉 journal 错误，仍可继续写入并返回 `RolledBack` | 静态 | 阻止开放 |
| F02 | P1 | M3 | 恢复核验后仍用请求携带的 Target 而非权威 journal 目标 | 静态 | 阻止开放 |
| F03 | P1 | M3 | `TransactionRecoveryRequired` 被当作已解释 intent，隐藏未知写入 | 静态 | 阻止开放 |
| F04 | P1 | M3 | 授权摘要与 broker 重验未绑定完整当前接口身份 | 静态 | 阻止开放 |
| F05 | P1 | M3 | 动作顺序未绑定授权，也未独立验证依赖拓扑 | 静态 | 阻止开放 |
| F06 | P1 | M1 | 真实探测地址族与诊断可比性契约互斥 | 动态 | 阻止开放 |
| F07 | P2 | M1 | HTTPS CONNECT 的代理 407 丢失为普通传输失败 | 动态 | 需修 |
| F08 | P2 | M1/M2 | 真实会话不向 DX-008 提供跨周期间歇样本 | 动态 | 需修 |
| F09 | P2 | M1/M2 | 双栈接口只采 IPv4 参数，IPv6 缺失却返回 Ok | 动态 | 需修 |
| F10 | P1 | M2 | 后窗中间完全缺样仍被认定完整 | 动态 | 阻止开放 |
| F11 | P2 | M2 | 回放保留首次 incident 标记，覆盖不了最终后窗状态 | 动态 | 需修 |
| F12 | P1 | M2 | 配额清理重试把唯一失败汇总覆盖为零 | 动态 | 阻止开放 |
| F13 | P2 | M2 | 不可信包的字段类型错误逸出而非结构化拒绝 | 动态 | 需修 |
| F14 | P2 | M3 UI | 修复页未通知命令可执行性变化 | 动态 | 需修 |
| F15 | P1 | M3 UI | 首个执行中的事务无法从修复页取消 | 动态 | 阻止开放 |
| F16 | P2 | M3 UI | 计划过期仅看预览时的布尔值，不随时钟失效 | 动态 | 需修 |
| F17 | P1 | M3 验证 | 修复前后路由均缺失却通过技术验证 | 动态 | 阻止开放 |
| F18 | P1 | M3 验证 | 修复前后可比性忽略路径、代理、boot 和窗口变化 | 动态 | 阻止开放 |
| F19 | P2 | M2 | incident 关闭后遗留失败连续计数可使单次失败重开事件 | 静态 | 需修 |
| F20 | P2 | M2 | incident 中部分悬空引用不会被完整性检查发现 | 静态 | 需修 |

完整触发条件、代码定位（精确到行）、影响、整改要求与对应 QA 条目见
[findings.json](evidence/G3-review-20261106/findings.json) 与
[全面复核报告](evidence/G3-review-20261106/全面复核报告.md) §6。逐条要点：

- **F01** `src/NetScope.Repair/RepairRecovery.cs:543/589/615`：intent / applied / 终态追加捕获异常却不传回失败，
  setter 与后续步骤继续，最终可能报告 `RolledBack`，违反"先持久化再写"。
  整改：恢复追加返回明确成功/失败；intent 不耐久禁止 setter；applied/终态不耐久必须停在 `recovery_required`。
- **F02** `RepairRecovery.cs:434/467/494`：`sameScope` 只比字段序号、动作和前后值，不逐项比 Target；
  执行身份取自 `request.Plan.Target`，补偿可能不落在原接口。整改：执行范围与 `InterfaceIdentity` 只从权威 journal 重建。
- **F03** `RepairRecovery.cs:118/141`、`RepairTransactionCoordinator.cs:429`：写结果未知时持久化
  `TransactionRecoveryRequired`，扫描器却把它列入 `explainedByState`，未知步骤不再标 Uncertain。整改：不确定性按步骤保留。
- **F04** `RepairPlan.cs:233`、`InterfaceIdentity.cs:104`、`BrokerValidation.cs:249/351`：摘要只绑定
  `TargetFingerprint`（GUID/族/compartment），设备实例 / LUID / ifIndex / 类型 / 驱动事实不进入授权；
  `RepairPreflightRevalidator` 无生产调用。整改：摘要与 broker 重验绑定完整身份事实并接通 revalidator。
- **F05** `RepairPlan.cs:233`、`BrokerValidation.cs:240`、`RepairTransactionCoordinator.cs:329`：摘要按 `ActionId`
  排序，执行按传入数组顺序，broker 无依赖 DAG / 拓扑校验。整改：定义唯一受授权执行顺序或由 broker 按已验证 DAG 排序。
- **F06** `HttpProbeTransport.cs:110`、`HttpProbeRunner.cs:69`、`DiagnosticComparability.cs:21`：null 组不可比；
  any 可完成三路径但诊断只接受 ipv4/ipv6；指定 ipv4/ipv6 时系统/显式代理路径 Unsupported，整组不可比。
  整改：区分请求策略族、实际连接族与代理端目标未知族，统一生产者 / 可比性 / 规则契约（不得伪造代理端族）。
- **F07** `HttpProbeTransport.cs:394/440`：仅有代理隧道异常的 `TransportFailure`，407 与代理端点归属丢失。
  整改：识别 `HttpRequestException` 中代理隧道状态，保留 407 归属并分类 `AuthenticationRequired`。
- **F08** `ObservationSession.cs:285/298`、`Dx008IntermittentFailure.cs:16`：每轮新 window 且证据集只有当轮样本，
  六轮交替超时/成功仍 DX-008=0。整改：在受控时间窗维护同目标/路径/族/options/boot 的跨周期证据并按 incident 窗口供规则。
- **F09** `WindowsSources.cs:92/103/124`：只调 IPv4 原生读取，双栈接口返回单条 ipv4 行，
  IPv6 metric / IgnoreDefaultRoutes 缺失却无缺口。整改：按接口 + 地址族分别采集参数与完整性，IPv6 未读单列缺口。
- **F10** `IncidentRecorder.cs:546/562/634`：`MaxSampleGap=60s` 未识别 180 秒完全缺样，
  状态转 Closed、`PostWindowComplete=true`。整改：逐段检查后窗覆盖，超限重开/延长或标 incomplete。
- **F11** `CaseReplayService.cs:704/710`：`??=` 锁死首次后窗状态，recorder 终态 true 而回放 false。
  整改：按事件序号归并，终态取最新有效标记。
- **F12** `QuotaManager.cs:639/749/786/908`：孤儿索引无源日志事实，`FailureCount` 默认 0 并覆盖权威汇总。
  整改：日志与索引按会话统一清理；无事实的索引不得覆盖权威汇总；汇总写入幂等。
- **F13** `CaseReplayService.cs:70/499`、`CaseReplayViewModel.cs:144`、`AsyncRelayCommand.cs:39`：
  `GetValue<bool>()` 抛 `InvalidOperationException` 而非结构化拒绝，命令无 onError。整改：整个解析边界先验类型/范围并返回 `Rejected`。
- **F14** `RepairViewModel.cs:125/585`、`AsyncRelayCommand.cs:20`：`CanExecute` 已 true 但
  `CanExecuteChanged` 通知 0 次。整改：预览 / 选择 / 忙碌 / 事务 / 过期变化时显式刷新命令，补真实 Command 绑定回归。
- **F15** `RepairViewModel.cs:128/409/433`：VM 直到最终 outcome 才设 `_currentTransaction`，首个 applying 事务期间取消不可用。
  整改：启动即建立活动事务并订阅状态，取消交由协调器安全补偿。
- **F16** `RepairViewModel.cs:272/359`：过期判定用预览时的布尔值，11 分钟后仍可执行。
  整改：每次计算/执行用当前可信时间核对过期并定时刷新（后端仍独立验证）。
- **F17** `RepairVerification.cs:110`：before/after `RouteInterfaceFingerprint` 均为 null 时
  `string.Equals(null,null)` 被当作 `route_unchanged`，最终 `Passed`。整改：关键观测先做完整性闸门，缺失返回 `Insufficient`。
- **F18** `RepairVerification.cs:219/231`：path / proxy / boot / `ObservationWindowId` 不同仍返回 `Comparable`。
  整改：显式核对路径、代理、启动边界、窗口与采样时间、有效期，未知维度拒绝因果比较。
- **F19** `IncidentRecorder.cs:140/373/557/685`：`_failureStreaks` 在 open 内成功与正常关闭时都不清零，
  下一事件沿用旧计数与 `FirstAt`。整改：按状态定义并重置失败序列，补连续两次 incident 整链回归。
- **F20** `CaseReplayService.cs:718/752/762`：只有全部引用都无法映射才告警，部分悬空引用静默保留仍可能 `Ok`。
  整改：逐个校验 event/snapshot/diagnosis 引用存在且作用域正确，不支持字段显式 unsupported。

## 5. 交付缺口

| ID | 范围/归属 | 缺口 | 定位 |
|---|---|---|---|
| G01 | **M2 / T10**（R2 关闭，非 R3） | 没有用户启用后的持续外部观察调度：只 `CollectOnceAsync`，外部探测仅一次性手动 `RunDiagnosisAsync`；30 秒观察与 5 秒/2 分钟退避未接通（且不得改成启动即自动外部请求）。持续观察是故障捕获的必需环节，必须在 M2 关闭 | `src/NetScope.Desktop/Services/ObservationSession.cs:128` |
| G02 | M2 / T11 | T11 配额/恢复/钉住未产品化：`QuotaManager`、`SessionRecovery` 无桌面会话调用；7 日/500MB、分级降采样、坏尾隔离、重建索引/补 gap、1GB 钉住与清理入口未接通；一次写失败后会话永久 `_persistenceDisabled` | `src/NetScope.Desktop/Services/Composition.cs:83` |
| G03 | M3 / T13—T16，**按任务分列真实能力交付**（不得只写"门禁待补"） | 真实修复链未交付：T13 可信平台事实生产者 + VM 三字段矩阵 + 可用原生适配器 + preflight 生产接线；T14 一次性 broker 进程/UI 客户端 + 真实 ACL/nonce/预算 + UAC + 签名部署与攻击面验证；T15 受限 ACL 权威 journal + 崩溃/断电切点 + 跨 boot 重建 + 实机恢复闭环；T16 真实 UI→broker 接线 + 逐动作确认 + 恢复 UI + 事务/业务关联。生产组合为 `UnavailableRepairGateway`，Broker 仅类库 | `src/NetScope.Desktop/Services/Composition.cs:85` |
| G04 | **M2 部分 + M3 部分分开** | M2 先交四类业务结果/时间/来源/备注录入、持久化、导出与离线重建（Chrome 国内外分列）；M3 再绑定具体事务，做恢复后十分钟/三轮观察与独立结果统计。当前 `RecordBusinessRound` 无生产调用、`RepairBusinessObservation` 只在内存、未连 incident 业务事件，修复页只有按钮不能关闭 | `src/NetScope.Desktop/Views/RepairView.xaml:197` |
| G05 | G1/G2/G3 | 完成声明引用的最终独立复核报告此前不存在：README 声称完成到最终独立复核并链接本文件，但文件缺失，旧 G1/R1 历史报告不能替代当前工作树验收 | `README.md:8` |

**归属更正**（相对首版本报告的整改编排）：G01 从 R3/T16 移回 R2/T10；G04 拆为 M2 业务记录闭环 + M3 事务后观察两段；
G03 在施工单 §7.3 按 T13—T16 分列真实能力、当前状态与资源阻塞。
**G05 已在本次收口处理**：本文件即为该报告，结论为不通过；README 的完成口径已同步更正（见 §9）。

## 6. 规则与真实生产者对账

| 规则 | 当前真实链能力 | 不能据此宣称 |
|---|---|---|
| DX-001 | 本地显式代理 TCP 拒绝有机会支持；环境变量单独存在保持 candidate | 不能证明任意已启动应用实际继承/采用代理 |
| DX-002 | 默认主要为磁盘 fake-ip 线索，保持 candidate | 无可信 runtime / `fake_ip_adapter` 生产者，不能把磁盘配置当运行态 |
| DX-005 | 有监听归属事实 | 缺 bind-failure / 相关运行配置生产者，并存不等于端口冲突 |
| DX-006 | 磁盘与进程事实可给 candidate | 默认缺运行态核验/反证生产者 |
| DX-008 | 纯规则能接受同窗多样本 | 默认真实会话不供跨周期样本（F08） |
| DX-010 | 支持合成 `ProposedChange` | 默认会话不提供真实提议变更；不等于 TUN 修复已可用 |
| DX-011 | 纯规则与 stub 测试能触发 | 真实族契约互斥（F06） |
| DX-003/004/007/009/012 | 未进入当前 `RuleCatalog` | 不能把现有七条规则说成覆盖全部 TUN 根因与修复资格 |

这是能力核对，不建议为了让规则产生结论而伪造 runtime 证据、地址族、可比性或 TUN 归属。

## 7. 验收边界与未执行项

完整 40 项对账见 [QA验收矩阵.csv](evidence/G3-review-20261106/QA验收矩阵.csv)；其中 QA-032/033 属 M4、QA-040 属开源前，
本次不验收，其余条目保留"自动化 / 缺陷 / 未接通 / 真实待验"不同状态，未用汇总测试通过覆盖。

本次未执行：隔离 VM 真实断网与三字段平台矩阵；高权限原生 setter / 恢复；真实签名管道部署与攻击面验证；
Windows 构建 × 驱动 × API × store 平台矩阵；真实断电 / 多实例 / 客户端并发漂移；实际托盘鼠标键盘操作；
24 小时 CPU 与磁盘预算；DSH / Antigravity / Chrome 国内外 / TUN 应用业务复测；正式发行、许可证与历史秘密扫描。
现有导出与秘密哨兵测试通过，独立复核未发现新的已证 secret 明文泄露，但这不等于任意自由文本完全匿名，
也不替代公开发布前的完整历史检查。

## 8. 整改顺序与复验停点

1. **先更正完成口径并继续关闭真实修复**（本次已办）：本版标为只读试用 / 部分 M2 与 M3 代码资产，
   移除不存在的独立复核完成声明，保留全部未验证项。
2. **补 M1 真实链（R1，按依赖分批，不齐开）**：先 R1-A（T03/F09 逐族采集）与 R1-B（T04/F06+F07 统一"请求策略族 /
   实际连接族 / 代理端目标未知族"合同）并行，写范围分离、公共文件单一 owner；合同稳定后由**单一会话 owner**
   做 R1-C（T06/T07 的 F08 跨周期供数，协调 T10 窗口语义，不另建平行窗口模型）。
   重跑 4 组 M1 反例（转为产品回归断言：修前失败、修后通过）与原套件，再做明确目标的用户主动探测验收。
3. **补 M2 闭环**：T10 接用户主动启用后的观察调度（**G01**）并修 F10/F19、M2 侧 G04 记录入口与事件生命周期；
   T11 先修 F12 再接入配额/恢复/钉住与摘要入口（**G02**）；T12 修 F11/F13/F20；
   补重复事件、多标记、两轮清理、写失败后恢复、导出回放组合测试，随后才进 G2。
4. **修 M3 核心与 UI，并把 G03 当真实能力交付**：T13/T14/T15 先关闭 F01—F05，再按施工单 §7.3
   分别交付可信事实生产者、原生平台适配器与 VM 实验、一次性 broker 进程/客户端与签名部署、权威 journal 与实机恢复；
   T16 关闭 F14—F18，并补 M3 侧 G04（事务关联、恢复后业务观察）。缺 VM / 签名等资源时记录具体阻塞并保持关闭，
   不把缺项笼统写成"最后补门禁证据"，也不靠更换 gateway 或开关宣称完成。
5. **补原设计门禁证据**：合格 VM 上做三字段矩阵、耦合/跨 store/并发/崩溃/断连/离线恢复；完成签名可信部署与独立高权限审查；
   由用户实际跑 DSH、Antigravity、Chrome 国内外、依赖 TUN 的普通应用，逐项记录 pass/fail/not_tested 与时间备注，
   十分钟三轮后再评价业务恢复。

每个补修仍沿用原任务 ID 与"一个任务、一份交付、一个评审停点"。本次复核不推进 T17/T18、不重新设计项目、
不把缺失能力挪到 M4 来降低 M3 要求。

## 9. 完成口径更正（相对上一版 README）

| 位置 | 上一版表述 | 更正为 |
|---|---|---|
| README 当前状态 | "M1–M3 代码施工完成（T04-R2—T16）" | "M1—M3 代码施工已落地，**独立复核未通过**；只读试用 + 部分 M2/M3 代码资产" |
| README 链接 | 指向不存在的 `G3-final-review.md`，称为"施工完成的最终独立复核" | 指向本文件，称为"外部复核转录：不通过"，并注明不是施工方新完成的 M3 独立高权限复审 |
| README 后续 | "当前完成到 T16 与最终独立复核" | "已有截至 T16 的部分实现与报告，**DoD 与门禁未完成**；复核不通过（20 条缺陷 / 5 项缺口）；未推进 T17/T18，未提交未推送" |
| 施工单 §7 | R3 笼统含 G01、G04 入口，G03 只写"门禁待补" | G01 归 R2/T10；G04 拆 M2 记录闭环 + M3 事务后观察；G03 按 T13—T16 分列真实能力、状态与资源阻塞 |

产品源码、测试、发布包与运行脚本本轮均未改动；`artifacts/M1-R2` 发布物保持复核时状态。

## 10. 索引

本次复核的完整交付（外部目录 `D:/AAA-课程资料/面试资料/需求调研/NetScopeRepair-全面复核-20261106`）：

> **证据归档性质**：仓库 `.gitignore:15` 忽略 `docs/reports/evidence/`，下列副本是**本地归档**，不是已纳入 Git 的证据。
> 真实 smoke 数据与原始日志只留本机，不整目录强制入库；来源 SHA256、路径映射与复跑说明见
> [归档说明.md](evidence/G3-review-20261106/归档说明.md)。

- [全面复核报告（完整版）](evidence/G3-review-20261106/全面复核报告.md)；
  [机器可读缺陷与缺口清单（原件）](evidence/G3-review-20261106/findings.json)；
  [可解析证据路径的派生清单](evidence/G3-review-20261106/findings-localized.json)
  （`findings.json` 的 15 条原始 `evidence` 相对路径指向外部目录，派生清单新增 `evidence_archived` 映射到归档文件）。
- [M3 静态复核说明与独立审查限制](evidence/G3-review-20261106/M3-静态复核说明.md)。
- 隔离反例输出：[M1](evidence/G3-review-20261106/repro-m1-results.json) ·
  [M2](evidence/G3-review-20261106/repro-m2-results.json) ·
  [UI/验证器](evidence/G3-review-20261106/repro-ui-results.json)；
  结论文本：[M1 审查结论](evidence/G3-review-20261106/repro-m1-审查结论.md) ·
  [M2 审查结论](evidence/G3-review-20261106/repro-m2-审查结论.md)。
- 复跑证据：[测试计数](evidence/G3-review-20261106/test-summary.json) ·
  [只读 smoke](evidence/G3-review-20261106/desktop-smoke.json) ·
  [QA 验收矩阵](evidence/G3-review-20261106/QA验收矩阵.csv) ·
  [312 项源码摘要](evidence/G3-review-20261106/source-manifest.json)。
- 外部目录另有：三套复现工程（`repro-m1` / `repro-m2` / `repro-ui`，直接引用已构建 DLL、不重新构建原项目）、
  九页离屏渲染截图（`evidence/screens-verified/`）、构建与测试原始日志、真实 smoke 数据目录、导出 ZIP。
  复跑方式：先在源码目录构建，再以各复现目录为工作目录执行
  `dotnet run --project <该目录 csproj>`；**退出 0 表示成功观察到错误**，修复产品代码后重跑，
  对应输出应消失，不能继续把退出 0 当修复通过。完整步骤见 [归档说明 §5](evidence/G3-review-20261106/归档说明.md)。

**结论：G3 不通过。M1/M2 需补修后重审，M3 仍未完成且不得开放真实执行。**
