# T06-R3：适配器事实接入真实诊断证据链

任务：T06-R3（原施工单定向补修；不重做 M0/M1，不领取 T07-R2）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
依赖：T09-R2（已复核的适配器事实）；T04-R2 的探测事实仍为并行未提交工作，本任务不依赖、不冒充
设计依据：原设计 03/04/07（QA-004/005/008/009/010/014/024/039）/09，施工单 T06-R3 卡

**结论：两个真实 Desktop 入口（本地采样、显式诊断）不再无条件写 `Records=[]`，也从强制空诊断改为运行本地规则。T09-R2 的进程/监听/磁盘白名单事实经新增的最小 `IObservationEvidenceSource`/`ObservationEvidenceBuilder` 投影为带状态、观察时间、boot、snapshot 引用、观察窗口作用的 `EvidenceRecord`，并由 Composition 注入同一证据源。外部证据缺失时规则只到 candidate/insufficient，没有伪造探测。存储对跨 boot、跨窗口、错 snapshot 引用和重复证据 ID 严格拒绝；旧包/无记录旧会话不补造事实。DX-005 因没有 bind-failure 生产者，只保留端口所有权事实与能力缺项，没有假造 supported。**

## 1. 实现了什么（用户可观察行为）

1. **本地采样产生证据与本地结论**：`ObservationSession.CollectOnceAsync` 每轮读取一次适配器事实，生成 `client_process`/`client_config_file`/`listening_ports`/`port_ownership` 记录；随后运行现有规则目录。规则需要外部探测时才用的分支保持 candidate/insufficient。真实 smoke 中 3 轮共产生 26 条记录、每轮 2 条候选结论（DX-001 配置层线索、DX-006 磁盘文件早于进程启动候选）。
2. **手动诊断复用同一证据源**：`RunDiagnosisAsync` 在同一观察窗口内先读取适配器事实，再运行用户显式启动的三路径探测；记录与探测共享 boot、窗口 id、观察时间，规则可对监听样本与本地代理连接做同窗口比较。
3. **一次采样只读一次适配器**：`GenericAdapter` 新增 `ReadObservationAsync` 返回同一次读取的原始源结果与派生客户端行；证据投影不再二次读取（客户端页仍是一次独立的只读展示读取，测试证明页面结果不会被拼进另一次采样的证据）。
4. **记录带显式作用域**：每条记录带 `snapshot_id` 事实、`ObservedAt`、`MonotonicMs`、`BootId`、`ObservationWindowId`，并保留源/转换版本与最小事实；写入时事件 `refs.snapshot_id` 明确指向本轮快照。
5. **严格作用域校验**：`EvidenceSessionEvents.Create` 拒绝跨 boot、跨窗口（与本次写入窗口或本轮探测窗口不一致）、指向其他/不存在快照、重复证据 ID 的记录；`EvidenceEventCodec` 拒绝“引用快照”和“记录自身 snapshot_id 事实”互相矛盾的包。旧会话（无 typed record）继续可读，记录集保持为空，不补造。
6. **规则失败与采集缺口可追踪**：规则执行失败、适配器数据域（进程/监听）读取失败/部分失败写成无类型 `gap` 事件（不冒充诊断、不引用证据），可回放、可导出；旧解码器按既有规则跳过，不会把不支持内容当正常。
7. **磁盘配置诚实标注**：磁盘白名单只生成 `client_config_file`（provenance `disk_only`/`process_start_reference`）；运行态 API 证据未接入时不降级成磁盘记录，而是产生 `Unsupported` 缺口。DX-006 因此只能到 candidate，不能伪造“运行态与磁盘一致/不一致”。
8. **DX-005 边界**：真实生产者只提供 `port_ownership`（监听所有者）与监听样本；没有 `port_bind_failure`，也不从 `mixed_port` 推断绑定地址生成 `client_configuration`，所以不会出现 supported 冲突结论，能力缺项写入本报告矩阵。

## 2. 契约与显式映射（rule → record kind → 真实 producer → 未支持项）

