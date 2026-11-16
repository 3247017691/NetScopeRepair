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

    [Fact]
    public void Cross_tier_selection_is_mutually_exclusive()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-s1", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        // 1. Default: Primary (repair) selected → both quiet tiers report null.
        Assert.Equal(NavGroup.Primary, vm.SelectedSection.Group);
        Assert.Null(vm.SecondarySelected);
        Assert.Null(vm.FooterSelected);

        // 2. Footer page via SelectedSection.
        var settings = vm.FooterSections.First();
        vm.SelectedSection = settings;
        Assert.Same(settings, vm.FooterSelected);
        Assert.Null(vm.SecondarySelected);

        // 3. Secondary page via SelectedSection (mirror).
        var diagnoses = vm.SecondarySections.First();
        vm.SelectedSection = diagnoses;
        Assert.Same(diagnoses, vm.SecondarySelected);
        Assert.Null(vm.FooterSelected);

        // 4. Drive back through the tier setters.
        var help = vm.FooterSections.Last();
        vm.FooterSelected = help;
        Assert.Same(help, vm.SelectedSection);
        var export = vm.SecondarySections.First(s => s.Title == "导出");
        vm.SecondarySelected = export;
        Assert.Same(export, vm.SelectedSection);

        // 5. Cross-tier clear: a secondary selection clears the footer tier.
        vm.FooterSelected = help;
        Assert.Same(help, vm.FooterSelected);
        vm.SecondarySelected = vm.SecondarySections.First(s => s.Title == "时间线");
        Assert.Null(vm.FooterSelected);

        // 6. SelectedSection change notifies both tier properties.
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        vm.SelectedSection = settings;
        Assert.Contains(nameof(MainViewModel.SecondarySelected), raised);
        Assert.Contains(nameof(MainViewModel.FooterSelected), raised);
    }

    [Fact]
    public void Secondary_and_footer_order_is_pinned()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-s2", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        Assert.Equal(new[] { "诊断与证据", "时间线", "多客户端", "导出", "案例回放" },
            vm.SecondarySections.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "设置", "帮助" }, vm.FooterSections.Select(s => s.Title).ToArray());
    }

    [Fact]
    public void Timeline_refresh_command_is_wired()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            clock, temp.Paths, sessionId: "session-nav-tl", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        // The 「重新读取时间线」 button binds to TimelineViewModel.RefreshCommand (its DataContext). It must
        // be non-null and executable even in the render harness, which has no ShellView ancestor to walk.
        Assert.NotNull(vm.Timeline.RefreshCommand);
        Assert.True(vm.Timeline.RefreshCommand.CanExecute(null));
    }
}
