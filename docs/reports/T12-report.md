# T12：案例重放工作台（M2）

任务：T12（施工单 T12 卡；停在 T12，不领取 T16）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，基线 HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`；未新增提交、未推送
设计依据：原设计 04（数据契约与隐私/包准入）、07 QA-012/014/015/024、08 T12 行、09 施工总指令；施工单 `docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T12 卡
工作树前提：与 T11/T13/T16 等并行施工，只修改本任务写范围；未改 SessionRecovery/QuotaManager，未改 Desktop ShellView/MainViewModel/Theme。

**结论：新增离线只读的案例重放工作台。输入按不可信 ZIP 准入（100MB/1000 文件/20MB 单文件/深度 8，绝对路径/drive/`..`/符号链接/重名拒绝），校验和与 manifest 全部通过后才严格解码事件；按 snapshot scope 逐周期回放，包内记录诊断与当前构建固定规则集重算分开显示，不支持的历史规则/包版本标 Unsupported，相同包重复回放输出一致，且未提供任何网络、采集、探测、Broker 或系统写入口。**

## 1. 任务 ID / 变更摘要

| 项 | 内容 |
|---|---|
| 任务 | T12 案例重放工作台 |
| 新增 Application 契约 | `CaseReplayLimits`、`CaseReplayStatus`、`CaseReplayResult`、`IRuleSetVersionProvider`、`ICaseRuleEvaluator`、`ICaseReplayService` 及记录/诊断/差异/scope/incident 类型 |
| 新增 Storage 实现 | `CaseReplayService`（准入、校验和/manifest、严格事件解码、逐 scope 重算、incident 窗口、确定性指纹） |
| Storage 复用 | `ReplayService`（新增 `ReplayText`，原 `Replay(path)` 行为不变）、`EvidenceEventCodec`、`UntrustedPackageValidator`（原设计 04 限额/路径/链接检查） |
| CLI | 新命令 `netscope case-replay <incident-package.zip>`（只读本地包；退出码 0/1/2/3/4） |
| Desktop | 只新增 `ViewModels/CaseReplayViewModel.cs`、`Views/CaseReplayView.xaml(.cs)`；未接导航（Lead 合并） |
| 测试 | Replay **97/97**（T12 23/23）、Contract **88/88**（T12 4/4）、Unit **320/320**（T12 5/5）、Desktop **134/134**（T12 4/4）；0 失败 0 跳过 |
| 真实网络/系统写 | **无** |

## 2. 实现了什么（用户可观察行为）

