# R1-B · T04 / F06 + F07：统一地址族语义合同，保留 CONNECT 407 归属

任务：R1-B（T04 补修，关闭外部复核 F06(P1) 与 F07(P2)；本任务同时是 R1 公共合同 owner）
日期：2026-11-06（Asia/Shanghai）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，基线 HEAD `a4b07a0`（未新增提交、未推送）
依据：`docs/reports/G3-final-review.md` §4 F06/F07、`docs/reports/evidence/G3-review-20261106/findings-localized.json`（F06/F07）、`repro-m1-results.json`；`docs/05`、`docs/03 §3/§8`、`docs/07` QA-002/010/020/021。
工作树基线：包含 T04-R2、T06-R3、T07-R2、T08-R2、T09-R2、T10—T16 等并行未提交修改；本任务只修改 `src/NetScope.Probes`、`src/NetScope.Application/Probing.cs`、`src/NetScope.Diagnostics`、`tests/Unit` 与报告。

**结论：F06 的生产者/Application 合同/诊断可比性/规则（含 DX-011）部分已按“请求策略族 / 实际连接族 / 代理端目标族未知”重做并有回归；F07 已从真实 `HttpRequestException` 保留 CONNECT 407、代理端点归属并分类 `AuthenticationRequired`，新增 127.0.0.1-only loopback 407 反例。F06 的 Desktop UI 三层文案不在本任务允许写范围，仍未消费新字段，故 F06 不能写“已完全关闭”；列为未解决项。** 未提交、未推送；未做真实外部联网、UAC、系统代理/TUN/路由写入。

## 1. 合同（R1 公共合同，已通知 Lead）

### 1.1 问题与目标

原实现把“计划请求的族”和“实际观测的族”压进 `ProbeResult.AddressFamily`：null/any 被判不可比，显式 ipv4/ipv6 时系统默认/显式代理路径直接 `Unsupported`，导致整组不可比且 DX-011 拿不到真实兼容输入。目标：显式区分 **请求策略族 / 本进程实际连接族 / 代理端目标族**，代理端目标族保持诚实未知，保留可达的降级诊断。

### 1.2 字段（`NetScope.Application.ProbeResult`，向后兼容的加法）

| 字段 | 类型 | 语义 | 生产规则 |
|---|---|---|---|
| `AddressFamily`（保留） | `string?` | 旧字段：计划/请求原始值（`null`/`any`/`ipv4`/`ipv6`），旧事件含义不变 | 新 producer 继续写原始请求值 |
| `RequestedAddressFamily`（新） | `string?` | 请求策略族，规范化 `any`/`ipv4`/`ipv6`；空/`any`/`dual`/`unspecified` → `any` | 每个新结果必填；旧事件回退 `AddressFamily` 再回退 `any` |
| `LocalConnectionAddressFamily`（新） | `string?` | 本进程真实建立的 TCP 连接族：无显式代理=到目标；显式代理=到代理端点；系统默认=handler 实际拨号的下一条腿（若观测）；未知 `null` | 只由 connect seam 的真实 socket 地址推导，禁止用策略值填充 |
| `TargetAddressFamily`（新） | `string?` | 已证实的目标族；只有无显式代理且本进程自己解析/dial 目标时才等于连接族 | 其他路径必须 `null` |
| `TargetAddressFamilyStatus`（新） | `string?` | `observed` / `unknown_through_proxy` / `unknown_system_default` / `unknown_after_attempt` / `not_attempted` | 新结果必填；代理端目标族永远 `unknown_through_proxy` |

补充：`unknown_after_attempt` 是为“请求已在本机尝试但未建立连接、因此目标族无法证实”的本地路径补的第五个状态；它与 `not_attempted`（请求未发出）严格区分，合同草案提交后追加说明。`TransportOutcome` 有同名字段（不持久化）；`ProbeRequest`/`ProbeGroupPlan` 无新字段，策略由现有 `AddressFamily` 规范化得到。

### 1.3 判定表（实现后的实际行为）

**A. 组级执行可比性**：仍要求全部路径 started、window/boot/options key 非空、monotonic 有效、组内启动跨度 ≤2000ms、无 `TopologyChanged`、无 budget 耗尽/用户取消、请求策略可解析。**变化**：不再要求每路径状态仅 `Ok/Failed/Timeout`，`Unsupported` 的诚实缺口不再使整组不可比；`Cancelled/AccessDenied/Unknown` 仍视为观察缺口（保持 DX-008 既有语义）。`null`/`any` 是合法策略。

