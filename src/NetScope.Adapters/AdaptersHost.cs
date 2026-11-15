using NetScope.Application;

namespace NetScope.Adapters;

/// <summary>The read-only ports the desktop composition consumes from this assembly.</summary>
public sealed record AdapterComposition(
    IClientStatusProvider ClientStatus,
    IDependencyExplainer Dependencies,
    IObservationEvidenceSource Evidence);

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

        var adapter = new GenericAdapter(
            new SystemProcessSource(),
            new SystemListenerSource(),
            new SystemNetworkAdapterSource(),
            new SystemProxyLayerSource(),
            configAdapters);

        return new AdapterComposition(adapter, new DependencyExplainer(), new AdapterObservationEvidenceSource(adapter));
    }
}
