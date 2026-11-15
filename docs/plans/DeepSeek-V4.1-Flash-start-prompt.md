# DeepSeek V4.1 Flash：下一轮只执行 T09-R2

将下方提示词交给源码目录中的 DeepSeek 编码会话。完整续建次序与后续任务卡见 [工程续建施工单](DeepSeek-V4.1-Flash-construction-plan.md)。不再复制原设计里的 T00/T01 初始化指令，不让模型接管全部阶段。

```text
本轮任务：T09-R2——修复现有客户端身份、内核监听归属、TUN候选展示及配置Gap分类。
本轮要实际编码和验证，不再重写产品设计。完成本任务报告即停，不自动领取下一任务。

源码目录：D:\Projects\NetScopeRepair
原设计包：D:\AAA-课程资料\面试资料\需求调研\代理网络诊断修复工具-设计交付-v1

必须读：
- 原设计 docs/11-用户需求确认与首版验收.md
- 原设计 docs/08-里程碑与施工任务.md 的单任务/门禁原则
- 原设计 docs/09-DeepSeek施工总指令.md 的边界、单任务模板、回报模板
- 原设计 docs/02-技术架构与ADR.md 的身份与只读采集
- 原设计 docs/03-诊断引擎与证据规则.md，docs/07-测试验收与故障实验室.md 中QA-009/010/019/039
- 源码 docs/plans/DeepSeek-V4.1-Flash-construction-plan.md 的起点和T09-R2卡
- 源码 docs/plans/evidence/20261105/ui-baseline.json
- 源码 docs/reports/G1-R1-M1-review.md

基线：
- 方案读取时 main 的 HEAD=a4b07a042d33746d664556933f562648ff1ba1e7；如果HEAD更新，先读新增提交，不覆盖它们。
- b348491：Magpie外壳与七页设计；196301f：胶囊组件与图标；a4b07a0：Manta Radar图标/Logo、关闭行为交互设置。必须保留。
- 07c6f5a的616测试通过仅是历史基线；当前实际build/test需重跑，不硬编码预期计数。
- 源码已存在且Git已初始化，不重新建工程/初始化仓库。

已确认缺陷：
1. Nice界面进程与FlClashCore监听进程是两个PID。ClientCatalog只识别Nice UI名；GenericAdapter按已识别PID挂端点，导致实际内核监听被分到unknown_listener，Nice卡片端点未知。
2. SystemProcessSource没保留路径/版本读取失败原因，也没有父子进程等关联数据。
3. ObserveTun得到一个全局TunView，随后赋给多个客户端，造成全局候选像每个客户端各自拥有。只取Addresses[0]又让IPv6首项掩盖IPv4。
4. 普通白名单外配置结构与必需字段缺失都进入Gaps，正常配置显示“已解析（存在缺口）”。不能通过隐藏全部Gap来修。

允许修改：
- src/NetScope.Adapters/**
- src/NetScope.Application/Adapters.cs 及此次只读适配所需类型
- tests/Adapters/**
- 必要的客户端展示绑定：src/NetScope.Desktop/ViewModels/ClientsViewModel.cs、Rows.cs、Views/ClientsView.xaml
- 上述模型变化导致的demo/mock编译适配及相关回归，限必要改动
- 新报告 docs/reports/T09-R2-report.md 与对应证据目录

禁止修改：
- Theme.xaml、应用图标/Logo、Shell外壳、关闭行为设置与用户既有UI风格
- ObservationSession/调度/规则逻辑、Probes、Repair/Broker（这些在下一任务做）
- 系统网络设置、客户端文件/配置、真实上行、用户代理环境、DSH/Antigravity配置
- 不启动外部请求、controller访问，不提权，不运行桌面TUN脚本，不切TUN/重启客户端
- 不上传源包、不把真实包加入fixture、不自动提交/推送、不git reset/clean

必须实现：
A. 先补只读边界模型：进程PID+启动时间、关联证据、字段读取状态/原因码；网卡GUID/LUID/ifIndex和每族地址集合。缺失保留unknown，不自动提权。
B. UI进程、核心候选、真实监听分实体；来源可追踪。有可验证父子/路径关联才关联客户端，并显示依据；单靠进程名或端口不能确定归属。
C. 归属暂不明仍展示实际监听及owner PID/读取状态，不能丢事实或只留一句未知；不同安装目录/两个同名核心不能串归属。
D. 系统级TUN候选与客户端拥有TUN分开。没有可信owner就显示归属未确认；不能把同一全局TunView赋给所有客户端。IPv4/IPv6分列。
E. 配置解析新增Notice/Coverage或等价字段；普通被忽略结构不再当异常，读取失败/坏格式/确有必需字段缺失继续Gap。IsComplete按每项事实定义计算，不能因改了文案就直接返回true。
F. 客户端页沿用现有主题/胶囊样式，最小增量显示上述字段；磁盘mixed-port不能冒充实际监听。

优先建立的失败测试：
1. UI/core不同PID+可靠关联+核心拥有监听：显示二者和实际端点，关联依据明确。
2. 两个同名core来自不同目录/无可靠关联：不串归属，不宣称都属Nice。
3. 只有UI、只有core、路径不可读但监听owner可读、PID复用：事实和缺口各自保留。
4. 两个客户端和平并存不判冲突；真实同地址端口争用仍可见。
5. IPv6 owner未支持不能借用IPv4结果。
6. 一个/两个本机TUN候选、无owner、无客户端进程、不相关VPN：不宣称每客户端都拥有；适配器列表Partial不当false。
7. 地址首项IPv6且同时有IPv4：两族都显示。
8. 完整白名单+普通proxies/rules/providers结构：有效，忽略项为Notice；缺关键字段/坏格式/读取失败仍为Gap。
9. 更新旧GenericAdapterTests中“所有statuses.TunEnabled=true”的错误语义断言，同时保留新正反例；不得删除测试来通过。

建议验证命令（以实际项目环境为准）：
git status --short
git log -4 --oneline
dotnet build NetScopeRepair.slnx
dotnet test tests/Adapters/NetScope.Adapters.Tests.csproj --logger "trx;LogFileName=adapters.trx" --results-directory docs/reports/evidence/T09-R2/test-results
dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj --logger "trx;LogFileName=desktop.trx" --results-directory docs/reports/evidence/T09-R2/test-results

先跑能证明缺陷的关键新反例，保存修前失败证据；补修后跑相关测试与全构建。需要其他受影响测试时补跑，记录真实计数/退出码。若使用真实只读读取，只做进程/监听/网卡枚举，不连接任何端点。页面渲染不等于鼠标交互通过。

交付：
- T09-R2-report：当前HEAD、实际改动、用户可观察行为、修前/修后反例、命令退出码与测试数、UI保护检查、只读核验、限制。
- 不能声称核心↔TUN强归属已经证明，除非确有来源；本轮只展示等级，不实施修复资格判定。
- 不能声称整个M1或M2已完成；真实诊断Records接线属于后续T06-R3，真实DNS/探测事实属于T04-R2。
- 完成本任务后停止，建议下一任务T04-R2，等待复核后再领取。普通任务内可逆编辑无需逐文件询问。
```
