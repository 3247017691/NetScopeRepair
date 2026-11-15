using System.Net;
using System.Net.NetworkInformation;
using System.Security;
using Microsoft.Win32;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Collectors.Windows;

/// <summary>Reads interface identity, addresses and per-interface metrics.</summary>
public sealed class WindowsInterfaceSource : IInterfaceSource
{
    private readonly IWindowsAdapterProvider _adapters;
    private readonly IWindowsNetworkApi _native;

    public WindowsInterfaceSource() : this(new SystemWindowsAdapterProvider(), NativeNetworkApi.Instance) { }

    internal WindowsInterfaceSource(IWindowsAdapterProvider adapters, IWindowsNetworkApi native)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }
    public Task<Collected<IReadOnlyList<InterfaceState>>> ReadAsync(CancellationToken cancellationToken)
    {
        var items = new List<InterfaceState>();
        var errors = new List<string>();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            foreach (var nic in _adapters.GetAdapters())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var described = Describe(nic);

                    // Filter-driver miniports are reported by the platform with no index and no
                    // address. They cannot be correlated with any route, so they are not
                    // presented as interfaces; the count is reported instead of hidden.
                    if (described.Rows.Count == 0)
                    {
                        errors.Add($"{SafeName(nic)}: an entry with neither an interface index nor an address was skipped.");
                        foreach (var error in described.Errors)
                        {
                            errors.Add($"{SafeName(nic)}: {error}");
                        }

                        continue;
                    }

                    foreach (var row in described.Rows)
                    {
                        items.Add(row);
                        if (row.Reason is not null)
                        {
                            errors.Add($"{SafeName(nic)}: {row.Reason}");
                        }
                    }
                }
                catch (Exception exception) when (WindowsReadFailure.IsExpected(exception))
                {
                    errors.Add($"{SafeName(nic)}: interface configuration could not be read ({exception.GetType().Name}).");
                }
            }
        }
        catch (Exception exception) when (WindowsReadFailure.IsExpected(exception))
        {
            return Task.FromResult(Collected<IReadOnlyList<InterfaceState>>.Unavailable(
                WindowsReadFailure.StatusFor(exception),
                "The interface list could not be read.",
                new[] { exception.GetType().Name }));
        }

        if (items.Count == 0)
        {
            return Task.FromResult(Collected<IReadOnlyList<InterfaceState>>.Unavailable(
                EventStatus.Failed,
                "No network interface was returned.",
                errors));
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(Collected<IReadOnlyList<InterfaceState>>.Partial(
                items,
                $"{errors.Count} interface observation(s) could not be fully read or correlated.",
                errors));
        }

        return Task.FromResult(Collected<IReadOnlyList<InterfaceState>>.Ok(items));
    }

    private InterfaceDescription Describe(IWindowsAdapter nic)
    {
        var properties = nic.ReadInterfaceConfiguration();
        var ipv4Addresses = new List<string>();
        var ipv6Addresses = new List<string>();
        var unclassifiedAddresses = new List<string>();

        foreach (var address in properties.Addresses)
        {
            var slash = address.IndexOf('/');
            var addressText = slash >= 0 ? address[..slash] : address;
            if (IPAddress.TryParse(addressText, out var parsed)
                && parsed.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                    or System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    ipv4Addresses.Add(address);
                }
                else
                {
                    ipv6Addresses.Add(address);
                }
            }
            else
            {
                unclassifiedAddresses.Add(address);
            }
        }

        var commonGaps = new List<string>(properties.Errors);
        if (unclassifiedAddresses.Count > 0)
        {
            commonGaps.Add($"address_family_unclassified: {unclassifiedAddresses.Count} address(es) could not be classified by family.");
        }

        var rows = new List<InterfaceState>();
        var hasIpv4Index = properties.Ipv4Index is > 0;
        var hasIpv6Index = properties.Ipv6Index is > 0;
        var hasIpv4Address = ipv4Addresses.Count > 0;
        var hasIpv6Address = ipv6Addresses.Count > 0;

        if (!hasIpv4Index && !hasIpv6Index)
        {
            if (properties.Addresses.Count == 0)
            {
                return new InterfaceDescription(rows, commonGaps);
            }

            if (hasIpv4Address && hasIpv6Address)
            {
                // Both address families are visible even though neither scalar index was
                // readable. Keep one honest gap row per family instead of merging them.
                rows.Add(DescribeFamily(nic, "ipv4", NativeNetworkApi.AfInet, null, ipv4Addresses, commonGaps));
                rows.Add(DescribeFamily(nic, "ipv6", NativeNetworkApi.AfInet6, null, ipv6Addresses, commonGaps));
                return new InterfaceDescription(rows, commonGaps);
            }

            // One address family without any index: keep one explicitly unknown row. Do not
            // promote a family from an address, because the scalar facts belong to an index.
            var unknownGaps = new List<string>(commonGaps)
            {
                "interface_index_unavailable: the interface index could not be read.",
            };
            rows.Add(CreateRow(nic, "unknown", 0, properties.Addresses, null, unknownGaps));
            return new InterfaceDescription(rows, commonGaps);
        }

        if (hasIpv4Index || hasIpv4Address)
        {
            rows.Add(DescribeFamily(
                nic,
                "ipv4",
                NativeNetworkApi.AfInet,
                properties.Ipv4Index,
                ipv4Addresses,
                commonGaps));
        }

        if (hasIpv6Index || hasIpv6Address)
        {
            rows.Add(DescribeFamily(
                nic,
                "ipv6",
                NativeNetworkApi.AfInet6,
                properties.Ipv6Index,
                ipv6Addresses,
                commonGaps));
        }

        return new InterfaceDescription(rows, commonGaps);
    }

    /// <summary>
    /// Reads scalar facts for exactly one interface+family. A metric failure for one family is
    /// recorded on that row; it is never replaced by the other family's successful read.
    /// </summary>
    private InterfaceState DescribeFamily(
        IWindowsAdapter nic,
        string family,
        ushort nativeFamily,
        int? familyIndex,
        IReadOnlyList<string> addresses,
        IReadOnlyList<string> commonGaps)
    {
        var index = familyIndex is > 0 ? familyIndex.Value : 0;
        var gaps = new List<string>(commonGaps);
        NativeNetworkApi.InterfaceMetrics? metrics = null;

        if (index > 0)
        {
            var observed = _native.ReadInterfaceMetrics(index, nativeFamily);
            if (observed.HasValue)
            {
                metrics = observed.Value;
            }

            if (observed.Status != EventStatus.Ok || metrics is null)
            {
                gaps.Add($"{family}_interface_metrics_unavailable: "
                    + (observed.Reason ?? $"The metric for interface {index}, family {nativeFamily} could not be fully read."));
            }
        }
        else
        {
            gaps.Add($"{family}_interface_index_unavailable: the {family} interface index could not be read.");
        }

        int? metric = metrics is not null && metrics.Metric <= int.MaxValue ? (int)metrics.Metric : null;
        if (metrics is not null && metric is null)
        {
            gaps.Add($"{family}_interface_metric_out_of_range: the interface metric exceeds the supported numeric range.");
        }

        return CreateRow(nic, family, index, addresses, metrics, gaps, metric);
    }

    private static InterfaceState CreateRow(
        IWindowsAdapter nic,
        string family,
        int index,
        IReadOnlyList<string> addresses,
        NativeNetworkApi.InterfaceMetrics? metrics,
        IReadOnlyList<string> gaps,
        int? metric = null)
    {
        var distinctGaps = gaps.Distinct(StringComparer.Ordinal).ToArray();
        return new InterfaceState
        {
            InterfaceGuid = nic.Id,
            Name = nic.Name,
            IfIndex = index,
            Family = family,
            Description = nic.Description,
            Kind = KindOf(nic.Type),
            OperationalStatus = nic.OperationalStatus.ToString().ToLowerInvariant(),
            Addresses = addresses,
            Metric = metric ?? (metrics is not null && metrics.Metric <= int.MaxValue ? (int)metrics.Metric : null),
            AutomaticMetric = metrics?.AutomaticMetric,
            DisableDefaultRoutes = metrics?.DisableDefaultRoutes,
            Luid = metrics?.Luid.ToString("x16"),
            Reason = distinctGaps.Length == 0 ? null : string.Join(" ", distinctGaps),
        };
    }

    private sealed record InterfaceDescription(
        IReadOnlyList<InterfaceState> Rows,
        IReadOnlyList<string> Errors);

    /// <summary>Maps the platform enum onto the small vocabulary the rules understand.</summary>
    public static string KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => "ethernet",
        NetworkInterfaceType.GigabitEthernet => "ethernet",
        NetworkInterfaceType.FastEthernetT => "ethernet",
        NetworkInterfaceType.FastEthernetFx => "ethernet",
        NetworkInterfaceType.Ethernet3Megabit => "ethernet",
        NetworkInterfaceType.Wireless80211 => "wifi",
        NetworkInterfaceType.Tunnel => "tunnel",
        NetworkInterfaceType.Loopback => "loopback",
        NetworkInterfaceType.Ppp => "ppp",
        _ => "unknown",
    };

    private static string SafeName(IWindowsAdapter nic)
    {
        try
        {
            return nic.Name;
        }
        catch (Exception exception) when (WindowsReadFailure.IsExpected(exception))
        {
            return "<unnamed interface>";
        }
    }
}