1. **不可信包准入**：`CaseReplayService` 先用现有 `UntrustedPackageValidator` 检查 ZIP 中央目录：条目数 ≤1000、声明解压总量 ≤100MB、单条目 ≤20MB、路径深度 ≤8；拒绝绝对路径、drive letter、`..`/`.` 段、空段、控制字符、`:`、重名条目和 Unix 符号链接。随后只读取 `manifest.json`、`events.ndjson`、`checksums.sha256`，对每个实际条目做字节数上限与实际大小核对，再逐文件 SHA-256 与 `checksums.sha256` 比对；校验和集合必须与实际文件集合一致，manifest `files` 的 name/sha256 必须与实际文件一致。任一失败即 `Rejected`，不进入分析、不读事件内容以外的解释。
2. **严格事件解码与损坏包拒绝**：仅当准入/校验和/manifest 全部通过后，用 `ReplayService.ReplayText`（与文件回放同一严格 reader + typed codec）解码。事件重复 ID、序列缺口、截断/损坏行、证据引用错误、manifest 计数不一致都会被拒绝；不会“跳过坏行后继续给结论”。这覆盖 QA-014 的“删除引用/篡改摘要后导入，拒绝或标 insufficient，不静默补造”。
3. **包版本与规则版本分开**：`package_format_version != "1"` 或 schema 版本不支持时返回 `Unsupported`：仍显示准入事实、包内记录诊断和 scope，但**不重算**，并明确“当前规则不冒充原版”。包内每条记录诊断带 `RuleSupported`/`VersionMatchesCurrent`；规则不存在或版本不同时，差异项标 `Unsupported`，不把当前规则的等级与记录等级硬比较。重算统一显示当前构建固定规则集版本（`diagnostics/<sha16>`，由规则 id+version 规范化后哈希），并与 `RecordedDiagnoses` 分列表展示。
4. **逐 snapshot scope 回放**：每个 scope 只用自己的 `EvidenceSet`（快照 + 本周期 probes + 本周期 records）计算，不借用其他周期证据；结果包含每 scope 的快照 ID、记录/探测数、记录诊断/重算诊断数、接口稳定标识与当周期 ifIndex。接口漂移测试中同一 GUID 在两个 scope 保持旧索引 12 → 新索引 44，未回写/覆盖旧值（QA-012）。
5. **incident 窗口与跨周期证据**：从包内 `incident_marker` 事件解析 incident id、触发、状态序列、开始/关闭时间、前后窗可用性、缺失范围、事件/快照/诊断引用；把引用映射到实际 snapshot scope，输出 `SnapshotScopeEventIds` 与 `SpansMultipleSnapshotCycles`。跨周期引用是显式字段，不与单 scope 证据混合。
6. **旧包只说明变化与缺口**：无 typed evidence 的旧包标 `legacy_no_typed_evidence`，不重算、不造诊断；无探测结果时提示“不能据此证明外部可达/恢复/根因”；无客户端进程/配置证据时提示“不推断归属/配置陈旧/端口冲突”；本地样本测试中没有出现 Supported/Verified 级重算结论。诊断正文只来自包内记录或当前规则对已存事实的重算，没有补造根因。
7. **确定性与只读**：相同包重复回放给出相同 `DeterministicFingerprint`（对状态、准入、版本、scope、记录/重算诊断、差异、incident、issues/notices 的规范化字段做 SHA-256；不含文件路径/读取时间/随机值），测试同时验证包文件哈希和目录文件列表在回放前后不变（QA-024）。
8. **CLI**：`netscope case-replay <pkg.zip>` 打印准入、版本、逐 scope、记录诊断、固定规则重算、差异、incident、issues/notices、指纹；退出码：`0` Ok、`1` Rejected、`3` Unsupported、`2` 文件不存在、`4` 参数错误。`netscope help` 已列该命令。
9. **Desktop 只读页面**：新 `CaseReplayViewModel`/`CaseReplayView` 支持选择本地 ZIP、离线重放、分开显示记录/重算/差异/scope/incident/问题；默认通过文件对话框选包，服务注入可测试；无任何写按钮或修复入口。导航未接，最小钩子见 §8。

## 3. 实际修改文件

新增：
- `src/NetScope.Application/CaseReplay.cs`
- `src/NetScope.Storage/CaseReplayService.cs`
- `src/NetScope.Cli/CaseReplayCommand.cs`
- `src/NetScope.Desktop/ViewModels/CaseReplayViewModel.cs`
- `src/NetScope.Desktop/Views/CaseReplayView.xaml`
- `src/NetScope.Desktop/Views/CaseReplayView.xaml.cs`
- `tests/Replay/T12CaseReplayFixture.cs`
- `tests/Replay/T12CaseReplayTests.cs`
- `tests/Contract/T12CaseReplayContractTests.cs`
- `tests/Unit/T12CaseReplayUnitTests.cs`
- `tests/Desktop/T12CaseReplayViewModelTests.cs`
- `docs/reports/T12-report.md`

