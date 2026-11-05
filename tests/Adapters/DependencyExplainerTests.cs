using NetScope.Adapters;
using NetScope.Application;

namespace NetScope.Adapters.Tests;

public sealed class DependencyExplainerTests
{
    private static readonly string[] AllowedStates = ["known", "unknown", "unsupported"];

    [Fact]
    public async Task Explains_both_applications_with_a_read_only_boundary()
    {
        var explanations = await new DependencyExplainer().ExplainAsync(Array.Empty<ClientStatus>(), CancellationToken.None);

        Assert.Equal(2, explanations.Count);
        Assert.Equal(["dsh", "antigravity"], explanations.Select(item => item.AppId));
        Assert.All(explanations, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(item.Summary));
            Assert.Contains("只读", item.Boundary, StringComparison.Ordinal);
            Assert.Contains("不修改环境变量", item.Boundary, StringComparison.Ordinal);
            Assert.Contains("不重启", item.Boundary, StringComparison.Ordinal);
            Assert.Contains("不向所列目标主机发起任何请求", item.Boundary, StringComparison.Ordinal);
            Assert.NotEmpty(item.Layers);
            Assert.NotEmpty(item.Gaps);
            Assert.All(item.Layers, layer => Assert.Contains(layer.State, AllowedStates));
        });
    }

    [Fact]
    public async Task Dsh_explains_every_layer_and_its_target_host_as_a_plain_name()
    {
        var explanation = Assert.Single(
            await new DependencyExplainer().ExplainAsync(Array.Empty<ClientStatus>(), CancellationToken.None),
            item => item.AppId == "dsh");

        Assert.Equal(
            new[]
            {
                "tool_process_environment",
                "user_environment",
                "machine_environment",
                "wininet",
                "winhttp",
                "start_parameters",
                "system_proxy",
                "target_hosts",
            },
            explanation.Layers.Select(layer => layer.Name));
        Assert.Contains(explanation.Layers, layer => layer.Name == "wininet" && layer.State == "known");
        Assert.Contains(explanation.Layers, layer => layer.Name == "tool_process_environment" && layer.State == "unknown");
        Assert.Contains(explanation.Layers, layer => layer.Name == "start_parameters" && layer.State == "unknown");
        Assert.Contains("api.deepseek.com", explanation.Summary, StringComparison.Ordinal);
        var hosts = Assert.Single(explanation.Layers, layer => layer.Name == "target_hosts");
        Assert.Contains("api.deepseek.com", hosts.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Antigravity_explains_go_environment_layers_and_google_hosts()
    {
        var explanation = Assert.Single(
            await new DependencyExplainer().ExplainAsync(Array.Empty<ClientStatus>(), CancellationToken.None),
            item => item.AppId == "antigravity");

        Assert.Contains(
            explanation.Layers,
            layer => layer.Name == "tool_process_environment" && layer.State == "known");
        Assert.Contains(explanation.Layers, layer => layer.Name == "wininet" && layer.State == "unsupported");
        Assert.Contains(explanation.Layers, layer => layer.Name == "winhttp" && layer.State == "unsupported");

        string[] hosts =
        [
            "oauth2.googleapis.com",
            "generativelanguage.googleapis.com",
            "daily-cloudcode-pa.googleapis.com",
            "play.googleapis.com",
        ];
        foreach (var host in hosts)
        {
            Assert.Contains(host, explanation.Summary, StringComparison.Ordinal);
            Assert.Contains(
                explanation.Layers,
                layer => layer.Name == "target_hosts" && layer.Detail.Contains(host, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Unknown_inheritance_is_visible_instead_of_being_assumed()
    {
        var explanations = await new DependencyExplainer().ExplainAsync(Array.Empty<ClientStatus>(), CancellationToken.None);

        foreach (var explanation in explanations)
        {
            Assert.Contains(explanation.Layers, layer => layer.State == "unknown");
            Assert.Contains(explanation.Gaps, gap => gap.Contains("未知", StringComparison.Ordinal)
                || gap.Contains("未读取", StringComparison.Ordinal)
                || gap.Contains("未确认", StringComparison.Ordinal)
                || gap.Contains("未获取", StringComparison.Ordinal));
            Assert.DoesNotContain(explanation.Layers, layer => layer.State == "known" && layer.Name == "system_proxy");
        }
    }

    [Fact]
    public async Task Hosts_are_plain_names_and_no_url_is_ever_composed()
    {
        var explanations = await new DependencyExplainer().ExplainAsync(Array.Empty<ClientStatus>(), CancellationToken.None);
        var text = string.Join(
            "\n",
            explanations.SelectMany(item => item.Layers.Select(layer => layer.Name + "|" + layer.Detail + "|" + layer.State)
                .Concat([item.Summary, item.Boundary])));

        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.Contains("api.deepseek.com", text, StringComparison.Ordinal);
        Assert.Contains("generativelanguage.googleapis.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observations_do_not_change_the_explanation_or_imply_a_diagnosis()
    {
        ClientStatus[] clients =
        [
            new()
            {
                ClientId = KnownClientIds.Nice,
                DisplayName = "Nice加速",
                Running = true,
            },
        ];

        var explanations = await new DependencyExplainer().ExplainAsync(clients, CancellationToken.None);

        Assert.Equal(2, explanations.Count);
        Assert.All(explanations, item => Assert.DoesNotContain("根因", item.Summary, StringComparison.Ordinal));
        Assert.All(explanations, item => Assert.Contains("不据此推断某个请求的实际路由", item.Summary, StringComparison.Ordinal));
    }
}
