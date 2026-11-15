# 统一修复引擎与 AI 接力闭环 · 设计文档

日期：2026-11-15
状态：待用户审阅
来源：桌面 5 个已验证脚本（fix-network-stale / fix-proxy-port / fix-tun-priority / proxy-switch / net-stale-lib）的软件化

## 1. 背景与用户决策

仓库现状：今天新增的 M3「快速修复」（NetScope.Maintenance，未提交）已覆盖桌面脚本约七成能力；
旧 TUN 三字段深度修复链路（NetScope.Repair / NetScope.Broker）经 G3 复核不通过，被 8 层 fail-closed 闸门关闭。

用户在需求调研中明确的决策：

1. **统一重写**：旧链路整体删除，Maintenance 成为唯一修复引擎。原话：「旧链路有点跑偏了，
   一个项目最重要的是将功能跑通，而不是不断的防御性编程」。
2. **TUN 修复以桌面脚本版为准**进 app，不补修旧链路。
3. **AI 介入 = 一键交接包**：app 不内置 AI、不联网调模型；失败案例打包交给 Qoder 等 AI 助手改代码、加新修复动作。
4. **修复深度原生融入软件**：零 .ps1 依赖，不携带、不调用外部脚本文件；所有判断逻辑为 C# 原生实现。
   netsh / ipconfig / pnputil / PowerShell cmdlet 是 Windows 系统接口，不属于「外部脚本」。

## 2. 目标与非目标

### 目标

- 桌面 4 个功能入口（网络残留 / 代理端口 / TUN 优先级 / 代理开关）的能力全部原生进 app，一个不少。
- 三类「修复不成功」（动作执行失败 / 修后自检不通过 / 用户手动标记）的原因全部进现有日志管线。
- 失败案例一键生成「AI 交接包」+ 剪贴板 prompt，喂给 AI 即可继续开发新修复能力。
- 修复成为软件主导航一等公民，与诊断、首页、时间线联动。

### 非目标（本轮不做）

- 内置 AI 助手 / 运行时调用大模型（后续里程碑再单独立项）。
- 自动申请 UAC、杀进程、自动切换 TUN、重启客户端/内核、注册服务/计划任务/开机启动。
- 保留旧链路的任何部分「以防万一」——直接删除，靠基线 commit 兜底。
- 不改事件契约：`repair_event` 是通用事件类型，新引擎复用，schema 不变，旧日志与回放不受影响。

## 3. 删除范围（旧 TUN 链路，已逐点核实引用）

| 位置 | 内容 |
|---|---|
| 整个工程 | `src/NetScope.Repair`（17 个源文件）、`src/NetScope.Broker`（11 个源文件）；slnx 移除两项 |
| Application | `Repairing.cs`、`RepairVerification.cs`、`SyntheticRepairPreview.cs`；csproj 移除 Repair 引用 |
| Desktop | `Views/RepairView.xaml(.cs)`、`ViewModels/RepairViewModel.cs`；`Composition.cs`、`MainViewModel.cs`、`App.xaml`、Shell 导航同步清理 |
| 测试 | `tests/WindowsIntegration` 中约 21 个旧链路测试文件（Broker*/Repair*/T15*/PlatformCapability*）；`tests/Desktop/T16RepairVerificationTests.cs` |
| 文档 | README 重写相关章节；G3 等历史报告保留不动（历史记录） |

引用核实结论：旧 Application 类型仅被 RepairViewModel / Composition 使用；CLI 与 IncidentExporter
均不依赖旧链路；Domain 仅含通用 `repair_event` 枚举，保留。

**保护**：实施第 0 步，经用户确认后先把当前未提交的 M3 现状提交为基线 commit，再动手删除。

## 4. 统一修复引擎（NetScope.Maintenance）

流水线：`体检 Probe → 计划 Planner(纯逻辑) → 执行 Executor(+journal) → 修后自检 PostCheck`。
权限模型沿用脚本语义：不自动弹 UAC；需管理员的动作在非管理员进程里如实跳过并报告。

### 4.1 已有能力（M3，保留）

死代理清理（ClearProxyEnv/AlignProxyEnv）、系统代理关闭（DisableSystemProxy）、WinHTTP 对齐/重置、
旧出口度量调高（RaiseInterfaceMetric）、静态 DNS 重置与幽灵网卡移除（--deep）、代理开关
（proxy on/off/status）、journal 一键回滚（repair --restore）。

### 4.2 TUN 优先级修复（新增，脚本 fix-tun-priority v3 语义）