| 规则 | 规则实际读取的 Facts/记录 | 本轮真实生产者 | 本轮可达结论 | 未支持/缺证 |
|---|---|---|---|---|
| DX-001 | 无 record；`Probes(ExplicitHttpProxy)`、`Snapshot.Proxies` | 探测：仅用户显式 `RunDiagnosisAsync`；代理层：Windows collector | 本地仅配置层 → candidate（`probe_on_explicit_proxy_path`/`application_effective_proxy`）；显式代理路径失败 → supported（既有规则） | 外部探测必须用户启用；不伪造 probe |
| DX-002 | `client_runtime_config`、`fake_ip_adapter`；`Probes.ResolvedAddresses`；`Routes`/`Dns` | 仅快照 routes/dns（既有 collector） | 保留段路由/DNS → candidate（`effective_fake_ip_configuration`） | 运行态 API 与 fake-IP 适配器生产者未接入；磁盘 `dns_fake_ip_range` 不冒充运行态 |
| DX-005 | `port_bind_failure`、`port_ownership`、`client_configuration` | `port_ownership`（T09-R2 监听所有者事实） | 只保留所有权事实，不产生 DX-005 结论 | `port_bind_failure` 无生产者；白名单只有 `mixed_port`、无 bind 地址，故 `client_configuration` 不生成 |
| DX-006 | `client_config_file`、`client_runtime_config`、`client_process` | 磁盘白名单（Nice/Apex 解析器）+ T09-R2 进程观测 | 文件 mtime 早于进程启动 → candidate（`runtime_api_configuration`） | 无运行态 API，故 supported/contradicted 不可达 |
| DX-008 | 仅 `Probes` | 用户显式探测组 | 本地采样不产生；多组可比样本留给 T10 | 本任务不伪造单组三路径间歇 |
| DX-010 | `Snapshot.Interfaces` + `ProposedChanges` | 无 ProposedChanges 生产者（T13+） | 静默 | 修复计划未接入 |
| DX-011 | `listening_ports`、`Probes` | T09-R2 监听采样 + 用户显式本地代理探测 | 空/不完整监听样本 + 同窗本地代理连接成功 → insufficient（`repeated_listening_sample`） | 需要用户显式探测；无 probe 时不出结论 |
| DX-003/004/007/009/012 | 未在 M0 规则目录 | — | 未实现（原计划） | 不在本任务范围 |

记录种类由 `ObservationEvidenceBuilder` 固定为：`client_process`、`client_config_file`、`listening_ports`、`port_ownership`。**不产生**：`port_bind_failure`、`client_runtime_config`、`client_configuration`、`fake_ip_adapter`（没有生产者就不写入）。DX-006/011 的 Facts 键即规则实际读取的键：`client`、`started_at`、`config_path`、`config_hash`、`mtime`、`count`、`sample_state`、`endpoint`、`owner_process`、`address_family` 等；白名单配置值只作文件事实随记录保存。

## 3. 实际修改文件

新增（本任务）：
- `src/NetScope.Application/ObservationEvidence.cs`：`IObservationEvidenceSource`、`ObservationEvidenceContext`、`ObservationEvidenceResult`、`ObservationEvidenceGap`。
- `src/NetScope.Adapters/ObservationEvidence.cs`：`AdapterObservationRead`、`ObservationEvidenceBuilder`、`AdapterObservationEvidenceSource`。

修改（本任务）：
- `src/NetScope.Adapters/GenericAdapter.cs`：抽出 `ReadObservationAsync` 单次读取，`ReadAsync` 复用其 ClientStatus；未改变归属/启发式语义。
- `src/NetScope.Adapters/AdaptersHost.cs`：`AdapterComposition` 增加同一 `IObservationEvidenceSource`（`AdapterObservationEvidenceSource`）。
- `src/NetScope.Desktop/Services/ObservationSession.cs`：注入证据源；两个入口读取证据并运行规则；记录收集失败为 gap；`SessionCycle` 增加 `ObservationWindowId`/`EvidenceGaps`；持久化失败规则与采集缺口。
- `src/NetScope.Desktop/Services/Composition.cs`：非合成模式构造一次适配器组合，把同一证据源注入会话，客户端页继续复用该组合的 ClientStatus 端口。
- `src/NetScope.Storage/EvidenceSessionEvents.cs`：记录写入显式 snapshot 引用与窗口；拒绝跨 boot/窗口/错引用记录；写入规则失败与采集缺口事件。
- `src/NetScope.Storage/EvidenceEventCodec.cs`：`RuleFailure`/`ObservationSourceGap` 无类型 payload；记录 refs 与 `snapshot_id` 事实一致性严格校验。

