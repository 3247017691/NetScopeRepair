# M0 施工报告：T02—T06

日期：2026-11-04 15:45 (Asia/Shanghai)
范围：T02 只读采集抽象、T03 Windows 只读采集、T04 三路径探测、T05 事件存储与回放、T06 首批诊断规则。
结论：**五项完成，干净重建 0 警告 0 错误，173/173 测试通过。**

## 1. 工程结构

```text
src/
  NetScope.Domain/              契约层（net10.0，零 NuGet 依赖）
  NetScope.Application/         采集抽象、结果建模、mock 采集器、契约检查
  NetScope.Collectors.Windows/  Windows 只读采集（net10.0-windows）
  NetScope.Probes/              三路径探测（可注入传输与时钟）
  NetScope.Storage/             NDJSON 事件日志 + SQLite 投影 + 离线回放
  NetScope.Diagnostics/         7 条纯函数诊断规则
  NetScope.Cli/                 check / snapshot / demo / replay
tests/
  Contract/ 72   Unit/ 55   Replay/ 21   Windows/ 25
```

依赖单向：Domain 无平台依赖；Application 只依赖 Domain；Windows/Probes/Storage/Diagnostics
依赖抽象；CLI 做组合根。Diagnostics 不引用 `Process`、`HttpClient` 或 Broker。

## 2. 各任务交付与证据

### T02 只读采集抽象

- `Collected<T>` 把“读到了”“部分读到”“读不到”分开；`HasValue` 只在状态真正产出数据时为真。
  不可用域不会退化成空数组。
- `IClock` / `ISnapshotCollector` / `IProbeRunner` / `IDiagnosticRule` / `IEventStore` 五个端口。
- `SyntheticSnapshotCollector`、`SyntheticProbeRunner` 提供确定性演示数据，全部标注 synthetic。
- 测试：`CollectionTests`、`ProbeRunnerTests` 中的结果语义与路径用例。

### T03 Windows 只读采集

- 网卡：`NetworkInterface` + 稳定身份（GUID / LUID / ifIndex / 类型 / 状态 / 地址）。
- 路由：`GetIpForwardTable2` 只读 P/Invoke，读取前缀、下一跳、接口、`RouteMetric`、`Protocol`、`Origin`。
- 接口度量：`GetIpInterfaceEntry`，读取 Metric / AutomaticMetric / DisableDefaultRoutes。
- DNS：逐接口服务器与后缀。
- 代理分层：进程环境、用户环境、机器环境、WinINET、WinHTTP（后者显式 `unsupported`，不猜值）。
- 隐私：只读 8 个代理相关变量名；URL 中的 userinfo 一律替换为 `***` 后再进入日志。

**真机交叉核对（只读）**：

```text
接口度量：本工具 9 个接口 vs Get-NetIPInterface IPv4 —— 全部一致（1:75 7:25 8:35 11:35 18:35 19:25 20:25 21:30 47:0/automatic=Disabled）
路由数量：本工具 69 vs Get-NetRoute 41(IPv4)+28(IPv6)=69
默认路由：IPv4 4 条、IPv6 2 条，下一跳与接口全部一致
WinINET：本工具 enabled=yes / ProxyServer=127.0.0.1:7890 vs 注册表 ProxyEnable=1 —— 一致
```

发现并修正：先前把 `NL_ROUTE_ORIGIN` 当成 protocol 输出，已改为分别读取 Protocol 与 Origin。
38 个过滤器驱动伪接口（无 ifIndex、无地址）被跳过，并在 `Reason` 中说明数量与原因。

### T04 三路径探测器

- `system-default` / `no-explicit-proxy` / `explicit-http-proxy` 三条路径各用独立 `HttpClient`，
  连接池不共享，避免把实现差异当成网络差异。
- 每组同目标、同方法、同窗口；组预算、单探测超时、并发上限、取消全部可注入。
- 未启动的路径报告为 `missing`，取消报告为 `cancelled`，超时报告为 `timeout`，
  三者都不等于成功。返回 `reachable` 但没有 HTTP 状态码时自动降级为 `partial`。
- `no-explicit-proxy` 的 notice 明确写“不证明物理直连”。
- 未完成：地址族强制（会返回 `unsupported` 而不是假装生效）、逐跳重定向记录（M0 不跟随重定向）。

### T05 事件存储与回放

- `NdjsonEventLog`：逐行严格反序列化；追加去重；写后 `Flush(flushToDisk: true)`；
  读取时报告重复、序号缺口、序号回退与截断坏尾，坏尾不阻断前面有效事件。
