# T10：观察会话与故障捕获（M2）

任务：T10（M2 故障捕获；基于 T06-R3 已接通的证据链）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
依赖：T06-R3 观察/证据入口已就绪；T07-R2 页面、T04-R2 探测事实、T08-R2 导出由并行任务维护，本任务不修改其文件
设计依据：原设计 03 §4（故障捕获状态机）、08/T10、11 §3—§4（托盘生命周期、四类业务）、07 QA-007/012/015/017/037/038、09；施工单 T10 卡

**结论：T10 的观察会话与故障捕获底座已实现。新增 `IIncidentRecorder`/`IncidentState`/`IncidentTrigger`/`IncidentWindow` 等最小契约，`IncidentRecorder` 作为纯状态机放在 Storage 会话层；`ObservationSession` 的两个真实入口把采样/探测输出送入 recorder，并把状态变化/用户标记/业务清单以无类型 `incident_marker` 事件落盘；`TrayController` 启用“刚才断网了”和“最近故障”（图标、关闭行为、现有菜单项均保持）。前窗 5 分钟、后窗 2 分钟、同目标同路径 3 次有效失败、3 组可比成功、5 分钟合并、缺样不计失败、无前窗标缺失、暂停/退出排空、睡眠/跨 boot 边界均已用假时钟验证；QA-015 真实断网未执行（需合格 VM），记待验。**

## 1. 实现了什么（用户可观察行为）

1. **故障捕获状态机**：`idle → observing → suspected → incident_open → recovering → closed`，另有 `paused`、`degraded`。前 5 分钟/后 2 分钟窗口、3 次同目标同路径有效失败阈值、3 组可比成功恢复、后窗无新增失败才 `closed` 均按设计默认值实现；状态机不读系统、不起探测、不写文件。
2. **会话接线**：`ObservationSession` 每轮采样/显式探测完成后把观察送入 recorder；recorder 返回的状态变化、用户标记、业务清单被持久化为 `incident_marker` 事件（引用 incident_id/快照，payload 无 `payload_version`，旧解码器可安全跳过），退出时排空未写标记。
3. **用户标记**：托盘“刚才断网了”立即打开/补充 incident，并把用户描述写入记录；未等阈值。会话停止后拒绝标记。
4. **最近故障**：托盘“最近故障”展示当前/最近 incident 的等级化摘要（用户标记/连续失败、记录中/恢复观察中/已结束、是否有前窗、有效失败次数、是否根因未证实），不泄露证据正文。
5. **窗口与缺口诚实表达**：前窗不足时 `PreWindowAvailable=false` 并记录 `pre_window_incomplete`；后窗不完整（暂停、退出、睡眠/跨 boot、缺探测覆盖）时 `PostWindowComplete=false` 并记录原因；缺样/取消/权限/不支持样本只记 `probe_gap`/`missing_ranges`，不计网络失败。
6. **合并与计数**：同类失败在 5 分钟内合并，保留 `Count`/`FirstAt`/`LastAt`；超过 5 分钟形成新的失败组；有效失败/成功计数独立保留。
7. **外部观察门控**：只有用户显式启动的探测组才带 `ExternalObservationEnabled=true`；本地采样/页面/适配器活动永远不能提供网络失败证据。策略类型记录 30 秒间隔、30 秒预算、必须用户启动；T10 不新增任何后台自动外部探测。
8. **四类业务清单**：DSH、Antigravity、Chrome、TUN 普通应用默认 `not_tested`；可记录 `pass/fail/not_tested` 及时间、来源、用户备注，`not_tested` 不计入 `AllBusinessChecksPassed`；不发起任何付费或真实业务请求。
9. **托盘生命周期**：隐藏到托盘继续当前观察（既有行为未改）；暂停/显式退出会排空可写标记；退出后 recorder 停止接收数据，未完成 incident 以 `session_end_before_post_window` 关闭并保留缺口。

## 2. 状态机、阈值与窗口

