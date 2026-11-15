using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

/// <summary>
/// The real shell composition entry (synthetic mode) with the repair page wired in. It proves the
/// page is reachable through the existing navigation, that the composition gateway is preview-only
/// (execution disabled), and that shell exit finalizes the repair state without a privileged call.
/// </summary>
public sealed class T16CompositionTests
{
    [Fact]
    public async Task Synthetic_composition_exposes_a_preview_only_repair_gateway_and_the_shell_page()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        Assert.IsType<PreviewOnlyRepairGateway>(services.Repair);
        Assert.Contains(vm.Sections, section => section.Title == "修复");
        Assert.Same(vm.Repair, vm.Sections.Single(section => section.Title == "修复").ViewModel);

        await vm.Repair.PreviewAsync();

        Assert.True(vm.Repair.HasPreview);
        Assert.True(vm.Repair.Preview!.Synthetic);
        Assert.Equal(2, vm.Repair.Actions.Count);
        Assert.All(vm.Repair.Actions, row => Assert.False(row.IsSelected));
        Assert.False(vm.Repair.CanExecute);
        Assert.Contains("Disabled", vm.Repair.GateText, StringComparison.Ordinal);
        Assert.Single(vm.Repair.NoOpFields);
    }

    [Fact]
    public async Task Shell_exit_finalizes_the_repair_state_without_a_privileged_call()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var vm = new MainViewModel(services, new InlineUiDispatcher());

        await vm.ExitAsync();

        Assert.Contains("没有修复事务记录", vm.Repair.ExitTransactionText, StringComparison.Ordinal);
        Assert.Equal("0 / 3 轮", vm.Repair.BusinessView.RoundsText);
        Assert.Empty(vm.Repair.Layers);
    }

    [Fact]
    public void The_delivery_default_repair_gateway_never_executes_or_scans_a_journal()
    {
        IRepairGateway gateway = new UnavailableRepairGateway();
        var vm = new RepairViewModel(gateway, new FakeClock());

        Assert.False(gateway.Gate.Enabled);
        Assert.Null(gateway.CurrentTransaction);
        Assert.Contains("Disabled", vm.GateText, StringComparison.Ordinal);
    }
}

