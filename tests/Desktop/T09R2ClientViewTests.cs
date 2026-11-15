using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T09-R2 display mapping: client cards and machine-level observations are two collections, and a
/// TUN candidate never appears as a client's own TUN. Synthetic demo data only.
/// </summary>
public sealed class T09R2ClientViewTests
{
    [Fact]
    public async Task Demo_status_splits_into_client_cards_and_machine_observations()
    {
        var provider = new DemoClientProvider();
        var viewModel = new ClientsViewModel(provider, provider);

        await viewModel.SetDemoModeAsync(enabled: true, CancellationToken.None);

        Assert.Contains(viewModel.Rows, row => row.ClientId == "nice");
        Assert.Contains(viewModel.Rows, row => row.ClientId == "apex");
        Assert.DoesNotContain(viewModel.Rows, row => row.ClientId == "system_tun");

        var nice = Assert.Single(viewModel.Rows, row => row.ClientId == "nice");
        Assert.Contains("内核", nice.ProcessesText, StringComparison.Ordinal);
        Assert.Contains("父子", nice.ProcessesText, StringComparison.Ordinal);
        Assert.Contains("内核进程监听", nice.EndpointsText, StringComparison.Ordinal);
        Assert.Equal("归属未确认", nice.TunText);
        Assert.Contains(nice.Evidence, evidence => evidence.NoticesText.Contains("配置提示", StringComparison.Ordinal));

        var system = Assert.Single(viewModel.Observations, row => row.ClientId == "system_tun");
        Assert.Contains("地址分族", system.SummaryText, StringComparison.Ordinal);
        Assert.Contains("IPv6", system.SummaryText, StringComparison.Ordinal);
        Assert.Equal("系统级", system.KindText);
    }

    [Fact]
    public async Task A_client_without_an_endpoint_says_no_actual_listener_instead_of_mixed_port_guess()
    {
        var viewModel = new ClientsViewModel(new DiskPortOnlyProvider(), explainer: null);

        await viewModel.RefreshAsync(CancellationToken.None);

        var row = Assert.Single(viewModel.Rows);
        Assert.Contains("磁盘配置中的端口不当作实际监听", row.EndpointsText, StringComparison.Ordinal);
        Assert.DoesNotContain("7890", row.EndpointsText, StringComparison.Ordinal);
        Assert.Contains(row.Evidence, evidence => evidence.FactsText.Contains("mixed_port=7890", StringComparison.Ordinal));
    }

    private sealed class DiskPortOnlyProvider : IClientStatusProvider
    {
        public Task<IReadOnlyList<ClientStatus>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientStatus>>(new[]
            {
                new ClientStatus
                {
                    ClientId = "nice",
                    DisplayName = "synthetic",
                    Running = true,
                    Endpoints = Array.Empty<ClientEndpoint>(),
                    Evidence = new[]
                    {
                        new ClientConfigEvidence
                        {
                            ClientId = "nice",
                            ParserVersion = "synthetic/1",
                            Provenance = ConfigProvenance.DiskOnly,
                            Valid = true,
                            Facts = new Dictionary<string, string> { ["mixed_port"] = "7890" },
                            Coverage = new Dictionary<string, string> { ["mixed_port"] = ConfigFactCoverage.Present },
                        },
                    },
                },
            });
    }
}
