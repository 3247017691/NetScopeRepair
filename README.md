# NetScope Repair

Windows 代理网络的**诊断与受控修复**工具。始终作为 Nice加速、Apex、mihomo/Clash 等客户端的
伴随工具存在：不承载流量、不内置代理内核或 TUN 驱动、不替代现有客户端。

## 当前状态

**M1-R1 使用前复核与补修已完成**（2026-11-05）。独立复核推翻了原 G1 的通过判断，修复连续快照导出、演示隔离、脱敏、设置与退出等缺陷后重新验证。当前可进行只读诊断试用；用户日用体验与真实外部链路仍待验证。

```text
dotnet build NetScopeRepair.slnx  ->  0 警告 0 错误
dotnet test  NetScopeRepair.slnx  ->  失败 0，通过 616，已跳过 0，总计 616
```

M1 仍是**只读增量**：界面没有任何修改系统网络设置或执行修复的入口；修复能力属于 M3。
**当前不能替代桌面的 TUN 优先级修复脚本**：修改 TUN 度量、自动度量、IgnoreDefaultRoutes，以及修后验证/撤销，尚未接入；原脚本保留，程序不会调用它。
真实外部探测只在用户配置目标并显式点击后发生；真实 IPv4/IPv6 强制仍未实现。

- [G1-R1 使用前复核、补修与 TUN 功能对账](docs/reports/G1-R1-M1-review.md)
- [M1-R1 最终核验汇总](docs/reports/evidence/M1-R1-20261105/verification-summary.json)
- [M1 / T07—T09 最初交付记录](docs/reports/M1-T07-T09-report.md)
- [G1 M1 门禁评审](docs/reports/G1-M1-review.md)
- [M1 最终核验汇总](docs/reports/evidence/M1-20261105/verification-summary.json)
- [G0-R1 补修门禁复审（通过 M0 范围）](docs/reports/G0-R1-M0-review.md)
- [M0-R2 交付与 18 项关闭对账](docs/reports/M0-R2-report.md)
- [最终测试、CLI、native 读回核验](docs/reports/evidence/M0-R2-20261104/verification-summary-v2.json)
- [G0 首轮正式复核（历史不通过）](docs/reports/G0-M0-review.md)
- [G0 缺陷清单与补修条件](docs/reports/G0-M0-findings.md)
- [G0 实际反例结果](docs/reports/evidence/G0-20261104/repro-results-final.json)
- [T02—T06 施工报告](docs/reports/M0-T02-T06-report.md)
- [T01-R1 契约修复报告](docs/reports/T01R1-report.md)
- [T01 最小工程与核心契约](docs/reports/T01-report.md)
- [T00 环境核对](docs/reports/T00-environment-report.md)

## 使用

### 桌面（M1-R1）

当前已构建的试用版：双击 [运行 NetScope 只读试用版](运行NetScope只读试用版.cmd)，或打开 [M1-R1 程序](artifacts/M1-R1/NetScope.Desktop.exe)。旧版如仍在托盘，请先在旧版菜单中退出再开新入口。依赖本机已有 .NET 10 Desktop Runtime。

开发运行：

```text
dotnet run --project src/NetScope.Desktop
```

手动启动；默认自动开始**本机只读快照**（不发起外部探测）。关闭窗口默认隐藏到托盘并继续观察，
显式“退出”才停止采集并 flush 日志。页面：首页、时间线、诊断与证据、多客户端、导出、设置、帮助。
“立即诊断”只在设置中配置 http(s) 目标后可用；导出只生成本地 ZIP，不上传。

开发自检（只读、不开窗口）：

```text
NetScope.Desktop.exe --render-screens <dir>   离屏渲染 7 个页面为 PNG
NetScope.Desktop.exe --smoke-test <file.json> 真实只读采集一次并尝试导出预览
```

### CLI

```text
netscope check [<file>]        校验契约与合成 fixture（或指定会话文件）
netscope snapshot              只读采集一次本机网络状态，不需要提权
netscope demo [--out <dir>]    跑合成流水线：采集→探测→诊断→落盘→回放
netscope replay [<file>]       离线回放已捕获会话
netscope help                  命令列表
```