/// <summary>Reads the routing table and attributes each route to its interface metric.</summary>
public sealed class WindowsRouteSource : IRouteSource
{
    private readonly IWindowsNetworkApi _native;

    public WindowsRouteSource() : this(NativeNetworkApi.Instance) { }

    internal WindowsRouteSource(IWindowsNetworkApi native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public Task<Collected<IReadOnlyList<RouteEntry>>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = _native.ReadRoutes(NativeNetworkApi.AfUnspec);
        if (!observed.HasValue)
        {
            return Task.FromResult(Collected<IReadOnlyList<RouteEntry>>.Unavailable(
                observed.Status, observed.Reason ?? "The routing table could not be read.", observed.Errors));
        }

        var rows = observed.Value!;
        var metricCache = new Dictionary<(int Index, ushort Family), (int? Metric, string? Reason)>();
        var errors = new List<string>(observed.Errors);
        if (observed.Status != EventStatus.Ok)
        {
            errors.Add(observed.Reason ?? "The routing table was incomplete.");
        }
        var entries = new List<RouteEntry>(rows.Count);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RouteMetric > int.MaxValue)
            {
                errors.Add($"Route {row.DestinationPrefix}: route metric exceeds the supported numeric range.");
                continue;
            }

            var key = (row.InterfaceIndex, row.Family);
            if (!metricCache.TryGetValue(key, out var attribution))
            {
                var metrics = _native.ReadInterfaceMetrics(row.InterfaceIndex, row.Family);
                var metricRow = metrics.HasValue ? metrics.Value : null;
                var metric = metricRow is not null && metricRow.Metric <= int.MaxValue
                    ? (int?)metricRow.Metric : null;
                var reason = metricRow is null
                    ? metrics.Reason ?? "The per-interface metric entry could not be read."
                    : metric is null
                        ? "The interface metric exceeds the supported numeric range."
                        : metrics.Status == EventStatus.Ok ? null : metrics.Reason ?? "The interface metric was incomplete.";
                attribution = (metric, reason);
                metricCache[key] = attribution;
                if (reason is not null)
                {
                    errors.Add($"Interface {row.InterfaceIndex}, family {row.Family}: {reason}");
                }
            }

