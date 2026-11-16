# 桌面客户端重构设计规格 · 「信号仪」

日期：2026-11-16
状态：待用户审阅（审阅通过后才动 WPF 代码）
视觉基准（mockup）：`.superpowers/brainstorm/1636-1794801396/content/signal-instrument-final.html`（静态）与 `signal-instrument-motion.html`（动效）

## 1. 背景与目标

NetScope Repair 是 Windows 代理网络的诊断与受控修复工具（Nice/Apex/mihomo/Clash 的伴随助手）。当前桌面有 9 个等权页面，修复只是第 5 页，修复页是「8 按钮一排 + 多堆卡片」的控制面板。

本次重构的目标：**修复是一等公民，其余是二等公民**，并建立一套独特、克制、贴合「网络诊断仪器」主题的视觉语言（不是通用 SaaS 后台，不是 Linear 克隆）。

目标用户（已确认）：懂一点网络的人——「先看清问题，再受控地修」。文案大白话但不幼稚；能力不隐藏，只降级。

## 2. 已锁定的决策（brainstorm 结论）

- 导航：**双层左栏**。顶部突出的「修复」主入口（带实时状态），分隔线 + 安静的「观察与工具」二级组，设置/帮助沉底。打开即修复。
- 修复页：**分阶段引导单页**。体检 → 看清问题 → 执行 → 自检；问题与修复配对；主按钮随阶段变化；次要动作降为安静工具条；只显示当前阶段相关内容。
- 视觉：**「信号仪」方向，白底**（参考 Qoder 浅色）。主角是实时网络路径信号迹线；IBM Plex 字体；单一信号青绿；状态色纯功能。
- Logo：**保留原样**（`Resources/netscope-logo.png` / `netscope.ico` 不动）。
- 修复导航图标：从「太阳/亮度」形改为**扳手**（明确表达修复，统一描边风格）。
- 动效：**克制、回应操作、一处大胆**（迹线）。跟随系统「显示动画」开关与 reduced-motion。

## 3. 范围与边界

**只改 `src/NetScope.Desktop`（UI 层）。** 以下一律不动：
`NetScope.Maintenance`（修复引擎）、`NetScope.Domain`（契约）、`NetScope.Collectors.Windows`、`NetScope.Probes`、`NetScope.Storage`、`NetScope.Diagnostics`、`NetScope.Adapters`、`NetScope.Cli`。

- 不新增修复能力；仪器是现有 `MaintenanceSnapshot` / `PostCheckResult` 的**只读投影**。
- 安全语义不变：勾选 + 二次确认才写入；journal 回滚；非管理员如实跳过；只动 TUN 网卡等边界全部保留。
- 进度流（`RepairProgress` 侧通道）契约不变，不写入 NDJSON/SQLite。
- 仅浅色，不做暗色模式 / 主题切换。

## 4. 设计系统

### 4.1 调色板 token（保持 key 稳定，替换 value → 二级页面自动继承）

`Resources/Theme.xaml` 的 Color/Brush key 全部保留，值替换为：

