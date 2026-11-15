# T14：Broker 协议、身份与白名单（真实提权能力默认关闭）

任务：T14（原 M3 第二阶段：broker 协议与身份；本轮不领取 T15）
日期：2026-11-05（Asia/Shanghai）
源码：`D:\Projects\NetScopeRepair`，分支 `main`，HEAD `a4b07a042d33746d664556933f562648ff1ba1e7`（未新增提交、未推送）
设计依据：设计包 `docs/05`（修复事务与权限边界）、`docs/07` QA-025/026、`docs/08`/T14、`docs/09`；施工单 `docs/plans/DeepSeek-V4.1-Flash-construction-plan.md` 的 T14 卡；`docs/reports/T13-report.md`；当前 `src/NetScope.Repair` 实际类型
工作树基线：含 T09-R2、T06-R3、T07-R2、T08-R2 等并行未提交修改；T13 的 `src/NetScope.Repair` 与 `tests/WindowsIntegration` 在当前工作树为未跟踪文件。本任务仅新增 `src/NetScope.Broker`、`tests/WindowsIntegration` 中的 broker 测试、`NetScopeRepair.slnx` 一条项目条目与报告；未覆盖他人文件。

**结论：新建了 `NetScope.Broker` 与完整的协议/身份/白名单/预算/信任闸门，真实提权能力在本交付中始终关闭。** 交付默认 `BrokerTrustPolicy.DeliveryDefault` 没有可信发布者指纹且不允许开发态提权，任何部署事实组合都不能打开真实提权；`BrokerHost` 在创建任何 listener 之前就拒绝启动。只有显式的开发态隔离（synthetic 策略 + `DevelopmentIsolation=true`，仅测试构造）可以启动一次性协议会话，而该会话仍无特权写路径：校验通过的交易在回应中明确 `executed=false` 并给 `execution_not_implemented`，断连只停止新动作并报告 journal 尚不存在。**本轮没有运行 UAC、没有提权、没有创建服务/计划任务/启动项、没有网络/注册表/真实客户端配置写入、没有真实修复。** 所有 214 个 WindowsIntegration 测试通过；其中本轮新增 93 个。

## 1. 实现了什么（边界与可验证行为）