**B. 诊断配对**：`DiagnosticComparability.Complete` 使用规范化请求策略，不再要求目标族已知；`SameProbeScope` 比较规范化请求策略（`any`/`ipv4`/`ipv6`），仍要求相同 target/path/proxy endpoint/method/options/window/boot、双方 `TimingComparable`、无 `TopologyChanged`。实际族是观测事实，不是 scope 键。

**C. DX-011**：`SameRecordProbeWindow` 比较监听记录族与探测的 **`LocalConnectionAddressFamily`**（旧事件回退 `AddressFamily` 的 `ipv4/ipv6`）；显式代理 407/成功 TCP 可证明“到本地代理端点的连接族”，但不冒充代理端目标族。

**D. F07 407**：`HttpRequestException.HttpRequestError == ProxyTunnelError` 且 `StatusCode == 407` 时，`Status=Ok`、`HttpStatus=407`、`TransportError="proxy_authentication_required"`、`FailedStage="proxy_connect"`、`FailedEndpoint="proxy"`（显式代理路径）、保留 `Connection`（到代理端点）与 `ProxyIsLocal`，分类 `AuthenticationRequired`。无 `StatusCode` 的 `ProxyTunnelError` 保持原有 `Failed` 行为。

**E. 传输行为**：显式 ipv4/ipv6 不再让系统默认/显式代理路径在发送前 `Unsupported`；请求照常发出，目标族标记 `unknown_system_default`/`unknown_through_proxy`；无显式代理仍真实强制并记录 `observed`。显式目标策略不会过滤代理腿的地址族（否则 IPv6 目标策略会错误地要求 IPv4 代理端点）。

**F. UI**：`src/NetScope.Desktop` 不在本任务写范围；`ProbeComparisonExplainer` 的三层文案/可比性展示尚未消费新字段（它仍把 `AddressFamily` 空/`any` 当“未指定族”而判不可比）。**F06 的 UI 部分未关闭**，见 §5。

## 2. 实际修改文件

生产代码（均在允许写范围）：

- [Probing.cs](../../src/NetScope.Application/Probing.cs)：新增 `ProbeFamilyPolicy`（`Normalize/IsKnown/IsEnforcedFamily`）、`ProbeTargetFamilyStatus` 常量，以及 `ProbeResult` 的 4 个加法字段（旧字段保留）。
- [ProbeTransport.cs](../../src/NetScope.Probes/ProbeTransport.cs)：`TransportOutcome` 增加请求/连接/目标族与目标状态字段。
- [ProbeAttemptObservation.cs](../../src/NetScope.Probes/ProbeAttemptObservation.cs)：从真实 socket 地址派生 `ConnectedAddressFamily`（不猜测）。
- [HttpProbeTransport.cs](../../src/NetScope.Probes/HttpProbeTransport.cs)：显式族不再整条路径 gap；`WithFamilyFacts` 仅从真实观测发布三族；显式目标策略不影响代理腿；`Classify` 保留 `ProxyTunnelError + StatusCode`（407 等）为 HTTP 响应事实；`Gap` 带请求策略与 `not_attempted`。
- [HttpProbeRunner.cs](../../src/NetScope.Probes/HttpProbeRunner.cs)：写入新字段；组级可比性允许 `Unsupported` 诚实缺口；策略非法时组不可比；`NotStarted` 写 `NotAttempted`。
- [DiagnosticComparability.cs](../../src/NetScope.Diagnostics/DiagnosticComparability.cs)：`KnownPolicy`/`RequestedPolicy`/`TargetFamily`/`ConnectionFamily`；`Complete` 按策略；`SameProbeScope` 按策略；`SameRecordProbeWindow` 按本地连接族；probe scope 的 `AddressFamily` 归一为策略；旧事件（无新字段）使用既有 `AddressFamily` 回退（迁移路径）。
- [Dx002FakeIpContext.cs](../../src/NetScope.Diagnostics/Rules/Dx002FakeIpContext.cs)：IPv4 判断改用已证实的 `TargetFamily`（旧事件回退），不再把请求策略冒充目标族。
- [Dx008IntermittentFailure.cs](../../src/NetScope.Diagnostics/Rules/Dx008IntermittentFailure.cs)：分区使用规范化请求策略，`null`/`any` 不再被拆成不可合并的两组。

