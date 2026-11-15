# T11：配额与崩溃恢复（M2）

任务：T11（M2 配额、轮转、坏尾与索引恢复；基于 T10 已完成的观察会话/incident 标记）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，构建时 HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
依赖：T10 的 `IncidentRecorder`/`IncidentSessionEvents` 已存在；T12 正在并行修改 Storage 的 CaseReplay/ReplayService，本任务未改其文件
设计依据：原设计 04 §4（存储、配额与故障捕获）、07 QA-016/017/018/035、08/T11、09；施工单 T11 卡

**结论：新增 Storage 最小服务 `RetentionPolicy`、`QuotaManager`、`SessionRecovery`（配套 `StorageLayout`、`StorageFileSystem` 文件 seam、共享 `EventLogParser`、`SessionLogFacts`）。已实现 7 日/500 MB 普通日志、1 GB 独立钉住池、80/90/100% 分级指令、100 MB/日仅告警；过期未钉住成功明细优先清理，失败明细先写聚合计数再清理；活动案例先封存并保护；钉住案例、未决 journal、聚合/隔离 sidecar 不被普通清理删除；末行截断隔离并记录一个显式 gap 事件，中间损坏不重写且其后有效行仍可读；SQLite 从 NDJSON 去重重建并写游标/原因/崩溃态；不可写时明确列出未持久化范围。T11 新增 34 个测试全部通过，相关四个套件（Unit 320 / Contract 88 / Replay 94 / Export 52）0 失败 0 跳过。真实磁盘未填、系统时钟未改、未发网络请求。**

## 1. 实现了什么（服务行为）

1. **普通日志保留 7 日、总量 500 MB**：`RetentionPolicy.Default` 固化原设计默认值；运行期可按策略覆盖但需通过 `Validate()`。超期（按文件最后写入时间）且未钉住的普通明细会被清理；阈值触发时先清理成功明细，再按最旧顺序清理。
2. **钉住案例独立 1 GB 上限**：`Evaluation` 分开计算普通池与钉住池；达到上限时 `EvaluatePinRequest` **拒绝新增钉住**并给出原因，从不解钉旧案例；`RemovePinned` 是唯一显式用户清理入口。
3. **80/90/100% 分级**：`QuotaLevel` 按普通池与钉住池中较高者取级；`RetentionDirective` 输出生产者可用的降级指令：Normal/Warning 保留原始快照明细；≥90% 停止冗余原始快照并把采样间隔乘 2；≥100% 停可选明细、只保留最小索引/计数并把间隔乘 4。分级只产生指令与警告，不悄悄改写 90% 阈值。
4. **100 MB/日只告警**：`DailyBytes` 按当前 UTC 日的文件最后写入量统计；超预算只产生 `WarnDailyBudget` 动作和警告，绝不单独触发删除。测试显式验证“日预算超限但配额等级仍为 Normal、文件不被删除”。
5. **过期未钉住成功明细优先**：计划按“成功明细（`FailureBearing=false`）→ 失败/缺口明细”排序，同级按最旧优先；每步都有可审计的 `QuotaAction`（种类/路径/字节/原因/输出 sidecar）。
6. **失败明细先聚合再清理**：失败/缺口/损坏的过期明细删除前，先写 `<session>.ndjson.aggregate.json`，包含事件数、失败数、按状态失败计数、partial 数、gap 事件数、序列缺口数、损坏行数、首末失败时刻、原因与“不是网络健康证明”的说明；聚合写失败则保留明细不删。
7. **活动案例先封存并保护**：达到 ≥80% 或清理压力时，对带活动 `incident_marker`（observing/suspected/incident_open/recovering）的会话写 `<session>.ndjson.sealed.json`（`kind=sealed_incident`，保留失败总数/首末时刻/gap/活动 incident id 语义）；活动案例正文永不进入删除动作，配额仍超限时在 `BlockedReasons` 明确“受保护，需用户处理”。
8. **未决 journal 与 sidecar 保护**：`journal/` 下文件视为未决（`PendingJournal=true`），不计入普通池且永不自动删除，观察列表会提示“先处理恢复再清理 journal”；聚合/封存/隔离/索引游标 sidecar 标记为 `Protected`，不参与普通清理（体积很小）。
9. **末行截断 → 隔离 + 显式 gap**：`SessionRecovery.QuarantineTruncatedTail` 先把原始尾部字节写入 `.tail-<UTC>.quarantine`，再只裁掉不完整的最后一行，最后追加一个 `event_type=gap` 事件（`kind=truncated_tail_quarantined`，`sequence_unknown=true`，不编造丢失的序号）。任一步失败都返回部分结果：隔离写失败则原日志不动；裁剪失败则有隔离副本但日志保留；gap 追加失败则明确“gap 未持久化、不得声称已记录”。
10. **中间损坏不伪造有效前缀**：严格读取器报告损坏行后继续读取**其后**的有效行；恢复服务不会把中间损坏之后的内容截掉，也不会把“损坏行之前”当作完整日志。`IsRecoverableTruncatedTail` 仅在“只有末行截断、无中间损坏”时为 true；否则拒绝自动修改并提示导出/人工保留。
11. **SQLite 从 NDJSON 去重重建**：`RebuildIndex` 以 NDJSON 为唯一真源，`INSERT OR IGNORE` 保留首次出现的 event id；写 `<db>.cursor.json`（status/reason/source bytes/lines/events/invalid lines/duplicates/sequence gaps/last sequence/last observed at）与 `<db>.state.json`（`rebuilding`→`ready`）。崩溃在提交点会留下 `rebuilding`，`ReadIndexState().NeedsRebuild=true`，再次重建幂等；编码非法时拒绝重建且不写投影。
12. **磁盘满/不可写时明确范围**：`QuotaManager.CheckPersistence` 分目录探测 sessions/index/exports/logs/pinned，`SessionRecovery.CheckPersistence` 可带尝试的序号区间；返回 `UnpersistedRange[]`（scope、路径、序号区间、原因、失败计数是否保留）和明确文案：会话日志不可写时“本周期事件、失败计数与 gap 标记未持久化，不得声称已保留或无损”。
13. **追加失败后可补记显式 gap**：`SessionRecovery.RecordGap` 在存储恢复可写后，把一次被拒绝的追加范围记录成 `kind=append_failure` 的 gap 事件（含尝试的首末序号/数量），不伪造原事件；仍不可写时返回 `Recorded=false` 与未持久化项。
14. **时钟跳变**：配额年龄按墙钟但**回拨时钳制为 0**，且整体标记 `ClockRollbackSuspected` 并告警，绝不因回拨删除新数据；恢复的 gap 事件使用单调钟且 `MonotonicMs = max(last, 0)`，不会产生负耗时，报告与 payload 标记 `clock_rollback_suspected`。

