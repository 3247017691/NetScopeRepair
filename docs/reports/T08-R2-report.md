# T08-R2：错误文本脱敏、缺口统计与版本溯源

任务：T08-R2（原 T08 定向补修；按施工单顺序停在 G1-R2 之前，不领取后续任务）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，构建时 HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：原设计 04（数据契约与隐私）、07 QA-014/022、09 施工总指令与回报模板；施工单 `docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T08-R2 卡；T09-R2 报告
工作树前提：继续在他人未提交改动（T09-R2 等）之上施工；本任务只改 Export、导出统计类型、Export 预览 VM/XAML、Export/Replay 测试与报告。全解决方案构建和全量七工程测试留给 G1-R2，本轮不复制历史计数。

**结论：新导出器不再只认已列在 `interfaces.value` 的身份名；错误文本里独有的网卡/虚拟交换机/过滤器名称、组合名和遍历顺序差异都会得到同一 device 别名。manifest 与预览把序列缺口、独立 gap 事件、观察缺口、Partial/Unsupported 域与代理子域、探测覆盖、版本、变化摘要和诊断来源分开；旧计数字段语义未改。102 快照合成形状报告 102 个 Partial、0 probe、0 诊断，并明确 `gaps=0` 不等于采集完整。**

## 1. 任务 ID / 变更摘要

| 项 | 内容 |
|---|---|
| 任务 | T08-R2 |
| 脱敏策略 | `m1-alias-v2` → `m1-alias-v3`（新增身份预收集和错误文本结构化名称识别；包格式版本仍为 `1`，所有 manifest 变化为增量） |
| 包格式 | `netscope-incident-package/1` 不变；文件集合、文件名和校验和语义不变 |
| 新 manifest 分区 | `versions`、`gap_statistics`、`completeness`、`probe_coverage`、`change_summary`、`diagnosis_provenance`；`counts` 增补 `gap_events`/`partial_events`/`probe_events`，旧字段保留 |
| 预览 | 新增“包形状与溯源（与 manifest 同源）”卡片；旧事件/诊断/文件/脱敏类别预览保留 |
| 测试 | Export 43→**51**（本轮 +8），Replay **70/70**（本轮 +2），Desktop **50/50**；全部 0 失败 0 跳过 |
| 独立隐私复核 | Python 黑盒检查 **36/36** 通过（只读 ZIP，不导入产品代码） |
| 真实网络操作 | 无 |

## 2. 实现了什么（用户可观察行为）

1. **错误文本独有的接口/交换机/过滤器名称**：`PackageAnonymizer` 增加“优先身份预收集 + 结构化识别”两层处理。
   - 导出前先对整个包做 `Precollect`：从 `interfaces.value` 的 name/description/alias、SSID、`interface_name` 事实、user/device 字段、路径中的用户名/UNC 主机、以及自由文本中可结构识别的名称登记身份，再开始改写任何值。
   - 错误文本中优先按结构识别：`adapter/interface/nic/switch/filter/port/device`（含中文 网卡/接口/适配器/交换机/过滤器/设备）+ 引号名、`interface Synthetic-Uplink-C` 这类多段名；组合名覆盖 `vEthernet (...)`（含嵌套 `vEthernet (WSL (Hyper-V firewall))`）、`WAN Miniport (...)`、`Hyper-V Virtual Ethernet Adapter #2`、`Microsoft Wi-Fi Direct Virtual Adapter`、`Npcap Loopback Adapter`、`TAP-Windows Adapter V9`、`WireGuard/Tailscale/Wintun Tunnel`、`Loopback Pseudo-Interface`、`Ethernet/Wi-Fi/WLAN/Local Area Connection` 及中文 以太网/本地连接/虚拟交换机 等，另有 `Nice WFP Filter` 这类组合过滤器名。
   - 不再依赖“名称已出现在 `interfaces.value` 中”；名称只在错误里出现时也会被替换为 `device-XX`，且同一名称在结构字段和错误文本中得到**同一个**别名。
