using System.Net.NetworkInformation;
using NetScope.Application;
using NetScope.Collectors.Windows;
using NetScope.Domain;

namespace NetScope.Windows.Tests;

/// <summary>Fault injection stays inside the read seams and never touches machine configuration.</summary>
public sealed class PartialSourceTests
{
    [Fact]
    public async Task Failed_route_metrics_remain_unknown_with_one_gap_per_interface_and_family()
    {
        var native = new FakeNative
        {
            Routes = Collected<IReadOnlyList<NativeNetworkApi.RouteRow>>.Ok([
                Route("0.0.0.0/0", NativeNetworkApi.AfInet),
                Route("10.0.0.0/8", NativeNetworkApi.AfInet),
                Route("::/0", NativeNetworkApi.AfInet6),
            ]),
            Metrics = (_, family) => family == NativeNetworkApi.AfInet
                ? Collected<NativeNetworkApi.InterfaceMetrics>.Unavailable(EventStatus.AccessDenied, "injected metric access denial")
                : KnownMetric(40),
        };

        var result = await new WindowsRouteSource(native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        Assert.True(result.HasValue);
        Assert.Equal(3, result.Value!.Count);
        Assert.All(result.Value.Where(route => route.Family == "ipv4"), route =>
        {
            Assert.Null(route.InterfaceMetric);
            Assert.Null(route.TotalMetric);
            Assert.Contains("injected metric access denial", route.Reason);
        });
        var ipv6 = Assert.Single(result.Value, route => route.Family == "ipv6");
        Assert.Equal(40, ipv6.InterfaceMetric);
        Assert.Equal(45L, ipv6.TotalMetric);
        Assert.Null(ipv6.Reason);
        Assert.Single(result.Errors);
        Assert.Equal(1, native.MetricCalls[(12, NativeNetworkApi.AfInet)]);
        Assert.Equal(1, native.MetricCalls[(12, NativeNetworkApi.AfInet6)]);
    }

    [Fact]
    public async Task A_readable_zero_metric_is_preserved_as_zero_and_does_not_create_a_gap()
    {
        var native = new FakeNative { Metrics = (_, _) => KnownMetric(0) };

        var result = await new WindowsRouteSource(native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Ok, result.Status);
        var route = Assert.Single(result.Value!);
        Assert.Equal(0, route.InterfaceMetric);
        Assert.Equal(5L, route.TotalMetric);
        Assert.Null(route.Reason);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Native_route_failure_preserves_unsupported_instead_of_claiming_an_empty_table()
    {
        var native = new FakeNative
        {
            Routes = Collected<IReadOnlyList<NativeNetworkApi.RouteRow>>.Unavailable(
                EventStatus.Unsupported, "unverified architecture"),
        };

        var result = await new WindowsRouteSource(native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Unsupported, result.Status);
        Assert.Null(result.Value);
        Assert.False(result.HasValue);
        Assert.Equal("unverified architecture", result.Reason);
        Assert.Empty(native.MetricCalls);
    }

    [Fact]
    public async Task A_successfully_read_empty_route_table_is_distinct_from_failure()
    {
        var native = new FakeNative { Routes = Collected<IReadOnlyList<NativeNetworkApi.RouteRow>>.Ok([]) };
        var result = await new WindowsRouteSource(native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Ok, result.Status);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Partially_decoded_native_routes_keep_their_domain_gap()
    {
        var native = new FakeNative
        {
            Routes = Collected<IReadOnlyList<NativeNetworkApi.RouteRow>>.Partial(
                [Route("0.0.0.0/0", NativeNetworkApi.AfInet)], "one native row was unreadable", ["row 2 invalid"]),
        };
        var result = await new WindowsRouteSource(native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        Assert.Single(result.Value!);
        Assert.Contains("row 2 invalid", result.Errors);
    }

    [Fact]
    public async Task Interface_metric_failure_keeps_identity_but_marks_item_and_domain_partial()
    {
        var adapters = new FakeAdapters(new FakeAdapter("good", 12), new FakeAdapter("metric-gap", 13));
        var native = new FakeNative
        {
            Metrics = (index, _) => index == 13
                ? Collected<NativeNetworkApi.InterfaceMetrics>.Unavailable(EventStatus.Failed, "injected missing native row")
                : KnownMetric(25),
        };

        var result = await new WindowsInterfaceSource(adapters, native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        Assert.Equal(2, result.Value!.Count);
        var good = Assert.Single(result.Value, item => item.IfIndex == 12);
        Assert.Equal("ipv4", good.Family);
        Assert.Equal(25, good.Metric);
        Assert.Null(good.Reason);
        var missing = Assert.Single(result.Value, item => item.IfIndex == 13);
        Assert.Equal("metric-gap", missing.Name);
        Assert.Equal("ipv4", missing.Family);
        Assert.NotEmpty(missing.Addresses);
        Assert.Null(missing.Metric);
        Assert.Null(missing.AutomaticMetric);
        Assert.Null(missing.DisableDefaultRoutes);
        Assert.Contains("injected missing native row", missing.Reason);
        Assert.Contains(result.Errors, error => error.Contains("metric-gap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_selected_IPv4_row_never_borrows_IPv6_metric_or_flags()
    {
        var adapter = new FakeAdapter("dual-family", 12)
        {
            InterfaceRead = () => new AdapterInterfaceConfiguration
            {
                Ipv4Index = 12,
                Ipv6Index = 13,
                Addresses = ["192.0.2.2/24", "2001:db8::2/64"],
            },
        };
        var native = new FakeNative
        {
            Metrics = (_, family) => family == NativeNetworkApi.AfInet
                ? Collected<NativeNetworkApi.InterfaceMetrics>.Unavailable(EventStatus.AccessDenied, "IPv4 metric denied")
                : Collected<NativeNetworkApi.InterfaceMetrics>.Ok(new(456, 40, true, true)),
        };

        var result = await new WindowsInterfaceSource(new FakeAdapters(adapter), native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        var item = Assert.Single(result.Value!);
        Assert.Equal(12, item.IfIndex);
        Assert.Equal("ipv4", item.Family);
        Assert.Null(item.Metric);
        Assert.Null(item.AutomaticMetric);
        Assert.Null(item.DisableDefaultRoutes);
        Assert.Null(item.Luid);
        Assert.Contains("IPv4 metric denied", item.Reason);
        Assert.Contains(result.Errors, error => error.Contains("IPv4 metric denied", StringComparison.Ordinal));
        var read = Assert.Single(native.MetricCalls);
        Assert.Equal((12, NativeNetworkApi.AfInet), read.Key);
        Assert.Equal(1, read.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Only_a_valid_IPv6_index_selects_labeled_IPv6_scalar_facts_when_IPv4_is_unavailable(int? ipv4Index)
    {
        var adapter = new FakeAdapter("ipv6-only", 13)
        {
            InterfaceRead = () => new AdapterInterfaceConfiguration
            {
                Ipv4Index = ipv4Index,
                Ipv6Index = 13,
                Addresses = ["2001:db8::2/64"],
            },
        };
        var native = new FakeNative
        {
            Metrics = (_, _) => Collected<NativeNetworkApi.InterfaceMetrics>.Ok(new(456, 40, true, true)),
        };

        var result = await new WindowsInterfaceSource(new FakeAdapters(adapter), native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Ok, result.Status);
        var item = Assert.Single(result.Value!);
        Assert.Equal(13, item.IfIndex);
        Assert.Equal("ipv6", item.Family);
        Assert.Equal(40, item.Metric);
        Assert.True(item.AutomaticMetric);
        Assert.True(item.DisableDefaultRoutes);
        Assert.Null(item.Reason);
        Assert.Equal((13, NativeNetworkApi.AfInet6), Assert.Single(native.MetricCalls).Key);
    }

    [Fact]
    public async Task No_valid_index_keeps_family_and_scalar_facts_unknown()
    {
        var adapter = new FakeAdapter("unknown-index", 0)
        {
            InterfaceRead = () => new AdapterInterfaceConfiguration { Addresses = ["192.0.2.2/24"] },
        };
        var native = new FakeNative();
        var result = await new WindowsInterfaceSource(new FakeAdapters(adapter), native).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        var item = Assert.Single(result.Value!);
        Assert.Equal(0, item.IfIndex);
        Assert.Equal("unknown", item.Family);
        Assert.Null(item.Metric);
        Assert.Null(item.AutomaticMetric);
        Assert.Null(item.DisableDefaultRoutes);
        Assert.NotNull(item.Reason);
        Assert.Empty(native.MetricCalls);
    }

    [Fact]
    public async Task A_failed_NIC_does_not_hide_the_successful_NIC_or_claim_complete_collection()
    {
        var bad = new FakeAdapter("unreadable-nic", 13)
        {
            InterfaceRead = () => throw new NetworkInformationException(5),
        };
        var source = new WindowsInterfaceSource(new FakeAdapters(new FakeAdapter("good", 12), bad), new FakeNative());

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        Assert.Equal("good", Assert.Single(result.Value!).Name);
        Assert.Contains(result.Errors, error => error.Contains("unreadable-nic", StringComparison.Ordinal));
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task An_index_read_gap_remains_partial_even_when_another_family_has_a_metric()
    {
        var adapter = new FakeAdapter("ipv6-readable", 12)
        {
            InterfaceRead = () => new AdapterInterfaceConfiguration
            {
                Ipv6Index = 12,
                Addresses = ["::1/128"],
                Errors = ["IPv4 index could not be read"],
            },
        };
        var result = await new WindowsInterfaceSource(new FakeAdapters(adapter), new FakeNative())
            .ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        var item = Assert.Single(result.Value!);
        Assert.Equal("ipv6", item.Family);
        Assert.Equal(25, item.Metric);
        Assert.Contains("IPv4 index", item.Reason);
    }

    [Fact]
    public async Task DNS_keeps_successful_servers_and_reports_the_unreadable_NIC()
    {
        var good = new FakeAdapter("good-dns", 12);
        var bad = new FakeAdapter("unreadable-dns", 13)
        {
            DnsRead = () => throw new UnauthorizedAccessException("injected denial"),
        };

        var result = await new WindowsDnsSource(new FakeAdapters(good, bad)).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        Assert.True(result.HasValue);
        Assert.Equal(new[] { "192.0.2.53" }, result.Value!.ServersByInterface[12]);
        Assert.False(result.Value.ServersByInterface.ContainsKey(13));
        Assert.Contains("corp.example", result.Value.SuffixSearchList);
        Assert.Contains(result.Errors, error => error.Contains("unreadable-dns", StringComparison.Ordinal));
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task A_successfully_read_empty_DNS_list_is_preserved_separately_from_a_failed_NIC()
    {
        var good = new FakeAdapter("empty-dns", 12)
        {
            DnsRead = () => new AdapterDnsConfiguration { Ipv4Index = 12, Servers = [] },
        };
        var bad = new FakeAdapter("unreadable-dns", 13)
        {
            DnsRead = () => throw new NetworkInformationException(5),
        };
        var result = await new WindowsDnsSource(new FakeAdapters(good, bad)).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Partial, result.Status);
        Assert.True(result.Value!.ServersByInterface.ContainsKey(12));
        Assert.Empty(result.Value.ServersByInterface[12]);
        Assert.False(result.Value.ServersByInterface.ContainsKey(13));
    }

    [Fact]
    public async Task No_readable_DNS_configuration_is_a_gap_not_an_empty_healthy_configuration()
    {
        var bad = new FakeAdapter("unreadable-dns", 13)
        {
            DnsRead = () => throw new NetworkInformationException(5),
        };
        var result = await new WindowsDnsSource(new FakeAdapters(bad)).ReadAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Failed, result.Status);
        Assert.False(result.HasValue);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Cancellation_is_preserved_before_native_or_adapter_reads()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var native = new FakeNative();
        var adapters = new FakeAdapters(new FakeAdapter("good", 12));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsRouteSource(native).ReadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsInterfaceSource(adapters, native).ReadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsDnsSource(adapters).ReadAsync(cancellation.Token));
        Assert.Empty(native.MetricCalls);
        Assert.Equal(0, adapters.ReadCount);
    }

    private static NativeNetworkApi.RouteRow Route(string prefix, ushort family) =>
        new(prefix, family, 0, 12, family == NativeNetworkApi.AfInet ? "192.0.2.1" : "::1", 5, 3, 0);

    private static Collected<NativeNetworkApi.InterfaceMetrics> KnownMetric(uint metric) =>
        Collected<NativeNetworkApi.InterfaceMetrics>.Ok(new(123, metric, true, false));

    private sealed class FakeNative : IWindowsNetworkApi
    {
        public Collected<IReadOnlyList<NativeNetworkApi.RouteRow>> Routes { get; init; } =
            Collected<IReadOnlyList<NativeNetworkApi.RouteRow>>.Ok([Route("0.0.0.0/0", NativeNetworkApi.AfInet)]);
        public Func<int, ushort, Collected<NativeNetworkApi.InterfaceMetrics>> Metrics { get; init; } = (_, _) => KnownMetric(25);
        public Dictionary<(int, ushort), int> MetricCalls { get; } = [];

        public Collected<NativeNetworkApi.InterfaceMetrics> ReadInterfaceMetrics(int index, ushort family)
        {
            MetricCalls[(index, family)] = MetricCalls.GetValueOrDefault((index, family)) + 1;
            return Metrics(index, family);
        }
        public Collected<IReadOnlyList<NativeNetworkApi.RouteRow>> ReadRoutes(ushort family) => Routes;
    }

    private sealed class FakeAdapters(params IWindowsAdapter[] adapters) : IWindowsAdapterProvider
    {
        public int ReadCount { get; private set; }
        public IReadOnlyList<IWindowsAdapter> GetAdapters() { ReadCount++; return adapters; }
    }

    private sealed class FakeAdapter(string name, int index) : IWindowsAdapter
    {
        public string Id => "guid-" + name;
        public string Name => name;
        public string Description => "synthetic adapter";
        public NetworkInterfaceType Type => NetworkInterfaceType.Ethernet;
        public OperationalStatus OperationalStatus => System.Net.NetworkInformation.OperationalStatus.Up;
        public Func<AdapterInterfaceConfiguration>? InterfaceRead { get; init; }
        public Func<AdapterDnsConfiguration>? DnsRead { get; init; }
        public AdapterInterfaceConfiguration ReadInterfaceConfiguration() => InterfaceRead?.Invoke()
            ?? new AdapterInterfaceConfiguration { Ipv4Index = index, Addresses = ["192.0.2.2/24"] };
        public AdapterDnsConfiguration ReadDnsConfiguration() => DnsRead?.Invoke()
            ?? new AdapterDnsConfiguration { Ipv4Index = index, Servers = ["192.0.2.53"], Suffix = "corp.example" };
    }
}
