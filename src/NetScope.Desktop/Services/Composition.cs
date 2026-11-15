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
        HttpProbeTransport transport,
        IRepairGateway? repair = null)
    {
        Paths = paths;
        SettingsStore = settingsStore;
        Settings = settings;
        Clock = clock;
        Session = session;
        Transport = transport;
        Repair = repair ?? new UnavailableRepairGateway();
    }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; set; }

    public IClock Clock { get; }

    public ObservationSession Session { get; }

    public HttpProbeTransport Transport { get; }

    /// <summary>T08. Null until the export implementation is wired.</summary>
    public IIncidentExporter? Exporter { get; set; }

    /// <summary>T16 repair gateway; the delivery default is unavailable and never writes.</summary>
    public IRepairGateway Repair { get; }

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

        // One evidence source is shared by every observation entry of this process. It reads the
        // same adapter seams the clients page uses, but in its own cycle: a page refresh is never
        // spliced into another cycle's evidence. Synthetic sessions stay a deliberate reduced
        // composition; the demo session itself is produced by SyntheticShowcase.
        NetScope.Adapters.AdapterComposition? adapters = synthetic
            ? null
            : NetScope.Adapters.AdaptersHost.CreateDefault();
        var session = new ObservationSession(collector, probes, clock, resolvedPaths, synthetic,
            evidenceSource: adapters?.Evidence);
        IRepairGateway repair = synthetic
            ? new PreviewOnlyRepairGateway(new SyntheticRepairPreviewSource(() => DateTimeOffset.UtcNow), new RepairGateView
            {
                Enabled = false,
                Text = "执行入口已禁用（Disabled）",
                Reasons = UnavailableRepairGateway.DeliveryReasons
                    .Concat(new[] { "合成演示计划：不是本机观测，也不是可执行计划。" })
                    .ToArray(),
            })
            : new UnavailableRepairGateway();
        var services = new AppServices(resolvedPaths, settingsStore, settings, clock, session, transport, repair)
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
            services.Clients = adapters!.ClientStatus;
            services.Explanations = adapters.Dependencies;
        }

        return services;
    }
}

