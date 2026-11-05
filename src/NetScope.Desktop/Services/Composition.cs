using NetScope.Application;
using NetScope.Probes;
using NetScope.Storage;

namespace NetScope.Desktop.Services;

/// <summary>
/// Everything the shell needs. Optional members stay null until their milestone task is wired;
/// the UI then says so instead of pretending the capability exists.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(
        AppPaths paths,
        SettingsStore settingsStore,
        AppSettings settings,
        IClock clock,
        ObservationSession session,
        HttpProbeTransport transport)
    {
        Paths = paths;
        SettingsStore = settingsStore;
        Settings = settings;
        Clock = clock;
        Session = session;
        Transport = transport;
    }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; set; }

    public IClock Clock { get; }

    public ObservationSession Session { get; }

    public HttpProbeTransport Transport { get; }

    /// <summary>T08. Null until the export implementation is wired.</summary>
    public IIncidentExporter? Exporter { get; set; }

    /// <summary>T09. Null until adapters are wired.</summary>
    public IClientStatusProvider? Clients { get; set; }

    public IDependencyExplainer? Explanations { get; set; }

    public void Dispose()
    {
        Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Transport.Dispose();
    }
}

public static class Composition
{
    public static AppServices CreateDefault(AppPaths? paths = null, bool synthetic = false)
    {
        var resolvedPaths = paths ?? AppPaths.Default;
        resolvedPaths.EnsureCreated();
        var settingsStore = new SettingsStore(resolvedPaths);
        var settings = settingsStore.Load();
        var clock = new SystemClock();
        var transport = new HttpProbeTransport();
        var probes = new HttpProbeRunner(transport, clock);
        ISnapshotCollector collector = synthetic
            ? new NetScope.Application.Mock.SyntheticSnapshotCollector()
            : new NetScope.Collectors.Windows.WindowsSnapshotCollector();
        var session = new ObservationSession(collector, probes, clock, resolvedPaths, synthetic);
        var services = new AppServices(resolvedPaths, settingsStore, settings, clock, session, transport)
        {
            Exporter = new NetScope.Storage.Export.IncidentExporter(),
        };
        if (synthetic)
        {
            var demo = new DemoClientProvider();
            services.Clients = demo;
            services.Explanations = demo;
        }
        else
        {
            var adapters = NetScope.Adapters.AdaptersHost.CreateDefault();
            services.Clients = adapters.ClientStatus;
            services.Explanations = adapters.Dependencies;
        }

        return services;
    }
}
