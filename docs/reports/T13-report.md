# T13：修复计划、能力矩阵与平台前置事实

任务：T13（原 M3 首任务：三字段平台实验与计划前置事实；本轮不领取 T14）
日期：2026-11-05（UTC+08:00）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：设计包 `docs/05`（修复事务与权限边界）、`docs/07`（QA-006/012/020/027/029/030）、`docs/08` 的 T13、`docs/09`；施工单 `docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T13 卡；`docs/reports/T09-R2-report.md`；现有 Domain/Application/Collectors 只读接口与模型。

**结论：本轮交付纯计划、前置校验、能力矩阵、授权绑定字段、DX-010 约束不足反例与平台实验资产。平台实验执行数为 0；默认能力矩阵中每个真实字段动作都是 `unavailable` 或 `disallowed` 并带原因；没有任何动作可执行、可授权或可提交。没有真实写网络、提权、改注册表、启动/停止客户端、外部探测或桌面脚本。**

## 1. 实现了什么（用户可观察行为）

本轮没有 UI 入口，也没有新增任何可点击修复按钮；可观察行为在计划对象、拒绝原因和构建/测试结果上：

1. **三动作闭集 + RecoverTransaction 例外**：只接受 `set_tun_ipv4_interface_metric`、`set_tun_ipv4_automatic_metric`、`set_tun_ipv4_ignore_default_routes` 与 `recover_transaction`。`set_property`、任意属性名、脚本/命令动作在解析层即拒绝；`recover_transaction` 只能来自 broker 权威 journal，诊断/计划请求会被拒绝（journal 属于 T15，本轮未实现）。
2. **逐字段 typed before/desired**：`InterfaceMetric` 保留原生 `uint`（0..4294967295），并区分 `manual_configured` / `effective_derived` / `unknown`；`AutomaticMetric` 只接受 `enabled|disabled`；`IgnoreDefaultRoutes` 只接受 `true|false`。派生 metric 不能作为 before/恢复基线，不做 `int?` 截断。
3. **目标身份**：动作与前置事实携带 GUID、LUID、ifIndex、IPv4/IPv6 族、compartment、PnP 设备实例、接口类型、boot，以及每个 before 事实的来源、来源版本、状态、UTC 观察时间和 TTL。计划自身引用 `preflight_id`，每个动作带 `valid_until_utc` 与最多 60 秒技术观察窗口。
4. **前置校验拒绝**：关键未知、缺失 ActiveStore 三字段、来源状态非 ok、TTL 过期、未来时间/时钟偏移、跨 boot、GUID/族/compartment 漂移、LUID/设备实例重建、ifIndex 变化、外部字段值漂移、受保护上行变化、真实上行/非 TUN、未知上行/未知默认路由归属、多候选归属，都会拒绝计划或使重验无效；拒绝带稳定 code 与缺失证据 id。
5. **核心→TUN 归属**：只有可信来源（客户端运行时 API、驱动/设备所有者、平台会话所有权）并带来源 ID 与证据时才算 `verified`。名称 token、198.18/15 地址、磁盘 `tun=true`、适配器描述和用户点选即使被标成 `verified`，也会被降级为 `unknown` 并拒绝计划。
6. **no-op 精确作用域**：按“GUID + 族 + store + 字段”逐字段比较；PersistentStore 的相同值不能抵消 ActiveStore；派生值相同也不构成 no-op；整计划无实际变化时 `decision=no_op`、动作数为 0、`counts_as_repair=false`，不生成授权动作。
7. **平台能力矩阵**：先执行策略闸门（只允许 IPv4 + ActiveStore；PersistentStore 与 IPv6 一律 disallowed），再按 Windows build × driver × API × store × action × family 查找已验证实验记录。默认矩阵没有任何已验证组合，三个字段动作均为 `unavailable`（原因写明缺隔离 VM/实验），`recover_transaction` 也为 `unavailable`。验证记录必须同时具备 experiment id、证据、验证时间和完整检查项（setter/getter 读回/精确恢复/非目标字段/PersistentStore 副作用；AutomaticMetric 还必须有耦合检查）。
8. **授权绑定字段**：计划摘要为规范化 SHA-256；授权条款包含计划摘要、到期时间、逐动作 ID、依赖组、逐动作必须确认、默认不选、UAC 不等于动作同意、绑定字段清单（user SID、登录会话、计划摘要、目标指纹、到期时间、已确认动作、nonce）。本轮 `binding_issued=false`，不签发任何授权。
9. **DX-010 反例**：`ProposedChange` 只有字段名/GUID/期望字符串，无法表达 store、族、typed uint/手动态、当前 boot 身份来源与可信归属；`RepairProposalGuard` 固定返回 `proposal_untyped` 问题，且 `disable_default_routes` 不会被静默映射成 `ignore_default_routes`。测试还展示了“派生 20 与期望 20 字符串相等”的旧比较会误判 no-op，而 typed 比较不会。
10. **跨进程时钟域说明**：`RepairClockNotice` 明确 desktop 与 broker 的单调时钟起点不可跨进程比较；单调值只在同一 ClockDomainId+BootId 内比较；TTL 只用 UTC 加 5 秒容忍并拒绝未来时间；授权租约必须由 broker 用自己的单调时钟计时。另有 `RepairMonotonicLease` 的受控形状测试，但本轮没有 broker。

## 2. 实际修改文件

新增项目与源码（仅新建，未改现有文件）：

- [NetScope.Repair.csproj](../../src/NetScope.Repair/NetScope.Repair.csproj)
- [RepairActions.cs](../../src/NetScope.Repair/RepairActions.cs)：动作/store/族/字段/值语义/计划判定闭集、16 动作上限、TTL 上限。
- [RepairFieldValues.cs](../../src/NetScope.Repair/RepairFieldValues.cs)：typed before/desired、严格 parser、no-op 评估与作用域 key。
- [InterfaceIdentity.cs](../../src/NetScope.Repair/InterfaceIdentity.cs)：身份、UI 显示与归属证据分离、可信归属策略、上行评估。
- [RepairFacts.cs](../../src/NetScope.Repair/RepairFacts.cs)：SourcedFact、来源/状态/TTL、时钟域、单调/UTC 比较、租约、字段状态。
- [RepairRefusals.cs](../../src/NetScope.Repair/RepairRefusals.cs)：稳定拒绝 code 与缺失证据。
- [RepairPreflight.cs](../../src/NetScope.Repair/RepairPreflight.cs)：前置事实、关键未知/歧义/漂移/重建/跨 boot/上行拒绝、重验。
- [PlatformCapability.cs](../../src/NetScope.Repair/PlatformCapability.cs)：能力矩阵、策略闸门、实验检查项、默认关闭原因。
- [PlatformExperimentManifest.cs](../../src/NetScope.Repair/PlatformExperimentManifest.cs)：平台实验资产类型与 not_run 校验器。
- [RepairPlan.cs](../../src/NetScope.Repair/RepairPlan.cs)：计划/动作/no-op/依赖组/授权条款/摘要。
- [RepairPlanBuilder.cs](../../src/NetScope.Repair/RepairPlanBuilder.cs)：只生成预览，不执行任何写入。
- [RepairAuthorizationBinding.cs](../../src/NetScope.Repair/RepairAuthorizationBinding.cs)：授权绑定字段与失效校验。
- [RepairProposalGuard.cs](../../src/NetScope.Repair/RepairProposalGuard.cs)：DX-010 约束不足反例守卫。

测试与实验资产（仅新建，全部 fake/合成数据）：

- [NetScope.WindowsIntegration.Tests.csproj](../../tests/WindowsIntegration/NetScope.WindowsIntegration.Tests.csproj)
- [RepairFixtures.cs](../../tests/WindowsIntegration/RepairFixtures.cs)
- [RepairPlanMainChainTests.cs](../../tests/WindowsIntegration/RepairPlanMainChainTests.cs)
- [RepairActionClosureTests.cs](../../tests/WindowsIntegration/RepairActionClosureTests.cs)
- [RepairPlanActionTests.cs](../../tests/WindowsIntegration/RepairPlanActionTests.cs)
- [RepairPreflightTests.cs](../../tests/WindowsIntegration/RepairPreflightTests.cs)
- [RepairRevalidationTests.cs](../../tests/WindowsIntegration/RepairRevalidationTests.cs)
- [RepairNoOpAndDx010Tests.cs](../../tests/WindowsIntegration/RepairNoOpAndDx010Tests.cs)
- [PlatformCapabilityTests.cs](../../tests/WindowsIntegration/PlatformCapabilityTests.cs)
- [RepairAuthorizationTests.cs](../../tests/WindowsIntegration/RepairAuthorizationTests.cs)
- [RepairClockTests.cs](../../tests/WindowsIntegration/RepairClockTests.cs)
- [platform-experiment-manifest.json](../../tests/WindowsIntegration/experiments/platform-experiment-manifest.json)

解决方案文件仅加两个新项目（未改现有项目条目）：

- [NetScopeRepair.slnx](../../NetScopeRepair.slnx)：`src/NetScope.Repair` 与 `tests/WindowsIntegration` 两条新条目。

未修改：`src/NetScope.Domain/**`、`src/NetScope.Application/**`、`src/NetScope.Collectors.Windows/**`、`src/NetScope.Adapters/**`、`src/NetScope.Desktop/**`、`src/NetScope.Probes/**`、`src/NetScope.Storage/**`、`src/NetScope.Cli/**`、其他测试工程、README、启动入口、客户端配置和系统网络。工作树中其它子代理/T09-R2 的未提交修改保持原样。

## 3. 关键设计与偏差

- **依赖收敛**：`NetScope.Repair` 只引用 `NetScope.Domain`，不引用 Application/Collectors，避免与正在修改 Application/Adapters/Desktop/Probes/Storage 的并行工作冲突，也保证本任务能独立构建。计划/事实模型自带 typed 字段，不依赖 `InterfaceState.int?`。
- **生产默认全关**：`PlatformCapabilityMatrix.UnverifiedDefault` 没有任何验证记录；`PlatformCapability.Verified` 只是未来实验导入的受控工厂，要求 experiment id、证据、验证时间与完整检查项，并且 `PlatformCapabilityMatrix.ForSyntheticTests` 生成的矩阵带 `IsSynthetic=true`，计划构建器禁止 synthetic 矩阵产生 `ExecutionAllowed=true`。
- **RecoverTransaction 例外**：动作枚举包含它是为了与 05 的 broker 协议保持闭集一致；但本任务没有 journal，计划请求中的 RecoverTransaction 会被 `recover_requires_journal` 拒绝，能力矩阵也保持 unavailable。
- **默认路由归属更严格**：除受保护上行 GUID 外，`TargetOwnsDefaultRoute` 未知也会拒绝（`uplink:target_default_route`），避免把“不知道目标是否已成为真实出口”当成安全。
- **没有新增路由动作**：闭集里没有任何创建/删除路由或“把 metric 改成 1”的动作；no-op 测试覆盖 metric=0 且 auto 关闭、不忽略默认路由时保持 no-op。
- **平台环境事实未采集**：矩阵环境默认是 `unverified`（build 0、driver unknown、api unverified），未把本机真实 Windows build/driver 写成已验证环境。平台实验资产中的行也保持 `executed=false`、`result=not_run`。

## 4. 执行的命令、环境、退出码

环境：Windows、普通权限用户；`.NET SDK 10.0.403`（`dotnet --version` 输出 `10.0.403`）；未使用管理员权限。仅构建 Repair 与 WindowsIntegration，没有构建整个 `NetScopeRepair.slnx`。

| 命令 | 退出码 | 结果 |
|---|---:|---|
| `dotnet build src/NetScope.Repair/NetScope.Repair.csproj -v minimal` | 0 | 0 警告 / 0 错误 |
| `dotnet build tests/WindowsIntegration/NetScope.WindowsIntegration.Tests.csproj -v minimal` | 0 | 0 警告 / 0 错误 |
| `dotnet test tests/WindowsIntegration/NetScope.WindowsIntegration.Tests.csproj --no-build --logger "console;verbosity=minimal" --logger "trx;LogFileName=t13-windows-integration.trx" --results-directory .artifacts/t13-final-test-results` | 0 | 121 通过 / 0 失败 / 0 跳过，115 ms |
| `dotnet sln NetScopeRepair.slnx list` | 0 | 仅解析并列出 18 个项目，确认新增两条；未构建整个解决方案 |

日志与 TRX：

- [t13-final-build-repair.log](../../.artifacts/t13-final-build-repair.log)
- [t13-final-build-tests.log](../../.artifacts/t13-final-build-tests.log)
- [t13-final-test.log](../../.artifacts/t13-final-test.log)
- [t13-windows-integration.trx](../../.artifacts/t13-final-test-results/t13-windows-integration.trx)
- [t13-test-list.log](../../.artifacts/t13-test-list.log)

`.artifacts/` 被 `.gitignore` 忽略，仅本机保留，未上传。

## 5. 测试数量与覆盖

**121 个实际执行测试用例，0 失败，0 跳过**（76 个 `[Fact]` + 45 个 `[Theory]` 行，共 9 个 Theory 方法）。

| 测试文件 | 用例数 | 覆盖内容 |
|---|---:|---|
| [RepairPlanMainChainTests.cs](../../tests/WindowsIntegration/RepairPlanMainChainTests.cs) | 6 | 三字段主链、typed before/desired、依赖、摘要稳定性、绑定字段、无执行权 |
| [RepairActionClosureTests.cs](../../tests/WindowsIntegration/RepairActionClosureTests.cs) | 35 | 闭集、通用/未知动作拒绝、uint 范围、token 严格性、PersistentStore、重复动作、RecoverTransaction |
| [RepairPlanActionTests.cs](../../tests/WindowsIntegration/RepairPlanActionTests.cs) | 12 | 每动作 1 正例 + 2 反例 + 1 缺证 |
| [RepairPreflightTests.cs](../../tests/WindowsIntegration/RepairPreflightTests.cs) | 29 | 可信归属、五种不可信来源、空候选/歧义、受保护上行、非 TUN、身份缺失、跨 boot、TTL、时钟、重复事实、归属/上行来源过期、自动 metric 读取失败 |
| [RepairRevalidationTests.cs](../../tests/WindowsIntegration/RepairRevalidationTests.cs) | 6 | ifIndex 漂移、LUID/设备重建、外部字段漂移、上行变化、未变化重验通过 |
| [RepairNoOpAndDx010Tests.cs](../../tests/WindowsIntegration/RepairNoOpAndDx010Tests.cs) | 9 | 全/部分 no-op、store 隔离、身份/族/store 作用域、派生值反例、DX-010 反例、metric=0 不改成 1 |
| [PlatformCapabilityTests.cs](../../tests/WindowsIntegration/PlatformCapabilityTests.cs) | 13 | 默认 unavailable、PersistentStore/IPv6 disallowed、验证记录完整性、耦合检查、synthetic 不可执行、实验资产 not_run/pass 拒绝 |
| [RepairAuthorizationTests.cs](../../tests/WindowsIntegration/RepairAuthorizationTests.cs) | 7 | 计划摘要变化、过期、用户/会话/目标、逐动作同意、依赖组、UAC 不等于同意 |
| [RepairClockTests.cs](../../tests/WindowsIntegration/RepairClockTests.cs) | 4 | 跨进程/跨 boot 单调不可比、UTC TTL/偏移、broker 租约 |

映射 QA：

- **QA-006**：metric 与请求失败同时存在时不被“修复”文本掩盖；本任务以 typed no-op/派生值反例覆盖 metric 语义（真实探测复测仍不在本轮）。
- **QA-012**：ifIndex 变化必须重验，旧索引不写回；`TargetDrift` 测试覆盖，结合稳定 GUID/LUID/设备实例。
- **QA-020**：分族处理；IPv6 在 capability 与 planner 层均为 disallowed，不借用 IPv4 结果。
- **QA-027**：外部字段漂移检测；重验发现当前值不等于计划 before 时整项授权失效，不覆盖他人修改。
- **QA-029**：恢复失败/不可恢复路径在当前范围只到“记录并拒绝/恢复字段模型”；broker recovery 实际能力保持 unavailable，未声称通过。
- **QA-030**：no_op 与 aborted 语义；无差异计划 `no_op`、不生成授权动作；缺证/策略拒绝为 refused。
- **DX-010**：见第 6 节反例。

## 6. DX-010 当前约束不足反例

`NetScope.Application.ProposedChange`（DX-010 输入）只有 `Field`、`TargetInterfaceGuid`、`DesiredValue`、`Rationale`。它无法表达：

1. store（ActiveStore/PersistentStore）；
2. 地址族；
3. 原始 uint 范围与 manual/derived 语义；
4. 当前 boot 的 LUID/ifIndex/compartment/设备实例及来源时间、状态、TTL；
5. 核心→TUN 的可信归属。

测试 [Dx010_untyped_proposal_is_structurally_insufficient_for_a_plan](../../tests/WindowsIntegration/RepairNoOpAndDx010Tests.cs) 断言这些缺口全部以 `proposal_untyped` 返回；`CanBecomePlan` 恒为 false。`disable_default_routes` 也不会被静默映射成 `ignore_default_routes`。

更强的反例：现有派生/有效 metric 20 与期望字符串 `"20"` 按旧方式字符串比较会得到 no-op，但 typed 比较识别 `Kind=effective_derived` 后返回 `value_not_manual`，不把派生值当手动基线，也不作为 no-op。见 [A_naive_dx010_string_comparison_would_wrongly_claim_no_op_on_a_derived_value](../../tests/WindowsIntegration/RepairNoOpAndDx010Tests.cs) 与 [A_derived_metric_equal_to_the_desired_value_is_not_a_no_op](../../tests/WindowsIntegration/RepairNoOpAndDx010Tests.cs)。

## 7. 能力关闭矩阵（本轮实际状态）

没有任何 `available` 记录。以下为本轮代码可解析的闭集与状态；“原因”均来自能力矩阵/前置校验，不是推测。

| 动作 | Store | Family | 状态 | 原因摘要 |
|---|---|---|---|---|
| SetTunIpv4InterfaceMetric | active_store | ipv4 | **unavailable** | 无隔离 VM 实验：setter/getter/读回/精确恢复/非目标字段/PersistentStore 副作用未验证 |
| SetTunIpv4AutomaticMetric | active_store | ipv4 | **unavailable** | 同上，且 AutomaticMetric↔InterfaceMetric 耦合未按平台实测 |
| SetTunIpv4IgnoreDefaultRoutes | active_store | ipv4 | **unavailable** | 同上；未验证 setter/读回/恢复/跨 store 副作用 |
| 三个字段动作 | persistent_store | ipv4 | **disallowed** | V1 只开放通过持久性/传播验证的 ActiveStore；禁止静默双写 |
| 三个字段动作 | active_store | ipv6 | **disallowed** | V1 只开放 IPv4；IPv6 需分族实验与证据 |
| RecoverTransaction | active_store | ipv4 | **unavailable** | 事务 journal 与精确恢复尚未实现（T15） |
| 任何通用 SetProperty / 脚本 / 路由动作 | — | — | **拒绝** | 不在动作闭集，parser 层拒绝 |
| 非 TUN / 真实上行 / 未知上行 / 未知默认路由归属 | — | — | **拒绝** | 前置校验拒绝 |

平台环境维度（Windows build × driver × API）本轮为未验证：`PlatformEnvironment.Unverified`（build 0、driver unknown、api unverified），没有任何验证记录。平台实验资产 [platform-experiment-manifest.json](../../tests/WindowsIntegration/experiments/platform-experiment-manifest.json) 有 4 行（三字段 + RecoverTransaction），全部 `executed=false`、`result=not_run`，前置条件要求 isolated_vm/console/verified_checkpoint/recovery_owner；校验器拒绝任何 claimed pass。

## 8. 证据路径

- 构建：见第 4 节日志。
- 测试：`121/121` 通过，TRX 见 [t13-windows-integration.trx](../../.artifacts/t13-final-test-results/t13-windows-integration.trx)。
- 平台实验资产：见上；**没有 raw 日志，因为没有任何平台实验被执行**。资产中的 `raw_log_path` 明确标注 `(not created)`。
- 只读 Collectors 核对（未运行真实查询、未改语义）：阅读了 [Sources.cs](../../src/NetScope.Collectors.Windows/Sources.cs)、[WindowsReadSeams.cs](../../src/NetScope.Collectors.Windows/WindowsReadSeams.cs) 与 [NativeNetworkApi.cs](../../src/NetScope.Collectors.Windows/NativeNetworkApi.cs)。现有 `NativeNetworkApi.InterfaceMetrics` 含 `ulong Luid` 与原始 `uint Metric`，但为 internal 且未接入 Repair；现有 `InterfaceState.Metric` 是 `int?` 且没有 compartment/设备实例。**因此本轮没有修改或复用 Collectors**，Repair 使用可注入的 preflight 事实 seam；真实 Windows 事实生产器未实现，留待后续任务（见限制）。

## 9. 只读/权限/隐私边界

- 没有真实网络写操作；没有 `SetIpInterfaceEntry`、netsh、PowerShell、注册表、服务、计划任务、启动项、桌面 TUN 脚本、UAC。
- 没有启动/停止 Nice、Apex、mihomo 或任何客户端；没有读取真实客户端配置、订阅、凭据、环境代理值或系统代理设置。
- 测试只用内存中的合成事实与合成能力矩阵；唯一文件读取是测试输出目录中的实验 manifest。
- 没有外部探测、DNS 解析、connect、上传或付费模型调用。
- 没有修改别的子代理/T09-R2 文件；没有 `git reset/clean/checkout`、没有提交或推送。
- 只构建了 `NetScope.Repair` 与 `tests/WindowsIntegration`，没有构建整个解决方案。

## 10. 已知限制与未验证项

1. **没有执行任何平台实验**：没有隔离 VM、console、checkpoint、恢复负责人；能力矩阵不宣称任何 Windows build/driver/API 组合可用。T13 的“VM 实验”部分仍未完成，不能把本报告当作平台实验通过。
2. **没有 native Repair 实现**：三字段 setter/getter、ActiveStore/PersistentStore 分离、API 副作用、耦合闭包、精确恢复都没有代码或实测证据；本轮只交付计划/矩阵/前置 seam。`PlatformCapability.Verified` 是未来受控导入入口，当前无调用者。
3. **真实事实生产器未接**：preflight 事实在测试中是 fake；真实 Windows 来源（包含 LUID、compartment、设备实例、原始 uint、GetBestRoute2/等价选路、可信 TUN 归属）尚未实现。由于没有可信归属来源，真实环境中的计划应保持拒绝，而不是猜测。
4. **没有 Desktop/Application 接线**：用户看不到计划预览；T16 负责计划、逐动作确认、事务进度的 UI。本轮没有修改 Desktop。
5. **没有 broker/IPC/journal/授权签发**：T14/T15 负责；本轮只定义授权绑定所需字段与校验，`binding_issued=false`。没有一次性 UAC、ACL 管道、nonce 或 journal。
6. **恢复与部分失败未实测**：QA-027/029 的完整 CAS 回滚、rollback_failed/recovery_required 需要 T15 journal 与 VM；本轮只有漂移/拒绝模型。
7. **时钟**：只定义并测试了跨进程单调不可比、UTC TTL 与 broker 租约形状；没有真实 desktop/broker 双进程时钟跳变实验。
8. **平台环境**：未采集本机 Windows build/driver/API 可用性，也未把实验 manifest 的占位环境填成真实值；这是有意的，避免把未验证环境写成已验证。
9. **测试性质**：121 个测试全部为 fake/合成数据；没有真实客户端配置、真实网络或系统注册表参与。测试通过只证明计划/拒绝逻辑，不证明平台动作可用。
10. **`PlatformCapability.Verified` 的滥用风险**：任何未来调用者都必须通过受控导入并保留 synthetic/real 区分；T14 前应由审查者确认没有生产路径调用它。

## 11. 是否有真实网络操作

**无。** 逐项：无 connect/DNS/HTTP；无接口 metric 读取或写入；无 route/DNS/代理/注册表变更；无提权/UAC；无客户端启动/停止；无桌面脚本；无平台实验；无上传/提交。

## 12. 建议下一任务（不自动执行）

建议本报告复核通过后，由架构/安全审查者决定是否进入 **T14（broker 协议与身份）**。T14 的前置条件仍包括：

- 独立 VM 上完成 T13 三字段平台实验（当前 0 执行），否则 T14 的真实提权/写入能力必须保持关闭；
- 可信 TUN 归属来源与 Windows 平台事实生产器（LUID/compartment/设备实例/原始 uint/选路）落地；
- 审查者确认 `PlatformCapability.Verified` 仅由受控实验导入使用，且 `IsSynthetic` 矩阵不进入生产。

本轮停止在 T13，不领取 T14，不创建提交或推送。