- `SqliteEventIndex`：WAL + 事务投影，`INSERT OR IGNORE` 保证幂等，可从 NDJSON 重建。
- `ReplayService`：只读回放，确定性摘要（同输入同输出），无网络依赖。
- 依赖：`Microsoft.Data.Sqlite 10.0.14`（T01 允许的固定版本依赖，未改全局源）。

### T06 首批诊断规则

| 规则 | 触发条件 | 等级策略 |
|---|---|---|
| DX-001 失效代理依赖 | 显式路径在 connect/tls 阶段失败 | 有采用证据=`supported`；同路径也成功过=`candidate`；仅配置存在=`candidate` |
| DX-002 fake-IP 上下文 | 解析命中 198.18.0.0/15，或存在落在该段内的路由/DNS | 有配置证据=`supported` 且写明“不是污染证据”；否则=`candidate` |
| DX-005 端口归属冲突 | 实际 bind 失败 + 同端点所有权 | 两者齐备=`supported`；否则 `candidate`；仅两客户端同端口=`candidate` 且写明不足以判定 |
| DX-006 配置陈旧 | 磁盘文件 mtime 早于进程启动 | `supported`，只说明磁盘不能代表运行态 |
| DX-008 间歇故障 | 同目标同路径同时出现成功与失败 | `observed`；样本期拓扑变化降为 `candidate` |
| DX-010 路由修复 no-op | 拟议变更目标值已等于当前值 | 全等=`contradicted`，反驳归因 |
| DX-011 采集相互矛盾 | 监听列表空/受限 + 同端点 connect 成功 | `insufficient`，阻止“端口未关闭”的错误结论 |

规则约束：每条结论必须携带 `evidence_ids`（空引用直接抛错）；`AllowedRepairActions` 恒为空；
引擎按 Rule ID 排序、顺序分配 `diagnosis-001..`，单条规则抛异常时产出可见的“规则执行失败”诊断
而不是中断整轮。

## 3. 端到端行为证据

```text
netscope demo --out C:\Temp\netscope-demo        exit=0
  snapshot 域：interfaces/routes/dns/proxy = ok
  6 次探测（2 组 × 3 路径）：no-explicit-proxy 组1 timeout、组2 ok
  诊断 2 条：DX-002 [supported] fake-IP 上下文；DX-008 [observed] 1/2 次失败
  写入 9 个事件 → NDJSON + SQLite → 回放 problems=0 gaps=0 duplicates=0

netscope check C:\Temp\netscope-demo\session-demo.ndjson   exit=0
  events=9 ok=8 partial=0 observation_gap=1 rejected_lines=0
  结论：工具自己产出的会话同样符合已发布契约

netscope snapshot                                 exit=0
  真实只读采集：9 个接口、69 条路由、4 条 IPv4 默认路由、代理 5 层状态

netscope replay C:\Temp\netscope-demo\session-demo.ndjson  exit=0
  events=9 synthetic=9 ok=8 gap=1
```

## 4. 测试

```text
tests/Contract  72   tests/Unit  55   tests/Replay  21   tests/Windows  25
合计 173，通过 173，失败 0，跳过 0，实际执行数 > 0
```

Windows 测试包含真实只读采集的结构断言：接口列表非空且身份完整、路由前缀可解析且族别一致、
代理层都有 provenance、任何一层都不得泄露凭据、采集不得因权限拒绝而抛异常。

## 5. 已知限制

1. 无 WPF 界面、无托盘、无故障前窗录制、无配额轮转（M1/M2 范围）。
2. 无修复能力：无 broker、无事务、无写入口；M0 任何命令都不修改系统。
3. 代理 WinHTTP 层未读取（显式 `unsupported`）。
4. 地址族强制与逐跳重定向未实现，已如实返回 `unsupported` / 不跟随。
5. DX-005/006 的输入（端口所有权、客户端配置与进程）由 M1 的适配器提供，本轮只用合成记录验证规则。
6. 真实外部探测未运行：默认走 mock 传输；真实探测需用户指定目标后主动启动。
7. `payload` 仍无按事件类型的专用 JSON Schema。

## 6. 真实网络操作

- 有：两次 `netscope snapshot` 与一次验证程序的只读采集（网卡/路由/接口度量/DNS/代理注册表），
  以及一次 `Get-NetIPInterface` / `Get-NetRoute` / 注册表读取用于交叉核对。全部只读，不需要提权。
- 无：未发出任何探测请求、未切换 TUN、未运行旧修复脚本、未修改任何网络或代理配置、未上传、未发布。

补充观察（不构成结论）：两次只读采集之间 WinINET `ProxyEnable` 由关闭变为开启（外部操作），
说明配置会漂移；因此每条证据都必须带读取时间与 provenance，而不能把一次读到的值当作长期事实。