2. **遍历顺序差异**：先收集后改写使别名不依赖事件顺序。先行错误文本、后行接口列表（或反过来）导出结果都使用同一别名；旧实现只在遍历到接口值时登记，先出现的错误文本会漏。
3. **全包合成秘密哨兵扫描**：新增 QA-022 覆盖，把合成 token、密码标记、Cookie、Bearer、URL query 密钥、用户名、设备名、私有地址、内网域、公网目标、URL 路径注入到快照域错误文本、代理层 Notice/Errors/ProxyServer/Variables/AutoConfigUrl、探测错误与 Notice、证据记录 Summary/Facts、诊断 Summary/SuggestedNextStep/MissingEvidence、事件 Source、以及附加异常文本成员，然后扫描 ZIP 的**每一个**文件的原始字节与 JSON 字符串值。事件 id、sequence、status、refs、`evidence_metadata.source_event_ids` 全部保持可回放。
4. **manifest/预览分开统计**（旧字段语义不变）：
   - `gap_statistics.sequence_gaps` = 旧 `counts.gaps`（序列号缺口）；
   - `gap_statistics.gap_events` = `event_type = gap` 的独立记录；
   - `gap_statistics.observation_gap_events` = 旧 `counts.observation_gap_events`（非 ok 状态）；
   - `completeness` 分开域 `interfaces/routes/dns/proxy` 与代理子域（`proxy.tool_process_environment` … `proxy.winhttp`）的状态计数、不完整快照数、`collection_complete` 和说明；
   - `probe_coverage` 分开探测事件、组、目标、地址族、路径、成功/部分/失败，并明确 0 probe = 未做外部验证；
   - `counts` 新增 `gap_events`、`partial_events`、`probe_events`；`events/snapshots/diagnoses/gaps/observation_gap_events/failed_events/duplicate_event_ids` 语义与旧包一致。
5. **变化摘要**：`change_summary` 对相邻且 interfaces 域可读的快照做参数对比，只输出 `interface_guid/family/if_index` 和数值/布尔/枚举/地址**数量**，不复制名称或地址值；最多列 20 条并带 `truncated`；明确“变化不是原因，也不推断操作者”。102 形状里复现了 metric 0→4225（seq 22→23）及接口消失/重现，不复制真实包内容。
6. **版本溯源**：`versions` 记录 app 版本、程序集内嵌 build commit（`assembly_informational_version`，实测 `a4b07a0…`，不是当前 checkout）、证据负载版本、证据记录 `parser_version`、诊断 `rule_version`、事件 `source_version`、脱敏策略和包格式版本；旧的 `app_version`/`rules_version`/`rules_versions` 保留。
7. **记录诊断 vs 离线重算**：`diagnosis_provenance.recorded_in_package` 与 `recomputed_by_exporter` 分开（本实现重算恒为 0），并写明“导出器不重跑规则；离线重算必须由独立只读工具完成”。
8. **102 快照形状**：合成 fixture 102 个 snapshot、全部 interfaces Partial、WinHTTP 子域 Unsupported、0 probe、0 diagnosis、0 序列缺口；包为 108 条目、106 个 payload 文件、107 行校验和，逐文件 SHA-256 通过；`gaps=0` 与 `incomplete_snapshots=102` 同时出现，并产生明确警告。
9. **桌面预览**：`ExportViewModel` 在 exporter 实现可选 `IExportPackageDetailsProvider` 时展示上述分区；不实现时退回旧 `Preview` 而不伪造计数。`ExportView.xaml` 只用现有样式增加一张数据卡，未改主题、图标、关闭行为或导航。

## 3. 实际修改文件

产品（仅本任务写范围）：

