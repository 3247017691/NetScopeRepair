using System.Net;
using NetScope.Application;
using NetScope.Collectors.Windows;
using NetScope.Domain;

namespace NetScope.Windows.Tests;

/// <summary>
/// Aggregation behaviour, exercised with fake sources so the assertions do not depend on how
/// this particular machine is configured.
/// </summary>
public sealed class CollectorAggregationTests
{
    private static CollectionContext Context(bool synthetic = false) => new()
    {
        SessionId = "session",
        BootId = "boot",
        SnapshotId = "snapshot-1",
        ObservedAt = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8)),
        MonotonicMs = 42,
        Synthetic = synthetic,
    };

    private static InterfaceState Interface() => new()
    {
        InterfaceGuid = "guid-1",
        Name = "Ethernet",
        IfIndex = 12,
        Kind = "ethernet",
        OperationalStatus = "up",
    };

    private static WindowsSnapshotCollector Collector(
        Func<CancellationToken, Task<Collected<IReadOnlyList<InterfaceState>>>>? interfaces = null,
        Func<CancellationToken, Task<Collected<IReadOnlyList<RouteEntry>>>>? routes = null) => new(
        new FakeInterfaceSource(interfaces),
        new FakeRouteSource(routes),
        new FakeDnsSource(),
        new FakeProxySource());

    [Fact]
    public async Task Snapshot_carries_the_context_identity()
    {
        var snapshot = await Collector().CollectAsync(Context(), CancellationToken.None);

        Assert.Equal("snapshot-1", snapshot.SnapshotId);
        Assert.Equal(42, snapshot.MonotonicMs);
        Assert.False(snapshot.Synthetic);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8)), snapshot.ObservedAt);
    }

    [Fact]
    public async Task A_failing_domain_becomes_a_gap_and_the_other_domains_survive()
    {
        var collector = Collector(routes: _ => throw new InvalidOperationException("routing table unavailable"));

        var snapshot = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal(EventStatus.Ok, snapshot.Interfaces.Status);
        Assert.Single(snapshot.Interfaces.Value!);

        Assert.Equal(EventStatus.Failed, snapshot.Routes.Status);
        Assert.False(snapshot.Routes.HasValue);
        Assert.Null(snapshot.Routes.Value);
        Assert.NotNull(snapshot.Routes.Reason);
    }

    [Fact]
    public async Task An_unavailable_domain_is_never_reported_as_an_empty_list()
    {
        var collector = Collector(interfaces: _ => Task.FromResult(
            Collected<IReadOnlyList<InterfaceState>>.Unavailable(EventStatus.AccessDenied, "denied")));

        var snapshot = await collector.CollectAsync(Context(), CancellationToken.None);
        var completeness = snapshot.Completeness();

        Assert.Equal(EventStatus.AccessDenied, completeness["interfaces"]);
        Assert.False(snapshot.Interfaces.Status.IsSuccess());
        Assert.NotEqual(EventStatus.Ok, completeness["interfaces"]);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_into_a_gap()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var collector = Collector(interfaces: token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Collected<IReadOnlyList<InterfaceState>>.Ok(Array.Empty<InterfaceState>()));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(Context(), source.Token));
    }

    [Theory]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "ethernet")]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet, "ethernet")]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "wifi")]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Tunnel, "tunnel")]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Loopback, "loopback")]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ppp, "ppp")]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Unknown, "unknown")]
    public void Interface_kind_uses_a_small_stable_vocabulary(
        System.Net.NetworkInformation.NetworkInterfaceType type,
        string expected)
    {
        Assert.Equal(expected, WindowsInterfaceSource.KindOf(type));
    }

    [Fact]
    public void Route_total_metric_adds_route_and_interface_metric()
    {
        var route = new RouteEntry
        {
            DestinationPrefix = "0.0.0.0/0",
            Family = "ipv4",
            IfIndex = 12,
            RouteMetric = 5,
            InterfaceMetric = 25,
            Protocol = "netmgmt",
        };

        Assert.Equal(30L, route.TotalMetric);
    }

    [Fact]
    public void Unknown_route_interface_metric_keeps_the_total_unknown()
    {
        var route = new RouteEntry
        {
            DestinationPrefix = "0.0.0.0/0",
            Family = "ipv4",
            IfIndex = 12,
            RouteMetric = 5,
            InterfaceMetric = null,
            Protocol = "netmgmt",
        };

        Assert.Null(route.TotalMetric);
        Assert.Equal(5L, (route with { InterfaceMetric = 0 }).TotalMetric);
        Assert.Equal(4294967294L, (route with { RouteMetric = int.MaxValue, InterfaceMetric = int.MaxValue }).TotalMetric);
    }

    [Fact]
    public async Task Source_internal_gaps_remain_partial_in_snapshot_completeness()
    {
        var collector = Collector(routes: _ => Task.FromResult(
            Collected<IReadOnlyList<RouteEntry>>.Partial(new[]
            {
                new RouteEntry
                {
                    DestinationPrefix = "0.0.0.0/0", Family = "ipv4", IfIndex = 12,
                    RouteMetric = 5, Protocol = "netmgmt", Reason = "metric unreadable",
                },
            }, "metric unreadable", new[] { "interface 12 metric unreadable" })));

        var snapshot = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal(EventStatus.Partial, snapshot.Completeness()["routes"]);
        Assert.Null(Assert.Single(snapshot.Routes.Value!).TotalMetric);
        Assert.Contains("interface 12 metric unreadable", snapshot.Routes.Errors);
    }

    private sealed class FakeInterfaceSource : IInterfaceSource
    {
        private readonly Func<CancellationToken, Task<Collected<IReadOnlyList<InterfaceState>>>> _read;

        public FakeInterfaceSource(Func<CancellationToken, Task<Collected<IReadOnlyList<InterfaceState>>>>? read = null)
        {
            _read = read ?? (_ => Task.FromResult(Collected<IReadOnlyList<InterfaceState>>.Ok(new[] { Interface() })));
        }

        public Task<Collected<IReadOnlyList<InterfaceState>>> ReadAsync(CancellationToken cancellationToken) =>
            _read(cancellationToken);
    }

    private sealed class FakeRouteSource : IRouteSource
    {
        private readonly Func<CancellationToken, Task<Collected<IReadOnlyList<RouteEntry>>>> _read;

        public FakeRouteSource(Func<CancellationToken, Task<Collected<IReadOnlyList<RouteEntry>>>>? read = null)
        {
            _read = read ?? (_ => Task.FromResult(Collected<IReadOnlyList<RouteEntry>>.Ok(new[]
            {
                new RouteEntry
                {
                    DestinationPrefix = "0.0.0.0/0",
                    Family = "ipv4",
                    IfIndex = 12,
                    RouteMetric = 0,
                    InterfaceMetric = 25,
                    Protocol = "netmgmt",
                },
            })));
        }

        public Task<Collected<IReadOnlyList<RouteEntry>>> ReadAsync(CancellationToken cancellationToken) =>
            _read(cancellationToken);
    }

    private sealed class FakeDnsSource : IDnsSource
    {
        public Task<Collected<DnsState>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Collected<DnsState>.Ok(new DnsState()));
    }

    private sealed class FakeProxySource : IProxySource
    {
        public Task<Collected<ProxyState>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Collected<ProxyState>.Ok(new ProxyState
            {
                ToolProcessEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read in this test"),
                UserEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read in this test"),
                MachineEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read in this test"),
                WinInet = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read in this test"),
                WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read in this test"),
            }));
    }
}