`demo` 默认写入 `%LOCALAPPDATA%\NetScopeRepair\demo`，可用 `--out` 指定新目录；已有日志或索引时拒绝覆写。
它保存完整合成事实、源时间及证据引用，并从磁盘重算核对诊断。`check <file>` 保留真实/合成标记，不强制把会话改为合成；默认 fixture 仍要求 synthetic=true。
所有命令均不修改系统网络配置；`demo` 写入本地演示日志和索引。M0/M1 不存在修改网络、启动修复或申请提权的路径。

## 目录

```text
contracts/                     事件封套 JSON Schema（与设计包同步）
fixtures/synthetic/            合成故障事件，全部 synthetic=true
src/NetScope.Domain/           契约层：强类型封套、严格序列化、枚举线名、契约校验
src/NetScope.Application/      采集抽象、结果建模、适配/导出端口、mock
src/NetScope.Collectors.Windows/ 网卡/路由/DNS/代理分层的只读采集
src/NetScope.Probes/           三路径探测（可注入传输与时钟）
src/NetScope.Storage/          NDJSON 事件日志、SQLite 投影、离线回放、脱敏导出
src/NetScope.Diagnostics/      DX-001/002/005/006/008/010/011
src/NetScope.Adapters/         Nice/Apex/generic 只读适配与依赖解释
src/NetScope.Desktop/          WPF 只读界面、托盘、观察会话
src/NetScope.Cli/              check / snapshot / demo / replay
tests/Contract 80 · Unit 243 · Replay 58 · Windows 79 · Adapters 81 · Export 43 · Desktop 32
docs/adr/                      架构决策记录
docs/reports/                  任务与门禁报告
```

## 设计要点

- **只有 `status=ok` 算成功。** `partial`、`timeout`、`failed`、`cancelled`、`unsupported`、
  `access_denied`、`unknown` 都不能被当作"空结果即正常"。
- **读不到就说读不到。** 每个数据域都有状态和原因，不会退化成空列表；被跳过的过滤器驱动伪接口
  会说明数量与原因。
- **结论必须引用证据。** 规则不引用 `evidence_id` 会直接失败；反证和缺失证据是必填表达。
- **规则不产出动作。** `AllowedRepairActions` 恒为空；M0/M1 没有可用于执行 repairs 的代码路径。
- **`no-explicit-proxy` 不证明物理直连。** TUN 开启时该请求仍可能经过 TUN。
- **保留段不等于污染，也不独自证明 fake-IP 模式。** 需要实际 DNS 返回与同作用域的可信运行态配置/适配器证据；只有路由或 DNS-server 线索时保持候选。
- **观察缺口与网络失败分开。** 用户取消、权限不足、能力缺失和本地执行错误不计有效网络失败；同作用域缺口仍保留计数、原因和引用。
- **未知事实保持未知。** scalar 接口事实带地址族，不借用另一族的标志；metric/TotalMetric 缺失为 null，部分读取失败传播 Partial。
- **合成的就标合成。** 演示数据在横幅、首页状态、时间线与诊断页都明确标为 mock，不冒充本机观测。
- **导出先预览、默认最小化。** 本地 ZIP 用包内稳定别名替换用户/设备/路径/内网地址，salt 不随包输出；D3 不采集也不导出。

## 边界

- 诊断默认只读，不需要管理员权限。
- 不修改或断开真实上行（PPPoE / 以太网 / Wi-Fi / RNDIS）。
- 不自动切换 TUN、不重启客户端或内核、不改全局代理环境变量。
- 不注册服务、计划任务或开机启动项；手动启动，显式退出即停止记录。
- 修复能力属于 M3，且必须先有证据、计划预览、用户确认与可恢复事务。

## 后续

当前停在 G1-R1 的只读试用准备完成处，未实施 M2/M3。用户日用试用尚未执行。
M2（T10—T12）才加入故障前后窗、自动触发、配额/恢复与案例回放；M2 之前退出期间不承诺留下故障现场。TUN 参数修改与可恢复事务属于 M3（T13—T16），仍是完整自用首版的待办核心需求。
当前能力与限界见 [G1-R1 报告](docs/reports/G1-R1-M1-review.md)，M0 关闭证据见 [G0-R1 报告](docs/reports/G0-R1-M0-review.md)。