1. **封闭协议**：`BrokerProtocol.Version = "1.0"`；消息种类闭集仅 `hello / challenge / authenticate / submit_transaction / transaction_result / cancel / close / error`，没有命令、脚本、PowerShell、任意程序集、通用 SetProperty 或任意路径消息。帧为 4 字节大端长度 + 严格 JSON：最大 **64 KiB**，未知字段、未知枚举、版本不兼容、空帧、超长帧在解析层拒绝（`frame_too_large`/`frame_empty`/`invalid_payload`/`protocol_version_mismatch`）。计划/动作经 `RepairPlan` typed 载荷传输，字段值使用封闭判别联合转换器；越界或未知动作值无法被反序列化。
2. **一次性 256 位 nonce**：broker 用 `RandomNumberGenerator.GetBytes(32)` 生成挑战 nonce（仅经已建立的本机管道传输），客户端 nonce 亦为 256 位；绑定值为 `SHA256(client_nonce ‖ broker_nonce ‖ transaction_id ‖ plan_digest)`，固定时间比较，由 `BrokerNonceLedger` 只消费一次（重复即 `nonce_replayed`），且有 60 秒寿命与 `nonce_expired` 判定。nonce/binding 不写审计、不进命令行、不从环境变量读取（见 §5 审计反例）。
3. **身份校验**（`BrokerPeerIdentityPolicy`，fail-closed）：远程客户端、非首实例管道、非随机管道名、内核上报 PID 与 hello 声称 PID 不一致、创建时间不一致（PID 复用）、会话不一致、用户 SID 不一致、镜像路径不在受信安装目录、签名非可信发布者、父进程无法验证/创建时间晚于子进程（伪父进程）、未保持进程句柄，都会以独立稳定 code 拒绝。进程信息来自内核（`GetNamedPipeClientProcessId`、`GetNamedPipeClientSessionId`、`OpenProcess`/`QueryFullProcessImageName`/`GetProcessTimes`/token SID/logon LUID/Queried parent）并保持 `SafeProcessHandle` 到会话结束（测试断言会话结束才释放）。
4. **随机 ACL named pipe**：名字为 `NetScopeRepair.Broker.` + 32 hex 随机字符；`PipeOptions.FirstPipeInstance` 防止预创建抢占；ACL 仅允许发起用户读/写、SYSTEM 与 Administrators 完全控制（测试逐个 SID 断言，无 Everyone/Anonymous；实际 3 条 allow 规则）。远程连接由 ACL + “内核无法给出客户端 PID 即视为远程/身份未知”双重拒绝（见限制 5）。
5. **信任/部署闸门**：`BrokerDeploymentVerifier` 读取真实 exe 路径、安装目录 ACL 是否用户可写、Authenticode 状态与签名指纹、当前 token 是否提权；`BrokerTrustPolicy` 要求“可信发布者指纹 allowlist 命中 + 签名有效 + 安装目录非用户可写 + 在安装目录内 + 已提权”全部满足才启用真实提权。**交付默认 allowlist 为空**，所有部署（包括本机开发树）都返回关闭原因（`signature_not_trusted`、`install_directory_user_writable`、`broker_not_elevated`、`real_elevation_disabled`）。`BrokerHost` 不满足信任时不会调用 listener factory（测试证明 listener 从未创建）。synthetic 策略仅测试可构造，且任何 synthetic 决定的 `IsSynthetic=true`；开发态隔离不等于提权。
6. **授权与独立 preflight**：broker 用 T13 的 `RepairPlanDigest.Compute` 从收到的 typed plan **重新计算摘要**，要求与提交摘要、授权摘要、plan.Digest 全部一致；比较目标指纹、plan id；校验授权绑定（用户 SID、登录会话、过期、逐动作确认、依赖组、UAC 不等于同意）；随后执行封闭白名单（3 个前向动作；RecoverTransaction 返回 `recover_requires_journal`；PersistentStore/IPv6/字段不匹配/派生 metric/重复动作/16 动作上限/非白名单动作逐一拒绝）；再到**broker 自己的 preflight**（`IBrokerPreflightSource`；T14 默认实现显式 `Unavailable`，不把客户端 plan 当 preflight；before 值/目标/boot/时间窗不一致返回 `requires_new_preview`）；最后按 broker 能力矩阵拒绝（交付默认矩阵全部 `unavailable`）。任一项不符都不进入执行。
7. **预算**：握手 10 秒、空闲 30 秒、总事务 180 秒、技术观察 ≤60 秒、预留 60 秒补偿、最晚 120 秒停止新的前向动作；实际观察窗 = min(60 秒, 120 秒 − 已用)，补偿预留不可被前向动作/观察占用；读帧等待取“握手/总预算剩余/空闲窗”最小值。全部边界有精确测试（9.9→ok/10.1→超时、119.9→ok/120→closed、观察 60/30/0.1/0 等）。
8. **无执行路径**：T14 没有 journal，也没有任何 setter/写网络代码；`submit_transaction` 即使通过全部校验，也只返回 `accepted=true, executed=false, codes=[execution_not_implemented]`，会话以 Refused 结束。断连/取消同样只停止新动作；断连结果明确包含 `transaction_journal_not_implemented`（补偿能力属 T15，不伪造恢复）。
9. **审计不泄密**：`BrokerAuditTrail` 仅内存保存事件种类、稳定 code、事务 id、PID、动作数；测试证明审计文本不含 broker/client nonce、binding、计划载荷、可执行文件名或本地路径。本交付没有写日志文件。

## 2. 实际修改/新增文件

新增项目（`net10.0-windows`，只引用 Domain 与 Repair，无额外 NuGet）：

