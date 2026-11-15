using System.Text;
using NetScope.Adapters;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters.Tests;

/// <summary>
/// T09-R2 regressions. The first block was written before the fix and failed against the then
/// current adapter; every scenario uses synthetic processes, listeners, adapters and files only.
/// </summary>
public sealed class T09R2RegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_core_listener_on_another_pid_is_attributed_to_the_client_with_its_evidence()
    {
        // Nice UI and FlClashCore are two PIDs; only FlClashCore owns the mixed port. The UI
        // process alone must not make the real listener disappear into "unknown".
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                Core(21148, "FlClashCore", @"C:\synthetic\Nice\FlClashCore.exe", parentPid: 35272),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                Build.Listener(7890, 21148, ownerName: "FlClashCore"),
            ]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        var endpoint = Assert.Single(nice.Endpoints);
        Assert.Equal("127.0.0.1:7890", endpoint.Display);
        Assert.Equal(21148, endpoint.OwnerProcessId);
        Assert.Equal("core", endpoint.Attribution);
        Assert.Contains("35272", endpoint.AttributionEvidence!, StringComparison.Ordinal);
        Assert.DoesNotContain(statuses, status => status.ClientId == KnownClientIds.UnknownListener);

        Assert.Equal(2, nice.Processes.Count);
        var ui = Assert.Single(nice.Processes, process => process.Role == "ui");
        Assert.Equal(35272, ui.ProcessId);
        var core = Assert.Single(nice.Processes, process => process.Role == "core");
        Assert.Equal(21148, core.ProcessId);
        Assert.Equal("parent_child", core.Association);
        Assert.Contains("可验证父子", core.AssociationEvidence!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_core_in_the_same_install_directory_is_attributed_with_path_evidence_only()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                // No parent link: only the install directory connects this core to the UI.
                Core(21148, "FlClashCore", @"C:\synthetic\Nice\FlClashCore.exe"),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
                [Build.Listener(7890, 21148, ownerName: "FlClashCore")]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        var endpoint = Assert.Single(nice.Endpoints);
        Assert.Equal("core", endpoint.Attribution);
        var core = Assert.Single(nice.Processes, process => process.Role == "core");
        Assert.Equal("same_install_directory", core.Association);
        Assert.Contains("目录相同", core.AssociationEvidence!, StringComparison.Ordinal);
        Assert.Contains("不是父子实例验证", core.AssociationEvidence!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_same_name_cores_in_unrelated_directories_are_not_claimed_by_nice()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                Core(5101, "FlClashCore", @"C:\other-tool\FlClashCore.exe"),
                Core(5102, "FlClashCore", @"D:\another-tool\FlClashCore.exe"),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                Build.Listener(7901, 5101, ownerName: "FlClashCore"),
                Build.Listener(7902, 5102, ownerName: "FlClashCore"),
            ]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.Empty(nice.Endpoints);
        Assert.DoesNotContain(nice.Processes, process => process.Role == "core");
        var cores = Status(statuses, KnownClientIds.UnattributedCore);
        Assert.Equal(2, cores.Processes.Count);
        Assert.All(cores.Processes, process => Assert.Null(process.Association));
        Assert.Contains(cores.Processes, process => process.ExecutablePath == @"C:\other-tool\FlClashCore.exe");
        Assert.Contains(cores.Processes, process => process.ExecutablePath == @"D:\another-tool\FlClashCore.exe");
        Assert.All(cores.Processes, process => Assert.DoesNotContain(
            process.FieldGaps,
            gap => gap.StartsWith("executable_path=", StringComparison.Ordinal)));
        var unknown = Status(statuses, KnownClientIds.UnknownListener);
        Assert.Equal(2, unknown.Endpoints.Count);
        Assert.Equal(new int?[] { 5101, 5102 }, unknown.Endpoints.Select(endpoint => endpoint.OwnerProcessId).OrderBy(pid => pid).ToArray());
    }

    [Fact]
    public async Task Only_the_ui_process_leaves_no_core_and_no_invented_endpoint()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
                [Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe")]),
        };
        var (adapter, _) = Create(processes);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.True(nice.Running);
        Assert.Empty(nice.Endpoints);
        Assert.DoesNotContain(nice.Processes, process => process.Role == "core");
        Assert.Contains(nice.Notes, note => note.Contains("没有可归属的实际监听", StringComparison.Ordinal));
        Assert.DoesNotContain(statuses, status => status.ClientId == KnownClientIds.UnattributedCore);
    }

    [Fact]
    public async Task Only_an_unrelated_core_is_kept_as_an_unattributed_fact()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
                [Core(21148, "FlClashCore", @"C:\synthetic\Nice\FlClashCore.exe")]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
                [Build.Listener(7890, 21148, ownerName: "FlClashCore")]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.False(nice.Running);
        Assert.Empty(nice.Endpoints);
        var cores = Status(statuses, KnownClientIds.UnattributedCore);
        var core = Assert.Single(cores.Processes);
        Assert.Equal(21148, core.ProcessId);
        Assert.Null(core.Association);
        Assert.Contains(cores.Gaps, gap => gap.Contains("缺少可验证父子/安装目录关联", StringComparison.Ordinal));
        var unknown = Status(statuses, KnownClientIds.UnknownListener);
        Assert.Equal("127.0.0.1:7890", Assert.Single(unknown.Endpoints).Display);
    }

    [Fact]
    public async Task An_unreadable_core_path_keeps_the_listener_and_the_path_gap_separately()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                new ProcessObservation
                {
                    ProcessId = 21148,
                    Name = "FlClashCore",
                    StartTime = Boot.AddMinutes(1).AddSeconds(2),
                    ParentProcessId = 35272,
                    UnreadableFields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ObservationFieldNames.ExecutablePath] = "access_denied",
                        [ObservationFieldNames.Version] = "executable_path_unreadable",
                    },
                },
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
                [Build.Listener(7890, 21148, ownerName: "FlClashCore")]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        var endpoint = Assert.Single(nice.Endpoints);
        Assert.Equal("core", endpoint.Attribution);
        Assert.Contains("父子", endpoint.AttributionEvidence!, StringComparison.Ordinal);
        var core = Assert.Single(nice.Processes, process => process.Role == "core");
        Assert.Contains(core.FieldGaps, gap => gap == "executable_path=access_denied");
        Assert.Contains(nice.Gaps, gap => gap.Contains("路径不可读", StringComparison.Ordinal));
        Assert.Contains("access_denied", nice.Gaps.First(gap => gap.Contains("路径不可读", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reused_pid_keeps_the_listener_fact_but_rejects_the_attribution()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                Core(21148, "FlClashCore", @"C:\synthetic\Nice\FlClashCore.exe", parentPid: 35272),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                // The socket existed before the current PID 21148 instance started.
                Build.Listener(
                    7890,
                    21148,
                    ownerName: "FlClashCore",
                    ownerInstanceStartTime: Boot.AddMinutes(-10)),
            ]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        Assert.Empty(nice.Endpoints);
        var unknown = Status(statuses, KnownClientIds.UnknownListener);
        Assert.Equal("127.0.0.1:7890", Assert.Single(unknown.Endpoints).Display);
        Assert.Contains(unknown.Gaps, gap => gap.Contains("PID 复用", StringComparison.Ordinal));
        var core = Assert.Single(nice.Processes, process => process.Role == "core");
        Assert.Equal(21148, core.ProcessId);
    }

    [Fact]
    public async Task Peaceful_coexistence_is_not_a_conflict_and_a_real_shared_endpoint_stays_visible()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                Ui(200, "Apex", @"C:\synthetic\Apex\Apex.exe"),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                Build.Listener(7890, 35272),
                Build.Listener(7891, 200),
            ]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        foreach (var clientId in new[] { KnownClientIds.Nice, KnownClientIds.Apex })
        {
            var status = Status(statuses, clientId);
            Assert.DoesNotContain(status.Notes, note => note.Contains("端点并存", StringComparison.Ordinal));
            Assert.DoesNotContain(status.Gaps, gap => gap.Contains("冲突", StringComparison.Ordinal));
        }

        // Two owners on one address:port remains visible as a coexistence note, not a silent merge.
        var sharedListeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Ok(
            [
                Build.Listener(7890, 35272),
                Build.Listener(7890, 200),
            ]),
        };
        var (sharedAdapter, _) = Create(processes, sharedListeners);
        var shared = await sharedAdapter.ReadAsync(CancellationToken.None);

        foreach (var clientId in new[] { KnownClientIds.Nice, KnownClientIds.Apex })
        {
            var status = Status(shared, clientId);
            Assert.Contains(status.Notes, note => note.Contains("端点并存", StringComparison.Ordinal));
            Assert.Contains(status.Notes, note => note.Contains("不判定冲突", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_ipv6_owner_without_support_keeps_the_endpoint_unattributed()
    {
        var processes = new FakeProcessSource
        {
            Result = Collected<IReadOnlyList<ProcessObservation>>.Ok(
            [
                Ui(35272, "NiceProxy", @"C:\synthetic\Nice\NiceProxy.exe"),
                Core(21148, "FlClashCore", @"C:\synthetic\Nice\FlClashCore.exe", parentPid: 35272),
            ]),
        };
        var listeners = new FakeListenerSource
        {
            Result = Collected<IReadOnlyList<ListenerObservation>>.Partial(
                [
                    Build.Listener(7890, 21148, ownerName: "FlClashCore"),
                    Build.Listener(
                        7891,
                        ownerProcessId: null,
                        host: "::1",
                        addressFamily: "ipv6",
                        ownerReadState: "unsupported_family",
                        ownerReadReason: "ipv6_owner_table_not_supported"),
                ],
                "ipv6 owner not read",
                ["ipv6 owner table unsupported"]),
        };
        var (adapter, _) = Create(processes, listeners);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var nice = Status(statuses, KnownClientIds.Nice);
        var ipv4 = Assert.Single(nice.Endpoints);
        Assert.Equal("ipv4", ipv4.AddressFamily);
        var unknown = Status(statuses, KnownClientIds.UnknownListener);
        var ipv6 = Assert.Single(unknown.Endpoints);
        Assert.Equal("ipv6", ipv6.AddressFamily);
        Assert.Equal("unsupported_family", ipv6.OwnerReadState);
        Assert.Contains(unknown.Gaps, gap => gap.Contains("IPv6 监听端点暂不支持", StringComparison.Ordinal));
        Assert.DoesNotContain(nice.Endpoints, endpoint => endpoint.AddressFamily == "ipv6");
    }

    [Fact]
    public async Task Two_tun_candidates_are_machine_level_and_both_address_families_are_listed()
    {
        var adapters = new FakeNetworkAdapterSource
        {
            Result = Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(
            [
                new NetworkAdapterObservation
                {
                    Name = "Nice-Tun",
                    Description = "Wintun Userspace Tunnel",
                    Kind = "tunnel",
                    OperationalStatus = "up",
                    InterfaceId = "{11111111-1111-1111-1111-111111111111}",
                    InterfaceIndex = 44,
                    Luid = 0x0000000000010001,
                    Addresses = ["2408:8207:1234::1/64", "198.18.0.1/16"],
                    Ipv4Addresses = ["198.18.0.1/16"],
                    Ipv6Addresses = ["2408:8207:1234::1/64"],
                },
                new NetworkAdapterObservation
                {
                    Name = "Meta Tunnel",
                    Kind = "tunnel",
                    OperationalStatus = "down",
                    Addresses = ["198.18.0.2/16"],
                    Ipv4Addresses = ["198.18.0.2/16"],
                },
            ]),
        };
        var (adapter, _) = Create(adapters: adapters);

        var statuses = await adapter.ReadAsync(CancellationToken.None);

        var system = Status(statuses, KnownClientIds.SystemTun);
        Assert.Equal(2, system.TunCandidates.Count);
        var niceCandidate = system.TunCandidates[0];
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", niceCandidate.Identity);
        Assert.Equal(44, niceCandidate.InterfaceIndex);
        Assert.Equal(0x0000000000010001L, niceCandidate.Luid);
        Assert.Equal("198.18.0.1/16", Assert.Single(niceCandidate.Ipv4Addresses));
        Assert.Equal("2408:8207:1234::1/64", Assert.Single(niceCandidate.Ipv6Addresses));
        Assert.Contains("IPv4 198.18.0.1/16", system.TunAddress!, StringComparison.Ordinal);
        Assert.Contains("IPv6 2408:8207:1234::1/64", system.TunAddress!, StringComparison.Ordinal);
        Assert.All(
            statuses.Where(status => status.ClientId is KnownClientIds.Nice or KnownClientIds.Apex),
            status => Assert.Null(status.TunEnabled));
    }

    [Fact]
    public async Task Ordinary_ignored_config_structures_are_not_parse_gaps()
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
        var evidence = await ReadNice(yaml);

        Assert.True(evidence.Valid);
        Assert.Equal(6, evidence.Facts.Count);
        Assert.Empty(evidence.Gaps);
        Assert.NotEmpty(evidence.Notices);
        Assert.All(evidence.Coverage.Values, state => Assert.Equal(ConfigFactCoverage.Present, state));
    }

    [Fact]
    public async Task A_missing_required_whitelist_field_remains_a_gap()
    {
        var evidence = await ReadNice("mode: rule\n");

        Assert.True(evidence.Valid);
        Assert.Contains(evidence.Gaps, gap => gap.Contains("mixed_port", StringComparison.Ordinal));
        Assert.Contains(evidence.Gaps, gap => gap.Contains("tun_enable", StringComparison.Ordinal));
        Assert.Equal(ConfigFactCoverage.MissingRequired, evidence.Coverage["mixed_port"]);
        Assert.Equal(ConfigFactCoverage.MissingOptional, evidence.Coverage["allow_lan"]);
        Assert.Contains(evidence.Notices, notice => notice.Contains("allow_lan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_format_and_read_failure_stay_gaps_not_notices()
    {
        const string path = "synthetic://t09-r2/bad.yaml";
        var malformed = await new NiceAdapter(
                new SyntheticConfigFileSource().AddFile(path, Encoding.UTF8.GetBytes("mixed-port 7890\n")),
                path)
            .ReadAsync(Now, CancellationToken.None);
        Assert.False(malformed.Valid);
        Assert.NotEmpty(malformed.Gaps);
        Assert.Empty(malformed.Notices);

        var unreadable = await new NiceAdapter(
                new SyntheticConfigFileSource().AddUnreadable(path, "access_denied"),
                path)
            .ReadAsync(Now, CancellationToken.None);
        Assert.False(unreadable.Valid);
        Assert.Contains("access_denied", unreadable.ValidityReason!, StringComparison.Ordinal);
        Assert.NotEmpty(unreadable.Gaps);
    }

    [Fact]
    public async Task A_complete_config_can_be_complete_while_ignored_structures_stay_notices()
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
            """;
        const string path = "synthetic://t09-r2/complete.yaml";
        var files = new SyntheticConfigFileSource().AddFile(path, Encoding.UTF8.GetBytes(yaml));
        var evidence = await new NiceAdapter(files, path).ReadAsync(Now, CancellationToken.None);
        Assert.True(evidence.Valid);
        Assert.Empty(evidence.Gaps);
        Assert.NotEmpty(evidence.Notices);
        var status = new ClientStatus
        {
            ClientId = KnownClientIds.Nice,
            DisplayName = "synthetic",
            Evidence = new[] { evidence },
        };
        Assert.True(status.IsComplete);
        Assert.False((status with { Evidence = new[] { evidence with { Coverage = new Dictionary<string, string>() } } }).IsComplete);
        Assert.False((status with { Evidence = new[] { evidence with { Valid = false } } }).IsComplete);
        Assert.False((status with { Gaps = new[] { "observation gap" } }).IsComplete);
    }

    /// <summary>UI process at 08:01:00; a core must start at or after the parent instance.</summary>
    private static ProcessObservation Ui(int pid, string name, string path) => new()
    {
        ProcessId = pid,
        Name = name,
        ExecutablePath = path,
        StartTime = Boot.AddMinutes(1),
    };

    private static ProcessObservation Core(int pid, string name, string path, int? parentPid = null) => new()
    {
        ProcessId = pid,
        Name = name,
        ExecutablePath = path,
        StartTime = Boot.AddMinutes(1).AddSeconds(2),
        ParentProcessId = parentPid,
    };

    private static Task<ClientConfigEvidence> ReadNice(string text)
    {
        const string path = "synthetic://t09-r2/runtime.yaml";
        var files = new SyntheticConfigFileSource()
            .AddFile(path, Encoding.UTF8.GetBytes(text));
        return new NiceAdapter(files, path).ReadAsync(Now, CancellationToken.None);
    }

    private static (GenericAdapter Adapter, SyntheticConfigFileSource Files) Create(
        FakeProcessSource? processes = null,
        FakeListenerSource? listeners = null,
        FakeNetworkAdapterSource? adapters = null)
    {
        var files = new SyntheticConfigFileSource();
        var adapter = AdapterFactory.Create(processes, listeners, adapters, configAdapters: [], timeProvider: new FixedTimeProvider(Now));
        return (adapter, files);
    }

    private static ClientStatus Status(IReadOnlyList<ClientStatus> statuses, string clientId) =>
        Assert.Single(statuses, status => status.ClientId == clientId);
}
