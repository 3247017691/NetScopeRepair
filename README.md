# NetScope Repair

Windows 代理网络的**诊断与受控修复**工具。始终作为 Nice加速、Apex、mihomo/Clash 等客户端的
伴随工具存在：不承载流量、不内置代理内核或 TUN 驱动、不替代现有客户端。

## 当前状态（2026-11-15 更新）

**统一修复引擎已落地：诊断 → 体检 → 受控修复 → 修后自检 → 失败进日志 → 一键交给 AI。**
旧的 TUN 三字段深度修复链路（NetScope.Repair / NetScope.Broker / 8 层 fail-closed 闸门）已整体删除，
其能力以真实可执行、可回滚的形式并入统一修复引擎；2026-11-06 G3 独立复核的历史结论保留存档（见下方链接）。
桌面脚本不再被依赖，软件零脚本依赖。

能力清单（与代码一致）：

- **本机真实体检**：网卡 / 默认路由 / 连通性 / DNS / 代理分层配置 / 端口存活 / 幽灵网卡 / TUN 三字段 / 端口归属
- **死代理清理**：配置指向没有监听的端口时，清掉或对齐环境变量、关闭指向死端口的系统代理、重置/对齐 WinHTTP
- **端口对齐**：开代理时把系统代理与环境变量对齐到**实测可用**的监听端口，不猜端口
- **旧出口自愈**：把「无 Internet 却仍持有默认路由」的旧物理接口度量调高；不禁用网卡、不删路由、不动唯一出口
- **TUN 三字段修复**：接口度量 → 1、自动度量 → Disabled、IgnoreDefaultRoutes → Disabled；只动 TUN 网卡，需管理员
- **端口归属点名**：候选代理端口被非代理进程占用、或多个客户端同时监听时，点名进程与 PID；只报告，不结束进程
- **深度清理（高风险模式 / `--deep`）**：移除幽灵网卡、重置旧接口静态 DNS（默认不选中）
- **代理开关**：`proxy on|off`，off 即纯直连，国内软件不需要开代理
- **修后自检 A~E**：执行后实测默认路由 / 物理出口 / DNS / 直连 / 经代理，最多 3 轮；
  只有「路由仍未走 TUN」（原因 A/B）会自动还原 TUN 度量，其余原因如实报告不自动乱动
- **失败事件进日志**：repair_applied / action_failed / postcheck_failed / user_unresolved 全部进事件日志
  （桌面进观察会话时间线，CLI 进 `maintenance\repair-events.ndjson`）
- **AI 交接包**：修复没好时一键生成脱敏失败案例包（case + context + developer guide）并复制提示词到剪贴板，
  粘贴给 Qoder 等 AI 助手即可按指南闭环新增修复能力
- **全部改动可回滚**：每次写入记入 journal，`repair --restore` / 页面「回滚全部改动」一键还原到第一次执行前（含 TUN 三字段）

```text
dotnet build NetScopeRepair.slnx  ->  0 警告 0 错误
8 个测试工程                      ->  919 通过 / 0 失败 / 0 跳过
dotnet publish -c Release         ->  artifacts/M4-UnifiedRepair/（可直接双击）
```

代理写入闭环（`proxy off` → `proxy on` → `repair --restore`）已在 M3 实机验证 0 失败；
TUN 三字段修复、修后自检与 AI 交接闭环的实机验收正在与用户配合推进（需管理员与客户端配合）。

真实采集（`WindowsSnapshotCollector`）、诊断规则、案例回放、脱敏导出一直读的是本机真实数据；
只有 CLI `demo` 与「载入合成演示」按钮是明确标注为 mock 的合成数据。

### 历史报告