- `src/NetScope.Storage/Export/PackageAnonymizer.cs`：身份预收集、结构化设备/交换机/过滤器名识别、边界感知替换、别名幂等、错误文本脱敏。
- `src/NetScope.Storage/Export/PackageStatistics.cs`（新增）：公开统计模型（缺口/域完整性/探测覆盖/版本/变化/诊断来源）、预览详情接口 `IExportPackageDetailsProvider`，以及只依赖已记录事件的统计构建器。
- `src/NetScope.Storage/Export/IncidentPackageBuilder.cs`：预收集接入、manifest 新分区、旧字段保留、脱敏策略版本与报告文案。
- `src/NetScope.Storage/Export/IncidentExporter.cs`：`PreviewDetails`、按统计生成警告、将统计传给 manifest，`Preview` 行为保持。
- `src/NetScope.Storage/Export/RedactionLedger.cs`：device 别名策略文案补充错误文本来源。
- `src/NetScope.Desktop/ViewModels/ExportViewModel.cs`：预览分区展示与回退。
- `src/NetScope.Desktop/Views/ExportView.xaml`：新增“包形状与溯源”卡片（仅必要字段）。

测试与证据：

- `tests/Export/T08R2Fixture.cs`（新增，全部 synthetic）、`tests/Export/T08R2AnonymizerTests.cs`、`tests/Export/T08R2SentinelTests.cs`、`tests/Export/T08R2CompletenessTests.cs`（新增）。
- `tests/Replay/T08R2ReplayDeterminismTests.cs`（新增）。
- 调整既有测试中因行为变化而必须更新的断言：`tests/Export/ReviewPrivacyTests.cs`（策略版本 v3）、`tests/Export/MultiSnapshotExportTests.cs`（不完整域警告不再是空列表；该合成采集器本来就不读 WinHTTP，旧断言掩盖了这一点）。
- 本地证据（`.gitignore` 已忽略 `docs/reports/evidence/`）：`docs/reports/evidence/T08-R2/`（日志、TRX、隐私复核、102 形状包、旧行为反例 harness；真实源包未使用）。

**未修改**：`src/NetScope.Probes/**`、`src/NetScope.Adapters/**`、`src/NetScope.Application/**`（含 `Exports.cs`）、`ObservationSession`、`EvidenceEventCodec`/`EvidenceSessionEvents`、Repair/Broker、主题/Logo/图标/Shell/关闭设置/托盘、真实客户端配置与系统网络设置。`git status` 中这些路径的改动属于其他队友的未提交工作，不在本任务范围内。

## 4. 修前反例（HEAD 旧实现行为）

用 `git show HEAD:…/PackageAnonymizer.cs` 等**复制**旧源码（仅改命名空间/类型名，不改算法）对同一组合成形状运行，输出见 [old-behavior.json](evidence/T08-R2/old-behavior/old-behavior.json)，日志 [old-behavior-run.log](evidence/T08-R2/logs/old-behavior-run.log)：

| 反例 | 旧实现 |
|---|---|
| 名称只出现在 `interfaces.errors` | 8 个标记全部原样保留：`Synthetic-Uplink-B`、`vEthernet (Default Switch)`、`vEthernet (WSL (Hyper-V firewall))`、`Hyper-V Virtual Ethernet Adapter #2`、`WAN Miniport (PPPOE)`、`Npcap Loopback Adapter`、`Nice WFP Filter`、`以太网 2`；同 payload 里 `interfaces.value` 的 name/description 已被换成 `device-*`，证明旧逻辑只认已列名 |
| 遍历顺序：错误事件在前、接口列表在后 | 错误文本中的 `Synthetic-Uplink-D` 漏出（leak=1） |
| 反向顺序：接口列表在前 | 旧实现碰巧不漏（leak=0），说明结果依赖遍历顺序 |
| `missing_evidence` 中的身份 | 旧版作为 protected key 原样写入 |
| 组合过滤器/交换机名 | 旧版无对应结构识别，原样保留 |

新增回归测试（当前实现）：