修改（最小）：
- `src/NetScope.Storage/ReplayService.cs`：新增 `ReplayText(string)`，原 `Replay(path)` 改为复用内部 `FromRead`；行为不变。
- `src/NetScope.Storage/NdjsonEventLog.cs`：`Parse` 由 `private static` 改为 `internal static`（供 `ReplayText` 复用同一严格 reader，不复制 codec）。
- `src/NetScope.Cli/Program.cs`：switch 增加 `case-replay`/`casereplay`，help 增加一行；其余命令不变。

未修改：`Export/*`、`Probes`、`Collectors`、`Broker`、`Repair`、`ObservationSession`、`TrayController`、`ShellView`、`MainViewModel`、Theme/图标/关闭行为、真实系统/客户端配置。

## 4. 关键设计与偏差

- **Application 只放契约，实现放 Storage**：Application 不能读 ZIP/不能引用 Storage；因此 `ICaseReplayService`/结果类型/规则集接口在 Application，`CaseReplayService` 在 Storage。固定规则求值通过 Application 的 `ICaseRuleEvaluator : IRuleSetVersionProvider` 注入，CLI/Desktop 各自提供约 20 行 `DiagnosticEngine` 适配器（见偏差 2）。
- **复用现有 `UntrustedPackageValidator`（设计 04 限额）而不是新写检查器**：该验证器已实现 100MB/1000/20MB/深度 8 及路径/链接/重名检查；本任务在其上增加“实际读取字节数 + checksums + manifest + manifest counts + 严格事件”的第二层校验。没有使用 T16 的只读检查脚本替代。
- **不复制 codec**：`ReplayService.ReplayText` 复用 `NdjsonEventLog.Parse` 与 `EvidenceEventCodec.Decode`；`CaseReplayService` 仅做包层与展示层。
- **固定规则版本字符串**：由规则目录 `ruleId@version` 排序拼接后 SHA-256 前 16 位生成，例如 `diagnostics/fdbef57647ea2372`；同一构建恒定，避免用时间戳/路径作为版本。
- **落盘/读取边界**：不把包解压到磁盘、不写临时文件；ZIP 直接从文件流读取并哈希。回放过程无文件写、无临时目录、无网络调用。
- **偏差 1**：`Application` 目前还引用了 `NetScope.Repair`（T13 的纯计划类型）。因此依赖测试不再要求 Application **程序集**完全不引用 Repair，而是断言包/应用回放契约的字段/参数/返回值不出现 Repair/Probes/Collectors/Adapters/Broker 类型；Storage 直接引用仍无这些程序集。README/报告注明这是共享 Application 工程的既有 T13 变化，不是本任务读取链。
- **偏差 2**：CLI 与 Desktop 各自有一个很薄的 `DiagnosticEngine` 适配器（约 20 行），因为 Diagnostics 不在本任务写范围且 Application 不引用 Diagnostics；未复制 codec/规则本身，两处都委托 `RuleCatalog.All` + `DiagnosticEngine`。后续可抽到 Diagnostics 的公共 provider，但本轮不做。
- **偏差 3**：包准入的“实际读取上限”以 20MB/100MB 为硬上限，与声明值双重检查；固定规则集重算失败时只记录 `recomputed_rule_failure`/`recomputed_rule_exception`，不为失败规则补造诊断。
- **偏差 4**：Desktop 导航钩子未接（按 Lead 要求只新建 VM/View），页面在 Lead 合并导航后才可从 Shell 进入。

## 5. 执行的命令、环境与退出码

环境：Windows，.NET 10，仓库 `D:\Projects\NetScopeRepair`；所有命令只读本地文件/测试临时目录。

