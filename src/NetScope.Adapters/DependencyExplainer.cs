using NetScope.Application;

namespace NetScope.Adapters;

/// <summary>
/// Explains which proxy layers DSH and Antigravity can read and which remain unknown. It makes no
/// change and performs no request: the target hosts are listed as plain names only. The
/// explanation is deliberately layered, because a variable existing in one layer says nothing about
/// what a request actually used.
/// </summary>
public sealed class DependencyExplainer : IDependencyExplainer
{
    private const string Known = "known";
    private const string Unknown = "unknown";
    private const string Unsupported = "unsupported";

    private const string BoundaryText =
        "M1 只读边界：本解释不修改环境变量、不修改 WinINET/WinHTTP/系统代理、不重启 DSH 或 Antigravity，"
        + "也不向所列目标主机发起任何请求。任何配置变更或修复不属于 M1，M3 及以后才可能在用户明确确认后执行。";

    public Task<IReadOnlyList<DependencyExplanation>> ExplainAsync(
        IReadOnlyList<ClientStatus> clients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clients);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DependencyExplanation> explanations =
        [
            ExplainDsh(clients.Count),
            ExplainAntigravity(clients.Count),
        ];
        return Task.FromResult(explanations);
    }

    private static DependencyExplanation ExplainDsh(int clientCount) => new()
    {
        AppId = "dsh",
        DisplayName = "DeepSeek Harness（DSH）",
        Summary = "DSH 是以 Electron/Chromium 为主、包含 Node 运行时的桌面应用。下面分别列出它在 Windows 上"
            + "可能读取的代理层、本工具能确认与不能确认的部分，以及目标主机名。目标主机仅作为纯名称记录："
            + "api.deepseek.com。M1 不主动请求该主机，也不替用户修改任何一层。"
            + $"本次多客户端状态包含 {clientCount} 项，本解释不据此推断某个请求的实际路由。",
        Layers =
        [
            Layer("tool_process_environment", Unknown,
                "DSH 可以读取自身进程环境；但启动时从父进程继承到哪些 HTTP_PROXY/HTTPS_PROXY/NO_PROXY 值，"
                + "M1 无法从外部证实，因此该层保持未知。"),
            Layer("user_environment", Unsupported,
                "用户级环境变量不会被 Electron 在进程启动后重新读取；只有启动时已继承到进程环境里的副本可能生效。"),
            Layer("machine_environment", Unsupported,
                "机器级环境变量同理，仅通过启动继承影响该进程；不是运行时再读取的配置源。"),
            Layer("wininet", Known,
                "Electron/Chromium 在 Windows 上使用 WinINET 系统代理配置（含 PAC 与自动检测），除非被启动参数覆盖。"
                + "存储值可以只读查看，但“存储值存在”不等于“每个请求都使用了它”。"),
            Layer("winhttp", Unsupported,
                "Electron/Chromium 不读取 WinHTTP 代理存储；修改 WinHTTP 不应被期待影响 DSH。"),
            Layer("start_parameters", Unknown,
                "Chromium 支持 --proxy-server、--proxy-bypass-list 等启动参数；M1 未读取 DSH 的实际命令行，"
                + "因此是否覆盖系统代理保持未知。"),
            Layer("system_proxy", Unknown,
                "系统代理即 WinINET 设置。其存储值可被读取，但是否对所有请求生效取决于 Chromium 网络栈、"
                + "启动参数与每个请求使用的会话，M1 不宣称实际路由。"),
            Layer("target_hosts", Known,
                "api.deepseek.com（仅纯主机名；M1 不发请求）"),
        ],
        Gaps =
        [
            "未读取 DSH 的实际启动命令行，--proxy-server 等覆盖情况未知。",
            "未获取 DSH 进程环境副本，父进程继承链与变量取值未知。",
            "未验证当前 DSH 版本所使用 Chromium 网络栈的版本细节。",
        ],
        Boundary = BoundaryText,
    };

    private static DependencyExplanation ExplainAntigravity(int clientCount) => new()
    {
        AppId = "antigravity",
        DisplayName = "Antigravity（Go language_server）",
        Summary = "Antigravity 的语言服务 language_server 是 Go 程序。Go 标准 HTTP 客户端默认按代理环境变量"
            + "（ProxyFromEnvironment）读取进程中的 HTTP_PROXY/HTTPS_PROXY/NO_PROXY；Go 标准库不读取 WinINET 或"
            + "WinHTTP 系统代理存储。下面列出可读层与未知层。目标主机仅作为纯名称记录：oauth2.googleapis.com、"
            + "generativelanguage.googleapis.com、daily-cloudcode-pa.googleapis.com、play.googleapis.com。"
            + "M1 不主动请求这些主机，也不替用户修改任何一层。"
            + $"本次多客户端状态包含 {clientCount} 项，本解释不据此推断某个请求的实际路由。",
        Layers =
        [
            Layer("tool_process_environment", Known,
                "Go 的 net/http 在未自定义 Transport 时使用 ProxyFromEnvironment，读取进程环境中的"
                + " HTTP_PROXY/HTTPS_PROXY/NO_PROXY；实际取值取决于 language_server 启动时的继承结果。"),
            Layer("user_environment", Unsupported,
                "Go 不直接读取用户级注册表环境；只有启动时已继承到进程环境中的副本可能生效。"),
            Layer("machine_environment", Unsupported,
                "Go 不直接读取机器级注册表环境；同样只有启动继承的副本可能生效。"),
            Layer("wininet", Unsupported,
                "Go 标准库不读取 WinINET 系统代理配置；修改 WinINET 不应被期待影响该语言服务。"),
            Layer("winhttp", Unsupported,
                "Go 标准库不读取 WinHTTP 代理存储；该层不是 language_server 的代理来源。"),
            Layer("start_parameters", Unknown,
                "language_server 的启动参数可能包含代理或端点覆盖；M1 未读取其实际命令行，因此保持未知。"),
            Layer("system_proxy", Unknown,
                "系统代理仅通过进程环境或应用自身设置间接影响 Go 程序；无法仅凭注册表确认该进程实际使用了它。"),
            Layer("target_hosts", Known,
                "oauth2.googleapis.com、generativelanguage.googleapis.com、daily-cloudcode-pa.googleapis.com、"
                + "play.googleapis.com（仅纯主机名；M1 不发请求）"),
        ],
        Gaps =
        [
            "未读取 language_server 的实际启动参数与 Transport 自定义情况。",
            "未确认 Antigravity 是否使用 gRPC/QUIC 或自定义代理设置；这些路径可能不遵循 HTTP_PROXY。",
            "未获取 language_server 进程环境副本，继承关系未知。",
        ],
        Boundary = BoundaryText,
    };

    private static DependencyLayer Layer(string name, string state, string detail) => new()
    {
        Name = name,
        State = state,
        Detail = detail,
    };
}
