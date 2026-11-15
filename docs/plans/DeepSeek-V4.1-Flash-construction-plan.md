# DeepSeek V4.1 Flash：现有工程续建施工单

日期：2026-11-05，Asia/Shanghai。源码：`D:\Projects\NetScopeRepair`。本次只制定施工单，未实施产品代码。

## 1. 以原设计为准，从现有代码接着做

设计源在 `D:\AAA-课程资料\面试资料\需求调研\代理网络诊断修复工具-设计交付-v1`，本单仅补**当前代码落点、先后顺序、失败反例与交接停点**，不重新制定产品架构。

必须遵循的原文：

- [08：任务与阶段门禁](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/08-里程碑与施工任务.md>)：沿用 T00—T18 与 G0—G3；一个任务、一份交付、一个评审停点。
- [09：施工指令与单任务模板](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/09-DeepSeek施工总指令.md>)：原开工提示词针对 T00/T01，不能原封不动拿来重建当前项目；本轮使用本文末尾的续建入口。
- [11：用户确认与验收](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/11-用户需求确认与首版验收.md>)：六项目标全部保留，完整自用首版至少 M3，四类业务分项验收。
- [02：组件与预算](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/02-技术架构与ADR.md>)、[03：诊断与捕获状态机](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/03-诊断引擎与证据规则.md>)、[04：数据与隐私](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/04-日志数据契约与隐私.md>)：契约和默认值不自行改写。
- [05：修复事务](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/05-修复事务与权限边界.md>)、[07：QA 矩阵](<D:/AAA-课程资料/面试资料/需求调研/代理网络诊断修复工具-设计交付-v1/docs/07-测试验收与故障实验室.md>)：M3 严格照原契约；真实写入/VM 实验不是方案自动授权的操作。

不另立 NS 编号或新 G 门禁，不提前跨过 G2 开放修复。原默认值：前窗 **5 分钟**、后窗 **2 分钟**；同目标同路径连续 **3 次有效失败**；连续 **3 组可比成功**再进入 recovering，完整后窗无新增失败才 closed；同类事件 **5 分钟**内合并。外部观察探测 **30 秒**，失败后短时 **5 秒**、最多 **2 分钟**后退避；仍须用户先启用明确目标。普通日志 **7 日/500MB**，钉住独立 **1GB**；100MB/日是性能目标。单探测 8 秒、组软预算 30 秒、并发 4、最多两个目标，每目标三路径。

## 2. 已核对的工程起点

### 2.1 保护用户的新 UI