            entries.Add(new RouteEntry
            {
                DestinationPrefix = row.DestinationPrefix,
                Family = row.Family == NativeNetworkApi.AfInet6 ? "ipv6" : "ipv4",
                NextHop = row.NextHop,
                IfIndex = row.InterfaceIndex,
                RouteMetric = (int)row.RouteMetric,
                InterfaceMetric = attribution.Metric,
                Protocol = NativeNetworkApi.ProtocolName(row.Protocol),
                Origin = NativeNetworkApi.OriginName(row.Origin),
                Reason = attribution.Reason,
            });
        }

        return Task.FromResult(errors.Count == 0
            ? Collected<IReadOnlyList<RouteEntry>>.Ok(entries)
            : Collected<IReadOnlyList<RouteEntry>>.Partial(entries,
                "Some routing table data or interface metric attributions could not be read.", errors));
    }
}

/// <summary>Reads the configured DNS servers per interface.</summary>
public sealed class WindowsDnsSource : IDnsSource
{
    private readonly IWindowsAdapterProvider _adapters;

    public WindowsDnsSource() : this(new SystemWindowsAdapterProvider()) { }

    internal WindowsDnsSource(IWindowsAdapterProvider adapters) =>
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));

    public Task<Collected<DnsState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var servers = new Dictionary<int, IReadOnlyList<string>>();
        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var readCount = 0;

        try
        {
            foreach (var nic in _adapters.GetAdapters())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var properties = nic.ReadDnsConfiguration();
                    readCount++;
                    errors.AddRange(properties.Errors.Select(error => $"{SafeName(nic)}: {error}"));
                    var index = properties.Ipv4Index ?? properties.Ipv6Index ?? 0;
                    if (index > 0)
                    {
                        // Keep a successful empty server list: it differs from an unreadable NIC.
                        servers[index] = properties.Servers;
                    }
                    else if (properties.Servers.Count > 0)
                    {
                        errors.Add($"{SafeName(nic)}: DNS servers were read but the interface index was unavailable.");
                    }

                    if (!string.IsNullOrWhiteSpace(properties.Suffix))
                    {
                        suffixes.Add(properties.Suffix);
                    }
                }
                catch (Exception exception) when (WindowsReadFailure.IsExpected(exception))
                {
                    errors.Add($"{SafeName(nic)}: DNS configuration could not be read ({exception.GetType().Name}).");
                }
            }
        }
        catch (Exception exception) when (WindowsReadFailure.IsExpected(exception))
        {
            return Task.FromResult(Collected<DnsState>.Unavailable(
                WindowsReadFailure.StatusFor(exception),
                "The adapter list for DNS configuration could not be read.",
                new[] { exception.GetType().Name }));
        }

        if (readCount == 0)
        {
            return Task.FromResult(Collected<DnsState>.Unavailable(EventStatus.Failed,
                "No interface DNS configuration could be read.", errors));
        }

        var state = new DnsState { ServersByInterface = servers, SuffixSearchList = suffixes.ToArray() };
        return Task.FromResult(errors.Count == 0
            ? Collected<DnsState>.Ok(state)
            : Collected<DnsState>.Partial(state,
                "DNS configuration could not be fully read for every interface.", errors));
    }

    private static string SafeName(IWindowsAdapter nic)
    {
        try { return nic.Name; }
        catch (Exception exception) when (WindowsReadFailure.IsExpected(exception)) { return "<unnamed interface>"; }
    }
}