测试：
- 新增 `tests/Desktop/T06R3PreFixReproducerTests.cs`、`T06R3EvidenceTestSupport.cs`、`T06R3EvidenceIntegrationTests.cs`。
- 新增 `tests/Replay/T06R3PreFixReproducerTests.cs`、`T06R3EvidenceScopeTests.cs`。
- `tests/Desktop/NetScope.Desktop.Tests.csproj`：增加对 `NetScope.Adapters` 的直接项目引用（测试用 fake seams）。

未修改：Probes、Storage Export/Anonymizer、Repair/Broker、系统网络设置、真实客户端配置、任何 XAML/Theme/Logo/Tray/启动入口；未新增项目或独立采样系统。

## 4. 修前失败证据（旧实现）

先写测试，在旧实现/等价旧行为上运行：

| 反例 | 旧行为 | 结果 |
|---|---|---|
| “本地采样即使有可见代理配置，也不运行本地规则” | `Diagnoses=[]`，DX-001 candidate 不产生 | **0 通过 / 2 失败，退出码 1**；`logs/prefix-desktop.log` |
| “另一观察窗口的记录被写入同一快照作用域” | `Create` 接受（窗口校验停用等价旧行为） | **1 失败 / 1 通过，退出码 1**；`logs/prefix-replay-both.log` |
| “记录引用与 snapshot_id 事实矛盾但被静默采信” | 解码器只信 refs（错引用校验停用等价旧行为） | **1 失败 / 1 通过，退出码 1**；`logs/prefix-replay-fact-mismatch.log` |

说明：第二/三条的“等价旧行为”是在最终树上仅临时停用本任务新增的那一项校验后测得，其余代码不变；运行后已还原，最终 T06-R3 测试全绿（`logs/postfix-*.log`）。桌面第一条测试的另一个修前失败项（手动诊断 Records 为空）在旧 API 下断言失败，见 `prefix-desktop.log`。

## 5. 修后验收与命令

环境：.NET SDK `10.0.403`，Windows 普通权限；无提权、无外部探测、未启动/停止任何客户端。

| 命令 | 结果 |
|---|---|
| `dotnet build src/NetScope.Application`、`Adapters`、`Storage`、`Diagnostics`、`Desktop`、`Cli` 六个工程 | 全部退出码 0，0 警告 / 0 错误（日志 `logs/build-*.log`） |
| `dotnet test tests/Adapters` | 99 通过 / 0 失败 / 0 跳过，退出码 0 |
| `dotnet test tests/Desktop` | **50 通过 / 0 失败 / 0 跳过**，退出码 0（基线 35 + 本任务 15） |
| `dotnet test tests/Replay` | **70 通过 / 0 失败 / 0 跳过**，退出码 0（基线 58 + 本任务 10 + 并行任务 2） |
| `dotnet test tests/Unit` | 269 通过 / 0 失败 / 0 跳过，退出码 0（含并行 T04-R2 新增） |
| `dotnet test tests/Contract` | 80 通过 / 0 失败 / 0 跳过，退出码 0 |
| `dotnet test tests/Windows` | 79 通过 / 0 失败 / 0 跳过，退出码 0 |
| `dotnet test tests/Export` | 51 通过 / 0 失败 / 0 跳过，退出码 0（含并行 T08-R2 新增） |
| **相关工程合计** | **698 通过 / 0 失败 / 0 跳过**，7 个测试工程退出码全为 0 |

