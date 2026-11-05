using NetScope.Adapters;
using NetScope.Application;

namespace NetScope.Adapters.Tests;

public sealed class AdapterPathTests
{
    [Fact]
    public void Default_config_paths_are_derived_from_appdata_without_hardcoding_a_user_name()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var nice = NiceAdapter.DefaultConfigPath();
        var apex = ApexAdapter.DefaultConfigPath();

        Assert.EndsWith(Path.Combine("com.core.NiceProxy", "runtime.yaml"), nice, StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine("com.nfsqapp.clients.apex", "农夫山泉-极速版", "shared_preferences.json"),
            apex,
            StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            Assert.StartsWith(appData, nice, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(appData, apex, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Host_composition_exposes_the_two_read_only_ports()
    {
        var composition = AdaptersHost.CreateDefault();

        Assert.IsAssignableFrom<IClientStatusProvider>(composition.ClientStatus);
        Assert.IsAssignableFrom<IDependencyExplainer>(composition.Dependencies);
        Assert.IsType<GenericAdapter>(composition.ClientStatus);
        Assert.IsType<DependencyExplainer>(composition.Dependencies);
    }
}