## 2. 实际修改文件

Storage 新增：

- `src/NetScope.Storage/RetentionPolicy.cs`：默认值、阈值校验、80/90/100 分级、分池评估、降级指令、日预算/时钟回拨警告。
- `src/NetScope.Storage/StorageLayout.cs`：数据根目录布局（sessions/exports/logs/pinned/journal）与会话、索引、聚合、封存、隔离文件命名。
- `src/NetScope.Storage/StorageFileSystem.cs`：`IStorageFileSystem` 文件访问 seam + 真实实现（含只读/写入探测与 flush）。
- `src/NetScope.Storage/EventLogParser.cs`：从 `NdjsonEventLog` 抽出的共享严格行解析器（行级结果/坏尾标记），供恢复与索引重建复用。
- `src/NetScope.Storage/SessionLogFacts.cs`：从已记录事件提取失败计数、首末失败时刻、gap/序列缺口、损坏行、活动 incident id，判断“成功明细/证据明细”。
- `src/NetScope.Storage/SessionRecovery.cs`：`Inspect`、`Read`、`QuarantineTruncatedTail`、`RecordGap`、`RebuildIndex`、`ReadIndexState`、`CheckPersistence`。
- `src/NetScope.Storage/QuotaManager.cs`：`Inspect`、`Plan`、`Enforce`、`EvaluatePinRequest`、`RemovePinned`、`CheckPersistence`，以及 inspection/plan/action/结果/持久化状态等公开记录。

Storage 修改（仅解析复用，行为不变）：

- `src/NetScope.Storage/NdjsonEventLog.cs`：`Parse` 委托给共享 `EventLogParser`；原有 175 行解析逻辑语义、错误文案、坏尾判定与全部既有测试保持一致。

测试新增：

- `tests/Unit/T11TestSupport.cs`：内存文件 seam（可声明 500 MB/1 GB 稀疏大小、模拟满盘/删除/移动/写探测失败、崩溃切点）、假时钟、合成事件/日志构造器、真实临时目录。
- `tests/Unit/T11RetentionPolicyTests.cs`（6）、`T11QuotaManagerTests.cs`（12）、`T11SessionRecoveryTests.cs`（12）。
- `tests/Replay/T11RecoveryReplayTests.cs`（2）。
- `tests/Export/T11QuotaExportTests.cs`（1）。
- `tests/Contract/T11QuotaContractTests.cs`（1）。

未修改：`CaseReplayService.cs`/`ReplayService.cs`（T12 并行改动）、T08 Export 语义与文件（`PackageAnonymizer` 等本轮未再动）、Probes、Adapters、Desktop/UI、ObservationSession、Repair/Broker、真实客户端配置、系统网络。工作树中这些路径的未提交改动属于其他队友。