- `T08R2AnonymizerTests.Names_only_in_error_text_and_combined_switch_filter_names_are_aliased`
- `T08R2AnonymizerTests.Traversal_order_does_not_change_which_alias_an_identity_gets`
- `T08R2SentinelTests.Every_package_surface_loses_the_synthetic_secrets_and_references_survive`
- `T08R2SentinelTests.Exported_sentinel_session_replays_cleanly_and_keeps_typed_evidence`
- `T08R2CompletenessTests.*`（4 个，含 102 形状、108 条目/校验和/逐周期回放、双导出确定性、阻断预览）
- `T08R2ReplayDeterminismTests.*`（2 个）

## 5. 执行的命令、环境、退出码

环境：Windows 普通权限；.NET SDK **10.0.403**（[dotnet-version.txt](evidence/T08-R2/logs/dotnet-version.txt)）；构建时 HEAD `a4b07a0…`（[git-head.txt](evidence/T08-R2/logs/git-head.txt)、[git-status.txt](evidence/T08-R2/logs/git-status.txt)）。未提权、未联网、未修改系统设置。

| 命令 | 结果 | 证据 |
|---|---|---|
| `dotnet build src/NetScope.Storage/NetScope.Storage.csproj` | 退出码 0，0 警告 / 0 错误 | [build-storage.log](evidence/T08-R2/logs/build-storage.log) |
| `dotnet build src/NetScope.Desktop/NetScope.Desktop.csproj` | 退出码 0，0 警告 / 0 错误（含 ExportView XAML 编译） | [build-desktop.log](evidence/T08-R2/logs/build-desktop.log) |
| `dotnet test tests/Export/NetScope.Export.Tests.csproj --logger trx` | 退出码 0，**51 通过 / 0 失败 / 0 跳过** | [test-export-final.log](evidence/T08-R2/logs/test-export-final.log)、[export-final.trx](evidence/T08-R2/test-results/export-final.trx) |
| `dotnet test tests/Replay/NetScope.Replay.Tests.csproj --logger trx` | 退出码 0，**70 通过 / 0 失败 / 0 跳过** | [test-replay-final.log](evidence/T08-R2/logs/test-replay-final.log)、[replay-final.trx](evidence/T08-R2/test-results/replay-final.trx) |
| `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --logger trx` | 退出码 0，**50 通过 / 0 失败 / 0 跳过**（验证 Desktop 与预览 VM 编译/既有桌面行为） | [test-desktop-final.log](evidence/T08-R2/logs/test-desktop-final.log)、[desktop-final.trx](evidence/T08-R2/test-results/desktop-final.trx) |
| 证据 harness（合成会话 + 导出） | 退出码 0 | [harness-run.log](evidence/T08-R2/logs/harness-run.log) |
| 独立隐私检查 `python check_privacy.py` | 退出码 0，**36 检查 / 0 失败** | [privacy-checker.log](evidence/T08-R2/logs/privacy-checker.log)、[privacy-review.json](evidence/T08-R2/privacy-review/privacy-review.json) |
| 旧行为反例 harness | 退出码 0（演示用，非产品） | [old-behavior-run.log](evidence/T08-R2/logs/old-behavior-run.log) |

说明：按任务要求“只构建/测试相关工程，避免与其他队友同时全解决方案构建”，本轮**未**执行 `dotnet build NetScopeRepair.slnx` 和全部七个测试工程；当前工作树还含 T09-R2、T04-R2、T06-R3、T13 等队友的未提交改动，因此上述计数是本工作树的实际计数，不是历史 616/637。完整构建与全量测试应在 G1-R2 做。

## 6. 隐私独立复核证据

证据链在 [privacy-review 目录](evidence/T08-R2/privacy-review)：