测试（tests/Unit）：

- 新增 [R1BLoopbackConnectProxy.cs](../../tests/Unit/R1BLoopbackConnectProxy.cs)：仅绑定 `127.0.0.1` 的 CONNECT 假代理，读 CONNECT 请求行后固定返回 407，不转发、不解析目标、不外部联网。
- 新增 [R1BProbeContractTests.cs](../../tests/Unit/R1BProbeContractTests.cs)（7 个）：null/any 组可比；显式族仍真正发送 proxy/system 请求；真实 loopback CONNECT 407（含 407/端点/分类/族字段）；IPv6 目标策略不影响 IPv4 代理腿；runner 端到端 407。
- 新增 [R1BFamilyContractTests.cs](../../tests/Unit/R1BFamilyContractTests.cs)（17 个）：策略规范化表；runner 保留三族；Unsupported 缺口不拖垮整组；DX-011 用本地连接族获得兼容输入；本地族未知/不同时拒绝；DX-008 合并 null/any 但分开 v4/v6；**旧 JSON 事件（无新字段）经严格 reader 回放仍 clean，并仍可被 DX-011 使用**。
- 修改既有测试以匹配新合同（不是放宽 unknown）：
  - [ProbeTransportTests.cs](../../tests/Unit/ProbeTransportTests.cs)：旧“显式族在代理/系统路径直接 Unsupported”改为“仍发送请求、目标族 unknown、策略可查”；新增 407 loopback 断言。
  - [ProbeMetadataTests.cs](../../tests/Unit/ProbeMetadataTests.cs)：`AddressFamily=null` 现在是合法 no-constraint；缺族用非法值 `carrier-pigeon` 验证不可比。
  - [T04R2ProbeObservationTests.cs](../../tests/Unit/T04R2ProbeObservationTests.cs)：显式族代理路径改为真实发送；无 connect seam 时不伪造连接事实。
  - [DiagnosticScopeRegressionTests.cs](../../tests/Unit/DiagnosticScopeRegressionTests.cs)：DX-008 “family 缺失”用非法族值而不是 null。

未修改：Desktop/Probes 以外的会话、修复、发布代码；`Theme`/UI；Export/Replay/Storage 生产代码；README/.gitignore/脚本/设计包。

## 3. 修前失败 / 修后通过

在实现前先写 [R1BProbeContractTests.cs](../../tests/Unit/R1BProbeContractTests.cs) 并运行旧实现，日志：`%TEMP%\NetScopeRepair-R1B\prefix.log` / `prefix.trx`：

| 测试 | 修前实际 | 修前结果 |
|---|---|---|
| `A_null_family_plan_is_a_valid_no_constraint_policy_and_stays_comparable` | `TimingComparable=false` | 失败 |
| `Explicit_family_on_the_explicit_proxy_path_still_sends_a_real_request` | handler 调用 0 次、`Unsupported` | 失败 |
| `Explicit_family_on_the_system_default_path_still_sends_a_real_request` | handler 调用 0 次、`Unsupported` | 失败 |
| `A_real_loopback_CONNECT_407_keeps_the_http_status_and_proxy_attribution` | `Failed`、`HttpStatus=null` | 失败 |
| `An_any_family_plan_is_a_valid_no_constraint_policy_and_stays_comparable` | 已为 true（runner 原就接受 `any`，但诊断层不接受；该点由后续诊断测试覆盖） | 通过（保留） |

修后同一组测试全部通过（`%TEMP%\NetScopeRepair-R1B\r1b6.log` + `r1b6.trx`，24/24）。另外新增的旧 JSON 兼容测试在修前也可读旧事件（旧生产端没有新字段），修后继续 clean 并验证迁移回退路径。

## 4. 命令与退出码

