using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

internal sealed class TempAppPaths : IDisposable
{
    public TempAppPaths()
    {
        Root = Path.Combine(Path.GetTempPath(), "netscope-desktop-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the test result.
        }
    }
}

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));

    public double MonotonicMs { get; set; } = 1000;

    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
        MonotonicMs += delta.TotalMilliseconds;
    }
}

internal sealed class StubSnapshotCollector : ISnapshotCollector
{
    public StubSnapshotCollector(EventStatus interfaces = EventStatus.Ok) => InterfacesStatus = interfaces;

    public EventStatus InterfacesStatus { get; set; }

    public string Source => "test.snapshot";

    public string SourceVersion => "1.0";

    public Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        var interfaces = InterfacesStatus == EventStatus.Ok
            ? Collected<IReadOnlyList<InterfaceState>>.Ok(Array.Empty<InterfaceState>())
            : Collected<IReadOnlyList<InterfaceState>>.Unavailable(InterfacesStatus, "synthetic interface gap");
        var snapshot = new NetworkSnapshot
        {
            SnapshotId = context.SnapshotId,
            ObservedAt = context.ObservedAt,
            MonotonicMs = context.MonotonicMs,
            Synthetic = context.Synthetic,
            Interfaces = interfaces,
            Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(Array.Empty<RouteEntry>()),
            Dns = Collected<DnsState>.Ok(new DnsState()),
            Proxies = Collected<ProxyState>.Ok(new ProxyState
            {
                ToolProcessEnvironment = Layer("tool_process_environment"),
                UserEnvironment = Layer("user_environment"),
                MachineEnvironment = Layer("machine_environment"),
                WinInet = Layer("wininet"),
                WinHttp = Layer("winhttp"),
            }),
        };
        return Task.FromResult(snapshot);
    }

    private static Collected<ProxyLayer> Layer(string name) =>
        Collected<ProxyLayer>.Ok(new ProxyLayer { Layer = name, Provenance = "test" });
}

internal sealed class FlakySnapshotCollector : ISnapshotCollector
{
    private readonly StubSnapshotCollector _inner = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public string Source => "test.flaky";

    public string SourceVersion => "1.0";

    public Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
            throw new InvalidOperationException("synthetic first-cycle failure");
        return _inner.CollectAsync(context, cancellationToken);
    }
}

internal sealed class StubProbeRunner : IProbeRunner
{
    public StubProbeRunner(EventStatus status = EventStatus.Ok) => Status = status;

    public EventStatus Status { get; }

    public int Calls { get; private set; }

    public Task<ProbeGroupResult> RunAsync(ProbeGroupPlan plan, CancellationToken cancellationToken)
    {
        Calls++;
        var results = plan.Paths.Select((path, index) => new ProbeResult
        {
            ProbeId = $"{plan.ProbeGroupId}-probe-{index + 1}",
            ProbeGroupId = plan.ProbeGroupId,
            TargetId = plan.Target.TargetId,
            Path = path,
            Status = Status,
            StartedAt = plan.StartedAt.AddMilliseconds(index),
            StartedMonotonicMs = 2000 + index,
            DurationMs = 5,
            HttpStatus = Status == EventStatus.Ok ? 204 : null,
            Classification = Status == EventStatus.Ok
                ? ProbeClassification.ReachableExpected
                : ProbeClassification.TransportFailure,
            FailedStage = Status == EventStatus.Ok ? null : "connect",
            FailedEndpoint = Status == EventStatus.Ok ? null : "unknown",
            AddressFamily = plan.AddressFamily,
            Method = plan.Target.Method,
            RequestOptionsKey = "test-options",
            ObservationWindowId = plan.ObservationWindowId,
            BootId = plan.BootId,
            ProxyEndpointId = path == ProbePath.ExplicitHttpProxy ? plan.ProxyEndpointId : null,
            ProxyIsLocal = path == ProbePath.ExplicitHttpProxy,
            TransportError = Status == EventStatus.Ok ? null : "connection_refused",
        }).ToArray();
        return Task.FromResult(new ProbeGroupResult
        {
            ProbeGroupId = plan.ProbeGroupId,
            StartedAt = plan.StartedAt,
            DurationMs = 20,
            Results = results,
            TimingComparable = false,
        });
    }
}