- [G3 门禁处置记录（外部复核转录：不通过，20 条缺陷 / 5 项缺口；旧链路已删除，此为历史存档）](docs/reports/G3-final-review.md)
- [T09-R2 客户端身份、监听归属、TUN 候选与配置 Gap](docs/reports/T09-R2-report.md)
- [T04-R2 真实 DNS/连接探测事实](docs/reports/T04-R2-report.md)
- [T06-R3 适配器证据接入真实诊断链](docs/reports/T06-R3-report.md)
- [T07-R2 变化、缺口与可比性 UI](docs/reports/T07-R2-report.md)
- [T08-R2 导出脱敏、统计与版本溯源](docs/reports/T08-R2-report.md)
- [T10 观察会话与故障捕获](docs/reports/T10-report.md)
- [T11 配额与崩溃恢复](docs/reports/T11-report.md)
- [T12 案例重放工作台](docs/reports/T12-report.md)
- [T13 修复计划与平台前置事实（历史）](docs/reports/T13-report.md)
- [T14 Broker 协议、身份与白名单（历史）](docs/reports/T14-report.md)
- [T15 事务 journal 与精确恢复（历史）](docs/reports/T15-report.md)
- [T16 修复计划、确认与恢复 UI（历史）](docs/reports/T16-report.md)
- [G1-R1 历史复核与 TUN 功能对账](docs/reports/G1-R1-M1-review.md)
- [M1-R1 最终核验汇总](docs/reports/evidence/M1-R1-20261105/verification-summary.json)
- [M1 / T07—T09 最初交付记录](docs/reports/M1-T07-T09-report.md)
- [G1 M1 门禁评审](docs/reports/G1-M1-review.md)
- [M1 最终核验汇总](docs/reports/evidence/M1-20261105/verification-summary.json)
- [G0-R1 补修门禁复审（通过 M0 范围）](docs/reports/G0-R1-M0-review.md)
- [M0-R2 交付与 18 项关闭对账](docs/reports/M0-R2-report.md)
- [最终测试、CLI、native 读回核验](docs/reports/evidence/M0-R2-20261104/verification-summary-v2.json)
- [G0 首轮正式复核（历史不通过）](docs/reports/G0-M0-review.md)
- [G0 缺陷清单与补修条件](docs/reports/G0-M0-findings.md)
- [G0 实际反例结果](docs/reports/evidence/G0-20261104/repro-results-final.json)
- [T02—T06 施工报告](docs/reports/M0-T02-T06-report.md)
- [T01-R1 契约修复报告](docs/reports/T01R1-report.md)
- [T01 最小工程与核心契约](docs/reports/T01-report.md)
- [T00 环境核对](docs/reports/T00-environment-report.md)

## 使用

### 桌面

双击 [运行 NetScope](运行NetScope.cmd)，或直接打开 [发布版程序](artifacts/M4-UnifiedRepair/NetScope.Desktop.exe)。
旧入口 [运行 NetScope 只读试用版](运行NetScope只读试用版.cmd) 已改为转发到新版本。依赖本机 .NET 10 Desktop Runtime。

开发运行：

```text
dotnet run --project src/NetScope.Desktop
```

默认自动开始**本机只读快照**（不发起外部探测）。关闭窗口隐藏到托盘继续观察，显式「退出」才停止采集。
页面共 9 个：首页、时间线、诊断与证据、案例回放、**修复**、多客户端、导出、设置、帮助。

**「修复」页是唯一的真实执行入口**：点「体检本机（只读）」读取真实状态 → 勾选动作 → 点「执行所选动作」→ 再点一次确认才写入。
同页还有「开代理」「关代理 · 纯直连」「回滚全部改动」「交给 AI」「还是没好」与「高风险模式」。
诊断页的「去修复」与首页入口会跳到本页并按诊断规则预选动作（如 DX-001 预选死代理清理）。
所有改动记入 `%LOCALAPPDATA%\NetScopeRepair\maintenance\journal.json`；修复事件进观察会话时间线。
需要管理员的动作（接口度量 / TUN 三字段 / WinHTTP / 机器级变量）在非管理员进程里会**如实跳过**，不会假装成功；
需要时请以管理员身份启动程序。