环境：Windows、普通权限；.NET SDK `10.0.403`；未提权、未运行 UAC、未改系统代理/TUN/路由/服务，仅 loopback/内存测试。

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet test tests/Unit/NetScope.Unit.Tests.csproj`（修前 R1B filter） | 1 | 4 失败 / 1 通过（见 §3） |
| `dotnet build NetScopeRepair.slnx -v q` | 0 | 0 警告 / 0 错误（全解决方案依赖自查） |
| `dotnet test tests/Unit/NetScope.Unit.Tests.csproj` | 0 | **344 通过 / 0 失败 / 0 跳过**（含 R1-B 24） |
| `dotnet test tests/Adapters/NetScope.Adapters.Tests.csproj` | 0 | 99 通过 / 0 失败 / 0 跳过 |
| `dotnet test tests/WindowsIntegration/NetScope.WindowsIntegration.Tests.csproj` | 0 | 273 通过 / 0 失败 / 0 跳过 |
| `dotnet test tests/Contract/NetScope.Contract.Tests.csproj` | 0 | 88 通过 / 0 失败 / 0 跳过（事件合同法未破） |
| `dotnet test tests/Replay/NetScope.Replay.Tests.csproj` | 0 | 97 通过 / 0 失败 / 0 跳过（旧事件回放） |
| `dotnet test tests/Export/NetScope.Export.Tests.csproj` | 0 | 52 通过 / 0 失败 / 0 跳过 |
| `dotnet test tests/Desktop/NetScope.Desktop.Tests.csproj` | 0 | 135 通过 / 0 失败 / 0 跳过（回归；未改 UI） |

日志/TRX 仅在本地 `%TEMP%\NetScopeRepair-R1B\`（未入仓）：`prefix.log`、`r1b6.log`、`unit-final.log`、`adapters.log`、`winint.log`、`NetScope.Contract.Tests.log`、`NetScope.Replay.Tests.log`、`NetScope.Export.Tests.log`、`desktop.log`、`solution-build.log` 及对应 `.trx`。

## 5. 未解决 / 未验证项（必须保留）

1. **F06 的 Desktop UI 部分未关闭**：`src/NetScope.Desktop/Services/ProbeComparisonExplainer.cs` 仍按旧字段/旧 `ipv4|ipv6` 判定“familySpecified”，不会显示“请求策略族 / 实际连接族 / 代理端目标族未知”三层；本任务写范围不含 Desktop，未修改。建议 R1-C/UI owner 消费 `RequestedAddressFamily`、`LocalConnectionAddressFamily`、`TargetAddressFamilyStatus` 后关闭 F06 的 UI 项。
2. **真实外部探测未执行**：没有对外部网站/真实域名做任何探测；loopback 假代理与假 DNS 边界的结论不外推到真实网络。未在隔离 VM 上跑真实 Windows 网络栈。
3. **代理端目标族永远未知**（设计如此）：没有也无法从代理取得目标族；显式策略在代理/系统路径只证明请求已发出与本地腿事实，不代表目标族被强制。
4. **407 只覆盖 CONNECT 407**：未覆盖 SOCKS、NTLM/Negotiate 多步认证、代理返回其它状态（如 401/403 on CONNECT）或代理在 CONNECT 前断开；这些路径保持原有/未知行为，未伪称认证。
5. **旧事件的迁移回退是有限的**：仅当旧 `ProbeResult` 完全没有 R1-B 字段时，诊断才把旧 `AddressFamily` 当作目标/连接族回退；`any` 旧事件无法证明本地连接族，DX-011 可能仍不匹配。新包被旧二进制读取不在兼容承诺内（新字段会被旧严格 reader 拒绝）。
6. **没有 UI 自动化/截图证据**：本轮只做数据/规则/transport 回归，未运行真实 UI；Lead 统一负责 9 页渲染与 smoke。
7. **未改会话/修复/发布物**：无 `ObservationSession`、Repair/Broker、Export/Replay 生产代码改动；这些套件回归通过仅表示未破坏。

## 6. 合同与范围声明

- 旧 JSON 兼容：新增字段全部可空，未删除/重命名既有属性；旧事件反序列化/回放测试通过；旧二进制读取新事件不在承诺内（严格 reader 会拒绝未知字段），需要时以显式版本迁移处理。
- 不为了通过测试把 unknown/Partial 改正常：`AccessDenied/Cancelled/Unknown` 仍是观察缺口且不参与可比性；`TargetAddressFamily`/`TargetAddressFamilyStatus` 保持 unknown；未清除 `TopologyChanged`；未拼接不可比周期。
- 未改真实系统代理/网络/TUN/路由；未提权；仅 loopback/内存测试。