本轮初始工作区干净，分支 main，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`。

| 提交 | 内容 | 施工要求 |
|---|---|---|
| `07c6f5a` | M1-R1 基线，历史 616 测试通过 | 不是最新 HEAD 的新测试结果 |
| `b348491` | Magpie 风格外壳、主题、七页与导航 | 保留布局，增量加功能 |
| `196301f` | 胶囊状态组件、应用/托盘图标与行展示 | 不拿旧 XAML/Rows 整文件覆盖 |
| `a4b07a0` | Manta Radar 图标/Logo、可交互关闭行为设置 | 保留询问/托盘/退出及立即保存逻辑 |

[20 文件 UI 基线](evidence/20261105/ui-baseline.json)记录读取时 SHA256；用于发现意外覆盖，不禁止功能增量。开始施工时再看 Git 增量；用户有新提交就以新提交为基础，不能 reset/clean。启动脚本已指向 M1-R2，README 仍有 M1-R1 说明，最终打包时同步。代码提交/推送不由本单自动授权。

### 2.2 这份导出包说明了什么

源：[用户 ZIP](../../incident-9be62cef1dd31253c9d4e2e4.zip)。[检查汇总](evidence/20261105/incident-inspection.json)与[可重跑检查器](evidence/20261105/inspect_incident.py)已保存。检查器只读且不解压；它不是产品导入器，其检查预算也不替代原设计 04 的产品准入上限。

- 108 条目，107 项校验和全部一致且覆盖完整；只证明包内自洽。
- 17:19:48—17:38:36，102 个真实 snapshot，0 probe_result、0 diagnosis；不能由空诊断认定网络健康。
- 接口域 102 次 Partial；WinHTTP 子域 102 次 Unsupported；`gaps=0` 指独立缺口事件，不代表采集完整。
- 第 23 个快照（17:23:29）：历史 ifIndex 44 的 metric 0→4225、DisableDefaultRoutes false→true；多个其他接口参数同时变化，PPP 接口出现在观测中。第 24 个快照未观测到 44；第 26 个快照再次观测到，metric=0、AutomaticMetric=false、DisableDefaultRoutes=false。
- 最长相邻观测间隔约 118.76 秒；不能凭间隔猜测用户暂停、进程卡顿或丢记录。
- 部分错误文本保留未别名化的网卡/虚拟交换机名称。要补脱敏反例，不推断存在凭据泄露。

上述索引仅用于历史分析，不进入任何真实写参数；地址别名不能重建任意路由前缀关系。源包不加入公开 fixture、不上传。回归仅抽取变化形状并使用合成身份。

### 2.3 已发现的真实断点

| 代码落点 | 当前行为 | 应在哪个任务补 |
|---|---|---|
| [ObservationSession](../../src/NetScope.Desktop/Services/ObservationSession.cs) | 本地采样不运行规则；手动诊断和本地采样的 Records 均为空 | T06-R3：证据接线，不只改 UI |
| [Composition](../../src/NetScope.Desktop/Services/Composition.cs) | Clients 供客户端页，未供会话证据采集 | T06-R3：注入统一证据 provider |
| [AdapterSeams](../../src/NetScope.Adapters/AdapterSeams.cs) | 缺父子进程/字段错误状态；网卡无稳定身份 | T09-R2：先补只读数据生产者 |
| [GenericAdapter](../../src/NetScope.Adapters/GenericAdapter.cs)、[ClientCatalog](../../src/NetScope.Adapters/ClientCatalog.cs) | UI 与内核 PID 分离时端点无法归属；全局 TUN 复用于客户端 | T09-R2：分开实际监听和归属等级 |
| [NiceConfigParser](../../src/NetScope.Adapters/NiceConfigParser.cs) | 忽略的普通非白名单结构也计 Gap | T09-R2：Notice 与关键缺失分开 |
| [EvidenceSessionEvents](../../src/NetScope.Storage/EvidenceSessionEvents.cs)、[EvidenceEventCodec](../../src/NetScope.Storage/EvidenceEventCodec.cs) | 有 Record 编码基础；新事实须显式绑定作用域，不能混到其他周期 | T06-R3：编解码/回放集成 |
| [TrayController](../../src/NetScope.Desktop/Services/TrayController.cs) | 标记故障仍为禁用占位，没有最近故障入口 | T10：接原设计 IIncidentRecorder |
| [RuleCatalog](../../src/NetScope.Diagnostics/RuleCatalog.cs) | DX-003/004/007/009/012 未实现 | 分任务补生产者与规则，不假定现有规则覆盖 TUN 修复 |

## 3. 先把 M1 试用缺陷关掉，再进入原 T10

执行顺序固定为：**T09-R2 → T04-R2 → T06-R3 → T07-R2 → T08-R2 → G1-R2 → T10 → T11 → T12 → G2 → T13 → T14 → T15 → T16 → G3**。

R 后缀表示原任务的定向补修，不重做 M0/M1，不代表已领取后续阶段。T17/T18 保留在原设计 M4，本轮不另扩写。每轮只给执行模型一张任务卡；完成后出具报告供复核，再领取下一张。

### T09-R2：先解决截图里的客户端识别问题（下一轮唯一任务）

**读写范围**：Adapters、Application 适配模型、Adapters 测试；Desktop 只允许为新字段显示做最小绑定修改。不得改主题、Logo、关闭行为，也不改会话调度、Repair/Broker。

具体落点：

1. 扩展 [ProcessObservation/ListenerObservation/NetworkAdapterObservation](../../src/NetScope.Adapters/AdapterSeams.cs)：进程字段读取状态/原因码、PID+启动时间、可验证的父子/路径关联；网卡稳定身份与按族地址集合。对老构造器保持可用的可选字段或同步测试 mock。
2. 在 [SystemSeams](../../src/NetScope.Adapters/SystemSeams.cs)实现普通权限可用的读取；路径失败保留原因码，不吞成同一个 null，不建议主程序自动提权。
3. 在 [ClientCatalog](../../src/NetScope.Adapters/ClientCatalog.cs)/[GenericAdapter](../../src/NetScope.Adapters/GenericAdapter.cs)区分 UI 进程、核心候选和确定归属。FlClashCore 的名字、共同端口不能单独证明属于 Nice；有可靠关联时归属，无关联仍展示“某核心实际监听某端口，客户端归属待确认”。Apex 随机名称同理。
4. [Adapters 模型](../../src/NetScope.Application/Adapters.cs)保留实际监听、磁盘配置端口、关联来源与 TUN 候选身份。TUN IPv4/IPv6 都显示；全局候选不能直接复用成每个客户端“拥有 TUN”。
5. [WhitelistParseResult](../../src/NetScope.Adapters/WhitelistParseResult.cs)/[NiceConfigParser](../../src/NetScope.Adapters/NiceConfigParser.cs)区分 Notice 与 Gaps；必需字段缺失、格式错误、读取失败继续保留 Gap，普通忽略结构不再使成功解析变橙色异常。
6. [ClientsViewModel](../../src/NetScope.Desktop/ViewModels/ClientsViewModel.cs)和[客户端页](../../src/NetScope.Desktop/Views/ClientsView.xaml)仅增量显示上述来源；配置端口不能冒充实测监听。

**先写的失败反例**：UI/核心不同 PID 且有归属证据；两核心同名但无强关联；仅 UI/仅核心；路径不可读但监听存在；PID 复用；两个客户端和平并存/真实端口冲突；IPv6 owner 不支持；一台机器两个 TUN；IPv6 排第一但同时有 IPv4；未知配置结构与关键字段缺失各一例。

**验收**：QA-009/010/019/039；新增对应回归先在旧实现失败、补修后通过。普通权限本机只读核对列表结果，不做 connect/外部请求，不使用固定历史 PID/端口作为业务逻辑。截图里不再仅剩无上下文的“未知”。UI 风格保持，且归属仍不确定时准确显示。

**报告/停点**：新增 `docs/reports/T09-R2-report.md`，含基线 HEAD、修改文件、命令退出码、实际测试数、只读结果与未验证项；完成即停，不自行领取 T06-R3。

### T04-R2：补真实探测的 DNS/阶段事实，避免规则缺输入

**依赖**：T09-R2。写范围：Probes、Application 探测模型与 Unit/Contract/Replay 的必要适配，不改观察调度或 UI 设计。

- 当前 [HttpProbeTransport](../../src/NetScope.Probes/HttpProbeTransport.cs)从未填写 ResolvedAddresses；[HttpProbeRunner](../../src/NetScope.Probes/HttpProbeRunner.cs)只能得到空地址。先给“真实解析/实际连接事实能进入结果”建立失败测试，再加入可注入解析/连接观测边界。
- 无显式代理路径记录真正参与连接的本地解析事实及来源/时间，不先查一次 DNS 就冒充 HttpClient 实际采用；代理端解析保持 proxy/unknown，不把代理服务器 IP 或本地查询冒充远端目标解析。
- 原设计 resolve/connect/proxy_connect/tls/http 的阶段值只在能准确测到时填；错误归因不明就保留 unknown。当前地址族选择 Unsupported 的限制独立列出，补强制族能力必须测试实际 socket 选择，不能只写 AddressFamily 标签。
- 同目标/方法/options/短窗、组内拓扑变化、代理端点和取消/预算语义保持；不为了拿地址而改变 system-default 的实际代理策略或关闭证书校验。复用连接时明确本轮未重新解析，而非冒充新 DNS 样本。

**验收**：QA-002/003/004/007/020/021；fake resolver/transport 和受控 loopback 集成证明观测供数；proxy端解析/复用连接/族不支持/取消/失败归因未知都有反例。外部实测仅在用户已启用目标时做，否则待验。报告 `docs/reports/T04-R2-report.md`，完成即停。

### T06-R3：把适配器事实接入真实诊断链

**依赖**：T09-R2、T04-R2 复核通过。写范围：Application/Adapters 证据投影、Desktop 会话/Composition、Storage codec/replay、Diagnostics 集成测试；不新增系统写能力。

拟新增 `IObservationEvidenceSource` 与 `ObservationEvidenceBuilder`，放在现有 Application/Adapters 中，不再建立一套独立采样系统。返回带状态、观察窗口/时间、boot、snapshot 引用的最小化进程/监听/磁盘白名单记录。字段名以现有规则实际读取的 Facts 为准，建立显式映射表。

改动顺序：

1. 先测试：fake 适配数据从 Desktop 入口进入 EvidenceSet，真实规则能消费；当前 Records=[] 时应失败。
2. [Composition](../../src/NetScope.Desktop/Services/Composition.cs)注入 evidence source 到 [ObservationSession](../../src/NetScope.Desktop/Services/ObservationSession.cs)，两个真实入口都不再无条件设空 Records。客户端页复用同份观察结果或明确时间差，不能拿另一个周期的页面结果拼证据。
3. 本地采样运行适用的本地规则；外部证据缺失时保持 candidate/insufficient，不为了产出诊断伪造探测。外部 RunDiagnosis 仍须用户启用具体目标。
4. [EvidenceSessionEvents](../../src/NetScope.Storage/EvidenceSessionEvents.cs)/[codec](../../src/NetScope.Storage/EvidenceEventCodec.cs)写入明确作用域并严格校验；保持 M1-R1 多快照回放、引用校验和记录提交取消边界。
5. 规则失败也可追踪，不能只在内存/界面出现。schema/typed payload 的兼容策略写 ADR；旧日志继续读，不能把不支持的新字段默默当正常。

**验收**：Desktop 组合入口→记录→诊断→导出→回放，建立“rule→record kind→真实 producer→未支持项”矩阵。DX-001/002/006/011 做可行供数与缺证反例；磁盘配置只能生成 client_config_file，不能冒充 runtime_config。DX-005 无 bind-failure 生产者时只保留冲突候选/能力缺项，不能假造 supported；DX-008 跨周期可比样本由 T10 补，不用单组三路径伪造间歇。没有调用客户端页刷新也有事实。跨 boot/窗口、错引用、重复 ID 被拒绝；旧包无新事实时不补造。关联 QA-004/005/008/009/010/014/024/039。

**停点**：`docs/reports/T06-R3-report.md`。这一步完成才有资格说真实桌面诊断链已接通。

### T07-R2：在用户新 UI 上显示“发生什么、还缺什么”

**依赖**：T06-R3。写范围：现有 Home/Diagnoses/Timeline/Clients VM 与页面、纯本地变化解释服务和桌面回归。不改视觉系统、图标与关闭设置。

- 相邻观察按 GUID/LUID/Family 对比，显示参数 before/after、出现/未观测到、时间范围；不推断操作者或因果。用源包变化形状构造合成 fixture。
- 首页分开采集健康、网络验证、最近诊断时间；0 probe 明确“尚未进行外部验证”，不因诊断为空转绿。
- 没配 URL 时仍能看本地诊断，外部诊断入口说明目标启用方法；设置中提供可编辑的原设计目标档案，不擅自发请求。三路径按同目标同方法/族/窗口比较。
- 不把 .NET 探测说成 DSH/Antigravity/Chrome 实际业务结果；未支持阶段/IPv4/IPv6 能力准确显示。
- 详情聚合 Partial 原因，允许展开；必要缺口保留。不能通过把 Partial 改 Ok 或隐藏错误来降低噪声。

**验收**：QA-002/003/006/007/011/020/021/023/037/039；七页渲染与关键交互；新关闭行为三选项与目标保存组合回归。截图只证明外观，不能代替行为测试。报告 `docs/reports/T07-R2-report.md`，完成即停。

### T08-R2：错误文本脱敏、缺口统计和版本溯源

**依赖**：T06-R3、T07-R2。写范围：Storage Export、Export VM、Export/Replay 测试，不改源包。

- [PackageAnonymizer](../../src/NetScope.Storage/Export/PackageAnonymizer.cs)补名称只存在于 Errors、组合过滤器/交换机名、遍历顺序差异等反例。优先结构化错误原因，身份预收集或模板化，不能只认已列在 interfaces.value 的名称。
- manifest/预览分开 gap 事件数、Partial/Unsupported 域与子域、探测覆盖；旧计数字段保持既有语义。附简短变化摘要、实际 app build commit/解析器/规则版本，区别记录诊断与离线重算。
- 全包扫描合成秘密哨兵：事件、快照、诊断、时间线、摘要和异常文本；保持同包引用和规则语义。地址别名局限仍告知，不能宣称拓扑完全保真。

**验收**：QA-014/022，102 快照形状测试报告 102 Partial、0 probe，不得把 gaps=0 说完整；所有校验和/引用正确，多快照可回放；隐私独立复核。报告 `docs/reports/T08-R2-report.md`，完成即停。

### G1-R2：这次补修的复核停点

独立审实际 diff、反例与命令结果；全 build/test、七页与关闭/托盘/退出、demo 隔离、三周期采样与诊断导出回放。当前 HEAD 必须重新测，不能复制历史 616。

发布独立只读包并同步启动入口、版本、README，不覆盖运行中的旧进程。用户机只读；外部实测仅用明确启用的目标，未测项目列未测。通过范围写清：允许进入 T10 的**代码施工**，不声称日用/外部业务/真实修复全部验收。报告 `docs/reports/G1-R2-review.md`。未通过先补修，不跨阶段。

## 4. 接回原 M2：T10/T11/T12 的具体工程落点

### T10：观察会话与故障捕获

**前提**：G1-R2。原设计：08/T10、03/4、04/Incident、11/托盘与业务清单。QA-007/012/015/017/037/038。

拟在 Application 增加 `IIncidentRecorder`、`IncidentState`、`IncidentTrigger`、`IncidentWindow`；实现优先放现有 Storage/会话服务，不为空职责新建项目。记录事件引用，不拷贝一套无关联快照。

1. 将现有 [ObservationSession](../../src/NetScope.Desktop/Services/ObservationSession.cs)的采样/探测输出送入 recorder，使用虚拟时钟先测 `idle/observing/suspected/incident_open/recovering/closed/paused/degraded`。
2. 前5/后2分钟由持续落盘事件提供；同目标同路径3失败、3可比成功和5分钟合并严格照原设计。缺样/取消/权限失败不算网络失败。没有前窗就如实标缺失。
3. 启用外部观察必须先展示目标/频率/预算并由用户启动；没有启用时仍采本地状态和接受人工标记。立即诊断和后台调度共享生命周期/预算，退出后不继续。
4. [TrayController](../../src/NetScope.Desktop/Services/TrayController.cs)启用“刚才断网了”，增加最近故障入口；Home/Timeline 显示 incident 与采集缺口。保持当前图标与关闭选择。
5. 持久化暂停/恢复/睡眠/跨 boot/时钟不连续/退出覆盖边界。原包118.76秒间隔只能显示未解释间隔；新版本靠新增生命周期事件解释，不追改历史。
6. 增加 DSH/Antigravity/Chrome/TUN 普通应用四项 `pass/fail/not_tested` 的时间与来源记录；只是业务观察，不自动付费请求或改应用配置。

**必须测试**：假时钟推进5分钟前窗；立即人工标记；缺样不计失败；抖动合并但失败总数不丢；后窗新失败重新打开；暂停/退出排空；睡眠和跨 boot。mock+本地离线记录可做，QA-015 真断网只在原设计合格 VM；无 VM 记真实实验待验。报告 `docs/reports/T10-report.md`，停在 T10。

### T11：配额与崩溃恢复

**前提**：T10。落点：[NdjsonEventLog](../../src/NetScope.Storage/NdjsonEventLog.cs)、[SqliteEventIndex](../../src/NetScope.Storage/SqliteEventIndex.cs)、会话存储目录与 Export，新增 `RetentionPolicy/QuotaManager/SessionRecovery` 等最小服务。QA-016/017/018/035。

- 7日/500MB普通日志、1GB钉住独立上限；80/90/100%分级按04执行，100MB/日只告警。拒绝新增钉住而非解钉旧案例。
- 优先清理过期未钉住成功明细；活动案例先封存，保留失败总数/首末时刻/聚合计数与 gap。未决 journal 不纳入普通清理。
- 末行截断隔离并记 gap；中间损坏不当有效前缀之后都正常；SQLite 从 NDJSON 去重重建，有游标/原因可追踪。存储不再可写时 UI 明确未持久化范围，不能说无损。
- 按文件访问/配额 seam 做空间耗尽、拒绝、crash 切点测试，不填满用户磁盘、不改系统时钟。

报告 `docs/reports/T11-report.md`，完成即停。

### T12：案例重放工作台

**前提**：T06-R3/T08-R2/T10，集成以 T11 结果为基础。复用 [ReplayService](../../src/NetScope.Storage/ReplayService.cs)与现有 CLI replay，新增 Application 的 `CaseReplayService`/规则集版本接口与 Desktop 的案例视图；如确需 CaseReplay 项目，先写依赖 ADR，不复制 codec。

- 包准入使用原设计04的100MB/1000文件/20MB单文件/深度8以及路径/链接/引用校验，不能用本轮检查器替代。输入默认不可信，读取链不引用真实 Collector/Probe/Broker。
- 固定规则版本重算，和包内记录诊断分开显示；不支持的历史版本准确标 Unsupported，不用当前规则冒充原版。相同输入重复输出一致。
- 逐 snapshot scope 回放，按 incident 窗口明确跨周期证据；输出等级/证据/缺项差异。旧包无客户端/探测时只说明变化及缺口，不能补造根因。
- QA-012/014/015/024；加禁止网络/系统写入的依赖断言与恶意包反例。报告 `docs/reports/T12-report.md`，停在 T12。

**G2 复核**：严格采用原08/G2。现场捕获、配额、离线回放及失败保留通过；尚无系统写入口。没有 VM 真实断网证据时明确剩余门禁，不用模拟结果宣称完整实验证明。

## 5. 接回原 M3：T13—T16，不改变原事务设计

### T13：三字段平台实验与计划前置事实

**前提**：G2；原05/07；隔离 VM、console、checkpoint、恢复负责人。没有这些条件时只完成 mock 和实验资产，动作保持关闭，不在用户日用机替代实验。

- 新建最小 `NetScope.Repair` 纯计划/能力矩阵和 `tests/WindowsIntegration`；原生平台实现先位于 Repair 的受控边界，不另立多余项目。类型包括 `RepairPlan/RepairAction/InterfaceIdentity/RepairPreflight/PlatformCapability`。
- 补 DX-003/004 所需生产者：有效运行 TUN 意图、核心到具体 TUN 的可信归属、逐族原始参数/存储、GetBestRoute2/等价查询与实际目标/映射目标选路。来源不支持时拒绝，不能用名字/198.18/磁盘tun=true/用户点选替代。DX-003/004 仍为纯规则，RepairPlanner 单独检查资格。
- 验证 GUID/LUID/ifIndex/设备实例/族/compartment 与动态受保护上行；多候选、重建、漂移、上行、关键未知都拒绝。
- 三字段仅 IPv4 ActiveStore；测 setter/getter、非目标字段、耦合闭包、PersistentStore 副作用和精确恢复。原生 metric uint 保留原始范围；分清手动配置值与有效派生值，不能从 int? 截断值恢复。AutomaticMetric 前态无法可靠还原时 Unsupported。
- DX-010/ProposedChange 目前按族约束不足；typed plan 的 no-op 按身份+族+store逐字段比较。metric已0且auto关闭/不忽略默认路由时不改成1，不新建路由。
- 当前 snapshot 多域顺序采集不是原子 preflight；修复须逐源时间/状态与关键身份重验。Desktop/broker 的 SystemClock 起点不同，TTL 使用明确共享系统时钟域或 broker 自计时租约。

**产物**：原设计要求的 Windows构建×驱动×API×store×动作矩阵、原始日志、side-effect/恢复结果与关闭项。QA-006/012/020/027/029/030；每个新规则至少1正例/2反例/1缺证，修复再加no-op/歧义/漂移。未证实动作不能进入 T14 实机启用。

### T14：Broker 协议与身份

**前提**：T13。新建原架构中的 `NetScope.Broker`，UI仍asInvoker。严格复用原05，不以本单放宽：

- 受信任、已签名且 ACL 保护的安装/加载目录；无签名/可信部署资源时仅开发态隔离测试，真实提权能力关闭，不能默默把用户可写目录当生产边界。
- 版本化三个动作枚举+RecoverTransaction；禁止通用SetProperty、shell、PowerShell、命令/脚本/任意程序集、任意用户路径。
- 随机 ACL named pipe，logon SID/会话与内核报告对端PID/创建时间/签名/路径验证；拒远程/抢占/伪父进程。256位nonce不在日志、命令行或环境暴露，绑定挑战、事务与计划摘要；UAC不代替逐动作同意。
- 64KiB帧、16动作、握手10s、空闲30s、总180s、技术观察最多60s，预留60s补偿，最晚120s停止新前向动作；照原05处理不可取消调用/超时，不能强杀后说已恢复。
- broker独立preflight与白名单验证；before/目标/store与已授权计划不符，返回新预览。不得从日志/导出包反序列化出特权恢复命令。

**测试**：QA-025/026；错用户/会话、重复nonce、未知字段、超限、路径替换、计划改值、PID复用、UAC拒绝、断连。报告 `docs/reports/T14-report.md`，完成即停。

### T15：Journal 与精确恢复

**前提**：T14。落点 Repair/Broker，独立受限事务目录；不能以普通 NDJSON/SQLite 替代。

- 状态名按原05固定：planned/authorized/prepared/applying/observing/committed/rolling_back/rolled_back/rollback_conflict/rollback_failed/recovery_required/aborted/no_op，不另造一套状态。
- 固定目录/排他句柄/最终路径与重解析点验证、ACL、长度/序号/版本/校验。完整before及授权摘要刷盘→逐步intent刷盘→fresh GET比较→最小写→读回→applied刷盘；磁盘失败停止前向动作。非取消调用须收尾，不能WaitAsync超时后遗留写入。
- 只有intent没有applied属于执行未决；即使现值等于after，也不能据此断定为本事务修改并自动回滚。保留recovery_required和不确定窗口，不重放setter。
- 只恢复已确证属于事务且current==after的字段；已before只记已恢复；其他值冲突，不覆盖。Get/Set不是原子CAS，工具互斥挡不住外部写入；不能安全满足平台边界的动作关闭。
- 崩溃后普通权限启动只提示未终结记录；用户确认新恢复计划再起新broker。没有服务/guard不承诺broker自身崩溃后即时自愈；已committed撤销须新授权，旧nonce不可复用。

**测试**：QA-027/028/029/030/036；每个flush/native/readback/ACK切点、坏尾/磁盘满、双writer、外部改值、接口重建、跨boot、取消/断连、部分成功。报告 `docs/reports/T15-report.md`，完成即停。

### T16：把既有新 UI 接到计划、确认、事务和业务复测

**前提**：T15。在现有视觉中增量加计划/进度/恢复组件，复用主题和胶囊；不重做外壳。Application/Composition连接 RepairPlanner/BrokerClient，Desktop不直接写网络。

- 逐动作默认未选；展示身份/IPv4/store/before→desired/依赖/证据/TTL，显式确认才UAC。no_op不计修复；目标漂移/过期/输入包不可信时禁用执行。
- 修复技术验证按原计划执行，观察失败或中途取消且已有变更时按原05进入恢复；不擅自改成“HTTP失败就保持并算committed”。committed仅指预定技术验收通过，业务结果独立。
- 新增RepairVerifier按计划允许delta核对前后目标/方法/族/options与路由/非目标；旧DiagnosticComparability拒绝TopologyChanged，不得清零变化标志来制造可比。
- 配置读回、技术探测、用户业务三层结果分开；十分钟/三轮业务观察由低权主进程完成，DSH/Antigravity/Chrome国内外/TUN应用分别pass/fail/not_tested，不自动付费调用。
- 关闭/退出/断连显示事务状态并按原契约收尾；恢复不依赖互联网。

**测试**：QA-023/025—031/036—039；真实UI组合入口的fake闭环+独立VM证据，样例/未验状态准确。报告 `docs/reports/T16-report.md`，完成即停。

**G3 安全复核**：原08/G3与07矩阵，独立审高权限代码、动作矩阵、VM故障注入/恢复。未满足不开放用户机写入；通过也不自动试修，用户机每次仍需具体动作确认。T17/T18 按原设计后续领取，本轮不自动推进。

## 6. 下一轮的发令方式

只把 [T09-R2 开工提示词](DeepSeek-V4.1-Flash-start-prompt.md)交给 DeepSeek。它包含精确读写范围、反例、命令、禁止事项与停点，要求实际编码而不是再次规划。

每张任务报告沿用原09模板，加入：当前HEAD、用户UI差异保护、回归fixture、实际命令退出码、未执行项目。可逆任务实现不逐文件询问，但不能自行跨任务/阶段。阶段推进、实机网络变更、上传/发布和设计偏离保留原确认点。

本轮产物仅本施工单、首任务提示词、只读ZIP检查器/汇总与UI基线；未修改原设计包、产品源代码/启动入口、用户导出包或系统网络，也未运行桌面脚本/外部探测。原设计的默认值、授权/签名/恢复要求和门禁顺序均不由本文替换。

## 7. 施工与复核结果（2026-11-06 更新，含收尾核查更正）

T09-R2 → T04-R2 → T06-R3 → T07-R2 → T08-R2 → T10 → T11 → T12 → T13 → T14 → T15 → T16 已落地并按任务报告交付，
但 G3 外部独立复核结论为**不通过**：20 条代码缺陷（P1 11 / P2 9）+ 5 项交付缺口，13 条已动态复现、7 条静态确认。
详见 [G3 门禁处置记录](../reports/G3-final-review.md)。**F01—F20 尚未修复，G01—G04 尚未关闭，G1/G2/G3 未因此通过。**

### 7.1 缺口归属更正（相对上一版本文档）

| 缺口 | 上一版归属 | 更正后归属 | 说明 |
|---|---|---|---|
| G01 持续外部观察调度 | 误放入 R3/T16 | **R2 / T10** | 持续观察是故障捕获的必需环节，必须在 M2 关闭；仍须用户显式启用目标、频率、预算，应用启动只做本地采集 |
| G04 四类业务结果 | 笼统写"M2/M3 入口" | **拆两段**：M2 交结果/时间/来源/备注录入、持久化、导出与离线重建（Chrome 国内外分列）；M3 再绑定具体事务，做恢复后十分钟/三轮观察与独立结果统计 | 只在修复页加按钮不能关闭 G04 |
| G03 真实修复链 | 笼统写"门禁待补" | **T13/T14/T15/T16 分别列出真实能力交付项**（见 7.3），缺 VM/签名等资源时记录具体阻塞 | 不得用更换 gateway 或开关宣称完成 |

### 7.2 R1 内依赖与分批（不按编号齐开）

F06 涉及探测、Application 合同、诊断消费与 UI 可比性；必须先统一"请求地址族策略 / 真实观测族 / 代理端目标未知族"的语义，
再进行 F08 的跨周期供数。F08 涉及 T06/T07 会话及 T10 窗口语义，必须有**单一会话文件 owner**，
R2 自动观察复用同一合同，不得另建平行窗口模型。

| 批次 | 原任务 | 关闭项与停点 | 写范围 |
|---|---|---|---|
| R1-A | T03 | F09：只改逐族采集及对应测试，避免触及会话与 UI | `src/NetScope.Collectors.Windows/`、`tests/Windows/` |
| R1-B | T04，协调 T06/T07 合同 | F06/F07：一个 owner 统一生产者/规则/UI 语义；先交合同与真实 transport 组合回归 | `src/NetScope.Probes/`、`src/NetScope.Application/`（观测/可比性合同）、`src/NetScope.Diagnostics/`、`tests/Unit/`、`tests/Adapters/` |
| R1-C（依赖 R1-B） | T06/T07，协调 T10 窗口 | F08：真实会话多轮 → 落盘 → 导出 → 回放，正反例均验证，不拼接跨 boot/拓扑不兼容样本 | `src/NetScope.Desktop/Services/ObservationSession.cs`、`src/NetScope.Application/ObservationEvidence.cs`、`src/NetScope.Adapters/ObservationEvidence.cs`、对应测试 |
| R2 | T10 / T11 / T12 | F10、F19、**G01**、M2 侧 G04 记录入口与事件生命周期 / 先 F12 再 G02 产品接线 / F11、F13、F20 | 按任务卡另行分配 |
| R3 | T13 / T14 / T15 / T16 | F01—F05 与 G03 真实链（平台实验 → 可信 broker → 耐久事务依赖推进）/ F14—F18、G04 事务关联与恢复后观察 | 按任务卡另行分配 |

R1-A 与 R1-B 在写入范围分离时并行，各公共文件只有一个写 owner；**R1-C 等待合同稳定后再开**。
首轮只设两名施工者 + 一名只读独立复核者，由主代理统一集成、构建与复验；子代理数量不是完成度指标。

### 7.3 G03：T13—T16 真实能力交付清单（当前均未完成）

| 任务 | 必须交付的真实能力 | 当前状态 / 阻塞 |
|---|---|---|
| T13 | 可信平台事实生产者（OS 构建、驱动、API 可用性）、隔离 VM 三字段动作矩阵实测、可用原生平台适配器、preflight revalidator 生产接线 | 适配器为不可用实现；`RepairPreflightRevalidator` 无生产调用；缺 VM 与平台实验 |
| T14 | 一次性 broker 可执行进程与 UI 客户端、真实 ACL/nonce/预算、UAC 提权、签名可信部署与攻击面验证 | Broker 仅为类库，无进程输出；缺签名管道与高权限审查 |
| T15 | 受限 ACL 下的权威 journal 部署、崩溃/断电切点、跨 boot 重建目标、实机恢复闭环 | 仅开发态隔离测试；恢复安全缺陷 F01—F03 未修 |
| T16 | 真实 UI → broker 接线、逐动作确认、恢复 UI、事务与业务结果关联持久化、恢复后业务观察 | 生产组合为 `UnavailableRepairGateway`；G04/M3 段未做 |

### 7.4 R1 发令要点

- 不为了通过测试把 `unknown`/`Partial` 改正常；不伪造代理端地址族；不清除 `TopologyChanged`；不拼接不可比周期。
- 原外部复核证据保持不变；把 13 条反例转为产品回归断言，证明**修前失败、修后通过**；旧反例程序退出 0 只表示错误仍存在，不是补修成功。
- 每个任务给出允许文件、实际 diff、相关用例/命令/退出码与未解决问题；主代理统一集成与最终构建测试。
- R1 完成后提交源码摘要、测试结果、真实组合入口证据与报告，**停在复审点**：不自行进入 R2/R3，不更新发布物宣称验收完成。
- 真实修复继续 Disabled：没有具体授权不 UAC、不改用户网络、不调付费业务、不上传或发布。
