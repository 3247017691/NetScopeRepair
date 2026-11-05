# G0-R1：M0-R2 补修门禁复审

日期：2026-11-05（UTC+08:00）。对象：M0-R2 最终源码、454 项最终回归、CLI 对照及只读 Windows 原生读回。

**结论：通过 M0 只读范围门禁；G0-01—18 关闭。** 本结论取代 [2026-11-04 首轮 G0 不通过](G0-M0-review.md) 的当前状态，保留首轮事实与反例不变。M1 尚未启动；此处不签发真实外部探测、修复执行或长期录制器的验收。

## 1. 判定依据

| 门禁维度 | 最终判定与证据 |
|---|---|
| 原生采集事实 | x64 ABI 明确验证，168 字节/标志 166；true/false 独立字节夹具；8 个真实 IPv4 接口与 Windows cmdlet 的 metric、AutomaticMetric、IgnoreDefaultRoutes 一致（7 Enabled、1 Disabled），未更改设置；未知架构 Unsupported；family/index 不串用 |
| 采集健康与隐私 | NIC/DNS/native 子项错误传播 Partial/Errors；未知度量不伪造 0；成功空表与失败分开；多条代理/PAC 的 userinfo、路径、query、fragment 在采集边界移除，畸形输入安全替换；NO_PROXY 保持独立语义 |
| 探测行为 | 预算约束排队及在途任务；明确区分预算/用户取消/探针超时/本地执行缺口；每条路径保留原因；请求端点决定 proxy pool；默认代理使用与解析地点无证明则 unknown |
| 推理最小证据 | TLS 不证明本地监听失败；mtime 不证明配置陈旧；保留地址路由不证明 fake-IP 模式；空端点/owner/权限错误不证明争用；跨目标、proxy、族、选项、boot 或窗口不强行关联；真实成功/失败计数与缺口分开 |
| 核心契约与持久化 | 同一坏行在 check/replay 均拒绝并不计成功；追加不预留失败 ID，不在坏尾后确认成功；完整尾行补分隔；UTF-16/32 与短读 BOM 边界拒绝且原字节保留；typed 域状态和值矛盾显式报错 |
| 证据可复核 | 保存完整前提、真实源时间、canonical IDs、来源事件和变换版本；11 个 synthetic 事件完整落盘，重算 2 条诊断与原结果一致；删除/损坏引用或改写时间会报 EvidenceProblems；规则执行错误单列，不编造 evidence_id |
| QA-020 | IPv6 失败在有/无 IPv4 成功时均保留，不能以 IPv4 成功掩盖或混成跨族间歇故障；mock/回放范围已执行 |
| 交付边界 | 无网络配置写入口；未调用旧修复脚本，未改变物理出口、环境代理或客户端状态；未启动 M1 |

完整 18 项关闭表见 [M0-R2 交付报告](M0-R2-report.md)。[机器核验汇总](evidence/M0-R2-20261104/verification-summary-v2.json)包含每项缺陷对应的已执行测试片段及通过数，脚本会检查 TRX、CLI 退出码、typed 引用、观测时间及独立 native 读回。

## 2. 实际执行结果

| 执行 | 结果 | 原始记录 |
|---|---|---|
| 最终全 solution build | exit=0，0 warning，0 error | [build-final-v2](evidence/M0-R2-20261104/build-final-v2.log) |
| 最终全 solution test | Contract 80、Unit 243、Replay 52、Windows 79；**454 通过、0 失败、0 跳过** | [test-final-v2](evidence/M0-R2-20261104/test-final-v2.log)、[4 份 TRX](evidence/M0-R2-20261104/test-results-final-v2/) |
| 合成端到端 | 11 事件、2 诊断，offline_diagnosis_match=yes；全部 synthetic=true | [demo](evidence/M0-R2-20261104/demo-final.log)、[事实日志](evidence/M0-R2-20261104/demo-final/session-demo.ndjson)、[SQLite 投影](evidence/M0-R2-20261104/demo-final/session-demo.db) |
| 最终编码改动后的离线回放 | problems=0、duplicates=0、gaps=0、recorded=2、recomputed=2、rule_errors=0，exit=0 | [回放读回](evidence/M0-R2-20261104/replay-after-encoding-fix.log) |
| 坏核心封套 CLI 对照 | check/replay 均 exit=1，events=0，不计成功 | [check](evidence/M0-R2-20261104/invalid_check-final.log)、[replay](evidence/M0-R2-20261104/invalid-replay-after-encoding-fix.log) |
| 非合成标记契约接受 | 保留 synthetic=false，check exit=0；该输入只是 marker 测试，未被算成真实网络观测 | [记录](evidence/M0-R2-20261104/real_check-final.log) |
| 本机只读采集与独立读回 | 8 个 IPv4 行一致；接口域 Partial 被展示；外层域 Ok 不掩盖 WinHTTP 子域 Unsupported | [snapshot](evidence/M0-R2-20261104/snapshot-after-family-label.log)、[Windows cmdlet JSON](evidence/M0-R2-20261104/windows-ipinterface-readback.json) |