| 项目 | 实现值 |
|---|---|
| 状态 | `Idle, Observing, Suspected, IncidentOpen, Recovering, Closed, Paused, Degraded` |
| 前窗/后窗 | 5 分钟 / 2 分钟（`IncidentWindow.DefaultPreWindow/DefaultPostWindow`） |
| 失败阈值 | 同 `target+path+proxy+family+method+options` 连续 3 次有效网络失败 → `Suspected` → `IncidentOpen` |
| 有效失败 | `Failed/Timeout` + `TransportFailure` + 失败阶段非 `not_started` + 非权限错误（5/13/10013）+ 非 budget/cancel/permission/unsupported 原因 |
| 有效成功 | `Ok` + `ReachableExpected/ReachableUnexpected`，同样按目标/路径/代理/族/方法/选项归组 |
| 恢复 | 触发键（手动标记则为首个外部探测组的键）连续 3 次可比成功 → `Recovering` |
| 关闭 | `Recovering` 起点 + 2 分钟且该窗口无有效失败、无暂停/降级中断、结束点有外部探测覆盖 → `Closed`；否则 `PostWindowComplete=false` |
| 合并 | 同类失败在 5 分钟内合并，保留次数与首末时刻 |
| 缺样 | 取消/权限/不支持/预算/未开始只记缺口，不计失败，也不重置失败序列（缺样不等于成功） |
| 边界 | 暂停/降级/退出/睡眠（墙钟-单调钟偏差 >5 秒）/单调钟回退/boot 变化：关闭未完成 incident 为不完整并重置前窗/连续计数 |
| 用户标记 | 立即 `IncidentOpen`（trigger=`ManualUserMark`），前窗按实际观测如实标记 |

## 3. 实际修改文件

Application：
- `src/NetScope.Application/Incidents.cs`（新增）：`IncidentState`、`IncidentTrigger`、`IncidentWindow`、`IncidentObservation`、`IncidentMarkContext`、`IncidentFailureGroup`、`IncidentRecord`、`BusinessCheckEntry`、`IncidentRecorderStatus`、`IncidentTransition`、`IncidentRecorderOptions`、`IncidentObservationPolicy`、`IIncidentRecorder`。

Storage：
- `src/NetScope.Storage/IncidentRecorder.cs`（新增）：纯状态机实现（滚动前窗缓冲、连续失败/成功计数、合并、暂停/降级/边界、业务清单、状态事件）。
- `src/NetScope.Storage/IncidentSessionEvents.cs`（新增）：`incident_marker` 严格封套 + 无类型 payload 编码；拒绝跨 session/boot/合成标记的 transition，不做重打戳。

Desktop：
- `src/NetScope.Desktop/Services/ObservationSession.cs`：注入/惰性启用 recorder；两个入口送入 `IncidentObservation`（手动探测器组才标记外部观察）；排空/关闭标记；提供 `MarkIncidentAsync`、`RecordBusinessResultAsync`、`Recorder`、`EnsureIncidentRecorder`。
- `src/NetScope.Desktop/Services/TrayController.cs`：启用“刚才断网了”“最近故障”，订阅 recorder 状态更新图标/菜单；对外提供可测试的启用判定与描述文本；图标/关闭/现有菜单项未改。
- `Composition.cs`/`App.xaml.cs`/ViewModels/Views 未修改（不在本任务写入范围）。真实应用由 `TrayController` 在 `ShowShell` 中启用 recorder（App 随后 `Session.Start`）。

测试：
- 新增 `tests/Unit/T10IncidentRecorderTests.cs`（16 个假时钟状态机测试）。
- 新增 `tests/Desktop/T10IncidentSessionTests.cs`（7 个会话/托盘/门控测试）。
- 新增 `tests/Contract/T10IncidentMarkerContractTests.cs`（3 个封套契约测试）。
- 新增 `tests/Replay/T10IncidentMarkerReplayTests.cs`（2 个标记回放/作用域测试）。
- `tests/Unit`、`tests/Contract` 增加对 Storage 的项目引用（测试用 recorder/marker 编码）。
- 未修改 `tests/Desktop/SessionLifecycleTests.cs` 等并行任务文件；`tests/Desktop/NetScope.Desktop.Tests.csproj` 的改动来自 T06-R3，本轮未再改。

## 4. 关键设计与 ADR 偏差

