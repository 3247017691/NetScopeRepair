# M1 只读 MVP 交付报告（T07 / T08 / T09）

> 使用前复核发现本报告自测未覆盖的实际缺陷，已在 M1-R1 补修。当前验证结果、剩余限制与试用入口见 [G1-R1 复核报告](G1-R1-M1-review.md)；下文保留最初交付记录，不作为当前放行依据。

日期：2026-11-05（UTC+08:00）。范围：WPF 只读桌面、脱敏导出、客户端适配与依赖解释。**未进入 M2，不包含任何网络写入口或修复执行。**

**结果：** 全 solution 构建 0 警告 0 错误；7 个测试程序集合计 **555 通过、0 失败、0 跳过**；7 个页面离屏渲染成功；真实本机只读 Desktop 启动冒烟通过并生成本地会话；该会话导出为 7 文件脱敏 ZIP（校验和全部一致），导出事件可离线重放且 `problems=0`。

## 1. T07 WPF 只读界面

新增 `src/NetScope.Desktop`（`net10.0-windows`，WPF + WinForms `NotifyIcon`，零产品 NuGet）与 `tests/Desktop`。

- **页面（全中文）**：首页（状态、数据域完整性、当前会话、最近诊断）、时间线（事件表、读取问题、序号缺口/重复、payload 展开）、诊断与证据（等级、引用、反证、缺失证据、规则失败单列）、多客户端、导出、设置、帮助。
- **状态语义**：`当前样本正常` 只在四个数据域完整且无异常结论时出现，并写明“不代表全网正常”；部分/权限不足/取消/未知一律显示为采集不足或缺口；M1 无“执行修复”按钮，`AllowedRepairActions` 仅展示为“M1 不执行修复”。
- **观察会话**：手动启动应用后自动开始**本机只读快照**（10 秒默认，可调），不自动发起外部探测；显式点击“立即诊断”才用已配置的 http(s) 目标运行一组三路径探测。每次观察写入完整 typed 事件（真实源时间、继续序号、canonical evidence id）。
- **托盘与退出**：关闭窗口默认隐藏到托盘并继续观察（首次询问并记住选择，可在设置查看）；托盘菜单打开/开始/暂停/立即诊断/退出，图标提示状态；显式退出停止调度并 flush 日志。没有开机启动、服务、计划任务或提权。
- **写入诚实**：日志追加失败时标记“日志写入失败”，不重试伪造成功，事件仍明确未持久化（`tests/Desktop` 覆盖：路径被目录占用导致打开失败）。
- **验证**：`tests/Desktop` 18 项通过（首个样本前的状态守卫、缺域、证据异常、反证不误报、追加/停止、三路径落盘、写失败、设置回读、合成演示、演示期间暂停真实观察、多批次序号连续、无目标不探测）；真实启动冒烟 `--smoke-test` 输出 `persisted=true`、`interfaces=Partial` 等诚实域状态（[desktop-smoke-3.json](evidence/M1-20261105/desktop-smoke-3.json)）。页面渲染见 [screens-9](evidence/M1-20261105/screens-9)；`--render-screens` 只读渲染，不打开窗口。

## 2. T08 导出与脱敏

新增 `src/NetScope.Storage/Export/**` 与 `tests/Export`（32 项通过）。

- **预览**：严格 `ReplayService` 读取；坏行、序号缺口、重复 ID、非 typed 或证据不一致一律给阻断原因且不生成文件；只显示文件类别/大小/用途、脱敏类别与计数、事件/缺口/诊断计数，不显示原值。
- **包结构**：`manifest.json`、`events.ndjson`、`snapshots/*.json`、`diagnoses.json`、`timeline.json`、`redaction-report.json`、`checksums.sha256`；manifest 含契约/应用/规则版本、时间范围、计数、synthetic 标记、脱敏策略与每文件 SHA-256。
- **匿名化**：每包随机 32 字节 salt（只在内存，不序列化）；用户/设备/本地路径/内网主机稳定别名；公网目标 `target-A…`；地址按类别替换为同类合法地址（fake-IP 仍属 `198.18.0.0/15`，私网仍属 10/8，公网映射 TEST-NET-3），保持规则语义；事件/证据 ID 与引用不变，导出事件可重新通过严格读取和规则评估。
- **不可信包**：只读中央目录的准入检查（100MB 总量、1000 文件、单文件 20MB、深度 8；拒绝绝对路径、盘符、`..`、反斜杠越界、点段、符号链接、大小写重复、超限），不打开条目、不解压；M1 不提供导入执行入口。
- **实测包**：真实会话导出 `incident-0aaa642b…zip`（7 条目，9331 字节），`verify-package.py` 校验结构、6 个校验和、salt 未包含、禁止标记（用户名/设备名/用户目录/本机内网段）全干净；导出事件用 CLI 重放 `events=1 problems=0 typed_evidence=yes`，并重算出 DX-001 候选（[回放日志](evidence/M1-20261105/exported-events-replay.log)）。

## 3. T09 客户端适配与依赖解释

新增 `src/NetScope.Adapters`（`net10.0-windows`，零产品 NuGet）与 `tests/Adapters`（51 项通过）。