1. [harness/Program.cs](evidence/T08-R2/harness/Program.cs)：只用**合成**数据构建两个会话——一个把 14 个合成哨兵注入全部分面，一个复现 102 快照形状（102 Partial / 0 probe）；调用生产导出器写出 ZIP 与 `expected.json`。
2. [check_privacy.py](evidence/T08-R2/privacy-review/check_privacy.py)：不 import 产品代码，只读 ZIP/infolist：
   - 逐文件原始文本 + JSON 字符串值扫描 14 个哨兵，两包均**0 命中**；
   - `checksums.sha256` 与 manifest `files[]` 的 SHA-256/大小逐项复算；
   - 旧 manifest 字段仍在；`redaction.salt_included=false` 且无 `salt` 成员；
   - 新分区齐全；策略 `m1-alias-v3`；build commit 形如 40 位 sha；timeline 的 source 只保留 `access_token=[redacted]`；
   - redaction-report 类别含 user/device/path/private_host/public_target/private_ip/proxy_credential/authorization/cookie/token/password/query_secret；
   - `evidence_metadata.source_event_ids` 全部能解析到现有事件；
   - 导出接口名 `device-XX` 与错误文本中的同名别名一致；
   - 102 包：108 条目、102 snapshot 文件、`counts.gaps=0`、`gap_events=0`、`observation_gap_events=0`、`partial_events=102`、`incomplete_snapshots=102`、`collection_complete=false`、interfaces partial=102、`proxy.winhttp` unsupported=102、`probe_events=0`、记录诊断 0/重算 0、变化 5 条、`gaps=0` 不被当作完整。
3. [expected.json](evidence/T08-R2/privacy-review/expected.json) 与 [evidence-summary.txt](evidence/T08-R2/logs/evidence-summary.txt) 记录两个包的 manifest 摘要（版本、计数、警告）；包本身也保留在本地证据目录（合成、已脱敏），**真实源包未读取、未加入、未上传**。
4. 独立复核的边界：证明的是列出的合成哨兵不在导出文件中，不等于任意未知字符串都无敏感性；别名是假名，不是数学匿名；地址别名保类不保拓扑。

## 7. 102 快照形状实测报告

测试：`T08R2CompletenessTests.One_hundred_two_partial_snapshots_are_reported_separately_from_gaps_and_probes`、`The_102_snapshot_package_has_108_entries_verified_checksums_and_replays_per_cycle`、`Two_exports_of_the_102_snapshot_session_report_the_same_shape`；证据包为 `shape-102-package`。

| 项目 | 值 |
|---|---|
| 事件 / 快照 / 诊断 | 102 / 102 / 0 |
| 状态 | 102 Partial（interfaces 域 Partial；WinHTTP 子域 Unsupported） |
| 序列缺口 `counts.gaps` | 0（旧字段语义不变） |
| 独立 `gap_events` | 0 |
| `observation_gap_events` | 0（Partial 不是观察缺口） |
| `incomplete_snapshots` / `collection_complete` | 102 / false |
| `domain_status_counts.interfaces.partial` | 102 |
| `proxy_subdomain_status_counts["proxy.winhttp"].unsupported` | 102 |
| probe 覆盖 | 0 事件、`has_probe_results=false` |
| 诊断来源 | 记录 0、导出时重算 0 |
| 变化摘要 | 101 个可比相邻对、5 条变化（metric 0→4225、automatic_metric、disable_default_routes、接口消失/重现），`truncated=false` |
| 包结构 | 108 条目（manifest + 106 payload + checksums）；107 行校验和；逐文件 SHA-256 通过 |
| 回放 | 102 个 snapshot scope，逐周期 0 probe/record/diagnosis，`IsClean`，`Evidence` 为最新周期 |
| 警告 | 3 条：无 probe、102/102 域不完整（明确 gaps/gap_events 不是完整性）、无诊断 |
| 版本 | app `1.0.0.0`，commit `a4b07a0…`（assembly informational version），策略 `m1-alias-v3` |

结论：`gaps=0` 只表示没有独立 gap 记录；manifest/预览用域与子域状态计数和 `collection_complete=false` 明确说明该包不是完整采集，也不是网络健康证明。

## 8. 只读/权限/隐私边界检查

