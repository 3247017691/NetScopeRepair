using System.Text;
using NetScope.Adapters;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters.Tests;

/// <summary>
/// The original T09-R2 reproducers, written before the fix and run against the unmodified adapter.
/// They are kept as the pre-fix evidence path: each one failed on the old implementation and is a
/// strict subset of the stronger scenarios in <see cref="T09R2RegressionTests"/>. No product data,
/// real process, real listener or real configuration file is used.
/// </summary>
public sealed class T09R2PreFixReproducerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Nice_ui_only_process_leaves_its_real_core_listener_unattributed()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Build.Process(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                Build.Process(21148, "FlClashCore", @"C:\synthetic\Nice\FlClashCore.exe"),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
                [Build.Listener(7890, 21148, ownerName: "FlClashCore")]),
        };
        var adapter = AdapterFactory.Create(processes, listeners, timeProvider: new FixedTimeProvider(Now));

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Assert.Single(statuses, status => status.ClientId == KnownClientIds.Nice);
        Assert.Contains(nice.Endpoints, endpoint => endpoint.Display == "127.0.0.1:7890");
    }

    [Fact]
    public async Task One_global_tun_candidate_is_currently_copied_onto_both_clients()
    {
        var adapters = new FakeNetworkAdapterSource
        {
            Result = Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(
                [Build.Adapter("Meta Tunnel", "tunnel", "up", "Wintun Userspace Tunnel", "198.18.0.1/16")]),
        };
        var adapter = AdapterFactory.Create(networkAdapters: adapters, timeProvider: new FixedTimeProvider(Now));

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.All(
            statuses.Where(status => status.ClientId is KnownClientIds.Nice or KnownClientIds.Apex),
            status => Assert.Null(status.TunEnabled));
    }

    [Fact]
    public async Task Ordinary_ignored_structures_currently_become_a_parse_gap()
    {
        const string yaml = """
            mixed-port: 7890
            mode: rule
            allow-lan: false
            tun:
              enable: true
              mtu: 9000
            dns:
              fake-ip-range: 198.18.0.1/16
            proxies:
              - name: node
                port: 443
            subscriptions:
              - name: hidden
                url: https://example.invalid/sub
            """;
        const string path = "synthetic://t09-r2/prefix.yaml";
        var files = new SyntheticConfigFileSource().AddFile(path, Encoding.UTF8.GetBytes(yaml));
        var evidence = await new NiceAdapter(files, path).ReadAsync(Now, CancellationToken.None);

        Assert.True(evidence.Valid);
        Assert.Empty(evidence.Gaps);
    }

    [Fact]
    public async Task Partial_adapter_list_currently_reports_tun_false_for_everyone()
    {
        var adapters = new FakeNetworkAdapterSource
        {
            Result = Collected<IReadOnlyList<NetworkAdapterObservation>>.Partial(
                [Build.Adapter("Ethernet", "ethernet", "up", "Intel Ethernet", "192.0.2.2/24")],
                "adapter list partially read",
                ["one adapter denied"]),
        };
        var adapter = AdapterFactory.Create(networkAdapters: adapters, timeProvider: new FixedTimeProvider(Now));

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.DoesNotContain(statuses, status => status.TunEnabled == false);
    }
}