| Key | 旧值（Linear） | 新值（信号仪·白底） | 用途 |
|---|---|---|---|
| CanvasColor | #F7F9FC | **#FFFFFF** | 主内容底（白） |
| RailColor | #F7F9FC | **#F7F8FA** | 导航栏浅冷灰 |
| SurfaceColor | #FFFFFF | **#FFFFFF** | 卡片/面板 |
| SubtleSurfaceColor | #F0F3F6 | **#F8F9FB** | 凹槽/仪器面板/次级面 |
| HoverSurfaceColor | #EAEDF1 | **#F0F2F5** | hover 面 |
| LineColor | #E0E3E7 | **#E8EAED** | 发丝线 |
| LineSubtleColor | #E7EAED | **#EFF1F4** | 更浅内分隔 |
| InkColor | #12181E | **#1F2329** | 主文字 |
| MutedColor | #474E55 | **#6B7280** | 次要文字 |
| SubtleTextColor | #767B80 | **#9AA0A8** | 弱化文字 |
| AccentColor | #5E6AD2 | **#0E7C66** | 信号青绿（品牌/主按钮/迹线） |
| AccentInteractiveColor | #7170FF | **#0C6B58** | 交互/hover |
| AccentLightColor | #ECEFFF | **#E6F2EE** | 青绿浅底（选中） |
| AccentBorderColor | #CCD5FF | **#BFE0D6** | 青绿浅边 |
| AccentDeepColor | #3F46AC | **#0A5A4A** | pressed/深 |
| OkColor | #006B2E | **#177A4C** | 正常（AA 文字） |
| OkBgColor | #E0F7E4 | **#E7F6EE** | 正常浅底 |
| OkBorderColor | #B7E5BF | **#BFE6D2** | 正常浅边 |
| WarnColor | #865900 | **#B45309** | 警告（AA 文字） |
| WarnBgColor | #FFEDD6 | **#FBF0E2** | 警告浅底 |
| WarnBorderColor | #F7D19C | **#F0D3A8** | 警告浅边 |
| BadColor | #A5292B | **#C0392B** | 故障（AA 文字/迹线中断） |
| BadBgColor | #FFEBE9 | **#FBEAEA** | 故障浅底 |
| BadBorderColor | #FFC8C3 | **#F2C4C4** | 故障浅边 |
| GapColor | #474E55 | **#6B7280** | 中性=次要文字 |
| GapBgColor | #F0F3F6 | **#F8F9FB** | 中性浅底 |
| GapBorderColor | #E0E3E7 | **#E8EAED** | 中性浅边 |
| HoverBorderColor | #CACED3 | **#D3D8DE** | hover 边 |
| ScrollThumbHoverColor | #999FA6 | **#B6BCC4** | 滚动条 hover |

新增 token：
- `UnknownColor` **#C4C9D0**（迹线「未测得」站点/图形的中性灰，仅图形用，配文字标签）。
- `Recess` brush = SubtleSurface（仪器凹槽面板，语义别名，便于组件引用）。

说明：状态文字色取 ≥4.5:1（AA）值，比 mockup 图形色略深，保证小字可读；迹线健康段用信号青绿、中断用 Bad、未测得用 Unknown。`ViewModels/Rows.cs` 的 `UiText` 冻结画刷同步为新值（保持 `SoftBackground/SoftBorder` 的引用相等映射不变）。

### 4.2 字体

- UI/正文：**IBM Plex Sans**（工程感，区别于默认 Inter/Segoe）。
- 数据/读数：**IBM Plex Mono**（真实数据，正当使用，非装饰）。
- CJK 回退：**Microsoft YaHei UI**（Plex Sans 无中日韩字形）。
- `UiFont` = `pack://application:,,,/Resources/Fonts/#IBM Plex Sans, Microsoft YaHei UI`
- `MonoFont`（新增 key，替换现 Mono 样式里的 Cascadia）= `pack://application:,,,/Resources/Fonts/#IBM Plex Mono, Consolas`
- 内嵌权重：Plex Sans Regular/Medium/SemiBold/Bold；Plex Mono Regular/Medium。OFL 许可证随包（`Plex-LICENSE.txt`）。
- 替换现有 Inter 内嵌资源（移除 Inter-*.ttf 与 Inter-LICENSE）。Logo/ico 不动。
- WPF 限制：无原生 letter-spacing、无可变字重插值；用静态字重 + 字号表达层级（不做 510/590 插值）。

### 4.3 字阶

| 角色 | 字号 | 字重 | 字体 |
|---|---|---|---|
| 正文默认 | 13 | Regular | Plex Sans |
| H1 页标题 | 18 | SemiBold | Plex Sans |
| Verdict 诊断结论（修复主角） | 20 | SemiBold | Plex Sans |
| H2 段标题 | 13.5 | SemiBold | Plex Sans |
| Caption 说明 | 12 | Regular | Plex Sans |
| Overline 标签 | 11 | Medium | Plex Sans（仅真实标签，不做眉标滥用） |
| Mono 数据 | 12 | Regular | Plex Mono |
| Readout 仪器读数 | 26 | Medium | Plex Mono |

