using System.Net.NetworkInformation;
using System.Security;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Collectors.Windows;

// Internal read-only boundaries let regressions supply missing native rows and per-NIC errors
// without changing the host's adapters, DNS configuration, routes or environment.
internal interface IWindowsNetworkApi
{
    Collected<NativeNetworkApi.InterfaceMetrics> ReadInterfaceMetrics(int interfaceIndex, ushort family);

    Collected<IReadOnlyList<NativeNetworkApi.RouteRow>> ReadRoutes(ushort family);
}

internal static class WindowsReadFailure
{
    public static bool IsExpected(Exception exception) => exception is NetworkInformationException
        or PlatformNotSupportedException or SecurityException or UnauthorizedAccessException;

    public static EventStatus StatusFor(Exception exception) => exception switch
    {
        PlatformNotSupportedException => EventStatus.Unsupported,
        SecurityException or UnauthorizedAccessException => EventStatus.AccessDenied,
        _ => EventStatus.Failed,
    };
}

internal interface IWindowsAdapterProvider
{
    IReadOnlyList<IWindowsAdapter> GetAdapters();
}

internal interface IWindowsAdapter
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    NetworkInterfaceType Type { get; }
    OperationalStatus OperationalStatus { get; }

    AdapterInterfaceConfiguration ReadInterfaceConfiguration();
    AdapterDnsConfiguration ReadDnsConfiguration();
}

internal sealed record AdapterInterfaceConfiguration
{
    public int? Ipv4Index { get; init; }
    public int? Ipv6Index { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

internal sealed record AdapterDnsConfiguration
{
    public int? Ipv4Index { get; init; }
    public int? Ipv6Index { get; init; }
    public IReadOnlyList<string> Servers { get; init; } = [];
    public string? Suffix { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

internal sealed class SystemWindowsAdapterProvider : IWindowsAdapterProvider
{
    public IReadOnlyList<IWindowsAdapter> GetAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Select(nic => (IWindowsAdapter)new SystemWindowsAdapter(nic)).ToArray();

    private sealed class SystemWindowsAdapter(NetworkInterface nic) : IWindowsAdapter
    {
        public string Id => nic.Id;
        public string Name => nic.Name;
        public string Description => nic.Description;
        public NetworkInterfaceType Type => nic.NetworkInterfaceType;
        public OperationalStatus OperationalStatus => nic.OperationalStatus;

        public AdapterInterfaceConfiguration ReadInterfaceConfiguration()
        {
            var properties = nic.GetIPProperties();
            var errors = new List<string>();
            var ipv4 = ReadIndex(properties, NetworkInterfaceComponent.IPv4, errors);
            var ipv6 = ReadIndex(properties, NetworkInterfaceComponent.IPv6, errors);
            return new AdapterInterfaceConfiguration
            {
                Ipv4Index = ipv4,
                Ipv6Index = ipv6,
                Addresses = properties.UnicastAddresses
                    .Select(address => $"{address.Address}/{address.PrefixLength}").ToArray(),
                Errors = errors,
            };
        }

        public AdapterDnsConfiguration ReadDnsConfiguration()
        {
            var properties = nic.GetIPProperties();
            var errors = new List<string>();
            var ipv4 = ReadIndex(properties, NetworkInterfaceComponent.IPv4, errors);
            var ipv6 = ReadIndex(properties, NetworkInterfaceComponent.IPv6, errors);
            return new AdapterDnsConfiguration
            {
                Ipv4Index = ipv4,
                Ipv6Index = ipv6,
                Servers = properties.DnsAddresses.Select(address => address.ToString()).ToArray(),
                Suffix = properties.DnsSuffix,
                Errors = errors,
            };
        }

        private int? ReadIndex(
            IPInterfaceProperties properties,
            NetworkInterfaceComponent component,
            ICollection<string> errors)
        {
            try
            {
                if (!nic.Supports(component))
                {
                    return null;
                }

                var index = component == NetworkInterfaceComponent.IPv4
                    ? properties.GetIPv4Properties()?.Index
                    : properties.GetIPv6Properties()?.Index;
                if (index is not > 0)
                {
                    errors.Add($"{component} interface index was unavailable.");
                    return null;
                }
                return index;
            }
            catch (Exception exception) when (WindowsReadFailure.IsExpected(exception))
            {
                errors.Add($"{component} interface index: {exception.GetType().Name}");
                return null;
            }
        }
    }
}