## 3. QA 覆盖

| QA | 覆盖方式 | 结果 |
|---|---|---|
| QA-016 崩溃恢复 | 末行截断隔离+保留有效前缀+显式 gap；中间损坏不重写且其后行仍读；隔离/裁剪/gap 三个 crash 切点的部分结果；索引 `rebuilding` 崩溃态与幂等重建 | 通过（Unit/Replay） |
| QA-017 时钟跳变 | 配额年龄回拨钳制且不删除、标记 rollback 警告；恢复报告标记时间不确定；gap 事件单调值非负且不小于前事件 | 通过（Unit）；睡眠/跨 boot 细节沿用 T10 结论 |
| QA-018 磁盘满 | 文件 seam 模拟追加失败（已有事件仍可读、抛 IOException 不静默）；失败后 `RecordGap` 补缺口；配额删除失败保留文件并 Degraded；不可写时明确未持久化范围 | 通过（Unit）；未真实填盘 |
| QA-035 配额降级 | 7 日/500 MB/1 GB 默认值；80/90/100 等级与指令；日预算仅告警；失败计数先聚合不因清理被抹除；钉住拒绝而非解钉；journal/sidecar/导出保护；活动案例封存 | 通过（Unit/Contract/Export） |

## 4. 执行的命令、环境、退出码

环境：Windows 普通权限；.NET SDK **10.0.403**（[dotnet-version.txt](evidence/T11/logs/dotnet-version.txt)）；构建时 HEAD `a4b07a0…`（[git-head.txt](evidence/T11/logs/git-head.txt)）。未提权、未填盘、未改系统时钟、未联网。

| 命令 | 结果 | 证据 |
|---|---|---|
| `dotnet build src/NetScope.Storage/NetScope.Storage.csproj` | 退出码 0，0 警告 / 0 错误 | [build-storage.log](evidence/T11/logs/build-storage.log) |
| `dotnet test tests/Unit` | 退出码 0，**320 通过 / 0 失败 / 0 跳过** | [test-unit-final.log](evidence/T11/logs/test-unit-final.log)、[unit-final.trx](evidence/T11/test-results/unit-final.trx) |
| `dotnet test tests/Contract` | 退出码 0，**88 通过 / 0 失败 / 0 跳过** | [test-contract-final.log](evidence/T11/logs/test-contract-final.log)、[contract-final.trx](evidence/T11/test-results/contract-final.trx) |
| `dotnet test tests/Replay` | 退出码 0，**94 通过 / 0 失败 / 0 跳过** | [test-replay-final.log](evidence/T11/logs/test-replay-final.log)、[replay-final.trx](evidence/T11/test-results/replay-final.trx) |
| `dotnet test tests/Export` | 退出码 0，**52 通过 / 0 失败 / 0 跳过** | [test-export-final.log](evidence/T11/logs/test-export-final.log)、[export-final.trx](evidence/T11/test-results/export-final.trx) |

说明：按要求只构建 Storage 与相关测试工程，未做全解决方案并发构建。当前工作树含 T12（CaseReplay/ReplayService）和其他队友的未提交改动；上述计数是本工作树实际结果，不是历史计数。T11 新增测试共 **34 个**（Unit 30、Contract 1、Replay 2、Export 1）。

## 5. T11 新增测试清单（行为证据）

配额与轮转（12）：过期成功明细先于失败明细删除、失败明细聚合计数保留、活动案例封存且不删、钉住独立池/拒绝新钉/显式清理、未决 journal 排除、导出默认不删/显式可纳入、时钟回拨不删除、删除失败保留并 Degraded、超硬上限追加清理且保留保护项、受保护 sidecar 不清理、不可写范围表达、日预算仅告警。

恢复（12）：末行截断隔离/裁剪/gap 三步与产物、中间损坏不重写且后行有效、隔离写失败不改日志、裁剪失败保留原文、gap 追加失败明确未持久化、索引重建去重/游标/幂等、非法编码拒绝、索引崩溃态、会话持久化范围、磁盘满追加失败保留旧事件、时钟回拨不确定性、可写恢复后补记 append_failure gap。

回放（2）：恢复后的日志回放干净且只多一个显式 gap；中间损坏回放报问题但保留其后事件、不产生“有效前缀”假象。

契约（1）：默认值与聚合/封存 sidecar 文档字段稳定，`session` 只含文件名、无完整本地路径，失败计数/首末时刻存在，不宣称网络健康。

导出（1）：普通日志清理不删除已导出 ZIP，清理后包字节不变且 `checksums.sha256` 全部复算通过（T08 导出语义不被 T11 破坏）。

