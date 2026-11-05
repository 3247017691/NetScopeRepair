# M0-R2 补修交付报告

日期：2026-11-05（执行跨 11-04/05，本机 UTC+08:00）。范围：用户授权补修 G0-01—18；不进入 M1。

**结果：18 项原缺陷已关闭，G0-R1 通过 M0 只读范围复审。** 最终 solution 构建 0 警告、0 错误；Contract 80、Unit 243、Replay 52、Windows 79，合计 **454 通过、0 失败、0 跳过**。正式门禁判定及限界见 [G0-R1 复审](G0-R1-M0-review.md)，逐项执行对账见 [验证汇总](evidence/M0-R2-20261104/verification-summary-v2.json)。

## 1. 修复结果

| 缺陷 | 完成的行为 | 实际回归依据（类或方法名） |
|---|---|---|
| G0-01 原生标志偏移 | typed x64 结构大小 168、标志偏移 166；其他 ABI 显式 Unsupported；核对返回的 family/index | NativeNetworkLayoutTests；8 个真实 IPv4 行的独立只读对照 |
| G0-02 采集隐私 | 代理映射逐项解析，仅保留校验过的 scheme/host/port；移除 userinfo、路径、query、fragment；畸形值安全替换；NO_PROXY 独立语法 | SecretRedactionTests，多条目/PAC/编码/畸形秘密扫描 |
| G0-03 核心契约准入 | Domain 内嵌同一 Schema，读、写和回放一致验证；重复 JSON 成员被拒绝 | StrictContractTests；StorageAdmissionTests.Invalid_core_events；CLI 坏行对照 |
| G0-04 缺样与网络失败 | 取消、权限、Unsupported、Partial、Unknown、预算未执行及本地执行错误不计有效网络失败；保留缺口计数、原因和证据 | DiagnosticScopeRegressionTests；DiagnosticRunnerIntegrationTests；ProbeMetadataTests |
| G0-05 本地代理归因 | 只有已选中、已证明本地端点、代理连接阶段拒绝/超时才能支持；TLS、权限、认证和上游失败不证明监听失效 | DX001 的正反例；ProbeTransportTests.Tls_failure |
| G0-06 配置陈旧 | mtime 顺序仅为候选；相同运行态 hash 是反证，可信且同时的 runtime/disk 差异才支持 | DiagnosticConfigurationRegressionTests.DX006 |
| G0-07 fake-IP 模式 | 保留路由/DNS-server 本身保持候选；实际保留段 DNS 返回与同目标/族/时段可信运行态证据共同支持；明确相反模式阻止升级 | DiagnosticScopeRegressionTests.DX002 |
| G0-08 证据作用域 | 保存端点、地址族、方法、固定选项摘要、boot、窗口、墙钟/单调时间、拓扑及可比性；不同/缺失作用域不能虚构关联 | DiagnosticScopeRegressionTests、DiagnosticProbeIdentityTests |
| G0-09 落盘证据 | 保存完整 typed snapshot/probe/record/diagnosis，真实观测时间、canonical evidence_id、source_event_ids 与变换版本；从磁盘重算与原结论一致 | EvidenceReplayTests；11 事件演示及离线重算 |
| G0-10 组预算 | 排队和在途任务均受 linked budget token 限制，WaitAsync 约束调用等待；预算、用户取消和单探针超时原因分开 | ProbeBudgetTests，手动 timer 确定性验证；保留真实时钟回拨 smoke |
| G0-11 未知度量 | 缺失 metric/TotalMetric 为 null，保留 reason/Partial；TotalMetric 为 nullable long，避免相加溢出 | PartialSourceTests、CollectorAggregationTests |
| G0-12 代理选择 | 请求里的代理决定实际 handler/pool；不同代理隔离；构造时约束不一致即拒绝，不能“走 A 报 B” | ProbeTransportTests，注入 handler 不开 socket |
| G0-13 失败写入重试 | 不在成功写入前预留 ID；重试核对实际磁盘内容；打开、写入、flush 失败不确认成功 | StorageAdmissionTests.Failed_open / Failed_write / Write_or_flush_failures |
| G0-14 坏尾追加 | 已损坏/歧义日志拒绝追加并保留原字节；完整无换行最后一条先补分隔符 | StorageAdmissionTests.Corrupt_tail / Complete_last_value |
| G0-15 端口竞争 | 需要规范化后的同一实际端点、监听 owner、同时可信争用错误；空值或 AccessDenied 不证明竞争 | DiagnosticConfigurationRegressionTests.DX005 |
| G0-16 真实标记 | 通用 check 接受 synthetic=false 并保留标记；只对默认合成 fixture 启用 synthetic-only 要求 | StrictContractTests.General_inspection / Fixture_only_inspection；CLI marker 用例 |
| G0-17 默认代理元数据 | system-default 的实际代理选择及 DNS 解析位置未知时保持 unknown，不从“默认栈”推断用了代理 | ProbeTransportTests、ProbeMetadataTests 的 system-default 反例 |
| G0-18 部分采集 | NIC、DNS、索引、native metric 子项失败传播 Partial/Errors，成功数据继续保留；成功空表与失败区分 | PartialSourceTests、MachineCollectorTests |

测试映射中的每一组都有最终 TRX 中已执行且通过的用例；[核验脚本](evidence/M0-R2-20261104/verify-repair-evidence.py) 会拒绝“只写了测试名却没有执行结果”的情况。映射是证据导航，关闭判定同时依据源码审查与语义断言，并非按测试数量放行。

## 2. 复核发现与追加修正

通用独立存储复核和组间交叉复核未直接签发通过，而是继续发现以下问题。本轮均已修复并实际执行新回归：