- [NetScope.Broker.csproj](../../src/NetScope.Broker/NetScope.Broker.csproj)
- [BrokerProtocol.cs](../../src/NetScope.Broker/BrokerProtocol.cs)：版本、64 KiB/16 动作/各预算常量、消息闭集、拒绝 code、严格 JSON（未知成员拒绝、去掉 get-only 计算属性）、字段值判别转换器、帧编解码、标识符约束、消息读取。
- [BrokerNonce.cs](../../src/NetScope.Broker/BrokerNonce.cs)：256 位 nonce 源、绑定计算/固定时间比较、一次性消费账本（只存绑定指纹）。
- [BrokerIdentity.cs](../../src/NetScope.Broker/BrokerIdentity.cs)：内核对端证据、fail-closed 身份策略、进程句柄租约接口。
- [BrokerTrust.cs](../../src/NetScope.Broker/BrokerTrust.cs)：部署事实、信任策略、交付默认关闭、提权探测接口。
- [BrokerBudget.cs](../../src/NetScope.Broker/BrokerBudget.cs)：单调时钟预算（握手/空闲/总/前向窗/观察/补偿预留/读超时）。
- [BrokerValidation.cs](../../src/NetScope.Broker/BrokerValidation.cs)：封闭动作白名单、typed 值校验、broker 独立 preflight seam、计划摘要重算、授权/目标/过期/consent 校验、新预览判定。
- [BrokerSession.cs](../../src/NetScope.Broker/BrokerSession.cs)：一次性状态机（hello→身份→挑战→认证→单一交易决策→拒绝执行）、预算超时映射、断连/取消/协议错误处理、信任闸门二次校验、租约释放。
- [BrokerHost.cs](../../src/NetScope.Broker/BrokerHost.cs)：先评估部署/信任再创建 listener；提权探测；单次会话后退出。
- [BrokerAudit.cs](../../src/NetScope.Broker/BrokerAudit.cs)：仅 code/计数的事件审计。
- [BrokerWindows.cs](../../src/NetScope.Broker/BrokerWindows.cs)：随机 ACL 管道、内核对端事实采集、进程句柄租约、WinVerifyTrust（`WTD_CACHE_ONLY_URL_RETRIEVAL`，不联网）、安装目录 ACL 检查、token 提权探测、原生 P/Invoke。

解决方案（仅加一条项目条目，未构建整个解决方案）：

- [NetScopeRepair.slnx](../../NetScopeRepair.slnx)：新增 `src/NetScope.Broker/NetScope.Broker.csproj`。另在 `tests/WindowsIntegration` 的 csproj 中加 Broker 项目引用（该 csproj 属本任务允许范围）。

测试（全部为 fake/合成/loopback，无真实提权）：

- [BrokerFixtures.cs](../../tests/WindowsIntegration/BrokerFixtures.cs)：手动时钟+定时器、内存双工流、假 listener/connection/租约、假部署/提权、固定 nonce 源、脚本化客户端。
- [BrokerPlanFixtures.cs](../../tests/WindowsIntegration/BrokerPlanFixtures.cs)：合成可信/开发部署事实、合成对端证据、typed 计划/授权/preflight、重新摘要辅助。
- [BrokerProtocolTests.cs](../../tests/WindowsIntegration/BrokerProtocolTests.cs)：帧严格性、64 KiB、未知字段/种类、消息与动作闭集、标识符、nonce 绑定/一次性、全部预算边界。
- [BrokerIdentityAndValidationTests.cs](../../tests/WindowsIntegration/BrokerIdentityAndValidationTests.cs)：14 类身份缺陷、路径包含、信任策略矩阵、白名单/计划/授权/preflight/能力/目标/过期/consent/RecoverTransaction/16 动作/重复/派生值/路径标识符。
- [BrokerSessionTests.cs](../../tests/WindowsIntegration/BrokerSessionTests.cs)：内存双工端到端（握手、身份拒绝、nonce 重放、绑定不匹配、交易不匹配、改值需重预览、取消/关闭/未知帧/超长帧/断连、静音握手超时、空闲超时、审计不泄密、租约释放、信任闸门）。
- [BrokerHostTests.cs](../../tests/WindowsIntegration/BrokerHostTests.cs)：交付默认不启动且不建 listener、fake UAC 拒绝、listener 失败、开发态隔离单会话、取消 accept、开发态不产生特权交易。
- [BrokerWindowsTests.cs](../../tests/WindowsIntegration/BrokerWindowsTests.cs)：真实 loopback named pipe ACL 与内核对端事实、真实开发树部署检查、unsigned 文件签名状态、真实 verifier 无法启动交付 host。

未修改：`src/NetScope.Application/**`、`src/NetScope.Adapters/**`、`src/NetScope.Desktop/**`、`src/NetScope.Probes/**`、`src/NetScope.Storage/**`、`src/NetScope.Cli/**`、`src/NetScope.Domain/**`、`src/NetScope.Collectors.Windows/**`、`src/NetScope.Repair/**`（只引用，未重写 T13 类型）、`ObservationSession`、启动脚本、README、真实客户端配置与系统网络设置。

## 3. 关键设计与偏差