**修复没好时的 AI 闭环**：点「还是没好」记录 user_unresolved → 点「交给 AI」生成脱敏交接包
（`%LOCALAPPDATA%\NetScopeRepair\handoff\handoff-*.zip`，含 case/context/developer guide）并复制提示词 →
粘贴给打开了本仓库的 AI 助手，按 guide 的四步（planner → executor → journal → 单测）新增或修正修复能力。

开发自检（不开窗口）：

```text
NetScope.Desktop.exe --render-screens <dir>   离屏渲染 9 个页面为 PNG
NetScope.Desktop.exe --smoke-test <file.json> 真实只读采集 3 次并尝试导出
```

### CLI

发布版 CLI：`artifacts/M4-UnifiedRepair/cli/netscope.exe`（开发期可用 `dotnet run --project src/NetScope.Cli -- <命令>`）。

```text
# 只读（永不修改系统）
netscope check [<file>]          校验契约与合成 fixture
netscope snapshot                只读采集一次本机网络状态
netscope demo [--out <dir>]      跑合成流水线，写本地演示日志
netscope replay [<file>]         离线回放已捕获会话
netscope case-replay <pkg.zip>   按固定规则只读重放故障包

# 真实维护（本机真实数据，写入能力 + 可回滚）
netscope status                  本机体检：死代理 / 残留接口 / TUN / 端口归属（只读）
netscope repair                  预览修复计划（等同 --dry-run）
netscope repair --apply --yes    真执行默认动作；加 --deep 含高风险项
netscope repair --restore        回滚到第一次执行前的状态（含 TUN 三字段）
netscope proxy status            查看代理配置与端口存活（只读）
netscope proxy on|off            开/关代理；off = 纯直连
netscope handoff [--case <id>] [--out <dir>]  生成 AI 失败案例交接包并打印提示词
netscope help                    命令列表
```

只有 `--apply` 与 `proxy on|off` 会写系统；`repair` 不带参数、`status`、`proxy status` 都是只读预览。
`demo` 默认写入 `%LOCALAPPDATA%\NetScopeRepair\demo`，可用 `--out` 指定新目录；已有日志或索引时拒绝覆写。
`check <file>` 保留真实/合成标记，不强制把会话改为合成；默认 fixture 仍要求 synthetic=true。
`--deep` 之外的高风险动作默认不选中，`--apply` 未带 `--yes` 时还会交互确认一次。
`handoff` 默认读取 `%LOCALAPPDATA%\NetScopeRepair\maintenance\repair-events.ndjson` 的最近失败批次。

## 目录

```text
contracts/                     事件封套 JSON Schema（与设计包同步）
fixtures/synthetic/            合成故障事件，全部 synthetic=true
src/NetScope.Domain/           契约层：强类型封套、严格序列化、枚举线名、契约校验
src/NetScope.Application/      采集抽象、结果建模、适配/导出端口、证据/incident 连接层
src/NetScope.Collectors.Windows/ 网卡/路由/DNS/代理分层的只读采集
src/NetScope.Probes/           三路径探测（可注入解析/连接观测与时钟）
src/NetScope.Storage/          NDJSON 事件日志、SQLite 投影、配额/恢复、离线回放、脱敏导出、AI 交接包
src/NetScope.Diagnostics/      DX-001/002/005/006/008/010/011
src/NetScope.Adapters/         Nice/Apex/generic 只读适配、证据投影与依赖解释
src/NetScope.Maintenance/      统一修复引擎：探测 / 计划 / 执行 / 修后自检 A~E / journal 回滚 / 代理开关 / 修复事件
src/NetScope.Desktop/          WPF 界面、托盘、观察会话、案例回放 + 修复页
src/NetScope.Cli/              check / snapshot / demo / replay / case-replay + status / repair / proxy / handoff
tests/Contract · Unit · Windows · Replay · Adapters · Export · Desktop · Maintenance
docs/adr/                      架构决策记录
docs/reports/                  任务与门禁报告
```

## 设计要点