1. **recorder 的启用点**：任务写入范围不含 `Composition.cs`/`App.xaml.cs`，因此 recorder 由 `TrayController` 构造时通过 `session.EnsureIncidentRecorder()` 启用；真实应用顺序为“创建托盘 → 启动观察”，满足 M2 托盘场景。无托盘的 headless smoke 不会创建 recorder（见限制 1）。
2. **无后台自动探测**：按任务禁令“不得新增外部自动请求”，T10 不实现 30 秒后台探测循环；只有用户显式 `RunDiagnosisAsync` 的探测组可作为外部观察。`IncidentObservationPolicy` 保留间隔/预算/必须用户启动的约束并在状态中暴露，后台调度留给后续任务。
3. **后窗闭环保守**：本地采样不能证明“后窗无新失败”，因此关闭需要结束点有外部探测覆盖；未覆盖时保持 `Recovering` 并写 `post_window_probe_coverage_missing`，不把“没有探测”当作恢复。
4. **无类型 incident_marker**：不新增 typed payload 版本；旧解码器按既有规则跳过，回放/导出不受影响。这与 T06-R3 的 gap 事件策略一致。
5. **recorder 状态与证据分离**：recorder 只接收会话已产生的观察与事件 ID；不复制快照/证据正文，不重放历史，不改变既有 probe/evidence 语义。

## 5. 执行的命令、环境与退出码

环境：.NET SDK `10.0.403`，Windows 普通权限；未提权、未启动客户端、未改网络、未发外部探测。

| 命令 | 结果 |
|---|---|
| `dotnet build src/NetScope.Application` | 退出码 0，0 警告 / 0 错误（`logs/build-NetScope.Application.log`） |
| `dotnet build src/NetScope.Storage` | 退出码 0，0 警告 / 0 错误 |
| `dotnet build src/NetScope.Diagnostics` | 退出码 0，0 警告 / 0 错误 |
| `dotnet build src/NetScope.Desktop` | 退出码 0，0 警告 / 0 错误 |
| `dotnet build src/NetScope.Cli` | 退出码 0，0 警告 / 0 错误 |
| `dotnet test tests/Unit` | **285 通过 / 0 失败 / 0 跳过**，退出码 0（基线 269 + 本轮 16） |
| `dotnet test tests/Contract` | **83 通过 / 0 失败 / 0 跳过**，退出码 0（基线 80 + 本轮 3） |
| `dotnet test tests/Replay` | **72 通过 / 0 失败 / 0 跳过**，退出码 0（基线 70 + 本轮 2） |
| `dotnet test tests/Desktop`（非 T07 类） | **57 通过 / 0 失败 / 0 跳过**，退出码 0（含本轮 7、T06-R3 15） |
| 本任务新增测试 | **28 个**（Unit 16、Desktop 7、Contract 3、Replay 2） |
| 只读 desktop smoke | 退出码 0；`desktop-smoke.json`：persisted=true、3 快照、36 事件、导出成功 |

TRX 在 `test-results-final/`；最终日志在 `logs/final-*`。未做全解决方案并发构建，只构建 `src` 侧 5 个受影响工程。

**并行任务说明（非 T10 失败）**：`tests/Desktop` 全量一起跑时，T07-R2 正在修改的类并行执行会挂起（疑似其新测试/静态 WPF 状态）；本任务单独运行 T07 类 24/24 通过，单独运行非 T07 类 57/57 通过，T10 7/7 通过。为避免干扰并行任务，报告采用“非 T07 类”的 Desktop 结果，并单独记录 T07 类结果。

## 6. 行为证据

- 假时钟覆盖：`tests/Unit/T10IncidentRecorderTests.cs`
  - 3 次连续有效失败 → suspected → incident_open（前窗缺失如实标记）；
  - 5 分钟前窗缓冲与事件引用；
  - 用户标记立即打开；
  - 缺样/取消/权限/未开始不计失败；
  - 5 分钟同类合并与超过窗口分组；
  - 3 组可比成功 → recovering，完整后窗 → closed；
  - 后窗新失败重开并重新计时；
  - 暂停 + 退出关闭为不完整；
  - 睡眠（墙钟/单调钟偏差）、boot 变化关闭未完成并重置；
  - degraded 单列并恢复；
  - 本地采样失败探测不产生网络失败；
  - 不同目标成功不计恢复；
  - 业务清单默认 not_tested、not_tested 不计 pass。
- 会话/托盘：`tests/Desktop/T10IncidentSessionTests.cs`
  - 手动标记立即落盘且标记可回放（payload 无 typed 版本、refs.incident_id 正确）；
  - 业务清单经会话落盘，其他类保持 not_tested；
  - 3 次失败探测组经 suspected 打开 incident；
  - 本地采样 8 轮不产生 incident、probe runner 0 次；
  - 暂停/退出排空标记，退出后拒绝标记；
  - 只有显式探测组带外部观察标记与预算；托盘启用判定/摘要文本。