- **能力状态诚实**：本交付没有 signed/trusted deployment，也没有 journal/setter，所以真实提权关闭且无执行路径。`BrokerTrustPolicy.ForSyntheticTests` 与 `DevelopmentIsolation` 仅用于测试/开发隔离，`BrokerHost` 需要显式 `DevelopmentIsolation=true`，且 synthetic 决定不会把 `RealElevationEnabled` 置真。
- **协议载荷最小化**：不传 capability 记录/路径/命令；只传 typed plan、授权绑定与动作字段值。broker 用自己矩阵与 preflight 复核，客户端 plan 的 capability 记录不构成许可。
- **审计只留证据骨架**：nonce 与 binding 属能力材料，绝不落审计/异常/命令行/环境；测试以固定 nonce 源反证。
- **远程拒绝的实现方式**：托管 `NamedPipeServerStream` 未暴露 `PIPE_REJECT_REMOTE_CLIENTS`，因此以“收窄 ACL + 首实例 + 内核客户端 PID/会话读取失败即拒绝”实现；真实远程客户端未实测（见 §8）。
- **ACL 可写判定为保守近似**：检查安装目录显式 Allow 规则对当前用户/其组/Everyone 是否授予写/改/完全控制/改权限/取得所有权；检查失败按“可写”处理（关闭）。未实现完整继承/拒绝优先的 effective rights 计算。
- 未新增设计默认值改动；预算/动作枚举/存储/地址族与 T13、原设计 05 一致。

## 4. 执行的命令、环境、退出码