- **体检补采集**：当前 `WindowsMaintenanceProbe.BuildInterfaces` 只保留物理网卡（`IsPhysical` 过滤），
  TUN 网卡不在快照里。新增 TUN 接口事实采集：识别依据 = 网卡名 / 198.18.x.0/15 地址 / Wintun 描述，
  记录 ifIndex、度量、AutomaticMetric、IgnoreDefaultRoutes、MTU、所属客户端进程。
  TUN 事实独立于物理出口逻辑，不参与「旧出口度量调高」候选。
- **新 finding `TUN_PRIORITY`**：TUN 存在且（自动度量开启 或 度量>1 或 IgnoreDefaultRoutes=Enabled）时产出，
  证据列三字段现值。度量已 ≤1 且自动度量关闭且 IgnoreDefaultRoutes 关闭时不产出（客户端自己设好了）。
- **新动作 `FixTunPriority`**（单动作三字段）：设度量=1 + 关自动度量 + IgnoreDefaultRoutes=disabled，
  仅 ActiveStore。**只动 TUN 网卡，绝不碰物理网卡**。风险 Elevated（需管理员），默认选中，执行前二次确认。
  journal 记录三字段原值，回滚一次性还原。
- TUN 未开启时如实报告「TUN 未开启」，不做脚本的 300 秒阻塞等待（app 常驻，开好客户端重新体检即可）。
- 修后自检与回滚语义见 4.4。

### 4.3 端口归属点名（新增，脚本 fix-proxy-port 语义）

- 体检对每个候选代理端口查监听进程（Get-NetTCPConnection → OwningProcess → 进程名/PID）。
- 新 finding `PORT_CONFLICT`：同一端口被两个代理客户端抢占、或端口被非代理进程占用时**点名报告，只报告不动作**
  （不杀进程）。
- 采集到的归属事实以 `port_ownership` 证据种类进证据管线，DX-005（端口归属冲突规则）可直接消费——
  诊断与修复共用同一份事实。

### 4.4 修后自检 PostCheck（新增，脚本语义已逐行核实）

执行完成后自动复测四项：

1. TUN 场景：默认路由是否走 TUN（非 TUN 场景：默认路由是否走有 Internet 的出口）；
2. DNS 解析（系统默认 DNS 解析国内域名）；
3. 直连连通（不走系统/环境变量代理，国内站；TUN 开启时仍可能经 TUN，如实标注）；
4. 经代理连通（仅代理端口活着时测）。

全失败自动重试，共 3 次（脚本 Retries=3 语义）。结果按脚本 A~E 分类：

| 类 | 含义 | 失败时行为 |
|---|---|---|
| A/B | 度量没生效：默认路由仍未走 TUN | **自动回滚 TUN 度量到修前值**（改了也没用，恢复原状），结论引导「客户端关开 TUN / 打开 auto-route 后重新体检」 |
| C | 本机无可用上网出口 | 不回滚，报告原因 |
| D | 代理内核出口不通（节点/订阅/绑定失效） | 不回滚，报告原因 |
| E | 分流 / strict-route / MTU 导致部分站点不通 | 不回滚，报告原因 |

PostCheck 结果进 RepairReport 与失败日志（第 5 节）。

### 4.5 体检与判定补项（新增）

- **真打判定对齐脚本**：候选端口经代理请求 `https://www.gstatic.com/generate_204` 返回 200/204 才算
  「可用代理端口」（当前 app 任何 HTTP 响应都算，偏松）；TCP 监听检测仍用于「死端口」判定。
- **ARP 邻居缓存刷新**：FlushDnsCache 动作补 ARP 清理（当前只刷 DNS）。
- **PAC 检测（只报告）**：读 AutoConfigURL，存在时报告其地址并提示「PAC 会覆盖 ProxyServer，
  请到 Internet 选项核对」——与脚本一致，不擅自清空。
- **内核出站源地址**、**客户端配置写死 interface-name/device**：只读体检报告项。

## 5. 失败日志数据流（进现有管线）

三类「修复不成功」全部作为 `repair_event` 进现有 NDJSON 事件日志 + SQLite 投影：

| payload kind | 触发 | 内容 |
|---|---|---|
| `action_failed` | 动作执行失败（权限不足/命令报错/跳过） | 动作 id/kind、错误消息、是否权限原因 |
| `postcheck_failed` | 修后自检不通过 | A~E 原因分类、四项自检结果、修前/修后快照对比、是否已自动回滚 |
| `user_unresolved` | 用户点「还是没好」 | 用户标记时间、关联的最近修复批次 |

- 每个事件携带证据引用（evidence_id）与修复批次 id，可回放、可导出。
- `QuickRepairService` 新增可选事件发射端口（如 `IRepairEventSink`），Desktop/CLI 组合根接到现有
  Storage writer；不传则保持纯本地行为（单测友好）。
- 修复事件与体检、诊断在同一条时间线上。

## 6. AI 交接包（新子系统）

### 触发

