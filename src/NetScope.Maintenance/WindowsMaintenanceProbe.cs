using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NetScope.Maintenance;

public interface IMaintenanceProbe
{
    Task<MaintenanceSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Real, local-only observation of the machine. It never changes a setting and never sends an
/// external probe except the optional "is this local port a working HTTP proxy" check.
/// A failed domain produces a note, never an empty-but-healthy list.
/// </summary>
public sealed class WindowsMaintenanceProbe : IMaintenanceProbe
{
    /// <summary>System components that must never be treated as stale hardware.</summary>
    public const string ProtectedPattern =
        "Loopback|VMware|vEthernet|VirtualBox|Hyper-V|WAN Miniport|6to4|Teredo|IP-HTTPS|Kernel Debug|Wi-Fi Direct|Bluetooth|蓝牙|本地连接\\*|Microsoft Wi-Fi";

    private static readonly string[] ProxyVariableNames =
        { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "ALL_PROXY", "all_proxy" };

    private readonly WindowsCommandRunner _runner;

    public WindowsMaintenanceProbe(WindowsCommandRunner? runner = null)
        => _runner = runner ?? new WindowsCommandRunner();

    public static bool IsPhysical(string alias)
        => !string.IsNullOrWhiteSpace(alias) && !Regex.IsMatch(alias, ProtectedPattern, RegexOptions.IgnoreCase);

    public async Task<MaintenanceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var observedAt = DateTimeOffset.Now;

        var layers = ReadProxyLayers();
        var (systemEnabled, systemServer, systemPort, autoConfigUrl) = ReadSystemProxy(notes);
        var (winHttpProxy, winHttpPort) = await ReadWinHttpAsync(notes, cancellationToken).ConfigureAwait(false);

        var machine = await ReadMachineAsync(notes, cancellationToken).ConfigureAwait(false);
        var interfaces = BuildInterfaces(machine, notes);
        var ghosts = machine.Ghosts
            .Where(device => Regex.IsMatch(device.Name,
                "Remote NDIS|RNDIS|Internet Sharing Device|Wintun|TAP-Windows|Meta Tunnel|Mihomo|Clash|Sing|TUN",
                RegexOptions.IgnoreCase))
            .ToArray();

        var candidatePorts = layers.Select(layer => layer.Port)
            .Append(systemPort)
            .Append(winHttpPort)
            .Where(port => port > 0 && port <= 65535)
            .Distinct()
            .OrderBy(port => port)
            .ToArray();

        var listening = new List<int>();
        foreach (var port in candidatePorts)
        {
            if (await IsTcpOpenAsync(port, 500, cancellationToken).ConfigureAwait(false)) listening.Add(port);
        }

        int? working = null;
        foreach (var port in listening.OrderByDescending(port => port == 7890).ThenBy(port => port))
        {
            if (await IsWorkingProxyAsync(port, cancellationToken).ConfigureAwait(false))
            {
                working = port;
                break;
            }
        }

        // Records are immutable; build a new list carrying the measured flag.
        var measuredLayers = layers.Select(layer => layer with
        {
            PortListening = layer.Port > 0 && listening.Contains(layer.Port),
        }).ToArray();

        var active = PickActiveEgress(machine.BestRouteIndex, interfaces);
        if (active == 0) notes.Add("没有找到有 Internet 的物理出口：禁止调整任何接口度量，避免把唯一链路也调高。");
        if (!string.IsNullOrWhiteSpace(autoConfigUrl))
            notes.Add("PAC/AutoConfigURL 存在：" + autoConfigUrl + "（会覆盖 ProxyServer，本工具不擅自清空）");

        return new MaintenanceSnapshot(
            observedAt,
            IsElevated(),
            active,
            interfaces.FirstOrDefault(item => item.Index == active)?.Alias ?? "(未找到)",
            working,
            listening,
            measuredLayers,
            systemEnabled,
            systemServer,
            systemPort,
            winHttpProxy,
            winHttpPort,
            interfaces,
            ghosts,
            notes);
    }