环境：Windows、普通权限用户；.NET SDK `10.0.403`；未提权、未运行 UAC、未改系统/网络/注册表/服务/启动项。仅构建 Broker 与 WindowsIntegration，未构建整个 `NetScopeRepair.slnx`。

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build src/NetScope.Broker/NetScope.Broker.csproj -v m` | 0 | 0 警告 / 0 错误 |
| `dotnet build tests/WindowsIntegration/NetScope.WindowsIntegration.Tests.csproj -v m` | 0 | 0 警告 / 0 错误 |
| `dotnet test tests/WindowsIntegration/... --filter "FullyQualifiedName~Broker"` | 0 | 94 通过 / 0 失败 / 0 跳过（93 个本轮新增 + 1 个 T13 既有名含 Broker 的测试） |
| `dotnet test tests/WindowsIntegration/...` | 0 | **214 通过 / 0 失败 / 0 跳过**（T13 既有 121 + 本轮 93） |
| `dotnet sln NetScopeRepair.slnx list` | 0 | 解析成功，列出含 `src\NetScope.Broker\NetScope.Broker.csproj` 与 `tests\WindowsIntegration\...`；未构建解决方案 |

日志与 TRX 仅在本地 `%TEMP%\NetScopeRepair-T14\`（未入仓）：`build-broker-final.log`、`build-tests-final.log`、`test-broker-final.log` + `t14-broker-final.trx`、`test-full-final.log` + `t14-full-final.trx`、`sln-list.log`。计数均为本次 HEAD 工作树实际执行，不复制历史 121 之外的旧数字。

## 5. 测试数量与覆盖（QA-025/026）

本轮新增 **93** 个测试用例（含 Theory 行；按 `--list-tests` 实测拆分见下）：

| 测试类 | 用例数 | 覆盖 |
|---|---:|---|
| BrokerProtocolTests | 28 | 帧/JSON 严格性、64 KiB、闭集、nonce 绑定与一次性、预算全部边界 |
| BrokerIdentityAndValidationTests | 35 | 对端身份逐缺陷、路径包含、信任矩阵、白名单/计划/授权/preflight/能力/目标/过期/consent/RecoverTransaction/上限/重复/路径标识符 |
| BrokerSessionTests | 17 | 端到端握手/身份拒绝/重放/绑定/改值/取消/关闭/未知帧/超长帧/断连/静音超时/空闲/审计/租约/信任闸门 |
| BrokerHostTests | 6 | 交付默认拒绝、fake UAC 拒绝、listener 失败、开发态隔离、取消、无特权交易 |
| BrokerWindowsTests | 7 | 真实 loopback 管道 ACL/内核事实、真实部署检查、unsigned 签名状态、真实 verifier 无法启动 host |
| **合计** | **93** | |

QA 映射：

- **QA-025 授权绑定**：计划摘要重算、改值/改目标/改 store 需新预览、计划或授权过期、错用户/错登录会话、逐动作确认缺失、依赖组部分确认、UAC 不等于同意、RecoverTransaction 无 journal；`RequiresNewPreview` 明确返回。
- **QA-026 broker 边界**：协议/动作闭集；未知字段、未知消息种类、未知动作值、超 64 KiB 帧、超 16 动作、重复动作、任意路径类标识符、远程/错用户/错会话/PID 复用/路径替换/伪父进程/非首实例/非随机管道名全部拒绝；无 shell/PowerShell/命令/脚本/任意程序集入口；日志/导出包不会成为特权恢复命令（broker 无文件输入，RecoverTransaction 仅 journal 可用且本轮拒绝）。

关键反例（均通过）：

1. 交付默认策略下真实提权关闭，listener 从未创建；本机真实部署检查同样关闭。
2. fake UAC 拒绝（token 未提权）时即使策略 synthetic-可信也不启动。
3. 身份：远程、非首实例、非随机名、错用户、错会话、hello PID/创建时间不一致（PID 复用）、镜像路径替换、未签名、伪父进程、父进程未知、句柄未保持、创建时间/用户缺失 → 各自 code。
4. 一次性 nonce：第二次使用同一绑定 → `nonce_replayed`；绑定未覆盖声明的 plan digest → `nonce_mismatch`；提交的交易 id 与绑定不一致 → `nonce_mismatch`。
5. 改值/改目标/过期/缺 consent/UAC 当同意 → 拒绝并 `requires_new_preview`（适用处）。
6. preflight 缺失 → `preflight_unavailable`；before 值或时间窗不符 → `preflight_mismatch`/`requires_new_preview`；交付能力矩阵 → `capability_unavailable`；合成完整交易通过校验但执行仍拒绝。
7. 帧：0 长度、64 KiB+1、未知 envelope 成员/种类、未知 plan 字段、`set_property` 动作值、路径型标识符 → 解析层拒绝。
8. 断连 → 停止新动作并明确 `transaction_journal_not_implemented`；取消/关闭/未知帧/超长帧分别有独立 stop reason。
9. 静音对端 10.1 秒 → 握手超时；握手后 30.1 秒 → 空闲超时；预算类精确边界（120 秒前向停止、60 秒观察上限、60 秒补偿预留不可花）。
10. 端到端审计文本不含 nonce/binding/计划载荷/可执行文件名/本地路径；租约在会话结束后才释放。
11. 真实 loopback 管道：ACL 仅 3 条 allow 规则（用户/SYSTEM/Administrators，无 Everyone/Anonymous），随机名，内核上报 PID/用户/登录会话/创建时间/签名状态，unsigned 开发树被拒绝。

## 6. 能力关闭矩阵（本轮实际状态）

| 能力 | 状态 | 原因/证据 |
|---|---|---|
| 真实提权 broker（UAC、管理员令牌） | **关闭** | 交付默认无可信发布者、开发树用户可写/未签名/未提权；`BrokerHost` 在创建 listener 前拒绝；本机真实 `WindowsBrokerDeploymentVerifier` 同样关闭 |
| 真实写入/修复动作（三字段 setter） | **不存在** | Broker 项目无任何 setter/网络/注册表写代码；通过校验的交易返回 `execution_not_implemented`、`executed=false` |
| RecoverTransaction / 断连补偿 | **关闭** | 无 journal（T15）；一律 `transaction_journal_not_implemented`/`recover_requires_journal` |
| 通用 SetProperty / shell / PowerShell / 命令 / 脚本 / 任意程序集 / 任意用户路径 | **不存在** | 协议/动作/标识符闭集，解析层拒绝 |
| 开发态协议隔离（测试） | 仅测试 | synthetic 策略 + `DevelopmentIsolation=true`，`IsSynthetic=true`，仍不执行特权写 |
| 本机真实命名管道（含 ACL/内核身份采集） | 已测（只读本机事实） | `BrokerWindowsTests`；无提权、无网络写 |
| 真实 UAC 对话框/提权进程 | **未执行** | 本环境不运行 UAC；只在 fake probe 下验证拒绝分支 |

## 7. 只读/权限/隐私边界检查

- 未运行 UAC、未请求提权、未创建服务/计划任务/启动项/托盘；未启动真实修复进程或 broker 管理员进程。
- 未写网络/路由/DNS/代理/防火墙/注册表/系统代理设置；未启动或停止任何客户端；未读取客户端订阅/凭据/真实配置。
- 唯一真实 I/O：本机 loopback named pipe（IPC，非网络）、进程/token 只读查询、文件签名与安装目录 ACL 只读检查、测试进程自身身份读取。
- Authenticode 使用 `WTD_CACHE_ONLY_URL_RETRIEVAL`，不做在线吊销/链下载，无外部请求。
- nonce/binding 不落审计/异常/命令行/环境；审计只含 code/事务 id/PID/动作数。测试用固定 nonce 反证。
- 未运行旧修复脚本、未 `git reset/clean/checkout`、未提交/推送；未覆盖并行任务文件。

## 8. 已知限制与未验证项

1. **真实提权与 UAC 未验证**：没有 signed/trusted deployment、没有管理员令牌执行路径；本环境未运行 UAC。真实 broker 提权启动、ACL 管道在提权进程中的实际行为均未实测。
2. **无 T15 journal/执行器**：断连补偿、部分失败、精确恢复、recovery_required 均未实现；T14 的“断连停止 + 不伪造恢复”已实现，真实补偿留给 T15。
3. **一次性 UAC 启动器/客户端接线**：属 T16/Composition；本轮没有 UI 改动，UI 仍 asInvoker。没有 Desktop/CLI 调用 broker 的入口。
4. **总预算/前向窗的会话级端到端路径未触发**：预算类边界有精确测试；会话级只覆盖握手/空闲超时，若要触发 120 秒前向关闭需会话先存活 120 秒，而静音对端会先命中 30 秒空闲窗。代码在提交前检查 `CheckForwardAction`，但该分支未用真实等待演练。
5. **远程客户端拒绝未实测**：依赖 ACL + 内核客户端 PID 读取失败即拒绝；托管 API 未设置 `PIPE_REJECT_REMOTE_CLIENTS`，真实 SMB 远程连接未在隔离网络验证。
6. **ACL 可写判定为近似**：只合并显式 Allow 规则与当前用户组；未做完整 effective rights/继承拒绝计算，失败按关闭处理。
7. **父进程校验依赖可读**：受保护/受限制父进程读不到路径或签名时一律拒绝（fail-closed），可能对合法签名部署产生额外限制。
8. **真实签名信任链未验证**：默认 allowlist 为空，唯一可验证的是“未签名/不受信任不会通过”；未建立真实发布者基线，也未验证 WinVerifyTrust 在各 Windows 版本/吊销策略下的行为。
9. **审计不持久化**：T14 无日志文件/ journal，审计仅内存；如需持久证据由 T15 设计，不能把本报告当持久审计已实现。
10. **合成能力的边界**：`BrokerTrustPolicy.ForSyntheticTests`/`synthetic capability matrix` 只用于测试；生产组合必须使用 `DeliveryDefault` + `PlatformCapabilityMatrix.UnverifiedDefault`（无调用者会切到 synthetic）。审查时应确认没有生产路径传入 synthetic 策略。
11. **未做性能/长稳测试**：帧解析、管道吞吐、并发会话（本设计单次单客户端）未做压力测量。

## 9. 是否有真实网络操作

**无外部网络操作。** 逐项：无 connect/DNS/HTTP/TCP 外部请求；无代理/路由/DNS/注册表/防火墙变更；无 UAC/提权；无服务/计划任务/启动项；无客户端启动/停止；无真实修复写入；无上传/提交。测试唯一的真实 IPC 是本机 loopback named pipe，用于验证 ACL 与内核身份事实；全部 broker 端到端协议测试使用内存双工流。

## 10. 建议下一任务（不自动执行）

按施工顺序为 **T15（Journal 与精确恢复）**。其前置仍包括：T13 三字段平台实验（当前 0 执行）、真实可信 TUN 归属/平台事实生产器、审查者确认 `PlatformCapability.Verified` 仅受控导入使用。T15 应复用本任务已冻结的协议版本、预算、nonce/身份边界与拒绝 code，并补齐：受限事务目录 ACL/排他句柄、intent/applied 刷盘、崩溃未决记录、精确恢复、断连补偿，以及真实隔离 VM/UAC 验证（未完成前真实提权保持关闭）。

本轮停止在 T14，不领取 T15，不创建提交或推送。