- 修复失败后修复页出现「交给 AI」按钮；时间线/诊断页可对任意历史失败案例导出。
- CLI：`netscope handoff [--case <id>] [--out <dir>]`（默认取最近失败案例）。

### 包内容（zip）

```
handoff-<case>.zip
├─ case/        失败事件(payload 全量) + 修前/修后快照对比 + 相关诊断结论 + 证据引用
├─ context/     Windows 版本、是否管理员、代理客户端运行态、app 版本、能力清单
└─ developer/   加新修复动作的四步指南(planner 规则→executor→journal 回滚→单测)、
                构建/测试命令、仓库路径、关键文件索引
```

- **脱敏复用现有 IncidentExporter 管线**：用户/设备/路径/内网地址 → 包内稳定别名，salt 不随包输出；
  别名稳定性保证 AI 可跨文件推理。
- **一键动作**：生成 zip + 复制 prompt 到剪贴板（含包绝对路径与任务说明：「读这个失败案例，诊断根因，
  给 NetScopeRepair 增加新修复能力，遵循 developer/ 指南，补测试」）。用户粘贴到 Qoder 即可。
- 导出器位置：`NetScope.Storage/Export/HandoffExporter.cs`，与 IncidentExporter 并列复用脱敏组件。
- 交接包生成失败（磁盘/权限）只如实报错，不影响修复主流程。

## 7. 深度融合（修复是一等公民）

1. **导航**：旧 Disabled「修复」页删除；「快速修复」页升级为唯一「修复」主页。
2. **诊断 → 修复联动**：诊断结论卡片加「去修复」按钮，跳修复页并预选对应动作。
   映射表在 UI/服务层（如 DX-001 死代理 → ClearProxyEnv/AlignProxyEnv；DX-006 残留配置 →
   RaiseInterfaceMetric/ResetInterfaceDns；DX-005 端口冲突 → 只报告无动作）。
   **诊断规则本身仍不携带命令/动作**（保留现有边界），映射只是跳转与预选。
3. **首页**：显示「发现 N 个可修复问题」入口，点击进修复页。
4. **时间线**：修复事件与体检/诊断同线（第 5 节）。
5. **证据互通**：端口归属等修复体检事实进证据管线供诊断规则消费（4.3）。

## 8. 错误处理原则（沿用，不新增防御层）

- 只有 `status=ok` 算成功；partial/timeout/access_denied 等如实记录，不当「空结果即正常」。
- 读不到就说读不到；动作失败不假装成功；非管理员跳过并报告。
- 不新增 fail-closed 闸门、签名 broker、TTL 授权等旧链路机制。

## 9. 测试与验收

- **单测**：planner 新规则（TUN_PRIORITY/PORT_CONFLICT/PAC）、FixTunPriority 计划与 journal 往返、
  PostCheck A~E 分类与「仅路由未走 TUN 才回滚」、真打判定、HandoffExporter 内容与脱敏别名稳定性。
  planner 纯逻辑无 I/O；executor 用 fake runner；PostCheck 探测可注入。
- **回归**：删除旧链路测试后整套件绿、构建 0 警告 0 错误（仓库现有标准）。
- **实机验收（需用户配合）**：
  1. 开客户端 TUN → app 体检 → 执行 FixTunPriority → PostCheck 通过；`repair --restore` 能还原。
  2. 用一次真实失败案例生成 AI 交接包 → 喂给 Qoder → AI 按 developer 指南加出新修复动作并通过测试，
     「失败 → AI → 新版」闭环走通。

## 10. 实施顺序

0. 经用户确认，提交 M3 现状为基线 commit（删除的保护网）。
1. 删除旧链路（工程/Application/Desktop/测试/slnx），套件恢复绿。
2. Maintenance 补齐：TUN 采集 + FixTunPriority、端口归属、PostCheck、真打判定、ARP/PAC 等补项。
3. 失败事件进现有日志管线（三类 payload）。
4. AI 交接包导出器 + 一键 prompt + CLI handoff + 修复页「交给 AI」「还是没好」。
5. 深度融合：导航合并、诊断联动、首页入口。
6. 实机验收两条 + README 重写。

## 11. 风险与权衡

- **删除不可逆** → 基线 commit 兜底；历史报告保留。
- **TUN 度量 ActiveStore 重启/重开 TUN 后重置** → 脚本同款语义，界面如实告知「届时重新体检修复即可」。
- **真打探测依赖外网（gstatic）** → 探测失败如实记录为「无法判定」，不误判为死端口；监听检测兜底。
- **脱敏 vs AI 诊断需要真实细节** → 稳定别名保持跨文件可推理；结构、端口、错误码、时序全部保留。
- **两个客户端抢端口** → 只点名不动手，由用户决定关哪个。