- **UTF BOM 混写**：旧 UTF-16/32 文件被识别并接受后，追加却使用 UTF-8。现在在任何 mutation 前检查编码，严格解码；遇到其他 BOM 拒绝且字节不变。再将前缀读取改为 ReadAtLeast，覆盖合法短读流；测试包含 UTF-16LE/BE、UTF-32LE/BE 及首读一个字节的注入流。
- **本地执行异常被当作网络失败**：同步/异步 transport 或 handler factory 的意外异常改为 Unknown/Inconclusive + transport_execution_error；不复制异常 Message，也不推断是否实际发包。生产 runner→DX008 回归确认不制造断网结论。
- **缺口被可比性分区丢失**：TimingComparable 不再作为身份分区键。由真正网络结果证明可比性，同作用域缺口继续引用和计数；204+timeout+AccessDenied 保留“1/2 有效网络失败，1/3 观察缺口”。任何样本的拓扑变化仍会降级。
- **跨地址族标志串用**：接口标量事实选择一个有效 index/family，只读该行；IPv4 失败后不借用 IPv6 的 metric/flags。InterfaceState 新增 Family，CLI 显示该列；未知保留 null。
- **typed 域自相矛盾**：回放拒绝 Ok/Partial 无值、不可用却带值、非 Ok 没有原因及缺少域的证据；也检查代理子域。
- **计时测试依赖冷启动速度**：首次全量运行有一条 40ms 测试在准备阶段就耗尽预算，导致首请求数量断言失败。改为注入手动 TimeProvider，先确认请求启动，再推进至预算前 1ms 和到期；保留原有在途/排队取消及原因断言，没有通过放宽断言掩盖失败。

## 3. 最终执行记录

| 验证 | 结果 | 记录 |
|---|---|---|
| solution build --no-restore | exit=0，0 warning/error | [最终构建](evidence/M0-R2-20261104/build-final-v2.log) |
| solution test --no-build --no-restore | 454/454，通过；0 fail/skip | [最终测试](evidence/M0-R2-20261104/test-final-v2.log)、[TRX](evidence/M0-R2-20261104/test-results-final-v2/) |
| demo → check → replay | 11 个 synthetic 事件，引用闭合，2 条 stored/recomputed 诊断，rule_errors=0，offline_diagnosis_match=yes | [demo](evidence/M0-R2-20261104/demo-final.log)、[check](evidence/M0-R2-20261104/check-final.log)、[回放](evidence/M0-R2-20261104/replay-after-encoding-fix.log)、[会话](evidence/M0-R2-20261104/demo-final/session-demo.ndjson) |
| 同一坏核心封套 check/replay | 两者 exit=1，坏行均未计成功 | [check](evidence/M0-R2-20261104/invalid_check-final.log)、[replay](evidence/M0-R2-20261104/invalid-replay-after-encoding-fix.log) |
| 通用 check 的 synthetic=false 标记 | exit=0，保持 False | [marker 校验](evidence/M0-R2-20261104/real_check-final.log)；此输入只是契约测试，不能当真实网络观测 |
| snapshot | 只读成功；interfaces Partial 如实报告，其他外层域 Ok；WinHTTP 子层 Unsupported 未掩盖 | [最终只读快照](evidence/M0-R2-20261104/snapshot-after-family-label.log) |
| native ↔ Windows cmdlet | 8 个 IPv4 行的 metric、AutomaticMetric、IgnoreDefaultRoutes 一致；7 Enabled、1 Disabled | [独立读回](evidence/M0-R2-20261104/windows-ipinterface-readback.json)、[机器核验](evidence/M0-R2-20261104/verification-summary-v2.json) |
| QA-020 | IPv6 失败在有/无 IPv4 成功时均保留；不混为单一间歇故障；真正 IPv6 间歇故障保留 IPv6 作用域 | DiagnosticScopeRegressionTests.QA020、EvidenceReplayTests.QA020，见最终 TRX |

[CLI 退出码](evidence/M0-R2-20261104/cli-exits-final.json)及[编码修正后复核退出码](evidence/M0-R2-20261104/followup-cli-exits.json)分别保留。最初构建错误、计时失败、451 项通过的中间记录也保留，不能用它们替代最终结果。旧 G0 反例目录未改写；旧审查程序会在原先应被接受的坏事件处触发新的严格拒绝，未将其再次执行完成冒称为回归通过。

## 4. 兼容性与停止点

- 新增 metadata/Family 字段为 additive；TotalMetric 现为 long?，调用方须处理 null。EvaluateDetailed 单列规则错误；兼容 Evaluate 遇错会抛带 Outcome 的异常，避免静默吞错。
- 本地 payload 版本为 m0-evidence-1。旧日志继续做事件统计；缺少完整事实时不会声称可以重建前提。当前一次重建一个 snapshot，未实现 M2 故障包导入/迁移/多快照工作台。
- 追加前读取现有日志优先保证正确性，尚无大文件吞吐承诺。没有恢复坏尾、轮转、配额或长期记录器的生产验收。
- 真实传输仍保守拒绝请求强制地址族；QA-020、代理 pool A/B 和预算 transport 测试为注入/mock/离线验证，不能冒称真实 IPv4/IPv6 路由、握手、认证或外部故障复现。
- 没有修改真实出口、路由、接口、DNS、环境代理、客户端配置、TUN、防火墙；没有客户端重启、UAC、服务/任务/自启、上传或 Git 初始化。测试期间仅额外读取了本机状态。

当前交付停在 **G0-R1 / M0-R2**。M1 未启动。架构决定见 [ADR 0003](../adr/0003-m0-r2-evidence-admission-and-scope.md)。