正式最终文件指纹覆盖 src/tests/contracts 共 87 个文件，见 [SHA-256](evidence/M0-R2-20261104/source-sha256-final-v2.json)。无 Git 仓库初始化或提交操作；此指纹标识已验证对象，不冒充完整的历史内容 diff。

初次集成构建的 nullable/analyzer 问题，以及首次测试的冷启动计时假设均修正后复跑。451 项全绿是中间状态；最后短读和 UTF-32BE 补测后的 **454 项 TRX** 才是本次最终依据。旧 G0 的 173 项及旧 C01—C18 记录保留作历史，未冒称它们能替代本轮语义验收。

## 3. 独立复核与发现闭环

本轮使用通用子代理及按不重叠实现范围安排的组间交叉复核。未提供专用 Bugbot / Security Review 执行接口，因此没有将这些结果标成专用工具审计。

- **独立存储/契约复核**：只读代理 `7c573b20-691e-43b5-b316-6dd66a7a100c` 审查 G0-03/09/13/14/16，确认主要路径修复，同时发现 UTF BOM 自动解码后混写 UTF-8 的确认丢失问题。已修复并加入原字节保持测试。
- **collector-fixes 交叉检查 Probes/Diagnostics**：发现本地执行异常被计为网络故障、TimingComparable 分区丢失缺口两项。后续仅复查其原反例，确认最新代码及生产 runner→规则回归关闭；它未参与这些文件的实现。
- **diagnostic-fixes 交叉检查 Collectors/Probes**：发现 IPv4 失败后借用 IPv6 标量字段，其他指定 G0 路径未见新阻断。由 collector-fixes 修改，probe-fixes 再独立检查选族逻辑及新回归。
- **probe-fixes 复查 Collectors/Storage**：确认 family 归属、typed 域一致性和标准 FileStream 的 BOM 路径修复，又指出单次 Stream.Read 可以短读。Lead 改为 ReadAtLeast；最终实际运行了短读 UTF-16LE/BE、UTF-32BE 等回归，全部通过。

以上代理复核都是**静态源码与测试检查**，未代为构建、测试、调用 native 或联网。实际构建、全测试、CLI 及本机读回由 Lead 统一执行。早先代理后端未产生结果的失败尝试不计为审查意见。

因此放行依据包含“独立反例发现→修复→生产链路/边界测试执行”，不仅是测试总数增加。已知的这四组新增反例均关闭，当前范围没有未关闭 P1/P2 阻断项。

## 4. 验证限界与后续

- 真实 transport 仍不强制 IPv4/IPv6；指定 family 会诚实返回 Unsupported。QA-020 是 mock/离线事实与推理验证。代理 pool A/B 测试注入内存 handler，没有证明真实报文路径、TLS/认证或外部目标可达。
- .NET 系统默认栈不等同浏览器/应用栈；只有实际端点证据才能进行相关推理。没有凭“无显式代理”宣称物理直连。
- Windows native 只在已验证 x64 布局启用；其他 ABI 不假定可用。本次活体只读对照覆盖 IPv4，两种标志值来自现状，不是人为开关。
- typed evidence 只支持本地 M0 格式和单 snapshot 重建；历史日志摘要、故障包导入/迁移、多 snapshot 视图不能混为同一能力。规则当前每次只返回一个所选作用域的结论。
- 全文件核对优先保障追加正确性，大日志性能、轮转、恢复、配额、生产长期录制及完整导出脱敏仍按 M1/M2 施工；本次未将它们写成已验收。
- 本地 TRX、snapshot 及 cmdlet 记录包含本机设备/用户名或接口信息，只保留在本地，没有上传。合成隐私回归不代表未来导出审查已完成。

停止点为 **G0-R1 通过 / M0-R2 完成**；没有自动开展 WPF、适配器、外部真实探测或受控修复。
