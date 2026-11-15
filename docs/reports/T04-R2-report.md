# T04-R2：真实 DNS/解析与连接阶段事实进入探测结果

任务：T04-R2（原施工单定向补修，不重做 M0/M1，不领取后续任务）
日期：2026-11-05（Asia/Shanghai）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：原设计 02/03/07/09，QA-002/003/004/007/020/021；`docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T04-R2 卡
工作树基线：包含 T09-R2 的未提交修改（适配器/客户端页）；本任务在其之上继续，未执行 `git reset/clean/checkout`，未覆盖他人文件

**结论：补上了可注入的本地解析边界与真实连接观测边界。默认 handler 现在只在真实 socket 建立时记录解析事实：`no-explicit-proxy` 路径发布本次真正参与连接的本地解析地址/来源/UTC 时间与真实连接地址；`explicit-http-proxy` 路径只记录代理端的连接事实，目标解析保持 `proxy/unknown`（不把代理 IP 或本地查询冒充远端解析）；`system-default` 路径保持解析未知，仅保留“role=unknown”的连接事实。连接复用被明确标记为“本轮未重新解析”，不冒充新样本；地址族只在能真正拨号的目标侧（`no-explicit-proxy`）强制，并用真实 loopback socket 证明 IPv4/IPv6 选择与失败，代理/系统路径返回独立的 Unsupported 缺口，不伪造分族成功；取消/预算/DNS 失败保留归因或未知。证书校验与 system-default 代理策略未改，且用自签名 loopback TLS 反例证明仍会失败。**

## 1. 用户可观察行为（结果层）

1. **真实解析事实进入结果**：`ProbeResult.ResolvedAddresses` 不再恒为空。`no-explicit-proxy` 路径经过代理关闭的真实连接时，记录本次调用操作系统解析器得到的地址、来源（`ResolutionSource`，默认 `system-dns`）与 UTC 时间（`ResolvedAt`）。DX-002 等规则第一次能消费真实地址。
2. **真实连接事实**：新增 `ProbeResult.Connection`（`ProbeConnectionFact`）：`EndpointRole`（target/proxy/unknown）、`RemoteAddress`/`RemotePort`（真正连上的 IP 与端口）、`Reused`（是否复用连接）。未连上时 `RemoteAddress` 保持 null。
3. **代理端解析区分**：`explicit-http-proxy` 路径的 `ResolutionLocation` 仍为 `proxy`，`ResolvedAddresses` 为空；连接事实 role=proxy，记录的是真实拨号到的代理地址。目标域名从未在本机解析。
4. **复用连接不冒充新解析**：第二次命中连接池的探测返回 `Connection.Reused=true`，`ResolvedAddresses` 为空、`ResolvedAt/ResolutionSource` 为 null，`Notice` 明确写“复用了池化连接，本轮没有新的解析/连接观测”。
5. **阶段归因**：默认 handler 自己解析并拨号，因此能准确给出 `resolve`（本地解析失败/无所需地址族）与 `connect`（TCP 失败）以及角色（target/proxy/unknown）；`tls`/`proxy_connect` 仍按原异常规则，归因不明保持 `unknown`。
6. **地址族**：`ipv4`/`ipv6` 在 `no-explicit-proxy` 路径上被真实强制（解析过滤 + 指定族的真实 socket），并有 loopback 反例：IPv4-only 服务在 `ipv6` 约束下不会被使用，结果保持失败。`explicit-http-proxy` 返回 `address_family_not_enforced_through_proxy`，`system-default` 返回 `address_family_unknown_for_system_default`；无法识别的族值返回 `unsupported_address_family`。三者都是 Unsupported 缺口，不发请求，不冒充成功。
7. **system-default 不改变策略**：仍使用 .NET DefaultProxy（含环境/平台设置与 bypass），只是新增了连接观测；解析位置/地址保持 `unknown`，代理是否本机保持 null。
8. **证书校验不变**：未新增任何证书回调/校验关闭；自签名 loopback HTTPS 反例仍以 `tls_failure` 失败，且失败前已记录的连接事实仍可见。

## 2. 修前失败证据（缺陷复现）

先写 2 个只使用既有 API 的反例并运行在未修改实现上：

| 反例 | 旧行为 |
|---|---|
| `Local_transport_did_not_publish_the_addresses_that_really_answered` | 真实 loopback 请求返回 204，但 `ResolvedAddresses` 为空 |
| `Address_family_constraint_was_reported_unsupported_instead_of_enforced` | `AddressFamily=ipv4` 时返回 `Unsupported / address_family_not_enforced`，一个请求都没发 |

运行结果：**0 通过 / 2 失败，退出码 1**（修后通过）。关键输出：

```text
失败 ...Local_transport_did_not_publish_the_addresses_that_really_answered [2 s]
  Assert.NotEmpty() Failure: Collection was empty
