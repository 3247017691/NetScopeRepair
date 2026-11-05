using System.Net;
using System.Runtime.InteropServices;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Collectors.Windows;

/// <summary>
/// Read-only IP Helper calls. Native layouts are enabled only for the verified Windows x64 ABI;
/// another architecture is an explicit observation gap, never a guessed layout.
/// </summary>
internal sealed class NativeNetworkApi(Architecture architecture) : IWindowsNetworkApi
{
    public static NativeNetworkApi Instance { get; } = new(RuntimeInformation.ProcessArchitecture);

    public const ushort AfUnspec = 0;
    public const ushort AfInet = 2;
    public const ushort AfInet6 = 23;

    /// <summary>
    /// Windows SDK MIB_IPINTERFACE_ROW, with BOOLEAN and NL_INTERFACE_OFFLOAD_ROD as single bytes.
    /// In the verified x64 ABI sizeof is 168; DisableDefaultRoutes is at byte 166.
    /// https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_ipinterface_row
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IpInterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ZoneIndices;
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    // MIB_IPFORWARD_TABLE2 and MIB_IPFORWARD_ROW2, verified for the same x64 ABI.
    private const int ForwardTableRowStart = 8;
    private const int ForwardRowSize = 104;
    private readonly record struct Sockaddr(ushort Family, string Address);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpInterfaceEntry(ref IpInterfaceRow row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr memory);

    public sealed record InterfaceMetrics(ulong Luid, uint Metric, bool AutomaticMetric, bool DisableDefaultRoutes);

    public sealed record RouteRow(
        string DestinationPrefix,
        ushort Family,
        byte PrefixLength,
        int InterfaceIndex,
        string NextHop,
        uint RouteMetric,
        uint Protocol,
        uint Origin);