/// <summary>
/// Reads proxy configuration one layer at a time. Each layer records where its values came from,
/// because a variable being set says nothing about which requests actually used it.
/// </summary>
public sealed class WindowsProxySource : IProxySource
{
    private static readonly string[] Variables =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "all_proxy", "no_proxy",
    };

    public Task<Collected<ProxyState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Collected<ProxyState>.Ok(new ProxyState
        {
            ToolProcessEnvironment = ReadProcessEnvironment(),
            UserEnvironment = ReadEnvironmentLayer(EnvironmentVariableTarget.User, "user_environment"),
            MachineEnvironment = ReadEnvironmentLayer(EnvironmentVariableTarget.Machine, "machine_environment"),
            WinInet = ReadWinInet(),
            WinHttp = Collected<ProxyLayer>.Unavailable(
                EventStatus.Unsupported,
                "M0 does not read the WinHTTP configuration store; no WinHTTP value is assumed."),
        }));
    }

    /// <summary>Only proxy-related names are read; the rest of the process environment stays private.</summary>
    public static IReadOnlyList<string> ReadProxyVariables(Func<string, string?> read)
    {
        var values = new List<string>();

        foreach (var name in Variables)
        {
            var value = read(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var sanitized = name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase)
                ? SecretRedaction.RedactBypassList(value)
                : SecretRedaction.RedactUrl(value);
            values.Add($"{name}={sanitized}");
        }

        return values;
    }

    private static Collected<ProxyLayer> ReadProcessEnvironment() => ReadEnvironmentLayer(
        EnvironmentVariableTarget.Process,
        "tool_process_environment");

    private static Collected<ProxyLayer> ReadEnvironmentLayer(EnvironmentVariableTarget target, string layerName)
    {
        try
        {
            var values = ReadProxyVariables(name => Environment.GetEnvironmentVariable(name, target));

            return Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = layerName,
                Provenance = target switch
                {
                    EnvironmentVariableTarget.Process => "process_environment",
                    EnvironmentVariableTarget.User => "registry:HKCU\\Environment",
                    _ => "registry:HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment",
                },
                Enabled = values.Count > 0,
                Variables = values,
                Notice = "The variable exists in this layer; it does not prove that any request used it.",
            });
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
        {
            return Collected<ProxyLayer>.Unavailable(
                EventStatus.AccessDenied,
                $"The {layerName} layer could not be read.",
                new[] { exception.Message });
        }
    }

    private static Collected<ProxyLayer> ReadWinInet()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");

            if (key is null)
            {
                return Collected<ProxyLayer>.Unavailable(
                    EventStatus.Failed,
                    "The WinINET settings key was not found for this user.");
            }

            var enable = ReadDword(key, "ProxyEnable");
            var proxyServer = key.GetValue("ProxyServer") as string;
            var autoConfigUrl = key.GetValue("AutoConfigURL") as string;
            var autoDetect = ReadDword(key, "AutoDetect");

            return Collected<ProxyLayer>.Ok(CreateWinInetLayer(enable, proxyServer, autoConfigUrl, autoDetect));
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Collected<ProxyLayer>.Unavailable(
                EventStatus.AccessDenied,
                "The WinINET configuration could not be read.",
                new[] { exception.Message });
        }
    }

    internal static ProxyLayer CreateWinInetLayer(int? enable, string? proxyServer, string? autoConfigUrl, int? autoDetect) => new()
    {
        Layer = "wininet",
        Provenance = "registry:HKCU\\...\\Internet Settings",
        Enabled = enable is not null and not 0,
        ProxyServer = proxyServer is null ? null : SecretRedaction.RedactUrl(proxyServer),
        AutoConfigUrl = autoConfigUrl is null ? null : SecretRedaction.RedactPacUrl(autoConfigUrl),
        AutoDetect = autoDetect is not null and not 0,
        Notice = "Proxy and PAC values retain only a validated origin or endpoint; paths, credentials and URL parameters are omitted. "
            + "This stored WinINET configuration does not prove which stack an application chose.",
    };

    private static int? ReadDword(RegistryKey key, string name) =>
        key.GetValue(name) is int value ? value : null;
}
