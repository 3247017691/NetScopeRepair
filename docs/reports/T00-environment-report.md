# T00 执行报告：环境核对

日期：2026-11-04 11:56 (Asia/Shanghai)
任务：T00 环境与 ADR 核对
结论：**环境不满足 T01 的构建与测试条件**。本报告只记录只读核对结果，未安装任何组件，未修改网络。

## 1. 核对结果

| 项目 | 实测 | 判定 |
|---|---|---|
| 操作系统 | Windows 11 家庭版 中文版，10.0.26300，x64 | 满足设计基线（Windows 11 x64） |
| dotnet 主机 | `C:\Program Files\dotnet\dotnet.exe`，产品版本 10.0.11 | 存在 |
| .NET SDK | `dotnet --list-sdks` 无任何输出；`dotnet --version` 报 `No .NET SDKs were found.` | **缺失，阻塞构建与测试** |
| .NET 运行时 | `Microsoft.NETCore.App 10.0.11`、`Microsoft.WindowsDesktop.App 10.0.11` | 仅运行时，无法编译 |
| SDK 目录 | `C:\Program Files\dotnet\sdk`、`Program Files (x86)\dotnet\sdk`、`%LOCALAPPDATA%\Microsoft\dotnet\sdk`、`%USERPROFILE%\.dotnet\sdk` 均不存在 | 无可用 SDK |
| Visual Studio / MSBuild | vswhere 不存在，`msbuild` 不在 PATH | 无备用编译链 |
| 包管理器 | 有 `winget`；无 `scoop`、`choco` | 可用的安装途径只有 winget 或官方安装包 |
| 本地 SDK 安装包 | Downloads、D:\、D:\App、C:\Temp 未发现 dotnet 安装包 | 需联网获取 |
| 候选源码目录 | `D:\Projects` 不存在，`D:\Projects\NetScopeRepair` 不存在 | 可新建，未覆盖任何已有内容 |
| D: 可用空间 | 18.9 GB | 充足（SDK 约 1 GB 量级） |

## 2. 阻塞的精确含义

`dotnet --version` 的原始输出：

```
* You intended to execute a .NET SDK command:
    No .NET SDKs were found.

Download a .NET SDK:
https://aka.ms/dotnet/download
```

因此 `dotnet build`、`dotnet test`、`dotnet new` 在本机当前**均不可执行**。按施工总指令要求，本轮不静默下载或安装 SDK，也不声称构建通过。

## 3. 最小依赖准备方案（待用户批准后执行）

1. 通过 winget 安装官方 .NET 10 SDK：`winget install --id Microsoft.DotNet.SDK.10 --exact`；若 winget 源不可用，改从 <https://aka.ms/dotnet/download> 获取官方 x64 安装包。
2. 安装后复验：`dotnet --list-sdks` 应至少列出一个 10.0.x 版本，且与 `global.json` 的 `rollForward` 策略相容。
3. 首次 restore 会从 NuGet 官方源下载测试框架等包。该操作只影响工程的 `obj/`、全局包缓存，不改全局源、代理和 TLS 设置；若 restore 失败，如实记录阻塞，不绕过、不空跑。

## 4. 本轮实际完成的范围

已完成（不依赖 SDK 的部分）：

- 新建专用仓库目录 `D:\Projects\NetScopeRepair`，未覆盖任何既有内容。
- 复制只读来源的契约与合成数据：`contracts/event-envelope.schema.json`、`fixtures/synthetic/synthetic-incident.ndjson`（源设计包未被修改）。
- `NetScope.Domain` 契约层：强类型封套、严格反序列化、零 NuGet 依赖的契约校验器。
- `NetScope.Cli` 只读检查器：读取合成 fixture，逐条校验并显式区分成功与非成功状态。
- 工程级 `global.json`、`Directory.Build.props`、`.gitignore`、ADR 0001。

未完成（阻塞于 SDK）：

- solution 文件与测试工程。计划在 SDK 就绪后用 `dotnet new sln` / `dotnet new xunit` 生成，以取得与本机 SDK 匹配的测试框架包版本，避免手写猜测版本号。
- `dotnet build` 与 `dotnet test` 的真实执行结果。

## 5. 边界声明

本轮未运行旧修复脚本，未切换 TUN，未修改任何网络配置、代理环境变量或客户端设置，未调用模型端点，未上传任何资料，未创建 GitHub 仓库。

## 6. 补记：SDK 已安装（2026-11-04 12:08）

T00 报告提交后，用户明确批准安装官方 SDK。执行内容：

```text
winget install --id Microsoft.DotNet.SDK.10 --exact --silent \
  --accept-package-agreements --accept-source-agreements --disable-interactivity
```

结果：安装 **Microsoft .NET SDK 10.0.403**（安装包 `dotnet-sdk-10.0.403-win-x64.exe`，来自 download.microsoft.com，安装器哈希校验通过），退出码 0。复验 `dotnet --list-sdks` 输出 `10.0.403 [C:\Program Files\dotnet\sdk]`。

该操作与本节第 5 条声明的边界不冲突：安装属于用户批准的系统组件变更，与网络配置、代理环境变量和客户端设置无关。第 3 节的阻塞已解除，T01 的构建与测试随后完成。