## 6. 只读/权限/隐私边界检查

- 新服务只做本地文件枚举/读取/写入/移动/删除，不读注册表/系统代理/真实客户端配置，不发起网络请求，不写系统网络设置，不提权。
- 配额测试用内存文件 seam 声明 500 MB/1 GB 大小与模拟故障，**未在真实磁盘创建大文件、未填满磁盘**；时钟测试用假 `IClock`，**未修改系统时钟**。
- 聚合/封存 sidecar 只含文件名、计数、首末失败时间和原因，不含事件正文、路径全文、凭据或配置内容；`session` 字段只写文件名。
- 未新增后台服务、启动项、计划任务或提权入口；未上传/提交/推送；证据仅写本机被 `.gitignore` 忽略的 `docs/reports/evidence/T11/`。

## 7. 已知限制与未实现项

1. **未接线到产品会话/UI**：写入范围禁止修改 Desktop/ObservationSession/UI，因此 `QuotaManager`/`SessionRecovery` 目前是可用服务与测试资产，真实应用尚未在会话开始/退出时调用 `Enforce`/`CheckPersistence`，也没有把未持久化范围显示到界面。后续接线任务必须在会话生命周期调用并展示 `StoragePersistenceStatus`，不能声称“已自动轮转”。
2. **钉住写入/布局未产品化**：假设钉住文件平铺在 `<root>/pinned/`；没有 pin 创建 API/UI，`EvaluatePinRequest`/`RemovePinned` 供后续 T15/T16 接入。
3. **日写入量是近似值**：按当前 UTC 日文件的最后写入时间与大小求和，不是逐字节计数器；被同一天反复重写的文件可能低估。
4. **Inspect 成本线性**：每次配额检查会读取并解析每个会话 NDJSON 以取得失败/活动事实；500 MB 级存储可能明显耗时，未做缓存或增量索引（可后续优化，不改变语义）。
5. **活跃案例判定依赖 T10 标记**：只把 `incident_marker` 的 `state_change` 最新状态为 observing/suspected/incident_open/recovering 的会话视为活动；无标记的旧会话按已结束处理。
6. **聚合只保留计数类事实**：不保留接口/路由/诊断正文或原始事件，不能据此重放或宣称根因；需要细节时必须先导出/钉住。
7. **中间损坏不自动修复**：损坏日志的追加仍会被 `NdjsonEventLog` 拒绝（防止歧义），自动恢复只覆盖“仅末行截断”；中间损坏需人工/导出处理。
8. **隔离/聚合 sidecar 无自动清理入口**：它们是受保护的少量元数据，只有钉住有显式 `RemovePinned`；隔离文件清理接口未提供（可后续补用户清理入口）。
9. **时钟检测容差 1 分钟**：更小的回拨不会被标记为 rollback；真实睡眠/跨 boot 的时序语义仍以 T10 为准。
10. **未做真实磁盘满/ACL 拒绝实验**：按任务禁令只用 seam 模拟，结果不能替代 G2 的真实环境验证。
11. **全量解决方案/全量七工程未跑**：按任务要求仅构建 Storage + 相关测试；G2 需重跑完整构建与全套测试。

## 8. 设计/ADR 偏差

- 新服务放在 Storage（不新建项目、不动 Desktop）；Application 未改，复用既有 `IClock`，没有为配额额外扩张接口。
- 从 `NdjsonEventLog` 抽出 `EventLogParser` 供恢复复用；公开行为与既有测试不变（全量 Replay/Contract/Export 通过）。
- 新服务统一使用 `IStorageFileSystem` seam；既有 `NdjsonEventLog`/`IEventLogFileAccess` 保持兼容，不强行改造其他调用方。
- 导出目录默认**不**纳入普通 500 MB 池，也默认不自动删除（原设计“导出后删除、本地保留期和用户主动清理分别处理”）；`RetentionPolicy.IncludeExportsInNormalQuota` 仅为测试/未来显式启用。
- 钉住布局与 pin 入口是最小占位；不改变原设计 1 GB 上限或“拒绝新钉而非解钉”的语义。

## 9. 是否有真实网络操作

**无。** 未发起探测/DNS/HTTP，未连接代理或客户端，未修改系统网络/防火墙/DNS/代理设置，未填盘、未改系统时钟、未提权、未上传任何文件。

## 10. 建议下一任务（不自动执行）

按施工顺序，T11 之后是 **T12 案例重放工作台**（该任务正在由并行队友推进，其 Storage 改动已在本工作树中且未被本任务修改）。T11 的服务接线（会话开始/退出时调用配额/恢复并把未持久化范围显示出来）建议作为 T12/G2 前的独立小任务或并入 G2 复核项。本轮完成即停，不创建提交、不推送、不领取 T12。