    private static IReadOnlyList<ProxyLayerSnapshot> ReadProxyLayers()
    {
        var result = new List<ProxyLayerSnapshot>();
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var name in ProxyVariableNames)
            {
                var value = Environment.GetEnvironmentVariable(name, scope);
                if (string.IsNullOrWhiteSpace(value)) continue;
                result.Add(new ProxyLayerSnapshot(scope.ToString(), name, value, ParsePort(value), false));
            }
        }
        return result;
    }

    private static (bool Enabled, string Server, int Port, string AutoConfigUrl) ReadSystemProxy(List<string> notes)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                notes.Add("HKCU Internet Settings 读取失败（键不存在）。");
                return (false, "", 0, "");
            }
            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1;
            var server = key.GetValue("ProxyServer", "")?.ToString() ?? "";
            var pac = key.GetValue("AutoConfigURL", "")?.ToString() ?? "";
            return (enabled, server, ParsePort(server), pac);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            notes.Add("系统代理读取失败：" + exception.GetType().Name);
            return (false, "", 0, "");
        }
    }

    private async Task<(bool IsProxy, int Port)> ReadWinHttpAsync(List<string> notes, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("netsh", new[] { "winhttp", "show", "proxy" }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            notes.Add("WinHTTP 读取失败：" + result.StandardError);
            return (false, 0);
        }
        var match = Regex.Match(result.StandardOutput, @"(?:[\w\.\-]+):(\d{2,5})");
        return match.Success ? (true, int.Parse(match.Groups[1].Value)) : (false, 0);
    }

    private async Task<MachineFacts> ReadMachineAsync(List<string> notes, CancellationToken cancellationToken)
    {
        const string script = @"
$ErrorActionPreference='SilentlyContinue'
$ifaces=@(Get-NetIPInterface -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Alias';e={$_.InterfaceAlias}},@{n='State';e={$_.ConnectionState.ToString()}},@{n='Metric';e={[int]$_.InterfaceMetric}},@{n='Auto';e={$_.AutomaticMetric.ToString()}},@{n='Dhcp';e={$_.Dhcp.ToString()}})
$routes=@(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.ifIndex}},@{n='NextHop';e={$_.NextHop}},@{n='Metric';e={[int]$_.RouteMetric}})
$profiles=@(Get-NetConnectionProfile | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Connectivity';e={$_.IPv4Connectivity.ToString()}})
$addrs=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Address';e={$_.IPAddress}})
$dns=@(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Servers';e={@($_.ServerAddresses)}})
$ghosts=@(Get-PnpDevice -Class Net | Where-Object {$_.Status -ne 'OK'} | Select-Object @{n='Id';e={$_.InstanceId}},@{n='Name';e={$_.FriendlyName}},@{n='Status';e={$_.Status.ToString()}})
$best=0; try { $best=[int](Find-NetRoute -RemoteIPAddress '8.8.8.8' | Select-Object -First 1).InterfaceIndex } catch {}
[pscustomobject]@{Interfaces=$ifaces;Routes=$routes;Profiles=$profiles;Addrs=$addrs;Dns=$dns;Ghosts=$ghosts;BestRouteIndex=$best} | ConvertTo-Json -Compress -Depth 8
";
        var result = await _runner.RunJsonAsync(script, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            notes.Add("网卡/路由/DNS/PnP 采集失败：" + (result.StandardError is { Length: > 0 } e ? e : "无输出"));
            return MachineFacts.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return MachineFacts.Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            notes.Add("采集结果解析失败：" + exception.Message);
            return MachineFacts.Empty;
        }
    }

    private static IReadOnlyList<InterfaceSnapshot> BuildInterfaces(MachineFacts facts, List<string> notes)
    {
        if (facts.Interfaces.Count == 0) return Array.Empty<InterfaceSnapshot>();

        var result = new List<InterfaceSnapshot>();
        foreach (var iface in facts.Interfaces)
        {
            if (!IsPhysical(iface.Alias)) continue;
            var route = facts.Routes.FirstOrDefault(item => item.Index == iface.Index);
            var connectivity = facts.Profiles.FirstOrDefault(item => item.Index == iface.Index)?.Connectivity ?? "Unknown";
            var addresses = facts.Addrs.Where(item => item.Index == iface.Index)
                .Select(item => item.Address)
                .Where(address => !address.StartsWith("127.", StringComparison.Ordinal)
                    && !address.StartsWith("169.254.", StringComparison.Ordinal))
                .ToArray();
            var dns = facts.Dns.FirstOrDefault(item => item.Index == iface.Index)?.Servers ?? Array.Empty<string>();
            result.Add(new InterfaceSnapshot(
                iface.Index, iface.Alias, route is not null, route?.NextHop ?? "",
                iface.Metric, iface.Auto, iface.Dhcp, connectivity, addresses, dns));
        }
        if (facts.Routes.Count == 0) notes.Add("没有读到 IPv4 默认路由。");
        if (facts.Routes.Count > 1)
            notes.Add("检测到 " + facts.Routes.Count + " 条 IPv4 默认路由并存：换网后旧接口可能没退场。");
        return result;
    }

    private static int PickActiveEgress(int bestRouteIndex, IReadOnlyList<InterfaceSnapshot> interfaces)
    {
        var withInternet = interfaces.Where(item => item.Connectivity == "Internet").ToArray();
        if (withInternet.Length > 0)
        {
            var best = withInternet.FirstOrDefault(item => item.Index == bestRouteIndex);
            return best?.Index ?? withInternet[0].Index;
        }
        var withRoute = interfaces.Where(item => item.HasDefaultRoute).OrderBy(item => item.InterfaceMetric).ToArray();
        return withRoute.Length > 0 ? withRoute[0].Index : 0;
    }

    private static async Task<bool> IsTcpOpenAsync(int port, int timeoutMs, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            using var registration = cancellationToken.Register(() => client.Close());
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            var completed = await Task.WhenAny(connect, Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
            return completed == connect && connect.IsCompletedSuccessfully;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> IsWorkingProxyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy("http://127.0.0.1:" + port),
                UseProxy = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("http://www.baidu.com/", cancellationToken).ConfigureAwait(false);
            // Any HTTP response means something speaking HTTP is listening; a dead port throws.
            return (int)response.StatusCode > 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public static int ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = Regex.Match(value, @":(\d{2,5})/?\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port <= 65535 ? port : 0;
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is SecurityException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Structured view of one PowerShell read. Empty means "read failed", see Notes.</summary>
    internal sealed class MachineFacts
    {
        public static readonly MachineFacts Empty = new()
        {
            Interfaces = Array.Empty<InterfaceFact>(),
            Routes = Array.Empty<RouteFact>(),
            Profiles = Array.Empty<ProfileFact>(),
            Addrs = Array.Empty<AddressFact>(),
            Dns = Array.Empty<DnsFact>(),
            Ghosts = Array.Empty<GhostDeviceSnapshot>(),
        };

        public IReadOnlyList<InterfaceFact> Interfaces { get; init; } = Array.Empty<InterfaceFact>();
        public IReadOnlyList<RouteFact> Routes { get; init; } = Array.Empty<RouteFact>();
        public IReadOnlyList<ProfileFact> Profiles { get; init; } = Array.Empty<ProfileFact>();
        public IReadOnlyList<AddressFact> Addrs { get; init; } = Array.Empty<AddressFact>();
        public IReadOnlyList<DnsFact> Dns { get; init; } = Array.Empty<DnsFact>();
        public IReadOnlyList<GhostDeviceSnapshot> Ghosts { get; init; } = Array.Empty<GhostDeviceSnapshot>();
        public int BestRouteIndex { get; init; }

        public static MachineFacts Parse(JsonElement root)
        {
            static IEnumerable<JsonElement> Items(JsonElement parent, string name)
                => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray()
                    : parent.TryGetProperty(name, out var single) && single.ValueKind == JsonValueKind.Object
                        ? new[] { single }
                        : Enumerable.Empty<JsonElement>();

            static string Str(JsonElement item, string name)
                => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? "" : "";

            static int Int(JsonElement item, string name)
                => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetInt32() : 0;

            static IReadOnlyList<string> Strings(JsonElement item, string name)
            {
                if (!item.TryGetProperty(name, out var value)) return Array.Empty<string>();
                return value.ValueKind switch
                {
                    JsonValueKind.Array => value.EnumerateArray()
                        .Where(element => element.ValueKind == JsonValueKind.String)
                        .Select(element => element.GetString() ?? "").ToArray(),
                    JsonValueKind.String => new[] { value.GetString() ?? "" },
                    _ => Array.Empty<string>(),
                };
            }

            return new MachineFacts
            {
                Interfaces = Items(root,"Interfaces").Select(item => new InterfaceFact(
                    Int(item, "Index"), Str(item, "Alias"), Str(item, "State"),
                    Int(item, "Metric"), Str(item, "Auto"), Str(item, "Dhcp"))).ToArray(),
                Routes = Items(root,"Routes").Select(item => new RouteFact(
                    Int(item, "Index"), Str(item, "NextHop"))).ToArray(),
                Profiles = Items(root,"Profiles").Select(item => new ProfileFact(
                    Int(item, "Index"), Str(item, "Connectivity"))).ToArray(),
                Addrs = Items(root,"Addrs").Select(item => new AddressFact(
                    Int(item, "Index"), Str(item, "Address"))).ToArray(),
                Dns = Items(root,"Dns").Select(item => new DnsFact(
                    Int(item, "Index"), Strings(item, "Servers"))).ToArray(),
                Ghosts = Items(root,"Ghosts").Select(item => new GhostDeviceSnapshot(
                    Str(item, "Id"), Str(item, "Name"), Str(item, "Status"))).ToArray(),
                BestRouteIndex = root.TryGetProperty("BestRouteIndex", out var best)
                    && best.ValueKind == JsonValueKind.Number ? best.GetInt32() : 0,
            };
        }
    }

    internal sealed record InterfaceFact(int Index, string Alias, string State, int Metric, string Auto, string Dhcp);
    internal sealed record RouteFact(int Index, string NextHop);
    internal sealed record ProfileFact(int Index, string Connectivity);
    internal sealed record AddressFact(int Index, string Address);
    internal sealed record DnsFact(int Index, IReadOnlyList<string> Servers);
}