### 4.4 间距 / 圆角 / 描边

- 卡片圆角 8；仪器面板 14；按钮 8–9；步骤 chip 8；checkbox 6。
- 发丝线 1px；段落间距 12–18；主内容 padding 24–28。
- 扁平、靠亮度阶梯分层，不堆阴影；仪器面板用极浅凹槽 + 1px 边，轻 inset 高光。

## 5. Shell 与信息架构（双层左栏）

`Views/ShellView.xaml` + `ViewModels/MainViewModel.cs`：

- 左栏宽约 232（CJK 需要，比 mockup 212 略宽）。
- 品牌区：**原 Logo** + 「NetScope」+ 副标题「网络诊断 · 受控修复」。
- **主入口**：修复（panel 底 + 发丝边 + 扳手信号图标 + live 状态点，绑定会话/修复状态）。
- 分隔线 + 组标题「观察与工具」（普通小标签，非全大写眉标）。
- **二级组**（安静、muted）：诊断与证据 / 时间线 / 多客户端 / 导出 / 案例回放。
- **栏底**：设置 / 帮助（与二级组以分隔线区隔）。
- `NavSection` 增加 `Group`（Primary / Secondary / Footer）；`Sections` 重排为 8 项（移除「首页」导航项），默认 `SelectedSection = 修复`。
- **首页降级**：不再作为导航页。其观察会话控制（开始观察 / 暂停 / 立即诊断）上移到 Shell 顶栏（全局）；健康概览由修复仪器取代。`HomeViewModel` 保留作会话状态管线（不暴露为导航页），避免功能丢失。
- 顶栏简化：左=页标题+副标题；右=全局会话控制 + 状态 pill。「载入合成演示」移到更安静处（设置页或菜单），保留演示横幅与合成标记。
- 修复导航图标 geometry 换为扳手：`M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z`。

## 6. 修复页（一等公民）

`Views/QuickRepairView.xaml` + `ViewModels/QuickRepairViewModel.cs`。引擎不动，VM 增加只读派生状态与投影。

### 6.1 阶段流（映射现有 VM 状态）

新增只读 `RepairStage`（派生，不引入引擎状态）：
- **Inspect 体检**：`!HasPlan`。仪器读数「—」、迹线基线空；主按钮「体检本机（只读）」（`RefreshCommand`）。
- **Review 看清问题**：`HasPlan && RunState==Idle`。仪器由快照填充；verdict = 头号问题的大白话结论；问题↔修复列表；主按钮「执行所选修复」（`ApplyCommand`，二次确认保留）。
- **Execute 执行**：`RunState==Running`。步骤清单 + 实时日志 + 进度条 + 计时；主按钮 busy/disabled。
- **Verify 自检**：写入完成（`RunState` Success/Failed 且有 PostCheck）。展示 A~E 实测结果；未解决则「交给 AI / 还是没好」突出；成功则 ok verdict。

阶段 stepper：编号 chip（真实顺序，非装饰编号），当前高亮、完成打勾。仪器作为 spine 贯穿各阶段。

### 6.2 仪器组件（新增）

新增 `Controls/NetworkPathInstrument.xaml(.cs)` + `ViewModels/NetworkPathModel.cs`（只读投影）。

数据契约（来自 `MaintenanceSnapshot`，见 `MaintenanceModels.cs`）：
- **本机**：恒在（本地）。
- **代理端口**：配置端口 = `SystemProxyPort`（`SystemProxyEnabled` 时）否则取代理分层/env；存活端口 = `WorkingProxyPort`。配置端口存在但无监听 → 故障；配置==存活且非空 → 正常；未配置代理 → 未知。
- **出口**：`ActiveEgressIndex` / `ActiveEgressAlias`；index<=0 或 alias 空 → 未知。
- **互联网**：可达性来自 `PostCheckResult`（修复后自检）或连通性实测；未测得 → 未知「—」。
- 读数：代理端口值（或 —）、出口延迟 ms（或 —）。

