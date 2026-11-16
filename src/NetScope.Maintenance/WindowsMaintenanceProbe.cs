using System.Net;
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

    private readonly ICommandRunner _runner;

    public WindowsMaintenanceProbe(ICommandRunner? runner = null)
        => _runner = runner ?? new WindowsCommandRunner();

    /// <summary>Alias shapes of known proxy-client TUN adapters (same list as the verified desktop script).</summary>
    public const string TunAliasPattern = "Mihomo|Nice|农夫|Meta Tunnel|FlClash|Clash|Wintun|TUN|Verge";

    public static bool IsTun(string alias, IReadOnlyList<string> addresses, string adapterDescription)
        => !Regex.IsMatch(alias, "VMware|vEthernet|VirtualBox|Hyper-V|Loopback|Bluetooth|蓝牙", RegexOptions.IgnoreCase)
            && (Regex.IsMatch(alias, TunAliasPattern, RegexOptions.IgnoreCase)
                || addresses.Any(a => a.StartsWith("198.18.", StringComparison.Ordinal) || a.StartsWith("198.19.", StringComparison.Ordinal))
                || Regex.IsMatch(adapterDescription, "Wintun|Tunnel", RegexOptions.IgnoreCase));

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

        var tuns = machine.Interfaces
            .Where(iface => iface.State == "Connected")
            .Where(iface => IsTun(iface.Alias,
                machine.Addrs.Where(a => a.Index == iface.Index).Select(a => a.Address).ToArray(),
                machine.Adapters.FirstOrDefault(a => a.Index == iface.Index)?.Desc ?? ""))
            .Select(iface => new TunInterfaceSnapshot(iface.Index, iface.Alias, iface.Metric, iface.Auto,
                iface.IgnoreDefaultRoutes, iface.NlMtu,
                machine.Addrs.Where(a => a.Index == iface.Index).Select(a => a.Address).ToArray()))
            .ToArray();
        var owners = machine.Owners.Where(o => candidatePorts.Contains(o.Port)).ToArray();
        if (machine.SourceAddress4.Length > 0)
            notes.Add($"内核出站源地址 8.8.8.8 → {machine.SourceAddress4}（if={machine.BestRouteIndex}）");
        if (machine.SourceAddress5.Length > 0)
            notes.Add($"内核出站源地址 223.5.5.5 → {machine.SourceAddress5}（if={machine.BestRouteIndex}）");

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

        ReportPinnedClientConfig(notes);

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
            notes,
            tuns,
            owners,
            machine.BestRouteIndex);
    }

    private static void ReportPinnedClientConfig(List<string> notes)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var root in new[] { "io.github.clash-verge-rev.clash-verge-rev", "Clash Verge" })
            {
                var dir = Path.Combine(appData, root);
                if (!Directory.Exists(dir)) continue;
                var files = Directory.EnumerateFiles(dir, "*.y*ml", SearchOption.AllDirectories).Take(40);
                foreach (var file in files)
                {
                    var hits = File.ReadLines(file)
                        .Select((text, i) => (text, i))
                        .Where(x => Regex.IsMatch(x.text, @"^\s*(interface-name|device)\s*:"))
                        .Take(5).ToArray();
                    foreach (var (text, i) in hits)
                        notes.Add($"客户端配置写死网卡：{file}:{i + 1} {text.Trim()}（只报告，不改文件）");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notes.Add("客户端配置扫描失败：" + exception.Message);
        }
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
$ifaces=@(Get-NetIPInterface -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Alias';e={$_.InterfaceAlias}},@{n='State';e={$_.ConnectionState.ToString()}},@{n='Metric';e={[int]$_.InterfaceMetric}},@{n='Auto';e={$_.AutomaticMetric.ToString()}},@{n='Dhcp';e={$_.Dhcp.ToString()}},@{n='Ignore';e={$_.IgnoreDefaultRoutes.ToString()}},@{n='Mtu';e={[int]$_.NlMtu}})
$routes=@(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.ifIndex}},@{n='NextHop';e={$_.NextHop}},@{n='Metric';e={[int]$_.RouteMetric}})
$profiles=@(Get-NetConnectionProfile | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Connectivity';e={$_.IPv4Connectivity.ToString()}})
$addrs=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object @{n='Index';e={[int]$_.InterfaceIndex}},@{n='Address';e={$_.IPAddress}})
$rawAdapters=@(Get-NetAdapter -ErrorAction SilentlyContinue)
$dns=@($rawAdapters | ForEach-Object { $ns=[string](Get-ItemProperty -Path ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\'+$_.InterfaceGuid) -Name 'NameServer' -ErrorAction SilentlyContinue).NameServer; [pscustomobject]@{Index=[int]$_.ifIndex;NameServer=$ns;Servers=if([string]::IsNullOrWhiteSpace($ns)){@()}else{@($ns -split '[,\s;]+' | Where-Object {$_})}} })
$ghosts=@(Get-PnpDevice -Class Net | Where-Object {$_.Status -ne 'OK'} | Select-Object @{n='Id';e={$_.InstanceId}},@{n='Name';e={$_.FriendlyName}},@{n='Status';e={$_.Status.ToString()}})
$best=0; try { $best=[int](Find-NetRoute -RemoteIPAddress '8.8.8.8' | Select-Object -First 1).InterfaceIndex } catch {}
$adapters=@($rawAdapters | Select-Object @{n='Index';e={[int]$_.ifIndex}},@{n='Desc';e={$_.InterfaceDescription}})
$owners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $p=Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; [pscustomobject]@{Port=[int]$_.LocalPort;Pid=[int]$_.OwningProcess;Name=if($p){$p.ProcessName}else{''}} })
$src4=''; $src5=''
try { $src4=[string](Find-NetRoute -RemoteIPAddress '8.8.8.8' | Select-Object -First 1).IPAddress } catch {}
try { $src5=[string](Find-NetRoute -RemoteIPAddress '223.5.5.5' | Select-Object -First 1).IPAddress } catch {}
[pscustomobject]@{Interfaces=$ifaces;Routes=$routes;Profiles=$profiles;Addrs=$addrs;Dns=$dns;Ghosts=$ghosts;BestRouteIndex=$best;Adapters=$adapters;Owners=$owners;Src4=$src4;Src5=$src5} | ConvertTo-Json -Compress -Depth 8
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

    public static IReadOnlyList<InterfaceSnapshot> BuildInterfaces(MachineFacts facts, List<string> notes)
    {
        if (facts.Interfaces.Count == 0) return Array.Empty<InterfaceSnapshot>();

        var result = new List<InterfaceSnapshot>();
        foreach (var iface in facts.Interfaces)
        {
            if (!IsPhysical(iface.Alias)) continue;
            var addresses = facts.Addrs.Where(item => item.Index == iface.Index)
                .Select(item => item.Address)
                .Where(address => !address.StartsWith("127.", StringComparison.Ordinal)
                    && !address.StartsWith("169.254.", StringComparison.Ordinal))
                .ToArray();
            // Spec §4.2: TUN facts live only in TunInterfaces and never become stale-egress candidates.
            if (IsTun(iface.Alias, addresses,
                    facts.Adapters.FirstOrDefault(item => item.Index == iface.Index)?.Desc ?? "")) continue;
            var route = facts.Routes.FirstOrDefault(item => item.Index == iface.Index);
            var connectivity = facts.Profiles.FirstOrDefault(item => item.Index == iface.Index)?.Connectivity ?? "Unknown";
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

    public static int PickActiveEgress(int bestRouteIndex, IReadOnlyList<InterfaceSnapshot> interfaces)
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

    private static Task<bool> IsWorkingProxyAsync(int port, CancellationToken ct) => ProxyPortTester.IsWorkingAsync(port, null, ct);

    public static int ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = Regex.Match(value, @":(\d{2,5})/?\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port <= 65535 ? port : 0;
    }

    public static IReadOnlyList<string> ParseStaticDnsServers(string? nameServer)
        => string.IsNullOrWhiteSpace(nameServer)
            ? Array.Empty<string>()
            : nameServer.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
    public sealed class MachineFacts
    {
        public static readonly MachineFacts Empty = new()
        {
            Interfaces = Array.Empty<InterfaceFact>(),
            Routes = Array.Empty<RouteFact>(),
            Profiles = Array.Empty<ProfileFact>(),
            Addrs = Array.Empty<AddressFact>(),
            Dns = Array.Empty<DnsFact>(),
            Ghosts = Array.Empty<GhostDeviceSnapshot>(),
            Adapters = Array.Empty<AdapterFact>(),
            Owners = Array.Empty<PortOwnerSnapshot>(),
        };

        public IReadOnlyList<InterfaceFact> Interfaces { get; init; } = Array.Empty<InterfaceFact>();
        public IReadOnlyList<RouteFact> Routes { get; init; } = Array.Empty<RouteFact>();
        public IReadOnlyList<ProfileFact> Profiles { get; init; } = Array.Empty<ProfileFact>();
        public IReadOnlyList<AddressFact> Addrs { get; init; } = Array.Empty<AddressFact>();
        public IReadOnlyList<DnsFact> Dns { get; init; } = Array.Empty<DnsFact>();
        public IReadOnlyList<GhostDeviceSnapshot> Ghosts { get; init; } = Array.Empty<GhostDeviceSnapshot>();
        public IReadOnlyList<AdapterFact> Adapters { get; init; } = Array.Empty<AdapterFact>();
        public IReadOnlyList<PortOwnerSnapshot> Owners { get; init; } = Array.Empty<PortOwnerSnapshot>();
        public string SourceAddress4 { get; init; } = "";
        public string SourceAddress5 { get; init; } = "";
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
                    Int(item, "Metric"), Str(item, "Auto"), Str(item, "Dhcp"),
                    Str(item, "Ignore"), Int(item, "Mtu"))).ToArray(),
                Routes = Items(root,"Routes").Select(item => new RouteFact(
                    Int(item, "Index"), Str(item, "NextHop"))).ToArray(),
                Profiles = Items(root,"Profiles").Select(item => new ProfileFact(
                    Int(item, "Index"), Str(item, "Connectivity"))).ToArray(),
                Addrs = Items(root,"Addrs").Select(item => new AddressFact(
                    Int(item, "Index"), Str(item, "Address"))).ToArray(),
                Dns = Items(root,"Dns").Select(item => new DnsFact(
                    Int(item, "Index"),
                    item.TryGetProperty("NameServer", out var ns)
                        ? ParseStaticDnsServers(ns.ValueKind == JsonValueKind.String ? ns.GetString() : null)
                        : Strings(item, "Servers"))).ToArray(),
                Ghosts = Items(root,"Ghosts").Select(item => new GhostDeviceSnapshot(
                    Str(item, "Id"), Str(item, "Name"), Str(item, "Status"))).ToArray(),
                Adapters = Items(root,"Adapters").Select(item => new AdapterFact(
                    Int(item, "Index"), Str(item, "Desc"))).ToArray(),
                Owners = Items(root,"Owners").Select(item => new PortOwnerSnapshot(
                    Int(item, "Port"), Int(item, "Pid"), Str(item, "Name"))).ToArray(),
                SourceAddress4 = Str(root, "Src4"),
                SourceAddress5 = Str(root, "Src5"),
                BestRouteIndex = root.TryGetProperty("BestRouteIndex", out var best)
                    && best.ValueKind == JsonValueKind.Number ? best.GetInt32() : 0,
            };
        }
    }

    public sealed record InterfaceFact(int Index, string Alias, string State, int Metric, string Auto, string Dhcp, string IgnoreDefaultRoutes, int NlMtu);
    public sealed record RouteFact(int Index, string NextHop);
    public sealed record ProfileFact(int Index, string Connectivity);
    public sealed record AddressFact(int Index, string Address);
    public sealed record DnsFact(int Index, IReadOnlyList<string> Servers);
    public sealed record AdapterFact(int Index, string Desc);
}
