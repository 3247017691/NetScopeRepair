using NetScope.Adapters;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters.Tests;

public sealed class GenericAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly string NicePath = RepoPaths.Fixture("nice-runtime.yaml");
    private static readonly string ApexPath = RepoPaths.Fixture("apex-shared_preferences.json");

    [Fact]
    public async Task Process_seam_drives_running_and_not_running_notes()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
                [Build.Process(100, "NiceProxy", @"C:\synthetic\NiceProxy.exe", "1.2.3")]),
        };
        var (adapter, _) = Create(processes);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.Equal(2, statuses.Count);
        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.True(nice.Running);
        Assert.Equal("1.2.3", nice.Version);
        Assert.Contains(nice.Notes, note => note.Contains("运行中", StringComparison.Ordinal));
        var apex = Status(statuses, KnownClientIds.Apex);
        Assert.False(apex.Running);
        Assert.Contains(apex.Notes, note => note.Contains("未运行", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_and_configured_are_reported_as_separate_statements()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
                [Build.Process(100, "NiceProxy", @"C:\synthetic\NiceProxy.exe")]),
        };
        var (adapter, _) = Create(processes);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.Contains(nice.Notes, note => note.Contains("运行中", StringComparison.Ordinal));
        Assert.Contains(nice.Notes, note => note.Contains("已配置", StringComparison.Ordinal));
        var apex = Status(statuses, KnownClientIds.Apex);
        Assert.Contains(apex.Notes, note => note.Contains("未运行", StringComparison.Ordinal));
        Assert.Contains(apex.Notes, note => note.Contains("已配置", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_process_list_never_claims_not_running()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Partial([], "partially read", ["one process denied"]),
        };
        var (adapter, _) = Create(processes);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.Null(Status(statuses, KnownClientIds.Nice).Running);
        Assert.Null(Status(statuses, KnownClientIds.Apex).Running);
        Assert.Contains(
            Status(statuses, KnownClientIds.Nice).Gaps,
            gap => gap.Contains("只读取了一部分", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_process_source_keeps_running_unknown_with_a_gap()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Unavailable(EventStatus.AccessDenied, "access denied"),
        };
        var (adapter, _) = Create(processes);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.Null(nice.Running);
        Assert.Contains(nice.Gaps, gap => gap.Contains("进程列表不可读", StringComparison.Ordinal));
        Assert.Contains(nice.Notes, note => note.Contains("运行状态未知", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Endpoints_are_attributed_by_owner_pid_and_displayed()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Build.Process(100, "NiceProxy", @"C:\synthetic\NiceProxy.exe"),
                Build.Process(200, "Apex", @"C:\synthetic\Apex.exe"),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                Build.Listener(7890, 100),
                Build.Listener(7891, 200),
                Build.Listener(7892, 100, host: null),
            ]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.Contains(nice.Endpoints, endpoint => endpoint.Display == "127.0.0.1:7890");
        Assert.Contains(nice.Endpoints, endpoint => endpoint.Display == "port 7892");
        Assert.All(nice.Endpoints, endpoint => Assert.Equal("listen", endpoint.Role));
        var apex = Status(statuses, KnownClientIds.Apex);
        Assert.Single(apex.Endpoints);
        Assert.Equal("127.0.0.1:7891", apex.Endpoints[0].Display);
        Assert.DoesNotContain(statuses, status => status.ClientId == KnownClientIds.UnknownListener);
    }

    [Fact]
    public async Task Listener_without_readable_owner_is_reported_as_unidentified_not_guessed()
    {
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok([Build.Listener(8888, ownerProcessId: null)]),
        };
        var (adapter, _) = Create(listeners: listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.Empty(Status(statuses, KnownClientIds.Nice).Endpoints);
        var unknown = Status(statuses, KnownClientIds.UnknownListener);
        Assert.Equal("127.0.0.1:8888", Assert.Single(unknown.Endpoints).Display);
        Assert.Contains(unknown.Gaps, gap => gap.Contains("无法读取所有者", StringComparison.Ordinal));
        Assert.DoesNotContain(statuses.Take(2), status => status.Endpoints.Count > 0);
    }

    [Fact]
    public async Task Listener_owned_by_an_unsupported_process_is_not_assigned_to_a_client()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok([Build.Process(300, "other-tool")]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok([Build.Listener(9999, 300, ownerName: "other-tool")]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.Empty(Status(statuses, KnownClientIds.Nice).Endpoints);
        Assert.Empty(Status(statuses, KnownClientIds.Apex).Endpoints);
        var unknown = Status(statuses, KnownClientIds.UnknownListener);
        Assert.Equal("127.0.0.1:9999", Assert.Single(unknown.Endpoints).Display);
        Assert.Contains(unknown.Notes, note => note.Contains("other-tool", StringComparison.Ordinal));
        Assert.Contains(unknown.Notes, note => note.Contains("不推断客户端身份", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_listener_source_is_a_gap_not_an_empty_healthy_list()
    {
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Unavailable(EventStatus.Failed, "listener read failed"),
        };
        var (adapter, _) = Create(listeners: listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.DoesNotContain(statuses, status => status.ClientId == KnownClientIds.UnknownListener);
        Assert.Contains(
            Status(statuses, KnownClientIds.Nice).Gaps,
            gap => gap.Contains("监听端点不可读", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tun_adapter_presence_is_reported_without_claiming_ownership()
    {
        var adapters = new FakeNetworkAdapterSource
        {
            Result = Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(
            [
                Build.Adapter("Meta Tunnel", "tunnel", "up", "Wintun Userspace Tunnel", "198.18.0.1/16"),
            ]),
        };
        var (adapter, _) = Create(adapters: adapters);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        foreach (var status in statuses)
        {
            Assert.True(status.TunEnabled);
            Assert.Equal("198.18.0.1/16", status.TunAddress);
            Assert.Contains(status.Notes, note => note.Contains("TUN 候选", StringComparison.Ordinal));
            Assert.Contains(status.Notes, note => note.Contains("不等于由该客户端创建", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task No_tun_candidate_is_false_when_the_adapter_list_is_readable()
    {
        var adapters = new FakeNetworkAdapterSource
        {
            Result = Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(
                [Build.Adapter("Ethernet", "ethernet", "up", "Intel Ethernet", "192.0.2.2/24")]),
        };
        var (adapter, _) = Create(adapters: adapters);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.All(statuses, status => Assert.False(status.TunEnabled));
        Assert.All(statuses, status => Assert.Null(status.TunAddress));
        Assert.Contains(statuses, status => status.Notes.Any(note => note.Contains("未观测到启用的 TUN", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unreadable_adapter_list_keeps_tun_unknown_with_a_gap()
    {
        var adapters = new FakeNetworkAdapterSource
        {
            Result = Collected<IReadOnlyList<NetworkAdapterObservation>>.Unavailable(EventStatus.Failed, "adapter read failed"),
        };
        var (adapter, _) = Create(adapters: adapters);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.All(statuses, status => Assert.Null(status.TunEnabled));
        Assert.All(statuses, status => Assert.Contains(
            status.Gaps,
            gap => gap.Contains("网络适配器列表不可读", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Missing_config_file_becomes_a_client_gap_with_invalid_evidence()
    {
        var files = new SyntheticConfigFileSource()
            .AddFixture(ApexPath, "apex-shared_preferences.json");
        IClientConfigAdapter[] configs =
        [
            new NiceAdapter(files, NicePath),
            new ApexAdapter(files, ApexPath),
        ];
        var adapter = AdapterFactory.Create(configAdapters: configs, timeProvider: new FixedTimeProvider(Now));

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.Contains(nice.Gaps, gap => gap.Contains("配置文件不存在", StringComparison.Ordinal));
        var evidence = Assert.Single(nice.Evidence);
        Assert.False(evidence.Valid);
        Assert.Equal(ConfigProvenance.DiskOnly, evidence.Provenance);
        Assert.True(Status(statuses, KnownClientIds.Apex).Evidence[0].Valid);
    }

    [Fact]
    public async Task Two_clients_on_the_same_endpoint_get_a_coexistence_note_not_a_conflict_diagnosis()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Build.Process(100, "NiceProxy", @"C:\synthetic\NiceProxy.exe"),
                Build.Process(200, "Apex", @"C:\synthetic\Apex.exe"),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                Build.Listener(7890, 100),
                Build.Listener(7890, 200),
            ]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        foreach (var status in statuses)
        {
            Assert.Contains(status.Notes, note => note.Contains("端点并存", StringComparison.Ordinal));
            Assert.Contains(status.Notes, note => note.Contains("不判定冲突", StringComparison.Ordinal));
            Assert.DoesNotContain(status.Gaps, gap => gap.Contains("冲突", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Matched_process_without_readable_path_stays_unknown_with_a_gap()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok([Build.Process(100, "NiceProxy")]),
        };
        var (adapter, _) = Create(processes);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.True(nice.Running);
        Assert.Null(nice.Version);
        Assert.Contains(nice.Gaps, gap => gap.Contains("可执行文件路径不可读", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proxy_layers_are_read_through_the_injectable_seam()
    {
        var proxies = new FakeProxyLayerSource
        {
            Result = Collected<IReadOnlyList<ProxyLayerObservation>>.Ok(
            [
                new ProxyLayerObservation
                {
                    Layer = "wininet",
                    Provenance = "registry:HKCU",
                    Enabled = true,
                    Detail = "synthetic layer",
                },
            ]),
        };
        var (adapter, _) = Create(proxies: proxies);

        var layers = await adapter.ReadProxyLayersAsync(CancellationToken.None);

        Assert.Equal(EventStatus.Ok, layers.Status);
        Assert.Equal("wininet", Assert.Single(layers.Value!).Layer);
        Assert.Equal(1, proxies.ReadCount);
    }

    [Fact]
    public async Task Proxy_layer_summary_is_visible_on_every_status_without_values()
    {
        var proxies = new FakeProxyLayerSource
        {
            Result = Collected<IReadOnlyList<ProxyLayerObservation>>.Ok(
            [
                new ProxyLayerObservation
                {
                    Layer = "wininet",
                    Provenance = "registry:HKCU",
                    Enabled = true,
                    Detail = "synthetic layer",
                },
                new ProxyLayerObservation
                {
                    Layer = "winhttp",
                    Provenance = "unsupported",
                    Enabled = null,
                    Detail = "synthetic unsupported layer",
                },
            ]),
        };
        var (adapter, _) = Create(proxies: proxies);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        Assert.Equal(1, proxies.ReadCount);
        Assert.All(statuses, status => Assert.Contains(
            status.Notes,
            note => note.Contains("代理层摘要", StringComparison.Ordinal)));
        Assert.All(statuses, status => Assert.Contains(
            status.Notes,
            note => note.Contains("wininet=已设置", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_any_seam_is_read()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var processes = new FakeProcessSource();
        var listeners = new FakeListenerSource();
        var adapters = new FakeNetworkAdapterSource();
        var proxies = new FakeProxyLayerSource();
        var files = new SyntheticConfigFileSource();
        IClientConfigAdapter[] configs = [new NiceAdapter(files, NicePath), new ApexAdapter(files, ApexPath)];
        var adapter = AdapterFactory.Create(processes, listeners, adapters, proxies, configs, new FixedTimeProvider(Now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ReadAsync(cancellation.Token));

        Assert.Equal(0, processes.ReadCount);
        Assert.Equal(0, listeners.ReadCount);
        Assert.Equal(0, adapters.ReadCount);
        Assert.Equal(0, files.ReadCount);
    }

    private static (GenericAdapter Adapter, SyntheticConfigFileSource Files) Create(
        FakeProcessSource? processes = null,
        FakeListenerSource? listeners = null,
        FakeNetworkAdapterSource? adapters = null,
        FakeProxyLayerSource? proxies = null)
    {
        var files = new SyntheticConfigFileSource()
            .AddFixture(NicePath, "nice-runtime.yaml")
            .AddFixture(ApexPath, "apex-shared_preferences.json");
        IClientConfigAdapter[] configs =
        [
            new NiceAdapter(files, NicePath),
            new ApexAdapter(files, ApexPath),
        ];
        var adapter = AdapterFactory.Create(
            processes,
            listeners,
            adapters,
            proxies,
            configs,
            new FixedTimeProvider(Now));
        return (adapter, files);
    }

    private static ClientStatus Status(IReadOnlyList<ClientStatus> statuses, string clientId) =>
        Assert.Single(statuses, status => status.ClientId == clientId);
}