| 命令 | 结果 | 退出码 |
|---|---|---|
| `dotnet build src/NetScope.Application/NetScope.Application.csproj --nologo -v q` | 0 warning / 0 error | 0 |
| `dotnet build src/NetScope.Storage/NetScope.Storage.csproj --nologo -v q` | 0 warning / 0 error | 0 |
| `dotnet build src/NetScope.Cli/NetScope.Cli.csproj --nologo -v q` | 0 warning / 0 error | 0 |
| `dotnet build src/NetScope.Desktop/NetScope.Desktop.csproj --nologo -v q` | 0 warning / 0 error | 0 |
| `dotnet test tests/Replay/NetScope.Replay.Tests.csproj --nologo -v q` | 97/97 通过，0 跳过 | 0 |
| `dotnet test tests/Replay/... --filter "FullyQualifiedName~T12CaseReplayTests"` | 23/23 通过 | 0 |
| `dotnet test tests/Contract/NetScope.Contract.Tests.csproj --nologo -v q` | 88/88 通过 | 0 |
| `dotnet test tests/Contract/... --filter "FullyQualifiedName~T12CaseReplayContractTests"` | 4/4 通过 | 0 |
| `dotnet test tests/Unit/NetScope.Unit.Tests.csproj --nologo -v q` | 320/320 通过 | 0 |
| `dotnet test tests/Unit/... --filter "FullyQualifiedName~T12CaseReplayUnitTests"` | 5/5 通过 | 0 |
| `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --nologo -v q --blame-hang-timeout 90s` | 134/134 通过 | 0 |
| `dotnet test tests/Desktop/... --filter "FullyQualifiedName~T12CaseReplayViewModelTests"` | 4/4 通过 | 0 |
| `netscope case-replay <T10 导出的真实脱敏包.zip>` | `RESULT=Ok`，3 个 scope、6 条记录诊断/6 条重算/6 条 Match，无探测提示 | 0 |
| `netscope case-replay <把 package_format_version 改成 2 且同步校验和的包>` | `RESULT=Unsupported`，未重算 | 3 |
| `netscope case-replay <非 ZIP 文件>` | `RESULT=Rejected`，`invalid_zip` | 1 |

说明：真实包取自本机 `docs/reports/evidence/T10/...zip`（该目录被 gitignore，未入库、未上传）；未执行任何真实网络请求。构建/测试期间遇到的其他子代理未完成文件（SessionRecovery/QuotaManager 编辑中）不并入本报告结论；最终上述命令均在该共享工作树上重跑通过。

## 6. 行为证据（T12 测试清单）

`tests/Replay/T12CaseReplayTests.cs`（23 例）：
- 真实 `IncidentExporter` 包通过同一准入路径；记录/重算分开；包哈希回放前后不变。
- 相同包两次回放 fingerprint/结构一致，文件与目录不变。
- incident 窗口解析出跨周期 2 个 scope、states closed、前后窗完整。
- 接口漂移：同 GUID 两个 scope 保留 12 → 44，不重写旧索引。
- 本地-only 旧包：提示无 probe/无客户端证据，重算无 Supported/Verified；legacy 无 typed evidence 不造诊断。
- 记录规则版本改为 `0.0.0-legacy`：全部差异 `Unsupported`，重算仍存在但标当前固定规则集。
- 包格式版本 2：`Unsupported`、不重算、保留记录诊断。
- 删除中间事件/重复事件/篡改校验和/manifest 计数不一致：均 Rejected，无重算。
- 记录诊断的等级、证据引用、缺失证据分别改动：差异输出 `LevelChanged`/`EvidenceChanged`/`MissingEvidenceChanged`，三项独立报告，不合并成一个“严重程度”。
- 恶意 ZIP：绝对路径、`..`、drive letter、符号链接；以及超条目/超总大小/超深度/超单文件限额。
- 依赖断言：Application/Storage 程序集不引用 Probes/Collectors/Adapters/Broker；回放契约签名不含这些命名空间（也不含 Repair）。
- `tests/Contract`：设计 04 限额默认值、包格式常量、契约字段/无网络能力。
- `tests/Unit`：Ok 才声称重算；Rejected 可无记录/重算列表；Unsupported 差异语义。
- `tests/Desktop`：Ok 结果绑定、选择包+重放命令、Rejected 文案、默认构造不读文件；无网络/写操作。

