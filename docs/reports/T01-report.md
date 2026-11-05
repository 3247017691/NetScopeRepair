# T01 执行报告：最小工程与核心契约

日期：2026-11-04 12:15 (Asia/Shanghai)
任务：T01 工程与核心契约
结论：**完成**。solution 建立，真实构建与测试已执行并通过，停在 T01 等待评审。

## 1. 交付物

| 路径 | 内容 |
|---|---|
| `NetScopeRepair.slnx` | 解决方案，含 `src/` 与 `tests/Contract/` 两个文件夹 |
| `global.json` | SDK 10.0.100 起、`rollForward: latestFeature` |
| `Directory.Build.props` | `Nullable`、`ImplicitUsings`、`Deterministic`，`nullable` 警告视为错误 |
| `contracts/event-envelope.schema.json` | 与设计包一致的封套契约（只读复制） |
| `fixtures/synthetic/synthetic-incident.ndjson` | 6 条合成事件，全部 `synthetic=true` |
| `src/NetScope.Domain/` | 强类型封套、严格反序列化、零 NuGet 依赖的契约校验器 |
| `src/NetScope.Cli/` | 只读检查器，读取合成 fixture 与契约并逐条校验 |
| `tests/Contract/` | xunit 契约测试 |
| `docs/adr/0001-...md` | 仓库位置、零依赖核心、延后生成工程文件的决策 |
| `docs/reports/T00-environment-report.md` | 环境核对（含 SDK 安装补记） |

Domain 层文件：`EventType.cs`、`EventStatus.cs`（含 `IsSuccess` / `IsObservationGap`）、
`EventRefs.cs`、`EventEnvelope.cs`、`EnvelopeJson.cs`、`SchemaConformance.cs`。

## 2. 实际执行的命令与结果

工具链：.NET SDK 10.0.403，Windows 11 10.0.26300 x64。

```text
dotnet new sln -n NetScopeRepair              exit=0   -> NetScopeRepair.slnx
dotnet new xunit -o tests\Contract            exit=0   -> xunit 2.9.3 / Test.Sdk 17.14.1 / runner 3.1.4
dotnet sln NetScopeRepair.slnx add ...        exit=0   -> 3 个项目
dotnet build NetScopeRepair.slnx --nologo     exit=0   -> 0 个警告，0 个错误（5.51 s）
dotnet test  NetScopeRepair.slnx              exit=0   -> 测试总数 36，通过 36，失败 0（3.34 s）
```

测试实际执行数 36，大于 0，非空跑。

## 3. 测试覆盖（36 个用例）

| 组 | 用例数 | 断言内容 |
|---|---:|---|
| 文件与 fixture 完整性 | 3 | 契约与 fixture 存在；全部事件可反序列化；全部 `synthetic=true` |
| fixture 结构 | 1 | 序号连续、`event_id` 唯一、单调时钟有序 |
| 严格反序列化 | 8 | 缺必填字段、缺 `payload`、未知封套字段、未知 `refs` 字段、非法状态值（3 例）、非法事件类型（2 例）均被拒绝 |
| 契约校验器 | 6 | schema 只用已实现关键字；6 条事件全部合规；缺必填、错版本、未知属性、非 RFC3339 时间戳均被判不合规 |
| 状态语义 | 16 | `ok` 是唯一成功；`failed`/`timeout`/`cancelled`/`unsupported`/`access_denied`/`unknown` 归为观测缺口；`partial` 既不算成功也不算缺口 |
| 往返一致 | 1 | 序列化后重新反序列化，字段与 payload 保持一致 |
| CLI 行为（人工执行） | 2 次运行 | 见下节 |

## 4. 行为证据

正常运行：

```text
events=6 ok=4 partial=1 observation_gap=1
OK: every envelope was read and conforms to the published contract.
cli_exit=0
```

负向对照：把第 2 条事件的 `status` 改成非法值 `totally-ok` 后

```text
events=5 ok=3 partial=1 observation_gap=1
FAILED: 1 problem(s) found.
  - line 2: the envelope could not be read - The JSON value could not be converted to NetScope.Domain.EventStatus.
cli_exit=1
```

确认：坏行被排除并显式报错，没有被静默计入成功。测试完成后 fixture 副本已还原。

## 5. 与设计的一致性

- `EventStatus.IsSuccess` 只在 `ok` 时为真，`partial` 及各类失败均不为真 —— 对应设计包"不得把 partial/timeout/access_denied 当空成功"。
- `SchemaConformance.FindUnsupportedKeywords` + 对应测试构成契约漂移护栏：schema 一旦新增 `oneOf`、`pattern` 等未实现关键字，测试立即失败而不是校验悄悄变松。
- Domain 层无第三方包，核心契约读取在离线或 restore 失败时仍可工作。
- CLI 明确打印"合成数据、未进行网络探测"，不暗示真实测量。

## 6. 已知限制与未实现项

1. 只有封套级契约，`payload` 尚无按事件类型的专用 schema（设计包允许，后续任务补）。
2. 无 T02 采集接口、无 T03 Windows 采集、无 T04 探测、无 T05 存储、无 T06 诊断规则 —— 均未开始。
3. `SchemaConformance` 只实现契约当前使用到的关键字，属刻意收窄，不是通用 JSON Schema 引擎。
4. 未做性能测试，未做 UI，未做任何修复能力（M3 才有写入口）。
5. 未初始化 Git 仓库、未建远程、未推送。是否本地 `git init` 留待评审决定。
6. 测试框架版本来自 SDK 模板；如后续需要统一升级，应单独提交变更。

## 7. 边界声明

本轮未运行旧修复脚本，未切换 TUN，未修改网络配置、代理环境变量或客户端设置，未读取或写入任何真实用户网络状态，未调用模型端点，未上传任何资料，未创建 GitHub 仓库。唯一的系统变更经用户明确批准的 .NET 10 SDK 安装。
