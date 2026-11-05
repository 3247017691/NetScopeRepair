# ADR 0001：仓库位置、零依赖核心与延后生成的工程文件

- 状态：已接受
- 日期：2026-11-04
- 关联任务：T00、T01

## 背景

T00 核对发现本机只有 .NET 10 运行时（`Microsoft.NETCore.App 10.0.11`、`Microsoft.WindowsDesktop.App 10.0.11`），没有任何 SDK，`dotnet build`、`dotnet test`、`dotnet new` 都不可用。设计包要求 T01 提交真实构建与测试结果，且不得静默安装 SDK。

## 决策

1. **仓库位置**：使用 `D:\Projects\NetScopeRepair`（设计包的安全默认候选）。目录此前不存在，未覆盖任何内容；未在课程资料目录、DSH 安装目录或桌面脚本上写应用代码。

2. **核心层零 NuGet 依赖**：`NetScope.Domain` 只使用 BCL（`System.Text.Json`）。契约校验由 `SchemaConformance` 实现，支持 `const`/`enum`/`type`/`required`/`properties`/`additionalProperties(false)`/`minLength`/`maxLength`/`minimum`/`format(date-time)`。理由：核心契约读取必须能在离线、restore 失败或被墙的环境下工作，这也符合"断网仍可用"的产品约束。

3. **契约漂移防护**：`SchemaConformance.FindUnsupportedKeywords` 会报出校验器未实现的关键字。测试固定断言该集合为空，因此一旦有人给 schema 加上 `oneOf`、`pattern` 等未实现关键字，测试立即失败，而不是让校验悄悄变松。

4. **延后生成 solution 与测试工程**：不在没有 SDK 的情况下手写 `.sln` 和带包版本号的测试 `csproj`。SDK 就绪后用 `dotnet new` 模板生成，采用与本机 SDK 匹配的 xunit / Microsoft.NET.Test.Sdk 版本，避免猜测版本导致 restore 失败或隐式降级。

5. **不静默安装 SDK**：SDK 属于系统级组件，安装需用户批准。未获批准前只交付不依赖 SDK 的成果，并将构建与测试标记为未验证。

## 后果

- 正面：仓库现在就能评审契约设计与 CLI 行为；核心层不会被 NuGet 网络问题卡住。
- 负面：T01 在 SDK 就绪前无法标记完成，`EventStatus.IsSuccess` 等行为目前只有代码、没有通过的测试证明。
- 缓解：SDK 安装后补 solution、测试工程，并一次性跑通 build + test 再提交 T01 完成报告。

## 后续待决

- 测试框架选型（xunit v2 或 v3）以本机 SDK 模板产出为准，不预先锁定。
- 是否需要 `NetScopeRepair.slnx` 或经典 `.sln`：以 `dotnet new sln` 生成结果为准。