未知态渲染：站点 dim（UnknownColor）+ 读数「—」+「未测得」标签；**绝不暗示健康**（贯彻项目「读不到就说读不到 / 未知保持未知」）。

迹线：健康段信号青绿脉冲；故障段 Bad 平线；故障下游 dim。WPF 用 `Path` geometry + Storyboard（draw-in / breathe），绑定 `NetworkPathModel`，受动画开关闸门。

### 6.3 问题↔修复配对

统一「问题 → 修复」列表（新增只读 `RepairItemRow` 投影，配对逻辑在 VM）：
- 每条 `Finding`（Code/Title/Detail/Severity）配对其映射的 `RepairAction`（按 Kind/Code，复用 `DiagnosisRepairMap`）。
- 行内：问题（Finding + Code 引用）→ 修复（Action.Title/Detail）+ checkbox（`Action.IsSelected`）+ before→after（`Action.Current/Desired`）+ 风险/需管理员 badge + journal 可回滚注记。
- 无配对 finding 的动作仍可选；开/关代理等放工具条。
- 空态诚实：「本次体检未发现可修复问题」，不伪装「已修复」。
- 保留 `Findings`/`Actions` 集合供既有逻辑；配对投影只读。

### 6.4 主按钮 + 次要工具条

- 主按钮随阶段：Inspect「体检本机（只读）」/ Review「执行所选修复」→「再次点击确认执行（N 项）」/ Execute busy / Verify「重新体检」或未解决时「交给 AI」。
- 次要工具条（安静 QuietButton）：开代理 / 关代理 / 回滚改动 / 交给 AI / 还是没好 / 高风险模式 toggle。从旧 8 按钮一排降级。
- **二次确认语义逐字保留**（`ApplyButtonText`/`RestoreButtonText` 的 pending 态）。

### 6.5 步骤 / 日志 / 进度

保留 `StepRow` 实时清单、`LogLines`、`LinearProgressBar`、计时（现有流式侧通道）。日志用 Expander：Execute 默认展开，其余默认折叠。浅色控制台。步骤逐条落定动画。

## 7. 动效（克制 · 回应操作 · 一处大胆）

WPF 用 Storyboard + easing（CubicEase/QuadraticEase）实现，时序对齐 mockup：

1. **迹线（大胆的一处）**：体检后迹线左→右 draw-in（~0.9s cubic-bezier(.2,.7,.3,1)），故障平线随后（~0.5s）；观察中健康段轻微呼吸 + 扫描点沿路径循环（~3.2s）。
2. **读数落定**：「—」→ 实测值淡入（~0.3s）；未测得保持「—」。
3. **阶段推进**：stepper 高亮过渡（~0.25s）；verdict 与问题↔修复行随测量结果 reveal（~0.4s，回应操作而非滚动）。
4. **微交互**：主按钮 hover 微抬+加深、press 回落；导航/次按钮 hover 底色；执行 spinner + 进度平滑填充 + 步骤落定。
5. **页面切换**：导航切换 ~140ms 轻交叉淡入。
6. **状态点**：修复入口 live 点观察中轻脉冲（~2.4s）。

**边界**：全部跟随系统「显示动画」开关（`SystemParameters.ClientAreaAnimation`）+ 应用设置；关闭时一切静止、无内容隐藏。无滚动淡入、无视差、无 scroll-jacking；循环动效可随开关停止。画刷 Freeze，避免布局抖动。

## 8. 二级页面继承规则

- 所有二级视图经 `Theme.xaml`（key 稳定）自动继承新 token。
- 清理视图内硬编码的旧靛蓝/旧灰值，改用 token。
- 安静样式：二级页**不放仪器主角**；统一卡片/按钮/字阶。
- 各页功能不变，只改视觉系统与层级。首页降级见 §5。