## 7. QA 覆盖

| QA | 覆盖方式 | 结果 |
|---|---|---|
| QA-012 接口漂移 | 两周期 ifIndex 12→44，同 GUID 逐 scope 保留 | 通过（无旧索引回写） |
| QA-014 证据关联/损坏包 | 校验和篡改、删事件（序列缺口）、重复事件 ID、manifest 计数、绝对/`..`/symlink/超限包 | 通过（拒绝或 Unsupported，不静默补造） |
| QA-015 断网落盘 | 离线回放/只读/包完整性/旧包缺口说明；真实 VM 断网实验未执行 | 部分：离线链路通过，真实 VM 实验待授权（见 §9） |
| QA-024 回放确定性 | 相同包两次 fingerprint/结构一致；文件哈希不变；无网络/修复副作用断言 | 通过 |

## 8. Desktop 最小导航钩子（交 Lead 合并，本任务未改）

1. `src/NetScope.Desktop/ViewModels/MainViewModel.cs`：
   - 属性区加 `public CaseReplayViewModel CaseReplay { get; }`
   - 构造器在 `Sections` 之前加 `CaseReplay = new CaseReplayViewModel();`
   - `Sections` 数组加一项：
     ```csharp
     new NavSection("案例重放", "离线故障包重算与差异", CaseReplay,
         "M12 2l8 4v6c0 5.25-3.5 10-8 11-4.5-1-8-5.75-8-11V6l8-4zm-2 10l2 2 4-4"),
     ```
2. `src/NetScope.Desktop/App.xaml` 的资源字典加：
   ```xml
   <DataTemplate DataType="{x:Type vm:CaseReplayViewModel}">
       <views:CaseReplayView />
   </DataTemplate>
   ```
3. 无需改 `ShellView.xaml`（其 `ContentControl` 已按 VM 类型选模板），也无需改托盘/关闭/主题。

## 9. 已知限制与未实现项

- **未做真实 VM 断网实验**：T12 只验证离线包读取/回放与确定性；QA-015 的“真实断网落盘后离线打开”仍需具有 console/checkpoint 的隔离 VM 和实验负责人授权，不能用模拟结果宣称完整 M2 实验通过。
- Desktop 页面未接导航（按 Lead 要求），合并前只能通过测试/未来导航钩子进入；未做鼠标点击/截图验收。
- 固定规则重算只覆盖当前构建的 `RuleCatalog` 规则；历史规则实现不存在时标 `Unsupported`，不提供历史规则解释器（符合“不用当前规则冒充原版”，但不是完整历史重放）。
- 包准入还依赖 `UntrustedPackageValidator` 的现有 ZIP 语义；未对 ZIP64/加密 ZIP 做专门支持测试（加密条目会在读取时按损坏/异常拒绝）。
- CLI/Desktop 的规则集适配器有少量重复（约 20 行），未抽到 Diagnostics 公共 provider，属有意最小改动。
- 未构建全解决方案；只构建/测试本任务相关工程，避免并行工作树的无关失败。

## 10. 是否有真实网络操作

**无。** 所有命令为本地构建、测试、ZIP 读取与本地证据生成；`CaseReplayService` 只读文件流，没有网络客户端、Collector/Probe/Broker/Repair 构造或系统写入口；测试断言相关程序集/签名无这些依赖。CLI/Desktop 只读本地包，不发起外部请求、不读取/修改真实客户端配置、不切换 TUN、不改系统网络设置、不提升权限、不上传文件。

## 11. 建议下一任务（不自动执行）

按施工单顺序，T12 之后是 **G2 复核**：独立审查本任务 diff、恶意包反例与固定规则/版本显示，在最新 HEAD 上重跑完整构建/测试与离线回放；真实断网实验（QA-015）仍需 VM 授权后才能宣称完整 M2 通过。本任务完成即停，不领取 T16，不创建提交/推送。