- **通用适配器**：进程身份、监听端点归属、TUN 适配器、代理层摘要；未知进程不扫描猜测，读不到即 `null` + gap。
- **Nice / Apex**：仅按字段白名单解析 `runtime.yaml` / `shared_preferences.json`（mixed-port、mode、TUN、fake-IP 范围等）；记录 parser 版本、mtime、sha256、有效性与读取时间；**不读订阅、凭据、provider 列表或配置全文**；测试夹具含合成 secret，扫描确认不进入任何事实或字符串。
- **来源矩阵**：Nice 恒为 `disk_only`；Apex 有 runtime 白名单事实时 `runtime_api`（不读磁盘），启动引用精确匹配时 `process_start_reference`，其余 `disk_only`；进程/监听/TUN 属状态证据，不是配置来源。
- **依赖解释**：DSH 与 Antigravity 分层说明工具进程环境、用户/机器环境、WinINET/WinHTTP、启动参数与目标主机，明确哪些层可读、哪些是未知继承；只读边界写在界面与代码。
- **多客户端**：同端点并存只给“并存”说明，不自动判冲突；没有停止/切换/重启 API。

## 4. G1 独立复核发现与修正

独立只读复核（未修改源码、未构建/测试）在实现自测之外确认了 6 个问题，均已在最终回归前修正：

1. **首个样本前误报“当前样本正常”**：`Recompute` 把空数据域列表当作完整。现增加“是否有已完成样本”守卫；启动/从暂停恢复会清空旧状态并显示“等待首个样本”，不沿用旧样本。新增 2 项 Desktop 回归。
2. **观察中载入合成演示被静默回退，诊断页仍显示合成结论**：现加载演示会先暂停真实观察并提示；诊断页新增 `IsSynthetic` 标签和“合成演示结论”横幅；点击开始观察会清空演示诊断与时间线并回到真实会话。新增 1 项回归并在演示截图核对。
3. **批次中间取消会复用序号，SQLite 投影失败会拖垮主日志**：序号改为每次成功追加后立即推进；SQLite 逐事件投影且失败只记 `ProjectionError`，不影响 NDJSON 主日志；`StopAsync` 等待循环结束再释放句柄。新增多批次序号连续回归。
4. **“存在反证/证据不足”被显示为“发现异常”**：现仅观察/候选/支持/已验证等级显示为异常，其余显示“证据不足或存在反证”。新增 1 项回归。
5. **含空格的本地路径只脱敏前缀**：路径正则不再在空白处截断；新增导出回归（`C:\Users\Alice Smith\…` 不再残留 `Smith\AppData`）。同步修正两处过度承诺的代码注释。
6. **托盘“立即诊断”无目标时静默无反馈**：无可用目标时给出托盘气泡说明；托盘提示文本纳入异常/采集不足状态。

## 5. 执行记录

| 验证 | 结果 | 证据 |
|---|---|---|
| solution build | exit=0，0 警告 0 错误 | [build-final.log](evidence/M1-20261105/build-final.log) |
| solution test | 555 通过 / 0 失败 / 0 跳过（Adapters 51、Contract 80、Unit 243、Windows 79、Export 32、Replay 52、Desktop 18） | [test-final.log](evidence/M1-20261105/test-final.log)、[TRX](evidence/M1-20261105/test-results-final/) |
| 页面离屏渲染 | 7 页 PNG（1440×900） | [screens-9](evidence/M1-20261105/screens-9) |
| Desktop 真实冒烟 | 只读采集 + 追加 1 事件 + 导出预览 7 文件 + 生成 ZIP；`interfaces=Partial` 如实显示 | [desktop-smoke-3.json](evidence/M1-20261105/desktop-smoke-3.json) |
| 包校验与重放 | 7 条目、6 校验和、禁止标记 0、salt 未包含；重放 problems=0 | [verify-package.py](evidence/M1-20261105/verify-package.py)、[exported-events-replay.log](evidence/M1-20261105/exported-events-replay.log) |
| 机器核验汇总 | 555/7 页/冒烟/指纹 | [verification-summary.json](evidence/M1-20261105/verification-summary.json)、[源指纹](evidence/M1-20261105/source-sha256-m1.json) |

## 6. 限界与未完成项

- **托盘与关闭交互未做自动化**：WPF 托盘点击、关闭询问与实际显隐依赖交互式桌面会话；本轮以代码审查 + 离屏渲染 + 启动冒烟覆盖，未声称其行为已自动验证。
- **真实探测能力**：transport 仍不强制 IPv4/IPv6（选择后诚实返回 unsupported）；外部目标由用户配置并显式触发，未做真实外部连通性验收。
- **适配器真机字段**：Nice/Apex 的真实进程名与配置结构、`GetExtendedTcpTable` 运行时行为、TUN 适配器命名未在真机验证；测试全部使用合成 fixture 与注入 seam，未读取真实用户配置。
- **导出残留风险**：公网目标固定别名（无保留原域选项）；预览只回传文件名；不匹配键名的自由文本 D3 依赖上游不采集；`target_id/proxy_endpoint_id/interface_guid` 视为不透明标识；包在内存构建，超大会话无显式上限；预览与导出之间若日志变化结果可能不同；公网别名使用 TEST-NET-3。
- **M2 范围**：故障前后窗、自动触发、配额/轮转/崩溃恢复、案例回放工作台未实现；M1 只有手动启动期间的记录。

## 7. 文件索引

- 桌面：`src/NetScope.Desktop/{App.xaml,App.xaml.cs,MainWindow.xaml,Views/*,ViewModels/*,Services/*}`
- 导出：`src/NetScope.Storage/Export/*`
- 适配：`src/NetScope.Adapters/*`
- 契约：`src/NetScope.Application/{Adapters.cs,Exports.cs}`
- 架构：`docs/adr/0004-m1-desktop-export-adapters.md`
- 证据：`docs/reports/evidence/M1-20261105/`
