using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Probes;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T07-R2 settings: the family notice no longer says the app never forces a family, and editing
/// or saving a target profile never sends a request by itself.
/// </summary>
public sealed class T07R2SettingsTests
{
    [Fact]
    public void Family_notice_names_the_forced_path_and_the_two_gap_codes()
    {
        using var temp = new TempAppPaths();
        var viewModel = new SettingsViewModel(new SettingsStore(temp.Paths), new AppSettings(), _ => { });

        viewModel.FamilyIndex = 1;
        Assert.Contains("IPv4", viewModel.FamilyNotice);
        Assert.Contains("address_family_not_enforced_through_proxy", viewModel.FamilyNotice);
        Assert.Contains("address_family_unknown_for_system_default", viewModel.FamilyNotice);
        Assert.Contains("真实 socket", viewModel.FamilyNotice);
        Assert.DoesNotContain("不强制地址族", viewModel.FamilyNotice);

        viewModel.FamilyIndex = 2;
        Assert.Contains("IPv6", viewModel.FamilyNotice);
        Assert.Contains("address_family_not_enforced_through_proxy", viewModel.FamilyNotice);

        viewModel.FamilyIndex = 0;
        Assert.Contains("未指定地址族", viewModel.FamilyNotice);
        Assert.DoesNotContain("不强制地址族", viewModel.FamilyNotice);
    }

    [Fact]
    public async Task Saving_a_target_profile_never_sends_a_probe_or_starts_a_session()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var runner = new StubProbeRunner();
        var session = new ObservationSession(new StubSnapshotCollector(), runner, clock, temp.Paths,
            sessionId: "session-t07r2-settings", bootId: "boot-t07r2");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        try
        {
            var viewModel = new MainViewModel(services, new InlineUiDispatcher());
            viewModel.Settings.TargetId = "target-lab-1";
            viewModel.Settings.TargetEndpoint = "https://example.invalid/health";
            viewModel.Settings.ExpectedStatus = "204";
            viewModel.Settings.FamilyIndex = 1;
            viewModel.Settings.Save();

            Assert.Equal(0, runner.Calls);
            Assert.Equal(0, session.EventCount);
            Assert.Equal(ObservationState.Idle, session.State);

            var saved = services.SettingsStore.Load();
            Assert.Equal("target-lab-1", saved.ProbeTargetId);
            Assert.Equal("https://example.invalid/health", saved.ProbeTargetEndpoint);
            Assert.Equal("ipv4", saved.AddressFamily);
            Assert.Contains("没有发起任何请求", viewModel.Settings.StatusMessage);

            // Re-opening the settings and re-reading the file must not probe either.
            var reopened = new SettingsViewModel(services.SettingsStore, saved, _ => { });
            Assert.Equal("target-lab-1", reopened.TargetId);
            Assert.Equal("https://example.invalid/health", reopened.TargetEndpoint);
            Assert.Equal(0, runner.Calls);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public void An_empty_target_profile_id_is_rejected_without_touching_the_endpoint()
    {
        using var temp = new TempAppPaths();
        var viewModel = new SettingsViewModel(new SettingsStore(temp.Paths), new AppSettings(), _ => { });
        viewModel.TargetId = "  ";
        viewModel.TargetEndpoint = "https://example.invalid/health";

        viewModel.Save();

        Assert.Contains("目标档案 ID 不能为空", viewModel.StatusMessage);
        Assert.Equal(string.Empty, new SettingsStore(temp.Paths).Load().ProbeTargetEndpoint);
    }
}