失败 ...Address_family_constraint_was_reported_unsupported_instead_of_enforced [3 ms]
  Assert.Equal() Failure: Values differ
  Expected: Ok
  Actual:   Unsupported
失败!  - 失败: 2，通过: 0，已跳过: 0，总计: 2
```

本地日志（未提交，见第 8 节）：`%TEMP%\NetScopeRepair-T04R2\prefix-failures.log`、`prefix.trx`。

## 3. 实际修改文件

探测实现（Probes）：

- `src/NetScope.Probes/HttpProbeTransport.cs`：默认 handler 增加 `ConnectCallback` 连接缝；本地解析 → 地址族过滤 → 真实 socket 拨号 → 记录连接事实；新增族缺口/不支持原因码；失败分类优先采用自测事实；`ProbeHandlerOptions` 增加 `AddressFamily`；客户端池按 path+proxy+family 隔离；`Reused` 判定由“本次请求期间连接缝被调用 0 次”推得（用全局连接计数兜底，宁可 unknown 不误报）。`UseProxy`/`Proxy`/`PooledConnectionLifetime`/重定向/解压保持原样，未添加任何证书校验关闭。
- `src/NetScope.Probes/ProbeNameResolution.cs`（新）：`ProbeAddressFamily`、`ProbeAddressFamilyPolicy`（解析/过滤/线值）、`ProbeNameResolution`、`IProbeNameResolver`、`SystemProbeNameResolver`（IP 字面量不查询；DNS 异常只保留类别码，不泄露主机名）。
- `src/NetScope.Probes/ProbeAttemptObservation.cs`（新）：单次尝试内部观测；只发布目标侧解析、首个成功连接、最后一次连接失败/解析失败；成功连接会清除先前地址的连接失败，避免 TLS/HTTP 失败被误归因成 connect。
- `src/NetScope.Probes/ProbeTransport.cs`：`TransportOutcome` 增加 `ResolutionSource`、`ResolvedAt`、`Connection`。
- `src/NetScope.Probes/HttpProbeRunner.cs`：把上述事实投影进 `ProbeResult`；路径 caveat（如“no-explicit-proxy 不证明 physical-direct”）与传输 Notice（复用提示）合并保留。

探测模型（Application，仅必要字段）：

- `src/NetScope.Application/Probing.cs`：`ProbeResult` 增加 `ResolutionSource`/`ResolvedAt`/`Connection`；新增 `ProbeConnectionFact`（`EndpointRole`、`RemoteAddress`、`RemotePort`、`Reused`）。字段全部可选，旧记录缺省为 null，不做补造。

测试（Unit，仅探测相关）：

- `tests/Unit/T04R2PrefixReproducerTests.cs`（新，2 个修前反例，保留作证据）
- `tests/Unit/T04R2ProbeObservationTests.cs`（新，8 个：runner 事实投影、未知保留、Notice 合并、解析失败/无族地址、非法族、代理路径不推断）
- `tests/Unit/T04R2LoopbackProbeTests.cs`（新，11 个：真实 loopback 解析事实、IPv4/IPv6 真实 socket 强制、族失败不冒充成功、复用连接、代理端解析、system-default 解析未知、取消、组预算、自签名 TLS 证书校验）
- `tests/Unit/T04R2LoopbackServer.cs`、`tests/Unit/T04R2ProbeFakes.cs`（新，loopback HTTP/HTTP+ TLS 服务与可控 resolver）
- `tests/Unit/ProbeTransportTests.cs`（更新旧的“族一律 Unsupported”测试为三条新语义 + 自定义 handler 不产生连接断言）
- `tests/Unit/ProbeRunnerTests.cs`（1 行：假的错误码与真实原因码对齐）

未修改：`src/NetScope.Desktop/**`、`ObservationSession`、`src/NetScope.Storage/**`（含 Export）、Repair/Broker、系统网络设置、真实客户端配置、solution、启动脚本。工作树中他人的 T09-R2/T06-R3/T08-R2 修改未被触碰。

## 4. 设计与语义决策（无设计默认值偏差）

- 新增两个缺口原因码，替代原先“所有路径一律 `address_family_not_enforced`”：
  - `address_family_not_enforced_through_proxy`：HTTP 代理自行解析目标，本机无法强制/验证目标族；
  - `address_family_unknown_for_system_default`：系统默认栈可能经代理/PAC/TUN，目标族不可观测。
  这是把原卡“当前地址族选择 Unsupported 的限制独立列出”落到每条路径；原设计阈值、窗口、QC 矩阵未改。
- 目标族只在 `no-explicit-proxy` 路径强制（本进程真正拨目标）。三条路径都带族约束时，代理/系统两条是 Unsupported 缺口，组因此不可比（诚实降级），不会伪造三路径分族结果。
- 连接复用判定：只有当本请求期间连接缝一次都没有被调用、且全局连接计数未增长时才标 `Reused=true`；并发下宁可 unknown，不误报“未重新解析”。
- 只有默认 handler（真实 socket）产生连接事实；注入内存 handler 时 `Connection=null`、不推断复用（测试 `A_custom_handler_receives_the_family_but_never_produces_connection_claims`）。
- 系统默认栈的解析/代理选择保持未知；连接事实只给 `role=unknown` + 真实拨号地址，不推断它是不是代理。
- 证书校验未触碰；自签名 loopback TLS 反例证明仍失败（`tls_failure`，stage=tls，连接事实仍在）。

## 5. 命令、环境、退出码

环境：Windows，普通用户；.NET SDK `10.0.403`；未提权、未改系统/网络/环境变量、未启动客户端。为避免与队友同时构建，只构建/测试本任务涉及的工程。

| 命令 | 结果 |
|---|---|
| `dotnet build src/NetScope.Probes/NetScope.Probes.csproj -v m` | 退出码 0，**0 警告 / 0 错误** |
| `dotnet build tests/Unit/NetScope.Unit.Tests.csproj -v m` | 退出码 0，**0 警告 / 0 错误** |
| `dotnet test tests/Unit/NetScope.Unit.Tests.csproj --filter "FullyQualifiedName~T04R2"` | 退出码 0，**21 通过 / 0 失败 / 0 跳过** |
| `dotnet test tests/Unit/NetScope.Unit.Tests.csproj` | 退出码 0，**269 通过 / 0 失败 / 0 跳过** |
| `dotnet test tests/Contract/NetScope.Contract.Tests.csproj` | 退出码 0，**80 通过 / 0 失败 / 0 跳过** |
| `dotnet test tests/Replay/NetScope.Replay.Tests.csproj --filter "FullyQualifiedName!~T06R3PreFixReproducerTests"` | 退出码 0，**58 通过 / 0 失败 / 0 跳过** |
| 同 Replay 不加过滤 | 退出码 1，58 通过 / **2 失败**；失败项为队友 T06-R3 的修前反例 `T06R3PreFixReproducerTests`（属 task-12，未完成前本就应失败），与本任务无关 |
| 修前反例（实现前） | 退出码 1，0 通过 / 2 失败（见第 2 节） |

TRX/日志在本地 `%TEMP%\NetScopeRepair-T04R2\`（未提交）：`prefix-failures.log`、`prefix.trx`、`t04r2-focus-3.log/.trx`、`unit-final2.log/.trx`、`contract-final.log/.trx`、`replay-final.log/.trx`。计数均为本次 HEAD 工作树实际执行，不复制历史 637/243 等旧数字；工作树含他人未提交修改，故不声称“全解决方案”结果。

## 6. 测试数量与覆盖

本任务新增 **21** 个测试，当前 Unit 工程 **269 通过**（新增前同一工作树实测 266 通过 / 18 个新测试）。

覆盖对应验收点：

- QA-002（三路径可比/缺失保留）：路径与窗口语义未改；Unsupported 缺口不计成功，组不可比（原有 runner 测试 + 新族缺口测试）。
- QA-003（不误标 physical-direct）：`no-explicit-proxy` Notice caveat 与复用提示合并后仍保留（`Runner_keeps_the_reuse_notice_and_the_path_caveat_together`）。
- QA-004（fake-IP 正常映射）：`ResolvedAddresses` 现在有真实值，DX-002 输入不再为空（runner 投影测试）；fake-IP 本身不判故障的规则逻辑未改。
- QA-007（路径差异）：三条路径的连接事实/失败阶段各自独立，未合并成“全网健康”。
- QA-020（分族）：IPv4/IPv6 真实 socket 强制与单族失败保留；IPv6 失败不会因 IPv4 可达而变成功（`A_failed_constrained_family_never_turns_into_a_success`）。
- QA-021（代理鉴权/握手/目标区分）：阶段值不改；代理路径解析保持 proxy；TLS/隧道错误归因沿用并补测自签名 TLS。

反例清单（全部在测试中）：

1. 真实 loopback 解析 → 发布地址/来源/时间/连接地址。
2. IPv4 约束 + resolver 混族 → 只拨 IPv4，IPv6 地址不参与。
3. IPv6 约束 + 真实 ::1 监听 → 只拨 IPv6。
4. IPv6 约束 + 只有 IPv4 服务 → 失败、stage=connect、端点 target、零请求，绝不成功。
5. 连接复用 → `Reused=true`、无新解析、resolver 只被调用一次、服务收到两次请求。
6. 明文代理路径 → role=proxy、解析位置 proxy、目标地址为空、resolver 只解析代理主机。
7. system-default → 解析位置/地址/时间为 unknown、role=unknown、不声称本地解析。
8. DNS 失败 → stage=resolve、端点 target、错误码不含主机名、无请求。
9. 无所需族地址 → stage=resolve、`no_address_for_requested_family`、无连接事实。
10. 非法族值 → `unsupported_address_family`，resolver 未被调用。
11. 取消（解析中）→ OperationCanceled 传播；runner 结果 Cancelled/Inconclusive/无事实。
12. 组预算耗尽 → Cancelled + `group_budget_exhausted` + 无事实。
13. 自签名 TLS → `tls_failure`，证书校验仍生效，连接事实保留。

## 7. 行为证据与 fixture 路径

- 修前反例与契约证据：`tests/Unit/T04R2PrefixReproducerTests.cs`。
- fake resolver/transport 证据：`tests/Unit/T04R2ProbeObservationTests.cs`、`tests/Unit/T04R2ProbeFakes.cs`。
- 受控 loopback 集成证据（仅 127.0.0.1/::1）：`tests/Unit/T04R2LoopbackProbeTests.cs`、`tests/Unit/T04R2LoopbackServer.cs`。
- 执行日志/TRX：本地 `%TEMP%\NetScopeRepair-T04R2\`（见第 5 节），未写入仓库以免越过本任务写范围；本报告第 2/5 节内联关键输出。
- 未读取/引用任何真实故障包或真实客户端配置；无真实外部目标。

## 8. 只读/权限/隐私边界检查

- 产品代码只新增：本地 DNS 解析（`Dns.GetHostAddressesAsync`）、真实 TCP connect、记录事实。无注册表/系统配置/客户端配置写入，无提权，无进程启动，无服务/任务。
- 未修改 `HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY` 等环境变量（本机原本存在环境代理，仅被 system-default 既有语义使用）；未改 system-default 的代理选择代码，未关闭证书校验。
- 解析失败只发布类别码（`name_resolution_failed`/`no_address_for_requested_family`），不把解析器异常文本或主机名写进 `TransportError/Notice`；新增的 `Connection.RemoteAddress` 只是 IP，与既有 `ResolvedAddresses` 同类。导出侧既有 `PackageAnonymizer` 对任意字符串 IP token 统一别名化（未在本任务重验证，属 T08-R2）。
- 测试只连 loopback（127.0.0.1 / ::1）；prefix 测试用操作系统解析 `localhost`（本机解析，无外部请求）；其余主机名由 fake resolver 供给，未触发真实 DNS。

## 9. 已知限制与未实现项

1. **族强制范围**：只有 `no-explicit-proxy` 路径能真实强制目标族；`explicit-http-proxy`/`system-default` 返回明确 Unsupported 缺口。带族约束的三路径组不可比（诚实结果，但用户此时拿不到分族三路径对比）。若要从代理端证明目标族，需要代理侧证据（如 CONNECT 响应/代理 API），本轮不做。
2. **连接事实仅限默认 handler**：注入内存 handler 时不产生连接/复用结论（测试明确断言），避免伪造。
3. **复用判定保守**：并发探测若在窗口内有其它路径建连，全局计数增长会使复用降级为 unknown，而不是误报。
4. **默认解析语义**：连接缝用 `Dns.GetHostAddressesAsync`（同一 OS 解析器）替代 SocketsHttpHandler 内部解析，并按族顺序串行尝试地址；不再使用其内置并行 happy-eyeballs。代理选择与 TLS 未改。
5. **system-default**：真实代理选择、PAC 与解析位置仍是 unknown；只记录 role=unknown 的连接事实。未验证是否经本机代理。
6. **真实外部实测未做**：没有用户启用的外部目标，QA-020 的真实双栈外网验证仍待验；本轮只做受控 loopback。
7. **IPv6 测试依赖本机 IPv6 loopback**：本机已验证可绑定 `[::1]`；在不支持 IPv6 loopback 的机器上该测试会失败而非跳过（未加入动态跳过，避免伪造）。
8. **UI 文案未更新**：`SettingsViewModel.FamilyNotice` 仍写“M1 真实传输不强制地址族”，现在只对 `no-explicit-proxy` 成立。Desktop 不在本任务写范围，需 T07-R2/后续任务按新语义更新（含族缺口码展示）。
9. **导出/回放展示**：新字段可被既有 STJ 编解码往返，但本轮未补 Replay/Export 展示断言；旧包无此字段时保持 null，不补造。
10. **未实现**：代理端解析证据、按族真实代理结果、DNS 查询细节（缓存/TTL/服务器）、连接时序、外部目标实测。

## 10. 是否有真实网络操作

**无外部网络操作。** 逐项：

- 产品代码：新增本地解析与 TCP connect 能力，但本任务未运行产品对任何外部目标的探测；桌面/CLI 未被启动。
- 测试：只绑定并连接 `127.0.0.1` / `::1` 的进程内服务；prefix 测试用 OS 解析 `localhost`（本机 loopback 解析）；其余主机名使用注入的 fake resolver，未做真实 DNS。
- 未修改系统代理、防火墙、路由、接口、DNS、证书存储；未启动/停止任何真实客户端。

## 11. 建议下一任务（不自动执行）

按施工顺序为 **T06-R3**（已由队友进行中）：将本任务发布的探测事实接入证据/回放链，让 DX-002/DX-007/DX-008 等规则消费真实地址与阶段。随后：

- **T07-R2**：更新设置页“地址族”提示与诊断详情，显示新增的 `Connection`/`ResolutionSource`/复用说明与两条族缺口码；不得把 Unsupported 显示成成功。
- **T08-R2**：确认导出对 `Connection.RemoteAddress`（IP token）继续别名化，并在 manifest/预览区分“未解析/复用/族缺口”。
- 若需代理端目标族证据，另立任务（需要代理可观测接口或 CONNECT 记录），不在本轮扩大范围。

本任务完成即停，不领取 T06-R3/T10+，不创建提交或推送。
