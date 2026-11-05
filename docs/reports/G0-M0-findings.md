# G0 M0 缺陷清单与补修验收条件

首轮日期：2026-11-04（Asia/Shanghai）。**当前状态：18 项均已在 M0-R2 关闭，2026-11-05 通过 [G0-R1 复审](G0-R1-M0-review.md)**。逐项修复与已执行回归见 [M0-R2 交付报告](M0-R2-report.md) 和 [最终机器核验](evidence/M0-R2-20261104/verification-summary-v2.json)。

以下保留首轮“开放、未补修”时的反例、影响与关闭条件；“当前”及源码行号指当时版本，不代表修复后的行为。首轮只审查，后续产品修改由用户明确授权。

配套结论：[G0 门禁报告](G0-M0-review.md)。实际反例输出：[最终复现结果](evidence/G0-20261104/repro-results-final.json)，复现程序：[Program.cs](evidence/G0-20261104/repro/Program.cs)，项目：[G0Repro.csproj](evidence/G0-20261104/repro/G0Repro.csproj)。C01—C18 是审查场景编号，不是新增 xUnit 通过数。复现程序的退出码 0 表示场景执行完成，不表示产品通过。

优先级：P1 阻断 G0；P2 为补修或后续能力启用前必须明确处理的问题。静态确认与实际运行证据分别标注，未运行项不写成通过。

## G0-01 / P1：IgnoreDefaultRoutes 读取了结构以外的字节