TRX 在 `test-results-final/`；最终日志在 `logs/final-*.log`。计数是共享工作树在本轮最终状态的实际执行数，不复制历史 637。按任务要求未做全解决方案并发构建；六个受影响工程逐一构建。

### 本任务新增 25 个测试

- Desktop（15）：旧入口回归 2；fake 适配事实→EvidenceSet→DX-006 candidate→导出→回放闭环；两个入口各自窗口；Composition 注入；磁盘≠运行态；DX-001 候选（无 probe、probe runner 0 次）；DX-002 候选与无反例；DX-005 无 bind-failure；DX-011 insufficient + 正常反例；DX-006 文件较新反例；页面刷新不拼证据；多快照作用域；运行时 API 证据不降级为磁盘记录；证据源失败为持久化 gap。
- Replay（10）：跨 boot/跨窗口/缺窗口/跨快照/重复 ID 拒绝；规则失败与采集缺口落盘；旧快照会话不补造；记录作用域经回放保持；两条旧行为回归（窗口、错引用）。

### QA 覆盖

| QA | 覆盖测试 |
|---|---|
| QA-004 fake-IP 不是故障 | `DX002_reserved_range_is_only_a_candidate_without_probes_or_runtime_context`（含无反例） |
| QA-005 多客户端不自动判冲突 | `DX005_has_no_bind_failure_producer_so_no_conflict_is_invented`（两个客户端同配置端口） |
| QA-008 环境代理不擅自清理 | `DX001_stays_a_candidate_when_only_local_configuration_is_visible`（仅配置层 candidate，无 probe） |
| QA-009 配置归属/来源 | `Disk_whitelist_is_projected_as_client_config_file_and_never_as_runtime_config`、`A_runtime_api_evidence_item_is_not_downgraded_to_a_disk_config_record` |
| QA-010 监听空结果不判端口关闭 | `DX011_empty_listener_sample_with_a_successful_local_proxy_connection_is_insufficient`（含正常反例） |
| QA-014 证据关联/损坏包 | `T06R3EvidenceScopeTests`（跨 boot/窗口/错引用/重复 ID、旧会话不补造）、`Records_keep_their_explicit_scope_across_replay` |
| QA-024 回放确定性 | `Fake_adapter_facts_reach_the_evidence_set_and_a_real_rule_consumes_them`（记录→规则→导出→回放再算一致）、`Two_samples_keep_each_record_in_its_own_snapshot_scope` |
| QA-039 多客户端展示不判冲突 | `DX005_has_no_bind_failure_producer_so_no_conflict_is_invented`、`Composition_injects_one_adapter_evidence_source_for_the_application` |

## 6. 行为证据

- 真实只读 smoke（真实 collector + 真实适配器，普通权限）：退出码 **0**，`desktop-smoke-final.json`：`persisted=true`、3 个快照、38 条事件、`export_preview_can_export=true`、导出包成功、会话回放无证据一致性问题。该会话中记录种类计数为 `client_process=6`、`client_config_file=6`、`listening_ports=6`、`port_ownership=8`（3 轮共 26 条记录）；每轮 2 条候选结论（DX-001 配置层线索、DX-006 磁盘文件早于进程启动）；监听所有者部分不可读按 `gap` 事件保留。
- CLI 合成演示：退出码 **0**，11 条事件，`problems=0 evidence_problems=0`，离线重算诊断一致（`logs/cli-demo-final.log`）。
- 证据目录：`docs/reports/evidence/T06-R3/`（本机被 `.gitignore` 忽略，未上传）。

## 7. 兼容策略（建议由复核提升为 ADR-0005）

- 新 facts 仍放在既有 `m0-evidence-1` 的 `evidence_record` payload 内，未改封套 schema、未新增 typed payload 版本；旧日志与旧会话继续读，旧解码器对新增 `gap` 事件按无类型事件跳过。
- 新增 `gap` 事件（规则失败/数据域缺口）故意不带 `payload_version`，避免旧解码器把它当未知 typed payload 报错；其 payload 只含规则 id/版本、错误类型、来源与原因，不含命令、路径或堆栈。
- 新 facts 键采用稳定小写下划线名，并以规则实际读取为准；新增规则/记录种类必须先有生产者与“未支持”声明，再进目录。
- 严格校验是单向收紧：混合作用域的旧包现在被报告为不一致，而不是被静默归入某个快照；不补造、不重写原始事实。
- ADR 文件未创建（本任务写入范围只到报告）；本节即待升格的兼容策略摘要。