- 产品改动只在导出路径：读取一个本地 NDJSON、内存中构建 ZIP、写本地文件；无网络客户端、无导入执行入口、无系统写入、无启动/停止客户端、无提权。
- 本任务未读取真实客户端配置、订阅、修复 journal；未运行旧修复脚本。
- 证据 harness 只生成合成会话/包并写入本地被忽略的 `docs/reports/evidence/`；未上传、未提交、未推送。
- 真实源包 `incident-9be62cef1dd31253c9d4e2e4.zip` 未打开、未解压、未加入 fixture；102 形状只按施工单文字描述构造合成数据，身份、地址、值全部自造。
- 包内仍保留（按原设计）：假名别名、`interface_guid`、if_index、状态/枚举、地址类别与端口；不保留 user/device 名、路径、凭据、订阅 URL、URL query/业务路径。别名和 salt 不随包输出，salt 只在内存。
- 地址别名是保类占位符，不是一一映射，不能重建任意路由前缀关系；导出包不能宣称拓扑完全保真。

## 9. 已知限制与未实现项

- **名称识别是结构化启发式**：若某个自定义适配器名既不在 `interfaces.value`/已知字段中，又在错误文本里既不加引号、也不跟在 `adapter/interface/switch/filter` 等关键词后、也不属于已知组合模式，则无法识别。这是“优先结构化”而不是“任意字符串全部脱敏”的边界；不宣称数学匿名。
- **变化摘要范围有限**：只覆盖 interfaces 域的参数（metric/automatic_metric/disable_default_routes/operational_status/kind/地址数量/在观测中出现或消失），不做 routes/DNS/proxy 差异；最多列 20 条并截断；不推断原因、操作者或因果。
- **`collection_complete` 只表示读取完整性与独立 gap 事件**，不代表网络健康；0 probe 明确是未验证。
- **build commit 是构建时程序集内嵌的源码修订**，不是当前 checkout；本次工作树含其他队友未提交改动，commit 本身不能唯一标识实际源（G1-R2 提交后才会与提交对应）。
- **解析器版本取决于生产者写入 `parser_version` 事实**：有该事实时进入 `versions.parser_versions`（测试/证据里已覆盖），没有时为空数组而不是猜测。
- **未做全解决方案构建/全量七工程测试**（任务要求避免与队友同时全解决方案构建）；G1-R2 必须重新构建和全量测试。
- 未实现导入执行、离线规则重算（manifest 明确 `recomputed_by_exporter=0`）、CaseReplay（原 T11/T12 范围）。
- 桌面预览只做数据绑定与回退，未做鼠标交互/截图验收（本任务不改视觉系统；G1-R2 复核页面）。

## 10. 设计/ADR 偏差

- 脱敏策略从 `m1-alias-v2` 升到 `m1-alias-v3`：新增身份预收集、错误文本结构化名称和 `missing_evidence` 文本脱敏；包格式仍 `1`，文件布局/校验和/引用语义不变，旧包仍可读。
- `missing_evidence` 从 protected key 列表移除：它表达“缺什么证据”的标签文本，不是证据 id 引用；移除后不改变编解码、引用校验或回放，只让它也过滤已知身份。
- 预览详情放在 Storage 的 `IExportPackageDetailsProvider`（`ExportPackageDetails`），没有扩展 `Application.ExportPreview`：Application 不在本任务写范围，旧接口/旧消费者保持可用，VM 对不实现该接口的 exporter 回退。
- `change_summary` 的“身份安全差异”是本任务新增的 manifest 语义，未经原设计逐字规定；只输出非身份字段并注明不是因果。

## 11. 是否有真实网络操作

**无。** 本任务所有命令均为本地构建、测试、ZIP 读写与本地证据生成；未发起外部请求、未连接代理/目标、未切换 TUN、未读取或修改真实客户端配置、未改系统网络设置、未提权、未上传任何文件。

## 12. 建议下一任务（不自动执行）

按施工单顺序，T08-R2 之后是 **G1-R2 复核停点**：独立审查本轮 diff、反例与命令结果，在最新 HEAD 上重跑完整 build/test、七页与关闭/托盘/退出、demo 隔离、三周期采样与诊断导出回放。复核通过前不进入 T10，也不建议以本报告计数替代 G1-R2 的全量结果。本轮完成即停，不创建提交、不推送、不领取下一任务。