- 契约/回放：`T10IncidentMarkerContractTests`（封套 schema、无 typed 版本、确定性 ID、跨 session/boot 拒绝）、`T10IncidentMarkerReplayTests`（标记不污染快照作用域、不生成诊断、回放干净）。
- 真实只读 smoke：`docs/reports/evidence/T10/desktop-smoke.json`（真实机器 3 轮采集，退出码 0；无托盘场景不启用 recorder，见限制 1）。

## 7. QA 覆盖

| QA | 覆盖 | 结果 |
|---|---|---|
| QA-007 间歇路径差分 | 失败/成功连续计数、5 分钟合并、后窗重开 | 假时钟通过；展示由 T07-R2 |
| QA-012 接口漂移 | T10 不改变 snapshot/evidence 作用域；标记为无类型事件 | Replay 现有用例 + T10 标记回放通过 |
| QA-015 断网落盘 | 需合格 VM/授权断网；本轮未执行 | **待验**（不声称通过） |
| QA-017 时钟跳变 | 暂停、睡眠/墙钟单调钟偏差、monotonic 回退、跨 boot | 假时钟通过；记录缺失范围而非负耗时 |
| QA-037 托盘生命周期 | 隐藏继续观察（既有行为未改）、暂停/显式退出排空、退出后停止、无启动项/服务 | 会话暂停/退出 drain 通过；NotifyIcon 点击未自动化（限制 4） |
| QA-038 四类业务 | 默认 not_tested，pass/fail/not_tested 分项、时间/来源/备注、不自动付费请求 | 服务与持久化通过；真实业务操作需用户（限制 5） |

## 8. 只读/权限/隐私边界检查

- recorder 不读系统、不连接、不写网络设置；会话新增的仅是把已有观察送入内存状态机，以及把状态变化写成 `incident_marker` 事件。
- 标记 payload 只含状态/时间/incident id/失败类计数/业务清单与用户备注，不含命令、完整路径、凭据或配置正文；导出脱敏仍由 T08-R2 面处理。
- 未新增外部自动请求、提权、服务、启动项、计划任务；未修改 Probes、Storage Export、Repair/Broker、Views/ViewModels 或真实客户端配置。
- 未创建提交、未推送、未上传；证据与 smoke 只写本机。

## 9. 已知限制与未实现项

1. **recorder 启用依赖托盘入口**：真实应用在 `TrayController` 构造时启用 recorder（随后 `Session.Start`）；headless smoke 不创建托盘，因此不产生 incident 标记。后续可由 Composition 显式接线（本轮 Composition 不在写入范围）。
2. **无后台外部探测循环**：按任务禁令不新增自动外部请求；“前窗/后窗”的探测覆盖依赖用户显式探测组；用户不运行探测时 incident 停在 open/recovering，退出按不完整关闭。30 秒间隔/30 秒预算的类型与默认值已就绪，调度留待后续任务。
3. **后窗关闭需要外部探测覆盖**：只有本地采样时不会关闭恢复中的 incident，避免“无探测=无失败”的错误结论。
4. **托盘 UI 交互未自动化**：`NotifyIcon`/菜单点击需要桌面会话；本任务测试了启用判定、摘要文本与会话/标记行为，实际点击与托盘气泡需人工试用。
5. **QA-015 真实断网 / QA-038 真实业务**：无合格隔离 VM 与用户真实操作，未执行；不据此宣称 M2 或四类业务完整验收。
6. **全量 Desktop 套件与并行 T07 类一起运行挂起**（外部并行改动导致，单独运行各自通过）；建议 Lead 在合流后重新跑全量。
7. **多 recorder/多会话**：每个 ObservationSession 只保留一个 recorder；晚绑定会替换旧实例（测试未覆盖多实例并发，单进程单会话是当前产品形态）。
8. **incident 事件体积**：incident 记录保存窗口内事件 id 与失败分组；5 分钟窗口/2048 条缓冲上限已设，配额与轮转属 T11。
9. **业务清单入口**：服务/API 与会话方法已提供，页面入口按任务范围留给 T07-R2/T16；本轮无 UI 记录按钮。

## 10. 是否有真实网络操作

无。未发起任何外部请求/探测/DNS 查询，未启动或停止客户端，未修改系统网络设置或真实客户端配置。真实只读 smoke 只执行既有本机只读采集与本地文件写入。

## 11. 建议下一任务（不自动执行）

按施工顺序建议复核后领取 **T11**（配额与崩溃恢复）；本任务完成即停，不领取 T11/T12，未提交或推送。