- **只有 `status=ok` 算成功。** `partial`、`timeout`、`failed`、`cancelled`、`unsupported`、
  `access_denied`、`unknown` 都不能被当作"空结果即正常"。
- **读不到就说读不到。** 每个数据域都有状态和原因，不会退化成空列表；被跳过的过滤器驱动伪接口
  会说明数量与原因。
- **结论必须引用证据。** 规则不引用 `evidence_id` 会直接失败；反证和缺失证据是必填表达。
- **规则不产出动作。** 诊断规则不携带命令、脚本或特权动作；诊断到修复的跳转只是 UI 层映射
  （DiagnosisRepairMap）。统一修复引擎使用 typed action + journal 边界，只在用户勾选并二次确认后执行。
- **修后自检是实测不是声明。** A~E 原因分类基于真实观测；只有 A/B（路由仍未走 TUN）触发自动还原，
  其余失败如实进日志，交给用户或 AI 处理。
- **`no-explicit-proxy` 不证明物理直连。** TUN 开启时该请求仍可能经过 TUN。
- **保留段不等于污染，也不独自证明 fake-IP 模式。** 需要实际 DNS 返回与同作用域的可信运行态配置/适配器证据；只有路由或 DNS-server 线索时保持候选。
- **观察缺口与网络失败分开。** 用户取消、权限不足、能力缺失和本地执行错误不计有效网络失败；同作用域缺口仍保留计数、原因和引用。
- **未知事实保持未知。** scalar 接口事实带地址族，不借用另一族的标志；metric/TotalMetric 缺失为 null，部分读取失败传播 Partial。
- **合成的就标合成。** 演示数据在横幅、首页状态、时间线与诊断页都明确标为 mock，不冒充本机观测。
- **导出先预览、默认最小化。** 本地 ZIP 用包内稳定别名替换用户/设备/路径/内网地址，salt 不随包输出；D3 不采集也不导出。AI 交接包同样先脱敏。

## 边界

- 诊断默认只读，不需要管理员权限。修复的写入只在**用户勾选动作 + 二次确认**之后发生。
- **不会自动申请 UAC**：程序不自行提权。接口度量 / TUN 三字段 / WinHTTP / 机器级变量在非管理员进程里如实跳过并报告，不假装成功；需要时由用户自行以管理员启动。
- **TUN 修复只动 TUN 网卡**：不碰物理网卡度量、不禁用物理网卡、不删除路由表条目、不动 VMware / 系统虚拟适配器。
  写入走 ActiveStore，客户端重开 TUN 或重启后可能被重置——这是 Windows 的行为，软件如实说明。
- **零脚本依赖**：旧桌面 TUN 修复脚本已删除，全部能力内置；不注册服务 / 计划任务 / 开机启动项，不重启客户端或内核。
- 修复确实会写用户级代理环境变量、HKCU 系统代理开关、TUN 三字段与（高风险模式下）幽灵设备/静态 DNS —— 这正是它要解决的问题；每次改动都进 journal，`repair --restore` 可回到第一次执行前。
- 端口冲突只点名不杀进程；多 TUN 只报告建议，不替用户关客户端。
- 手动启动，显式退出即停止记录；不上传日志，AI 交接包只生成本地文件，是否交给 AI 由用户决定。

## 后续

- **内置 AI 助手是下一里程碑**：当前 AI 介入通过「交接包 + 剪贴板提示词」在外部助手（Qoder 等）中闭环；
  后续计划在软件内直接接入 AI 诊断与修复建议。
- TUN 三字段修复与 AI 交接闭环的实机验收（管理员执行、真实失败案例、非管理员跳过）正在与用户配合推进。
- 案例回放：已支持本地不可信包的固定规则只读重放；真实断网 VM 实验（QA-015）仍待授权。
- 历史能力与限制见 [G3 复核报告（历史存档）](docs/reports/G3-final-review.md) 与各 T*-report；M0/M1 历史见 [G1-R1 报告](docs/reports/G1-R1-M1-review.md)、[G0-R1 报告](docs/reports/G0-R1-M0-review.md)。