## 9. 字体内嵌与资源

`NetScope.Desktop.csproj`：
- 移除 `Inter-*.ttf` 资源与 `Inter-LICENSE.txt`。
- 新增 `Resource`：`Resources/Fonts/IBMPlexSans-{Regular,Medium,SemiBold,Bold}.ttf`、`IBMPlexMono-{Regular,Medium}.ttf`。
- 新增 `Content`：`Resources/Fonts/IBMPlex-LICENSE.txt`（OFL，Link=`Plex-LICENSE.txt`，CopyToOutputDirectory=PreserveNewest）。
- 保留 `netscope.ico` / `netscope-logo.png`。
- 字体来源：IBM Plex 官方 GitHub（OFL）。实现计划负责获取 TTF 并校验字形/字重解析（WPF 用 typographic family name）。

## 10. 文件改动清单

- `Resources/Theme.xaml`：token 值、UiFont/MonoFont、PrimaryButton（青绿）、新增仪器/Unknown/Recess、字阶。
- `ViewModels/Rows.cs`：`UiText` 冻结画刷同步。
- `ViewModels/MainViewModel.cs`：Sections 重排/分组、默认修复、顶栏会话控制、首页降级。
- `Views/ShellView.xaml`：双层左栏 + 简化顶栏。
- `Views/QuickRepairView.xaml`：阶段流 + 仪器 + 配对列表 + 工具条 + 步骤/日志。
- `ViewModels/QuickRepairViewModel.cs`：`RepairStage`、verdict、`NetworkPathModel` 投影、`RepairItemRow` 配对。
- 新增 `Controls/NetworkPathInstrument.xaml(.cs)`、`ViewModels/NetworkPathModel.cs`。
- `Views/{Timeline,Diagnoses,CaseReplay,Clients,Export,Settings,Help}View.xaml`：token 继承 + 硬编码清理 + 安静化。`HomeView` 内容并入顶栏/降级。
- `NetScope.Desktop.csproj` + `Resources/Fonts/*`：IBM Plex 内嵌。
- `Services/ScreenRenderer.cs`：适配 8 页渲染循环。
- `tests/NetScope.Desktop.Tests/*`：更新断言（旧色值/标签/页数）。

## 11. 测试与验证

- `dotnet build NetScopeRepair.slnx` → 0 警告 0 错误。
- `dotnet test NetScopeRepair.slnx` → 全绿（更新 Desktop UI 断言；引擎测试不变，维持现有通过数）。
- 离屏渲染 `NetScope.Desktop.exe --render-screens <dir>` → 逐页 PNG 目视对照 mockup。
- 冒烟 `--smoke-test <file.json>` → 真实只读采集 + 导出。
- 实机（用户常驻环境）：修复全流程（体检→执行→自检）、二次确认、回滚、动画开关、reduced-motion、非管理员跳过。
- 字体：验证 Plex Sans/Mono 在 WPF 的字重解析与 CJK 回退基线。

## 12. 风险与开放问题

- **Plex CJK 回退**：Plex Sans 无 CJK，混排 Latin/CJK 基线需实测对齐（YaHei UI 回退）。
- **互联网可达性数据**：快照不含互联网探测；仪器绑定 PostCheck，修复前显示未知「—」（诚实，待确认接受）。
- **问题↔动作配对算法**：精确映射在实现计划定义；部分动作无配对 finding。
- **WPF 排版限制**：无 letter-spacing/可变字重，用静态字重近似。
- **动效性能**：连续呼吸/扫描受开关闸门；Freeze 画刷。
- **Desktop 测试断言**：旧色值/页数需更新。
- **首页移除**：确保会话控制迁移、无功能丢失。

## 13. 不在范围

修复引擎/契约/采集/探测/存储/诊断/适配/CLI；新增修复能力；暗色模式/主题切换；Logo/ico 改动。