## 8. 只读/权限/隐私边界检查

- 证据源只调用现有 T09-R2 只读 seam：进程列表、TCP 监听、磁盘配置白名单；无 connect/DNS/HTTP、无配置写入、无提权、无启动/停止客户端、不改网络设置。
- 记录最小化：不保存可执行文件完整路径（只保留路径读取原因码）、不保存订阅/凭据/配置正文；保存的配置值仅为已解析白名单字段。`config_path` 属设计 04 的本地 D2 事实，导出时由既有路径脱敏替换（本轮导出→回放已实际通过）。
- 未修改任何 UI 文件；工作树中 `ClientsView/ExportView/Rows` 等改动来自并行任务，不属于本任务。
- 未新增提交、未推送、未上传；真实 smoke 只写本机临时/证据目录。

## 9. 已知限制与未实现项

1. **运行态配置生产者缺失**：默认组合未注入 Apex runtime API seam，故 `client_runtime_config`/fake-IP 记录不产生；DX-002 supported、DX-006 supported/contradicted 仍不可达。运行态证据被显式标为 `Unsupported` 缺口，绝不降级成磁盘记录。
2. **DX-005 仍无 bind-failure 生产者**：只有 `port_ownership`；`client_configuration` 因白名单没有绑定地址而不生成，故不产生 supported 冲突。该能力缺项需在有实际 bind 失败来源的任务中补。
3. **本地采样现在会展示候选结论**：Home/诊断页对所有自动候选仍使用“手动诊断/发现异常”等旧文案，且每轮候选会刷新诊断页。文案与聚合属于 T07-R2（本任务禁止改页面/VM 视觉），本轮未改 UI。
4. **观察时间归一化**：适配器记录统一以本轮快照的 `ObservedAt/MonotonicMs` 作为观察时间。若一次适配器读取耗时超过 2 秒，DX-011 的“同时段”判定会保守失败（不产出结论），而不是放宽时间窗。
5. **IPv6 所有者仍不支持**（T09-R2 既有边界）：IPv6 监听只计入采样与缺口，不产生 `port_ownership`。
6. **未读所有者只体现在采样/缺口**：`port_ownership` 只对已读所有者生成；不可读所有者不猜测。
7. **证据量增加**：每轮约 8–9 条记录（真实 smoke 3 轮 38 条事件，其中记录 26 条），配额/轮转属 T11；100MB/日本就是调优目标而非硬阈值。
8. **合成组合不接证据源**：`Composition.CreateDefault(synthetic: true)` 保持演示路径（SyntheticShowcase），合成会话本地采样记录为空；真实非合成组合已注入。演示数据仍由原流程生成，未在本轮伪造适配证据。
9. **客户端页与样本是两次独立只读**：不共享页面结果（测试证明不会拼接）；页面展示时间可能早于/晚于样本时间，页面文案的“时间差”说明留给 T07-R2。
10. **未执行**：DX-003/004/007/009/012 规则、T10 故障窗、T11 配额、T13+ 修复生产者；全解决方案并发构建（按任务约束避免）。

## 10. 是否有真实网络操作

无。没有外部请求、探测、DNS 查询、客户端启动/停止、系统网络设置或真实客户端配置写入。真实只读 smoke 只读取本机进程/监听/网卡/代理配置（既有 collector 行为）并写本地证据文件。

## 11. 建议下一任务（不自动执行）

按施工顺序建议复核后领取 **T07-R2**（在新 UI 上显示“发生什么、还缺什么”，并处理本报告第 9.3 条自动候选文案/聚合问题）；本任务完成即停，未领取 T07-R2/其他共享任务，未提交或推送。