- 位置：[NativeNetworkApi.cs:25](../../src/NetScope.Collectors.Windows/NativeNetworkApi.cs#L25)、[偏移常量:37](../../src/NetScope.Collectors.Windows/NativeNetworkApi.cs#L37)、[读取:99](../../src/NetScope.Collectors.Windows/NativeNetworkApi.cs#L99)。
- 当前：分配并清零 176 字节，读取偏移 172。微软定义的 x64 结构对应大小 168，DisableDefaultRoutes 在 166；TransmitOffload/ReceiveOffload 各为单字节位域。
- 实际 C11：按官方字段顺序构造的托管镜像得到 sizeof=168、offset=166；三个现有接口的 GetIpInterfaceEntry 返回 0，预填的偏移 172 哨兵 0x5A 均原样保留。该字节未被 API 写入。产品清零后读它，会恒报 false。
- 局限：三个现有接口的真实偏移 166 均为 0；没有为测试开启任何接口标志，因此没有“真实 Enabled 接口”的业务对照。结构及未写入字节证据已足以证明读取错误。
- 关闭条件：修正或改用经验证的结构绑定；以原生布局、true/false 合成行及只读读回验证字段。不要仅靠 metric 一致或路由条数一致验收整个结构。
- 官方依据：[MIB_IPINTERFACE_ROW](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_ipinterface_row)、[NL_INTERFACE_OFFLOAD_ROD](https://learn.microsoft.com/en-us/windows/win32/api/nldef/ns-nldef-nl_interface_offload_rod)。

## G0-02 / P1：采集边界仍保留凭据和 URL 密钥

- 位置：[SecretRedaction.cs:22](../../src/NetScope.Collectors.Windows/SecretRedaction.cs#L22)、[WindowsSources.cs:325](../../src/NetScope.Collectors.Windows/WindowsSources.cs#L325)、[PAC 采集:389](../../src/NetScope.Collectors.Windows/WindowsSources.cs#L389)。
- 实际 C09：两个带 userinfo 的代理条目只脱敏第一个，第二个合成密码仍存在；PAC 的 token 查询参数和注入式代理变量查询密钥均保留。
- 影响：公开采集结果携带 D3 数据，违反“进入日志之前按字段白名单最小采集”的契约。**没有证明当前真实密码泄露到控制台或日志**；当前 snapshot CLI 只打印代理层状态、开关与条数。
- 关闭条件：按代理映射逐项解析；清除 userinfo 和敏感 query/fragment/路径形式，尽量只保留诊断必要字段；加入多条目、PAC、编码与畸形 URL 的合成秘密扫描。不得靠 UI 暂不显示来放行采集契约。

## G0-03 / P1：存储和回放绕过已发布的封套约束

- 位置：[EnvelopeIntegrity.cs:18](../../src/NetScope.Domain/EnvelopeIntegrity.cs#L18)、[NdjsonEventLog.cs:45](../../src/NetScope.Storage/NdjsonEventLog.cs#L45)、[读取:138](../../src/NetScope.Storage/NdjsonEventLog.cs#L138)。
- 实际 C01：schema_version=99.0、sequence=0、monotonic_ms=-1、空 snapshot_id 的事件被 AppendAsync 接受；check 拒绝该行，ReplayService 却返回 events=1、ok=1、IsClean=true。
- 影响：同一坏行在两个入口有相反含义，契约违规事件污染回放的成功统计。这是核心封套约束缺失，与后续 payload 专用 Schema 是两件事。
- 关闭条件：所有入库/回放入口执行同等核心约束；不支持版本明确报告 unsupported 或拒绝，违规行不得计入成功。保留拒绝原因和行号。

## G0-04 / P1：未测到的样本被报告成间歇断网

- 位置：[ProbeClassificationPolicy.cs:34](../../src/NetScope.Probes/ProbeClassificationPolicy.cs#L34)、[Dx008IntermittentFailure.cs:30](../../src/NetScope.Diagnostics/Rules/Dx008IntermittentFailure.cs#L30)。
- 实际 C05：同目标同路径 Ok 与 Cancelled / Unsupported / AccessDenied / Partial 任一组合，均生成 observed“1/2 次失败”。C15：正在运行的 Cancelled / AccessDenied 被分类为 TransportFailure；排队前取消却为 Inconclusive。
- 影响：用户取消、权限不足和能力缺失被变成了网络故障。设计 03 §4 明确不允许这样计数。
- 关闭条件：区分有效目标失败与观察缺口；保留有效失败分母、缺口分母及原因；Ok+Timeout 正例继续有效，Ok+缺样不产生断网结论。

## G0-05 / P1：目标 TLS 失败被归因于本地代理不可用

- 位置：[Dx001DeadProxyDependency.cs:33](../../src/NetScope.Diagnostics/Rules/Dx001DeadProxyDependency.cs#L33)、[连接阶段判定:77](../../src/NetScope.Diagnostics/Rules/Dx001DeadProxyDependency.cs#L77)。
- 实际 C06：只有一个显式代理探针，FailedStage=tls、certificate_validation_failed，仍生成 supported“该请求被不可用的本地代理阻断”。
- 影响：目标证书失败可以发生在成功 CONNECT 之后；权限失败或 CONNECT 上游失败同样不证明本地监听端点失效。当前计划也允许非本地代理。
- 关闭条件：仅在端点身份与真正的代理端连接拒绝/超时有证据时支持该命题；TLS、鉴权、上游 CONNECT、权限和远程代理加反例，不扩大结论范围。

## G0-06 / P1：正常的文件加载顺序被判成配置陈旧

- 位置：[Dx006StaleConfiguration.cs:49](../../src/NetScope.Diagnostics/Rules/Dx006StaleConfiguration.cs#L49)。
- 实际 C10：文件在启动前写入，进程加载相同配置；同时提供相同 hash 的运行态记录，规则仍 supported“磁盘文件不能代表当前运行态”。规则完全不检查运行态一致性。
- 影响：程序正常加载先前写好的配置是常态；mtime<进程启动不能证明失配。
- 关闭条件：区分 disk_only 的未验证线索与真正 runtime/disk 不一致；相同有效配置作为反证，真实不一致须带来源、时间和哈希/字段差异。

## G0-07 / P1：保留段路由被当作已确认 fake-IP 模式

- 位置：[Dx002FakeIpContext.cs:28](../../src/NetScope.Diagnostics/Rules/Dx002FakeIpContext.cs#L28)、[证据升级:53](../../src/NetScope.Diagnostics/Rules/Dx002FakeIpContext.cs#L53)。
- 实际 C12：只有 Ethernet 上 198.18.0.0/15 静态路由，没有 DNS 返回地址、运行态 fake-IP 配置或可靠适配器证据，仍输出 supported fake-IP 映射上下文。
- 影响：198.18/15 还有基准测试等用途。避免“污染”误报是正确边界，但不能因此把未确认用途说成已确认正常映射。当前 demo 的 DX-002 同样没有 DNS 返回证据。
- 关闭条件：按设计 03 的最小证据核验模式；路由-only、DNS-server-only、无关 /32、未知接口或明确非 fake-IP 模式保持未知/候选。真实正常 fake-IP 有完整证据时继续正确解释。

## G0-08 / P1：不同端点、不同观察窗的证据被强行关联

- 位置：[Dx001DeadProxyDependency.cs:22](../../src/NetScope.Diagnostics/Rules/Dx001DeadProxyDependency.cs#L22)、[Dx008IntermittentFailure.cs:22](../../src/NetScope.Diagnostics/Rules/Dx008IntermittentFailure.cs#L22)、[Dx011CollectionContradiction.cs:33](../../src/NetScope.Diagnostics/Rules/Dx011CollectionContradiction.cs#L33)。
- 实际 C13：proxy-A 的监听样本与 proxy-B 的成功被描述为“同一代理端点”；C18：相隔七天且代理不同的成功/失败被写成“同一观察窗内”的 observed 间歇故障。
- 静态：DX-001 把任意显式代理的成功当作另一端点失败的反证。EvidenceSet 丢失组级 TimingComparable；DiagnosisScope 缺路径和观察起止，ProbeResult 不保留地址族/方法/计划摘要，EvidenceRecord 没有观测时间，无法完整约束关联。
- 关闭条件：保留目标、端点、地址族、请求选项、单调时间窗、拓扑和可比性；未满足同一作用域时降级，不能虚构“同一”。补齐不同端点/时间窗/请求方法/地址族及 TimingComparable=false 的反例。

## G0-09 / P1：演示落盘丢掉证据，并改写观测时间

- 位置：[DemoPipeline.cs:124](../../src/NetScope.Cli/DemoPipeline.cs#L124)、[SnapshotPayload:183](../../src/NetScope.Cli/DemoPipeline.cs#L183)、[ProbePayload:201](../../src/NetScope.Cli/DemoPipeline.cs#L201)。
- 实际 C04：诊断引用 snapshot_id / probe_id，而落盘定义的 evidence_id 另有前缀，证据引用集合不闭合；快照只有计数和完整性，没有支持 DX-002 的路由与 DNS 内容。
- 实际 C17：第二组实际合成开始于 11:00:20，落盘却成为 11:00:04、05、06；payload 没保留真实开始时间。单调时间也只按事件序号生成，无法重建原观察顺序。
- 影响：当前 replay 是读取事件并计数，没有从保存的完整事实重新执行规则；不能将它称为诊断重算或忠实现场回放。完整案例工作台可放 M2，但 M0 示例证据必须真实且可追踪。
- 关闭条件：保存本轮诊断所需的规范化事实、明确 evidence 对象到 source_event 的关联、实际起止/单调时间、目标和端点；从磁盘验证引用闭合和诊断前提，禁止用虚构时间填充。

## G0-10 / P1：组预算无法停止已经启动的探测

- 位置：[HttpProbeRunner.cs:94](../../src/NetScope.Probes/HttpProbeRunner.cs#L94)、[SendAsync:162](../../src/NetScope.Probes/HttpProbeRunner.cs#L162)。
- 实际 C07：三条可取消 mock 请求，组预算 25ms，单探针上限 2s；实测约 207ms 完成，三条均 Ok，BudgetExhausted=false。任务只在开始前检查组 deadline，在途任务没有组预算取消。
- 关闭条件：用单调时钟及组级取消限制排队和在途请求，区分用户取消/单探针超时/组预算耗尽；所有未执行路径保留原因；加入在途超预算和非正单探针超时反例，使用合理计时容差。

## G0-11 / P1：读取不到 metric 时伪造 0

- 位置：[WindowsSources.cs:196](../../src/NetScope.Collectors.Windows/WindowsSources.cs#L196)、[Collection.cs:139](../../src/NetScope.Application/Collection.cs#L139)。
- 静态确认：ReadInterfaceMetrics 失败被 `?? 0` 缓存，再写入非空 InterfaceMetric，整个 routes 仍为 Ok。即使手动保留 null，TotalMetric 也将未知值当 0。
- 未执行：未在日用机制造接口消失/权限拒绝，现有源缺少可注入原生读取 seam。
- 关闭条件：未知 metric 保持 null+reason，合计保持未知，域状态正确 partial；用注入式原生失败测试，不修改真实路由。

## G0-12 / P1：传输实际选用的显式代理与计划可能不同

- 位置：[HttpProbeTransport.cs:32](../../src/NetScope.Probes/HttpProbeTransport.cs#L32)、[代理构造:50](../../src/NetScope.Probes/HttpProbeTransport.cs#L50)、[HttpProbeRunner.cs:109](../../src/NetScope.Probes/HttpProbeRunner.cs#L109)。
- 实际 C16：请求提供显式代理端点，默认传输实例仍返回 explicit_proxy_not_configured；没有发送请求。
- 静态确认：传输只用构造时端点，根本不读取 ProbeRequest.ExplicitProxyEndpoint。若传输构造为 A、计划为 B，将使用 A，却由 runner 记成 B 的 ID。
- 未执行：未启用真实 HTTP/代理监听服务器；A/B 的实际收包实验未跑，不冒称通过。
- 关闭条件：计划与传输配置只有一个权威来源，按端点隔离连接池或拒绝不一致；使用注入 handler/factory 验证最终选用端点，再由用户单独启动真实目标验收。

## G0-13 / P2：失败的追加占用了尚未落盘的事件 ID

- 位置：[NdjsonEventLog.cs:53](../../src/NetScope.Storage/NdjsonEventLog.cs#L53)。
- 实际 C02：先正常写入初始化 writer，再独占文件制造打开失败；首次追加 IOException，解除锁后重试同 ID 却 DuplicateEventException，磁盘没有该事件。
- 初次实验说明：未初始化 writer 就锁文件，失败发生在加载阶段，重试成功，未复现该缺陷。初次结果保留在 [初次输出](evidence/G0-20261104/repro-results.json)；修正前置条件后才得到最终结果，不将初次实验伪写成复现成功。
- 关闭条件：确认持久化后才登记 ID，或明确维护失败/可能已写入状态；重试策略不误判重复；覆盖失败发生于打开、写入、flush 等阶段。

## G0-14 / P2：坏尾之后追加返回成功，新增事件却不可读

- 位置：[NdjsonEventLog.cs:66](../../src/NetScope.Storage/NdjsonEventLog.cs#L66)、[EnsureLoadedAsync:219](../../src/NetScope.Storage/NdjsonEventLog.cs#L219)。
- 实际 C03：文件已有完整首行及无换行的截断 JSON，继续追加第二事件返回成功；新增事件粘在坏尾之后，回放只剩首事件。
- 范围：本轮不要求实现完整 M2 崩溃恢复，**至少须拒绝继续写该文件并说明原因**，不能给出“已保存”的错误承诺。
- 关闭条件：不干扰有效前缀，显式拒写或隔离坏尾后再安全追加；被确认追加的事件须能读取，去重集合不可掩盖损失。

## G0-15 / P2：缺失端点和 owner 也能产生 supported 端口冲突

- 位置：[Dx005PortOwnershipConflict.cs:28](../../src/NetScope.Diagnostics/Rules/Dx005PortOwnershipConflict.cs#L28)。
- 实际 C14：bind 记录只有 error=access_denied，ownership 记录没有任何 facts，两个缺失 endpoint 被空字符串相等关联，输出 supported 冲突，端点/owner 均空。
- 关闭条件：要求非空且可规范化的同一地址端口、有效 owner、争用类 bind 失败与时间关联；权限错误/空字段保持缺证。多进程本身仍不得直接判冲突。

## G0-16 / P2：通用 check 命令仍把真实事件当成无效 fixture

- 位置：[ContractInspection.cs:82](../../src/NetScope.Application/ContractInspection.cs#L82)。
- 实际 C08：符合核心契约且 synthetic=false 的事件，被“synthetic must be true for the T01 fixture set”拒绝。当前 CLI 已宣称支持指定会话文件。
- 关闭条件：fixture-only 约束与通用会话校验分离；真实/合成属性都保留且不更改它们，混合来源清晰呈现。

## G0-17 / P2：system-default 元数据把推测写成实际行为

- 位置：[HttpProbeTransport.cs:114](../../src/NetScope.Probes/HttpProbeTransport.cs#L114)、[HttpProbeRunner.cs:209](../../src/NetScope.Probes/HttpProbeRunner.cs#L209)。
- 静态确认：system-default 的成功一律 resolution_location=proxy，即使默认代理为空或命中 bypass。Proxy=null 的 .NET DefaultProxy 可能采用进程环境变量，windows-system-proxy 标签不充分说明实际栈。
- 未执行：真实 HTTP 传输仍未运行。
- 关闭条件：没有证据时标 unknown，明确 .NET/环境/PAC/自动检测支持及代理选择来源；启用真实 M1 探测前补齐。

## G0-18 / P2：源内部缺项未传播为 partial

- 位置：[WindowsSources.cs:93](../../src/NetScope.Collectors.Windows/WindowsSources.cs#L93)、[DNS 跳过:237](../../src/NetScope.Collectors.Windows/WindowsSources.cs#L237)、[返回 Ok:264](../../src/NetScope.Collectors.Windows/WindowsSources.cs#L264)。
- 静态确认：接口 metric 失败只写 item.Reason；DNS 对单接口 GetIPProperties 失败直接跳过，最后仍 Ok，无法区分“没有配置”与“没有读到”。
- 未执行：未注入真实 NIC 故障。
- 关闭条件：源内部缺项汇总到域状态和缺口；通过 seam 模拟一项成功、一项失败验证，不依赖当时机器恰好全部可读。

## 补修顺序与门禁

1. **M0-R2-A：事实采集与隐私。** G0-01/02/11/18。先保证字段不是伪值，秘密不进入公开结果。
2. **M0-R2-B：探测与诊断边界。** G0-04/05/06/07/08/10/12/15/17。将反例固化为有行为断言的回归，保留正确正例。
3. **M0-R2-C：持久化与证据。** G0-03/09/13/14/16。统一准入、忠实落盘、引用闭合；对暂不支持的坏尾续写可以明确拒绝。
4. **G0-R1：重新评审。** 原 173 项回归 + 新缺陷回归 + 更新的离线示例，逐项对账；复核 M0 的 IPv6/单族失败回放要求。P2 要么修复，要么列明尚不可启用的具体能力及验收条件。所有 P1 关闭后再决定能否进入 M1。

本清单没有授权上述补修或 M1 施工；本轮的已授权产出为 G0 评审及证据。未改变任何上行、TUN、代理环境、客户端或防火墙设置。
