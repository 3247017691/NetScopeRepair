# T15：事务 journal 与精确恢复（真实执行能力仍关闭）

任务：T15（原 M3 第三阶段：事务 journal 与精确恢复；本轮不领取 T16）
日期：2026-11-05（Asia/Shanghai）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：设计包 `docs/05`（修复事务与权限边界、状态机、预算）、`docs/07` QA-027/028/029/030/036、`docs/08`/T15、`docs/09`；施工单 `docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T15 卡；`docs/reports/T13-report.md`、`docs/reports/T14-report.md`；当前 `src/NetScope.Repair`、`src/NetScope.Broker` 实际类型
工作树基线：含 T04-R2、T06-R3、T07-R2、T08-R2、T09-R2、T10、T13、T14 等并行未提交修改；`src/NetScope.Repair`、`src/NetScope.Broker`、`tests/WindowsIntegration` 在当前工作树为未跟踪目录。本任务只新增/修改这三个目录与 `docs/reports/T15-report.md`，未覆盖他人文件。

**结论：实现了权威 append-only 事务 journal、13 态状态机、写前日志顺序（完整 before + 授权摘要 → 逐步 intent → fresh GET → 最小写 → 读回 → applied → 观察 → commit/精确回滚）、坏尾/版本/序号/校验和检测、排他文件句柄、固定目录策略、崩溃后只读扫描与用户再次授权的恢复执行器，并把 journal/协调器接到 broker 的执行边界（默认关闭，只有开发态隔离的测试路径可运行）。生产真实执行在本交付中仍关闭**：没有验证过的平台字段 adapter、没有签名/ACL 的固定事务目录、没有可信提权部署，所以普通启动与交付默认 broker 都不会执行特权写；`BrokerTrustPolicy.DeliveryDefault` 与 T14 语义保持不变。**本轮没有运行 UAC、没有提权、没有创建服务/计划任务/启动项、没有真实网络/注册表/系统网络设置/真实客户端配置写入、没有真实修复；所有 273 个 WindowsIntegration 测试通过，其中本轮新增 59 个。**

## 1. 实现了什么（边界与可验证行为）

1. **严格状态名与迁移表**：`RepairTransactionState` 恰好为设计要求的 13 个状态：`planned / authorized / prepared / applying / observing / committed / rolling_back / rolled_back / rollback_conflict / rollback_failed / recovery_required / aborted / no_op`，无多余状态；`RepairTransactionStateRules` 固化迁移表、终态集合与 wire 名。`recovery_required` 在状态机上是终态，但仍阻塞新事务，直到用户确认恢复（journal 的未决查询把它算作未决）。
2. **自定义二进制 journal（非 NDJSON/SQLite）**：文件头 `NSRJTXN1` + 版本 `u16` + 保留 `u16` + `createdAt i64` + 16 字节 SHA-256 = 36 字节；记录 = magic `0x4E534A52` + 版本 + kind + 严格递增 sequence + payload 长度 + 严格 JSON payload + 16 字节 SHA-256（覆盖记录头与 payload）= 36 + payload。JSON 使用 `JsonUnmappedMemberHandling.Disallow`、snake_case、`MaxDepth=16`，未知字段/未知枚举即损坏。单条 payload 上限 256 KiB。
3. **打开时完整校验**：空设备先写头；头无效/截断/版本不支持 → `journal_version_unsupported` / `journal_corrupt`，journal 不可用；逐条读取，任一记录校验失败、截断或序号不连续 → `journal_corrupt` / `journal_sequence_gap`，记录为坏尾；坏尾存在时 **Append 拒绝**，必须显式 `TruncateCorruptTail()` 截到最后一条完整记录；截断只删不可读尾字节，不把坏 intent 修成看似合法的记录。磁盘写满/刷盘失败/锁不可用映射为 `journal_flush_failed` / `journal_locked`。
4. **排他句柄与固定目录策略**：`RepairFileJournalDevice` 以 `FileShare.None`、`FileOptions.WriteThrough` 打开，`Flush(true)` 落盘；第二个写者得到 `journal_lock_held`（真实临时文件测试验证）。`RepairJournalDirectoryPolicy.Design05` 的生产根固定为 `%ProgramData%\NetScopeRepair\Transactions`，要求存在、非重解析点、非用户可写、签名部署、已提权；用户可写/重解析点/非固定路径分别有独立拒绝 code。开发态隔离目录仅在显式请求、明确标记 `IsDevelopmentIsolation=true`、且路径等于显式开发根时允许，并始终带 `journal_capability_closed` 标注，**绝不作为生产边界**。
5. **写前日志顺序**：`RepairTransactionCoordinator` 依次持久化 planned → authorized → prepared（完整 before 值、计划摘要、授权摘要、目标身份、计划步骤、观察窗）→ 每步 `step_intent`（含 before/after）刷盘 → fresh GET 与授权 before 比较（含 boot 校验）→ 最小写 → 读回 → `step_applied` 刷盘 → observation_started → 观察 → committed。任何一步的 journal 写失败都停止后续特权动作；写入已经发生但 applied 未持久化时进入 `recovery_required`，绝不继续写。
6. **精确回滚**：只按已持久化 `step_applied` 逆序处理，且只在当前值仍等于本事务写入的 after 时恢复 before；当前值已是 before → 记录 `already_restored` 不写；当前值既非 before 也非 after → `rollback_conflict`，保留外部值、不覆盖；回滚写被拒绝/读回不符 → `rollback_failed`；回滚写结果未知 → `recovery_required`。只有 intent 没有 applied 的步骤永不重放 setter，即使当前值等于 after 也不自动回滚。
7. **预算与取消**：使用设计 05 的 10/30/180/60/60 秒预算；已用时间达到 120 秒后不再开始新的前向动作或观察，进入回滚；取消在首次写之前 → `aborted`，写之后 → 仍完成补偿回滚；补偿使用非取消 token，native 调用直接 await，**没有 `WaitAsync` 超时后遗留写入的路径**。技术观察不可用/失败/取消均回滚，不把“未观察到”当作成功。
8. **崩溃恢复**：`RepairJournalRecoveryScanner` 只读解释 journal：把“有 intent 无 applied 且没有解释性终态”标为 uncertain；把有 applied 未回滚的字段列为可恢复；已提交事务列为需要新授权才能撤销。`RepairRecoveryPlanner` 生成恢复计划与稳定摘要；`RepairRecoveryExecutor` **重新从 journal 推导计划并逐字段比较请求计划**（客户端构造的恢复范围会被拒绝），验证新的恢复授权（计划 id/摘要、用户、会话、有效期、逐动作同意、nonce）与 boot/目标指纹，之后只恢复“有 applied 记录且 current==after”的字段，冲突保留，intent-only 步骤只报告不写；恢复结果再写回 journal（`rollback_applied` / `rolled_back` / `rollback_conflict` / `rollback_failed` / `recovery_required`）。普通权限启动只输出未终结事务提示，不自动执行。
9. **Broker 接入（默认仍关闭）**：新增 `IBrokerTransactionHandler` seam；默认注册 `UnavailableBrokerTransactionHandler`，保持 T14 的 `accepted=true, executed=false, codes=[execution_not_implemented]`。`RepairBrokerTransactionHandler` 只有信任决定允许且固定目录策略通过时才打开 journal；journal 损坏/版本不支持/有未决事务/目录不合格都在任何写之前拒绝。`BrokerSession` 增加可选 handler 与 `Completed` 停止原因、`BrokerSessionResult.Execution`；`BrokerHost` 增加可选启动扫描（只报告未终结记录与恢复预览，从不执行）。T14 的协议常量、消息种类、预算、nonce、身份策略、默认信任闸门未改。
10. **注入 seam，不触真实系统**：native 读/写通过 `IRepairFieldAdapter`，观察通过 `IRepairObservationProbe`；生产默认 `UnavailableRepairFieldAdapter` / `UnavailableRepairObservationProbe` 永不写。测试只用 fake adapter（内存值 + 调用记录）与临时目录内的真实 journal 文件。

## 2. 实际修改/新增文件

新增（Repair，`net10.0`）：

- [RepairTransactionState.cs](../../src/NetScope.Repair/RepairTransactionState.cs)：13 态枚举、迁移表、终态、wire 名、事务预算常量、拒绝 code 与拒绝记录。
- [RepairJournalFormat.cs](../../src/NetScope.Repair/RepairJournalFormat.cs)：journal kind 闭集、扁平 typed 值/目标/步骤/条目、严格 JSON、头/记录编解码与校验和、`IRepairJournalDevice` 与 `RepairFileJournalDevice`。
- [RepairJournal.cs](../../src/NetScope.Repair/RepairJournal.cs)：打开/头校验/逐条读取/坏尾/序号/追加/截断/未决事务、目录事实与策略、路径 probe seam。
- [RepairTransactionCoordinator.cs](../../src/NetScope.Repair/RepairTransactionCoordinator.cs)：native/观察 seam、`RepairTransactionRequest/Result`、写前顺序、预算/取消、精确回滚、结果诚实映射。
- [RepairRecovery.cs](../../src/NetScope.Repair/RepairRecovery.cs)：只读扫描、恢复计划/摘要、恢复授权、恢复执行器（journal 权威重推导）。

新增（Broker，`net10.0-windows`，仅新增文件与对既有文件的加法式扩展）：

- [BrokerTransactionExecution.cs](../../src/NetScope.Broker/BrokerTransactionExecution.cs)：handler seam、默认拒绝 handler、journal-gated 真实 handler、启动恢复扫描与预览。

修改（Broker 既有文件，均为加法式，默认行为不变）：

- [BrokerProtocol.cs](../../src/NetScope.Broker/BrokerProtocol.cs)：新增拒绝 code `journal_unavailable / journal_corrupt / execution_write_failed / execution_observation_failed / execution_rollback_conflict / execution_rollback_failed / execution_recovery_required`。
- [BrokerAudit.cs](../../src/NetScope.Broker/BrokerAudit.cs)：新增审计事件种类 `transaction_executed / transaction_rolled_back / recovery_required / journal_refused`。
- [BrokerSession.cs](../../src/NetScope.Broker/BrokerSession.cs)：可选 `TransactionHandler`、`Completed` 停止原因、`Execution` 结果；无 handler 时保持 T14 行为。
- [BrokerHost.cs](../../src/NetScope.Broker/BrokerHost.cs)：可选 `RecoveryStartup` 与 `TransactionHandler`，启动扫描结果只读返回。

新增（WindowsIntegration 测试，全部 fake/临时目录）：

- [T15Fixtures.cs](../../tests/WindowsIntegration/T15Fixtures.cs)：带故障注入的 fake journal device（append 失败/crash/截断记录）、记录型 fake adapter（get/set、脚本化写结果、外部改值、boot 覆盖）、fake observation、计划/授权/恢复授权辅助。
- [T15TransactionCoordinatorTests.cs](../../tests/WindowsIntegration/T15TransactionCoordinatorTests.cs)：33 个用例（含 7 行 flush 切点 Theory）。
- [T15JournalAndRecoveryTests.cs](../../tests/WindowsIntegration/T15JournalAndRecoveryTests.cs)：18 个用例（格式/坏尾/版本/序号/真实文件锁与截断/目录策略/扫描/恢复执行）。
- [T15BrokerIntegrationTests.cs](../../tests/WindowsIntegration/T15BrokerIntegrationTests.cs)：8 个用例（默认拒绝、关闭信任、开发态 handler、session 端到端、损坏 journal、未决事务、启动扫描、观察失败回滚）。

未修改：`src/NetScope.Application/**`、`src/NetScope.Adapters/**`、`src/NetScope.Desktop/**`、`src/NetScope.Probes/**`、`src/NetScope.Storage/**`、`src/NetScope.Cli/**`、`src/NetScope.Domain/**`、`ObservationSession`、真实客户端配置与系统网络设置；未新增服务/计划任务/启动项；未运行 UAC。

## 3. 关键设计与偏差

- **journal 格式自定二进制而非 NDJSON/SQLite**：设计要求固定目录、排他句柄、长度/序号/版本/校验和与可截断坏尾；自定义头 + 定长记录头 + 严格 JSON payload + 16 字节 SHA-256 直接满足，不引入外部依赖。
- **坏尾不自动修**：检测到坏尾只报告并拒绝追加；截断必须由显式调用（恢复流程/维护）执行，避免把撕裂的 intent 静默修成“合法”。
- **恢复计划权威性**：`RepairRecoveryExecutor` 不只信任请求里的计划与摘要，而是重新扫描 journal 生成权威计划并逐字段比较；客户端构造/篡改的恢复范围返回 `journal_corrupt`。
- **协调器新增开发态开关**：`RepairTransactionRequest.DevelopmentIsolation` 仅用于测试/开发隔离；关闭时要求非 synthetic 计划、`ExecutionAllowed/CanAuthorize`、验证过的 adapter 与能力矩阵。生产没有注册真实 adapter。
- **Broker 接入是加法式**：无 handler 时完全保持 T14 行为（`execution_not_implemented`）；`Completed` 停止原因、`Execution` 字段、审计事件、拒绝 code 都是新增值。T14 的协议版本/消息闭集/预算/nonce/身份/默认信任闸门未变。
- **未新增 broker `recover_transaction` 消息**：恢复入口是本交付的 journal 驱动执行器（host 启动扫描 + 用户再次授权），客户端不能把恢复作为普通修复动作提交；T14 已对 `RecoverTransaction` 动作返回 `recover_requires_journal`，本轮不改变该封闭白名单语义。
- **Get/Set 非原子 CAS 已被显式承认**：协调器在写前 fresh GET 并在回滚前再读；两个读之间仍可能有外部写入，这属于设计 05 的非原子边界，通过 `external_value_changed` / `rollback_conflict` 保守处理，不声称原子性。
- **未实现服务/guard 自愈**：没有常驻服务或启动 guard；崩溃后只有普通权限启动扫描与提示，恢复必须用户确认新的恢复计划并再次授权。

## 4. 执行的命令、环境、退出码

环境：Windows、普通权限用户；.NET SDK `10.0.403`；未提权、未运行 UAC、未改系统/网络/注册表/服务/启动项。只构建 Repair、Broker 与 WindowsIntegration，未构建整个 `NetScopeRepair.slnx`。

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build src/NetScope.Repair/NetScope.Repair.csproj -v q` | 0 | 0 警告 / 0 错误 |
| `dotnet build src/NetScope.Broker/NetScope.Broker.csproj -v q` | 0 | 0 警告 / 0 错误 |
| `dotnet build tests/WindowsIntegration/NetScope.WindowsIntegration.Tests.csproj -v q` | 0 | 0 警告 / 0 错误 |
| `dotnet test tests/WindowsIntegration/... --filter "FullyQualifiedName~T15"` | 0 | **59 通过 / 0 失败 / 0 跳过**（33 coordinator + 18 journal/recovery + 8 broker） |
| `dotnet test tests/WindowsIntegration/...` | 0 | **273 通过 / 0 失败 / 0 跳过**（T13 既有 121 + T14 93 + 本轮 59） |

日志与 TRX 仅在本地 `%TEMP%\NetScopeRepair-T15\`（未入仓）：`t15-9.log` + `t15-9.trx`（T15 全绿）、`all-2.log` + `all-2.trx`（全量）、`t15-list.log`（按类计数）。计数均为本次 HEAD 工作树实际执行。

## 5. 测试数量与覆盖（QA-027/028/029/030/036）

本轮新增 **59** 个测试用例（Theory 展开后按 `--list-tests` 实测：`T15TransactionCoordinatorTests` 33、`T15JournalAndRecoveryTests` 18、`T15BrokerIntegrationTests` 8）。

- **QA-027 冲突/外部改值不覆盖**：外部值在写前改变 → `external_value_changed` 且不写；第二步外部改值 → 回滚第一步；回滚时读到第三个值 → `rollback_conflict` 且不覆盖；恢复时冲突同样保留；篡改恢复计划被拒绝。
- **QA-028 每个中断切点状态诚实**：`A_flush_failure_at_each_cut_point_is_reported_honestly` 对 planned/authorized/prepared/intent/applied/observation/commit 共 7 个刷盘切点逐一断言结果状态与是否已写，并重新打开 journal 扫描，验证 intent-without-applied 只被标 uncertain；另有坏尾、版本不支持、序号缺口、未知 JSON 字段、超长 payload、真实文件锁/截断测试。
- **QA-029 回滚精确性**：只回滚已 applied 且 current==after 的字段；current==before → `already_restored` 不写；回滚写被拒绝/读回不符 → `rollback_failed`；回滚读失败 → `rollback_failed`；回滚结果未知 → `recovery_required`；回滚按逆序执行。
- **QA-030 no-op/aborted 区分**：空 diff → `no_op` 且零写入；授权无效/计划改值/跨 boot/能力关闭/取消在写前 → `aborted` 且零写入；已写后取消/观察失败/观察不可用 → `rolled_back`。
- **QA-036 结果如实**：只有 `committed`/`no_op` 被 broker 映射为 `executed=true`；观察失败经 broker session 返回 `executed=false`、会话 Refused、journal `rolled_back`；未决事务阻塞下一笔；启动扫描只提示、零写入；恢复只在 journal 权威且用户再次授权后执行。
- **其他**：真实临时文件 journal 的排他锁与持久化、目录策略（用户可写/重解析点/未签名/非固定根）、恢复的跨 boot/目标重建/过期或错误用户或会话/无逐动作同意、committed 撤销需新授权、intent-only 恢复零 setter、broker 默认 `execution_not_implemented` 保持、损坏 journal 拒绝、开发态隔离端到端（journal 文件 + session + `Completed`）。

## 6. 能力关闭矩阵（本轮实际状态）

| 能力 | 状态 | 说明 |
|---|---|---|
| 真实特权修复执行 | **关闭** | 无验证过的平台字段 adapter；`UnavailableRepairFieldAdapter` 永不写 |
| 生产事务 journal 目录 | **关闭** | 无签名部署、无 ACL 保护目录、未提权；`Design05` 策略拒绝 |
| 开发态隔离 journal/执行 | 仅测试可用 | 显式 `AllowDevelopmentIsolation` + `DevelopmentIsolation=true`，结果标记 synthetic，不作为生产边界 |
| 技术观察 | **关闭（默认）** | `UnavailableRepairObservationProbe` 失败即回滚 |
| Broker 真实提权 | **关闭** | `BrokerTrustPolicy.DeliveryDefault` 未改；无 handler 时仍 `execution_not_implemented` |
| 启动自动恢复/自愈 | **关闭** | 只扫描提示；恢复需用户确认新计划并再次授权 |
| 跨 boot / 接口重建自动恢复 | **关闭** | 显式拒绝 `cross_boot` / `target_rebuilt` |

## 7. 只读/权限/隐私边界检查

- 未调用真实网络、未写注册表、未改系统网络设置、未写真实客户端配置、未安装/启动服务、未运行 UAC、未提权。
- 测试中的 journal 文件只写在测试自建临时目录（`%TEMP%\netscope-t15-*`），fake adapter 的值只在内存；未在仓库内产生证据目录。
- broker 审计仍只保留事件种类/稳定 code/事务 id/PID/动作数，不写 nonce、binding、计划载荷或路径；T15 没有新增日志文件。
- 恢复执行器拒绝客户端构造的恢复范围；`RecoverTransaction` 仍不能作为普通动作提交。

## 8. 已知限制与未验证项

- **没有真实平台实验**：未在 VM/真实 Windows 上验证三个字段的 native Get/Set/readback 语义；生产 adapter 不存在，因此真实修复、真实回滚、真实观察均未验证。不得声称 VM/平台实验通过。
- **崩溃是故障注入而非真实断电**：append 失败/“crash”由 fake device 注入，真实 NTFS 断电/缓存丢失未实测；`Flush(true)` 语义按 .NET 文档实现。
- **断连补偿**：session 在 submit 后执行 handler 时不监听对端断连（对端断连不会中途取消已开始的 handler）；设计要求断连停止新动作并补偿，本轮只保证单事务执行完/回滚，未实现“执行中断连即取消并补偿”的读线程。
- **回滚预算**：补偿使用非取消 token 且未用独立 60 秒计时器强制截止；设计 05 的补偿预留只在观察窗计算中体现，未对超长 native 调用做硬超时（native adapter 不存在，无法定义安全超时）。
- **目录/ACL 判定是保守近似**：重解析点只检查固定根本身；未做完整继承/拒绝优先 effective rights；生产路径要求签名部署但本交付没有签名部署可测。
- **journal 无压缩/轮转/归档**；未验证超大 journal 的启动扫描性能；未实现 UI 恢复向导（本任务只提供扫描/计划/执行 API 与 broker 启动提示）。
- **没有服务/guard**：崩溃后必须由用户再次启动并确认恢复；不承诺自愈。
- **broker 无 `recover_transaction` wire 消息**：恢复通过 host/API 层执行，客户端协议仍只有 T14 的封闭消息集。

## 9. 是否有真实网络操作

没有。所有网络/注册表/系统网络设置写入均为零；测试只用内存 fake 与临时目录文件。真实修复能力保持关闭。

## 10. 建议下一任务（不自动执行）

T16（按施工单，未领取、未开始）：在 T15 journal/恢复基础上接入真实的平台能力验证与端到端恢复 UI/服务化，但仍需先满足签名部署、ACL 目录、提权与平台实验门槛；本轮不启动 T16。
