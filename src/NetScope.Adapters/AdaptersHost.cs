using NetScope.Application;

namespace NetScope.Adapters;

/// <summary>The two read-only ports the desktop composition consumes from this assembly.</summary>
public sealed record AdapterComposition(IClientStatusProvider ClientStatus, IDependencyExplainer Dependencies);

/// <summary>Default composition with the system seams and the two supported configuration adapters.</summary>
public static class AdaptersHost
{
    public static AdapterComposition CreateDefault()
    {
        var files = new SystemConfigFileSource();
        IClientConfigAdapter[] configAdapters =
        [
            new NiceAdapter(files),
            new ApexAdapter(files),
        ];

        var status = new GenericAdapter(
            new SystemProcessSource(),
            new SystemListenerSource(),
            new SystemNetworkAdapterSource(),
            new SystemProxyLayerSource(),
            configAdapters);

        return new AdapterComposition(status, new DependencyExplainer());
    }
}
