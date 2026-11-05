using NetScope.Domain;

namespace NetScope.Application.Mock;

/// <summary>
/// Deterministic stand-in for the Windows collector. It returns a plausible, clearly synthetic
/// topology so the rest of M0 can be exercised without reading the real machine. Every value is
/// invented; nothing here is a measurement.
/// </summary>
public sealed class SyntheticSnapshotCollector : ISnapshotCollector
{
    public const string TunInterfaceGuid = "synthetic-tun-guid";

    public const string EthernetInterfaceGuid = "synthetic-ethernet-guid";

    public string Source => "synthetic.windows-collector";

    public string SourceVersion => "1.0";

    public Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new NetworkSnapshot
        {
            SnapshotId = context.SnapshotId,
            ObservedAt = context.ObservedAt,
            MonotonicMs = context.MonotonicMs,
            Synthetic = context.Synthetic,
            Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(new[]
            {
                new InterfaceState
                {
                    InterfaceGuid = TunInterfaceGuid,
                    Name = "Synthetic-TUN",
                    IfIndex = 40,
                    Family = "ipv4",
                    Description = "Synthetic tunnel adapter used only for demos",
                    Kind = "tunnel",
                    OperationalStatus = "up",
                    Addresses = new[] { "198.18.0.1/30" },
                    Metric = 1,
                    AutomaticMetric = false,
                    DisableDefaultRoutes = false,
                    Luid = "synthetic-luid-40",
                },
                new InterfaceState
                {
                    InterfaceGuid = EthernetInterfaceGuid,
                    Name = "Synthetic-Ethernet",
                    IfIndex = 12,
                    Family = "ipv4",
                    Description = "Synthetic physical uplink used only for demos",
                    Kind = "ethernet",
                    OperationalStatus = "up",
                    Addresses = new[] { "192.0.2.10/24" },
                    Metric = 25,
                    AutomaticMetric = true,
                    Luid = "synthetic-luid-12",
                },
            }),
            Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(new[]
            {
                new RouteEntry
                {
                    DestinationPrefix = "0.0.0.0/0",
                    Family = "ipv4",
                    NextHop = "198.18.0.2",
                    IfIndex = 40,
                    RouteMetric = 0,
                    InterfaceMetric = 1,
                    Protocol = "manual",
                },
                new RouteEntry
                {
                    DestinationPrefix = "198.18.0.0/15",
                    Family = "ipv4",
                    NextHop = "0.0.0.0",
                    IfIndex = 40,
                    RouteMetric = 0,
                    InterfaceMetric = 1,
                    Protocol = "manual",
                },
                new RouteEntry
                {
                    DestinationPrefix = "0.0.0.0/0",
                    Family = "ipv4",
                    NextHop = "192.0.2.1",
                    IfIndex = 12,
                    RouteMetric = 0,
                    InterfaceMetric = 25,
                    Protocol = "dhcp",
                },
            }),
            Dns = Collected<DnsState>.Ok(new DnsState
            {
                ServersByInterface = new Dictionary<int, IReadOnlyList<string>>
                {
                    [40] = new[] { "198.18.0.2" },
                    [12] = new[] { "192.0.2.53" },
                },
                SuffixSearchList = new[] { "synthetic.invalid" },
            }),
            Proxies = Collected<ProxyState>.Ok(new ProxyState
            {
                ToolProcessEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
                {
                    Layer = "tool_process_environment",
                    Provenance = "synthetic",
                    Enabled = true,
                    ProxyServer = "http://127.0.0.1:7890",
                    Variables = new[] { "HTTPS_PROXY=http://127.0.0.1:7890", "HTTP_PROXY=http://127.0.0.1:7890" },
                    Notice = "Synthetic values; no process environment was read.",
                }),
                UserEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
                {
                    Layer = "user_environment",
                    Provenance = "synthetic",
                    Enabled = true,
                    ProxyServer = "http://127.0.0.1:7890",
                    Variables = new[] { "HTTPS_PROXY=http://127.0.0.1:7890" },
                }),
                MachineEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
                {
                    Layer = "machine_environment",
                    Provenance = "synthetic",
                    Enabled = false,
                    Variables = Array.Empty<string>(),
                }),
                WinInet = Collected<ProxyLayer>.Ok(new ProxyLayer
                {
                    Layer = "wininet",
                    Provenance = "synthetic",
                    Enabled = false,
                    AutoDetect = true,
                }),
                WinHttp = Collected<ProxyLayer>.Unavailable(
                    EventStatus.Unsupported,
                    "M0 does not read the WinHTTP configuration store."),
            }),
        };

        return Task.FromResult(snapshot);
    }
}
