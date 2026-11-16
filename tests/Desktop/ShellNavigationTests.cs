using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Probes;

namespace NetScope.Desktop.Tests;

public sealed class ShellNavigationTests
{
    [Fact]
    public void Repair_is_first_and_default()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-1", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        Assert.Equal(8, vm.Sections.Count);
        Assert.Same(vm.QuickRepair, vm.Sections[0].ViewModel);
        Assert.Same(vm.QuickRepair, vm.SelectedSection.ViewModel);
        Assert.Equal(NavGroup.Primary, vm.Sections[0].Group);
    }

    [Fact]
    public void Secondary_and_footer_groups_are_quiet()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-2", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        Assert.Equal(5, vm.SecondarySections.Count());
        Assert.Equal(2, vm.FooterSections.Count());
        Assert.DoesNotContain(vm.Sections, s => s.Title == "首页");
    }
}
