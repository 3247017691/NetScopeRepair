using NetScope.Application;

namespace NetScope.Desktop.Services;

/// <summary>
/// Clearly synthetic client state used by the demo/render path. It exists so screenshots and the
/// synthetic walkthrough never read the real machine's client configuration.
/// </summary>
public sealed class DemoClientProvider : IClientStatusProvider, IDependencyExplainer
{
    public Task<IReadOnlyList<ClientStatus>> ReadAsync(CancellationToken cancellationToken)
    {
        var nice = new ClientStatus
        {
            ClientId = "nice-proxy",
            DisplayName = "Nice加速（合成示例）",
            Running = true,
            Version = "synthetic",
            Endpoints = new[] { new ClientEndpoint { Role = "mixed", Host = "127.0.0.1", Port = 7890, Source = "synthetic.runtime" } },
            TunEnabled = true,
            TunAddress = "198.18.0.1/30",
            Evidence = new[]
            {
                new ClientConfigEvidence
                {
                    ClientId = "nice-proxy",
                    ParserVersion = "demo/1.0.0",
                    Provenance = ConfigProvenance.DiskOnly,
                    SourcePath = "synthetic/NiceProxy/runtime.yaml",
                    ReadAt = DateTimeOffset.Now,
                    FileModifiedAt = DateTimeOffset.Now.AddMinutes(-5),
                    ContentHash = "synthetic-hash-nice",
                    Valid = true,
                    Facts = new Dictionary<string, string>
                    {
                        ["mixed_port"] = "7890",
                        ["mode"] = "rule",
                        ["tun_enabled"] = "true",
                        ["fake_ip_range"] = "198.18.0.1/16",
                    },
                },
            },
            Notes = new[] { "合成演示：不代表真实运行状态。" },
        };

        var apex = new ClientStatus
        {
            ClientId = "apex",
            DisplayName = "农夫山泉-极速版（合成示例）",
            Running = false,
            Version = "synthetic",
            Endpoints = new[] { new ClientEndpoint { Role = "mixed", Host = "127.0.0.1", Port = 7892, Source = "synthetic.listener" } },
            TunEnabled = null,
            Evidence = new[]
            {
                new ClientConfigEvidence
                {
                    ClientId = "apex",
                    ParserVersion = "demo/1.0.0",
                    Provenance = ConfigProvenance.RuntimeApi,
                    SourcePath = null,
                    ReadAt = DateTimeOffset.Now,
                    ContentHash = "synthetic-hash-apex",
                    Valid = true,
                    Facts = new Dictionary<string, string>
                    {
                        ["mixed_port"] = "7892",
                        ["mode"] = "rule",
                        ["tun_enabled"] = "false",
                    },
                },
            },
            Gaps = new[] { "进程未运行；配置来自合成运行 API，不能当作当前状态。" },
            Notes = new[] { "合成演示：两个客户端同时存在只表示并存，不是冲突结论。" },
        };

        return Task.FromResult<IReadOnlyList<ClientStatus>>(new[] { nice, apex });
    }

    public Task<IReadOnlyList<DependencyExplanation>> ExplainAsync(
        IReadOnlyList<ClientStatus> clients, CancellationToken cancellationToken)
    {
        var explanations = new[]
        {
            new DependencyExplanation
            {
                AppId = "dsh",
                DisplayName = "DeepSeek Harness（合成示例）",
                Summary = "可能读取工具进程环境变量、用户环境变量与系统代理；浏览器 PAC 与 WinHTTP 不自动继承。",
                Layers = new[]
                {
                    new DependencyLayer { Name = "工具进程环境", State = "known", Detail = "合成示例中存在 HTTP(S)_PROXY。" },
                    new DependencyLayer { Name = "用户环境变量", State = "known", Detail = "合成示例中存在代理变量。" },
                    new DependencyLayer { Name = "系统 WinINET", State = "unknown", Detail = "进程是否读取系统代理未知。" },
                },
                Gaps = new[] { "实际进程继承无法由本机只读配置证明。" },
                Boundary = "M1 只解释依赖，不修改环境变量、不重启应用、不代发请求。",
            },
            new DependencyExplanation
            {
                AppId = "antigravity",
                DisplayName = "Antigravity（合成示例）",
                Summary = "Go 语言服务通常只读取环境变量代理，不读取 Windows 系统代理。",
                Layers = new[]
                {
                    new DependencyLayer { Name = "用户环境变量", State = "known", Detail = "合成示例中存在代理变量。" },
                    new DependencyLayer { Name = "系统 WinINET", State = "unsupported", Detail = "该栈不读取系统代理，不能据此判断。" },
                },
                Gaps = new[] { "未观测到真实进程网络行为。" },
                Boundary = "M1 不修改代理配置，也不验证付费接口可用性。",
            },
        };
        return Task.FromResult<IReadOnlyList<DependencyExplanation>>(explanations);
    }
}