    internal static bool HasVerifiedLayout(Architecture candidate) => candidate == Architecture.X64
        && Marshal.SizeOf<IpInterfaceRow>() == 168
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.InterfaceLuid)).ToInt32() == 8
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.InterfaceIndex)).ToInt32() == 16
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.UseAutomaticMetric)).ToInt32() == 44
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.Metric)).ToInt32() == 148
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.TransmitOffload)).ToInt32() == 164
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.ReceiveOffload)).ToInt32() == 165
        && Marshal.OffsetOf<IpInterfaceRow>(nameof(IpInterfaceRow.DisableDefaultRoutes)).ToInt32() == 166;

    internal static InterfaceMetrics MetricsFromRow(IpInterfaceRow row) => new(
        row.InterfaceLuid, row.Metric, row.UseAutomaticMetric != 0, row.DisableDefaultRoutes != 0);

    public Collected<InterfaceMetrics> ReadInterfaceMetrics(int interfaceIndex, ushort family)
    {
        if (!OperatingSystem.IsWindows() || !HasVerifiedLayout(architecture))
        {
            return Collected<InterfaceMetrics>.Unavailable(EventStatus.Unsupported,
                "IP Helper interface layout is verified only for Windows x64.");
        }

        if (interfaceIndex <= 0 || family is not (AfInet or AfInet6))
        {
            return Collected<InterfaceMetrics>.Unavailable(EventStatus.Failed,
                "A positive interface index and IPv4 or IPv6 family are required.");
        }

        var row = new IpInterfaceRow
        {
            Family = family,
            InterfaceIndex = (uint)interfaceIndex,
            ZoneIndices = new uint[16],
        };

        try
        {
            var error = GetIpInterfaceEntry(ref row);
            if (error != 0)
            {
                return Collected<InterfaceMetrics>.Unavailable(StatusFor(error),
                    $"GetIpInterfaceEntry failed for interface {interfaceIndex}, family {family} (native error {error}).");
            }

            if (row.Family != family || row.InterfaceIndex != (uint)interfaceIndex)
            {
                return Collected<InterfaceMetrics>.Unavailable(EventStatus.Failed,
                    "The IP Helper interface row did not match the requested interface and address family.");
            }

            return Collected<InterfaceMetrics>.Ok(MetricsFromRow(row));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Collected<InterfaceMetrics>.Unavailable(EventStatus.Unsupported,
                $"The IP Helper interface API is unavailable ({exception.GetType().Name}).");
        }
    }

    public Collected<IReadOnlyList<RouteRow>> ReadRoutes(ushort family)
    {
        if (!OperatingSystem.IsWindows() || !HasVerifiedLayout(architecture))
        {
            return Collected<IReadOnlyList<RouteRow>>.Unavailable(EventStatus.Unsupported,
                "IP Helper route layout is verified only for Windows x64.");
        }

        var table = IntPtr.Zero;
        try
        {
            var error = GetIpForwardTable2(family, out table);
            if (error != 0 || table == IntPtr.Zero)
            {
                return Collected<IReadOnlyList<RouteRow>>.Unavailable(StatusFor(error),
                    $"GetIpForwardTable2 did not return a routing table (native error {error}).");
            }

            var count = Marshal.ReadInt32(table, 0);
            if (count < 0)
            {
                return Collected<IReadOnlyList<RouteRow>>.Unavailable(EventStatus.Failed,
                    "IP Helper returned an invalid routing table count.");
            }

            var rows = new List<RouteRow>();
            var errors = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, checked(ForwardTableRowStart + (index * ForwardRowSize)));
                var interfaceIndex = Marshal.ReadInt32(row, 8);
                var prefixLength = Marshal.ReadByte(row, 40);
                var destination = ReadSockaddr(row, 12);
                var nextHop = ReadSockaddr(row, 44);

                if (destination is null || nextHop is null
                    || destination.Value.Family != nextHop.Value.Family
                    || prefixLength > (destination.Value.Family == AfInet ? 32 : 128))
                {
                    errors.Add($"Route row {index} had an unsupported address family or invalid prefix.");
                    continue;
                }

                rows.Add(new RouteRow(
                    $"{destination.Value.Address}/{prefixLength}",
                    destination.Value.Family,
                    prefixLength,
                    interfaceIndex,
                    nextHop.Value.Address,
                    (uint)Marshal.ReadInt32(row, 84),
                    (uint)Marshal.ReadInt32(row, 88),
                    (uint)Marshal.ReadInt32(row, 100)));
            }

            return errors.Count == 0
                ? Collected<IReadOnlyList<RouteRow>>.Ok(rows)
                : Collected<IReadOnlyList<RouteRow>>.Partial(rows,
                    $"{errors.Count} routing table row(s) could not be decoded.", errors);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Collected<IReadOnlyList<RouteRow>>.Unavailable(EventStatus.Unsupported,
                $"The IP Helper routing API is unavailable ({exception.GetType().Name}).");
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                FreeMibTable(table);
            }
        }
    }

    private static EventStatus StatusFor(uint error) => error switch
    {
        5 => EventStatus.AccessDenied,
        50 or 120 => EventStatus.Unsupported,
        _ => EventStatus.Failed,
    };

    public static string ProtocolName(uint protocol) => protocol switch
    {
        1 => "other",
        2 => "local",
        3 => "netmgmt",
        4 => "icmp",
        8 => "rip",
        13 => "ospf",
        14 => "bgp",
        19 => "dhcp",
        20 => "autostatic",
        _ => "unknown",
    };

    public static string OriginName(uint origin) => origin switch
    {
        0 => "manual",
        1 => "well_known",
        2 => "dhcp",
        3 => "router_advertisement",
        4 => "6to4",
        5 => "direct",
        _ => "unknown",
    };

    private static Sockaddr? ReadSockaddr(IntPtr row, int offset)
    {
        var pointer = IntPtr.Add(row, offset);
        var family = (ushort)Marshal.ReadInt16(pointer, 0);
        if (family == AfInet)
        {
            var bytes = new byte[4];
            Marshal.Copy(IntPtr.Add(pointer, 4), bytes, 0, 4);
            return new Sockaddr(family, new IPAddress(bytes).ToString());
        }

        if (family == AfInet6)
        {
            var bytes = new byte[16];
            Marshal.Copy(IntPtr.Add(pointer, 8), bytes, 0, 16);
            var scopeId = (uint)Marshal.ReadInt32(pointer, 24);
            return new Sockaddr(family, new IPAddress(bytes, scopeId).ToString());
        }

        return null;
    }
}
