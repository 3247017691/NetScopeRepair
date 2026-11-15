using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters;

/// <summary>Read-only file boundary. It opens one already-known file and never creates or writes one.</summary>
public sealed class SystemConfigFileSource : IConfigFileSource
{
    private readonly Func<string, Stream> _openRead;
    private readonly Func<Stream, DateTimeOffset> _readModifiedAt;

    public SystemConfigFileSource() : this(
        path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, options: FileOptions.SequentialScan),
        stream => new DateTimeOffset(File.GetLastWriteTimeUtc(((FileStream)stream).SafeFileHandle), TimeSpan.Zero))
    {
    }

    /// <summary>Read-only stream seams allow bounded-read and file-change tests without user files.</summary>
    public SystemConfigFileSource(Func<string, Stream> openRead, Func<Stream, DateTimeOffset> readModifiedAt)
    {
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _readModifiedAt = readModifiedAt ?? throw new ArgumentNullException(nameof(readModifiedAt));
    }

    public ConfigFileReadResult Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ConfigFileReadResult.Unreadable("empty_path");
        }

        try
        {
            // Open once: size, bytes and mtime all refer to this handle, not separate path lookups.
            // On Windows FileShare.Read also prevents replacement or writes during this read.
            using var stream = _openRead(path);
            if (!stream.CanRead || !stream.CanSeek)
            {
                return ConfigFileReadResult.Unreadable("unsupported_file_stream");
            }

            var initialLength = stream.Length;
            if (initialLength < 0 || stream.Position != 0)
            {
                return ConfigFileReadResult.Unreadable("invalid_file_stream");
            }
            if (initialLength > ConfigEvidence.MaxBytes)
            {
                return ConfigFileReadResult.Unreadable("file_too_large");
            }

            var modified = _readModifiedAt(stream);
            using var content = new MemoryStream((int)initialLength);
            var buffer = new byte[8192];
            while (true)
            {
                // One extra byte detects growth even if Length was stale or the file changed
                // after opening. Never retain or return an oversized file's partial prefix.
                var remaining = ConfigEvidence.MaxBytes - (int)content.Length;
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1));
                if (read == 0)
                {
                    break;
                }
                if (read > remaining)
                {
                    return ConfigFileReadResult.Unreadable("file_too_large");
                }
                content.Write(buffer, 0, read);
            }

            var finalLength = stream.Length;
            if (finalLength > ConfigEvidence.MaxBytes)
            {
                return ConfigFileReadResult.Unreadable("file_too_large");
            }
            if (finalLength != initialLength || content.Length != initialLength
                || _readModifiedAt(stream) != modified)
            {
                return ConfigFileReadResult.Unreadable("file_changed_during_read");
            }
            return ConfigFileReadResult.Found(content.ToArray(), modified);
        }
        catch (UnauthorizedAccessException)
        {
            return ConfigFileReadResult.Unreadable("access_denied");
        }
        catch (SecurityException)
        {
            return ConfigFileReadResult.Unreadable("access_denied");
        }
        catch (PathTooLongException)
        {
            return ConfigFileReadResult.Unreadable("path_too_long");
        }
        catch (DirectoryNotFoundException)
        {
            return ConfigFileReadResult.NotFound();
        }
        catch (FileNotFoundException)
        {
            return ConfigFileReadResult.NotFound();
        }
        catch (IOException)
        {
            return ConfigFileReadResult.Unreadable("io_error");
        }
        catch (NotSupportedException)
        {
            return ConfigFileReadResult.Unreadable("unsupported_path");
        }
        catch (ArgumentException)
        {
            return ConfigFileReadResult.Unreadable("invalid_path");
        }
    }
}

/// <summary>Enumerates process identity read-only. It never starts, stops or signals a process.</summary>
public sealed class SystemProcessSource : IProcessSource
{
    public Collected<IReadOnlyList<ProcessObservation>> ReadProcesses()
    {
        var items = new List<ProcessObservation>();
        var errors = new List<string>();
        // Parent links are read from one read-only toolhelp snapshot. A null map means the parent
        // field is unknown, not that the process has no parent.
        var parents = NativeProcessParents.TryRead();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        items.Add(Describe(process, parents));
                    }
                    catch (Exception exception) when (IsExpected(exception))
                    {
                        errors.Add($"一个进程的身份不可读（{exception.GetType().Name}）。");
                    }
                }
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Collected<IReadOnlyList<ProcessObservation>>.Unavailable(
                StatusFor(exception),
                "进程列表不可读。",
                new[] { exception.GetType().Name });
        }

        return errors.Count == 0
            ? Collected<IReadOnlyList<ProcessObservation>>.Ok(items)
            : Collected<IReadOnlyList<ProcessObservation>>.Partial(items, "部分进程身份不可读。", errors);
    }

    private static ProcessObservation Describe(
        Process process,
        IReadOnlyDictionary<int, int>? parents)
    {
        var id = process.Id;
        var unreadable = new Dictionary<string, string>(StringComparer.Ordinal);

        string? name = null;
        try
        {
            name = process.ProcessName;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            unreadable[ObservationFieldNames.Name] = ReasonFor(exception);
        }

        string? path = null;
        try
        {
            path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                unreadable[ObservationFieldNames.ExecutablePath] = "not_reported";
                path = null;
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            unreadable[ObservationFieldNames.ExecutablePath] = ReasonFor(exception);
        }

        DateTimeOffset? startTime = null;
        try
        {
            startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            unreadable[ObservationFieldNames.StartTime] = ReasonFor(exception);
        }

        string? version = null;
        if (path is null)
        {
            unreadable[ObservationFieldNames.Version] = "executable_path_unreadable";
        }
        else
        {
            try
            {
                version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                unreadable[ObservationFieldNames.Version] = ReasonFor(exception);
            }
        }

        int? parent = null;
        if (parents is null)
        {
            unreadable[ObservationFieldNames.ParentProcessId] = "parent_table_unavailable";
        }
        else if (parents.TryGetValue(id, out var parentId))
        {
            // 0/4 are the idle/system roots; they are read facts, not an unreadable field.
            parent = parentId is 0 or 4 ? null : parentId;
        }
        else
        {
            unreadable[ObservationFieldNames.ParentProcessId] = "process_not_in_parent_table";
        }

        return new ProcessObservation
        {
            ProcessId = id,
            Name = name,
            ExecutablePath = path,
            Version = version,
            StartTime = startTime,
            ParentProcessId = parent,
            UnreadableFields = unreadable,
        };
    }

    /// <summary>Short fixed code; never includes an exception message or a path.</summary>
    private static string ReasonFor(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => "access_denied",
        System.ComponentModel.Win32Exception win32 when win32.NativeErrorCode == 5 => "access_denied",
        InvalidOperationException => "process_exited",
        NotSupportedException => "not_supported",
        IOException => "io_error",
        ArgumentException => "invalid_argument",
        _ => "unreadable",
    };

    private static bool IsExpected(Exception exception) => exception is Win32Exception
        or InvalidOperationException
        or NotSupportedException
        or UnauthorizedAccessException
        or SecurityException
        or IOException
        or ArgumentException;

    private static EventStatus StatusFor(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => EventStatus.AccessDenied,
        NotSupportedException => EventStatus.Unsupported,
        _ => EventStatus.Failed,
    };
}

/// <summary>Reads PID -> parent PID from one read-only toolhelp snapshot. It never opens a process.</summary>
internal static class NativeProcessParents
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint InvalidHandleValue = 0xFFFFFFFF;

    public static IReadOnlyDictionary<int, int>? TryRead()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(unchecked((int)InvalidHandleValue)))
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return null;
            }

            var parents = new Dictionary<int, int>();
            do
            {
                parents[(int)entry.Th32ProcessId] = (int)entry.Th32ParentProcessId;
            }
            while (Process32NextW(snapshot, ref entry));

            return parents;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or MarshalDirectiveException
            or SEHException)
        {
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessId;
        public IntPtr Th32DefaultHeapId;
        public uint Th32ModuleId;
        public uint CntThreads;
        public uint Th32ParentProcessId;
        public int PcPriClassBase;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Enumerates TCP listening endpoints read-only. Owner PID and a best-effort owner instance time
/// are filled only where the local IPv4 owner table could be read; every other endpoint is still
/// reported with the exact reason its owner stayed unknown. It never connects to a listener.
/// </summary>
public sealed class SystemListenerSource : IListenerSource
{
    public Collected<IReadOnlyList<ListenerObservation>> ReadListeners()
    {
        try
        {
            var owners = NativeTcpOwners.TryRead();
            // The same port can appear once per address in the API projection while the owner table
            // stores one wildcard row (for example 0.0.0.0:7890). A unique per-port fallback keeps
            // the owner fact without inventing a different socket.
            var ownersByPort = owners?
                .GroupBy(pair => pair.Key.Port)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pair => pair.Value).DistinctBy(owner => owner.ProcessId).ToArray());
            var items = new List<ListenerObservation>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unsupportedFamily = 0;
            var missingOwnerRow = 0;
            var portFallback = 0;

            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                var family = endpoint.AddressFamily switch
                {
                    AddressFamily.InterNetwork => "ipv4",
                    AddressFamily.InterNetworkV6 => "ipv6",
                    _ => "unknown",
                };

                var host = endpoint.Address.ToString();
                if (!seen.Add($"{family}:{host}:{endpoint.Port}"))
                {
                    // A duplicate projection of the same address:port is not a second listener.
                    continue;
                }

                int? owner = null;
                DateTimeOffset? ownerInstance = null;
                string state;
                string? reason = null;

                if (owners is null)
                {
                    state = "unreadable";
                    reason = "owner_table_unavailable";
                }
                else if (family == "ipv4")
                {
                    if (owners.TryGetValue((host, endpoint.Port), out var found))
                    {
                        owner = found.ProcessId;
                        ownerInstance = found.CreatedAt;
                        state = "read";
                    }
                    else if (ownersByPort is not null
                        && ownersByPort.TryGetValue(endpoint.Port, out var candidates)
                        && candidates.Length == 1)
                    {
                        owner = candidates[0].ProcessId;
                        ownerInstance = candidates[0].CreatedAt;
                        state = "read";
                        reason = "address_mismatch_port_match";
                        portFallback++;
                    }
                    else
                    {
                        // The listener may have closed between the two reads; the endpoint fact is
                        // still reported, but the owner is not invented.
                        state = "unreadable";
                        reason = "owner_row_not_found";
                        missingOwnerRow++;
                    }
                }
                else if (family == "ipv6")
                {
                    state = "unsupported_family";
                    reason = "ipv6_owner_table_not_supported";
                    unsupportedFamily++;
                }
                else
                {
                    state = "unreadable";
                    reason = "unknown_address_family";
                }

                items.Add(new ListenerObservation
                {
                    Protocol = "tcp",
                    Host = host,
                    Port = endpoint.Port,
                    AddressFamily = family,
                    OwnerProcessId = owner,
                    OwnerReadState = state,
                    OwnerReadReason = reason,
                    OwnerInstanceStartTime = ownerInstance,
                });
            }

            var errors = new List<string>();
            if (owners is null)
            {
                errors.Add("监听端点所有者 PID 表不可读。");
            }

            if (unsupportedFamily > 0)
            {
                errors.Add($"{unsupportedFamily} 个 IPv6 监听端点的所有者 PID 未读取（当前仅支持 IPv4 所有者表）。");
            }

            if (missingOwnerRow > 0)
            {
                errors.Add($"{missingOwnerRow} 个 IPv4 监听端点未在所有者表中定位（可能是采样竞态）。");
            }

            if (portFallback > 0)
            {
                errors.Add($"{portFallback} 个监听端点的所有者通过唯一端口匹配得出（地址表示不同）。");
            }

            return errors.Count == 0
                ? Collected<IReadOnlyList<ListenerObservation>>.Ok(items)
                : Collected<IReadOnlyList<ListenerObservation>>.Partial(
                    items,
                    "监听端点已读取，但部分所有者归属未知。",
                    errors);
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException or SecurityException or UnauthorizedAccessException)
        {
            var status = exception switch
            {
                PlatformNotSupportedException => EventStatus.Unsupported,
                SecurityException or UnauthorizedAccessException => EventStatus.AccessDenied,
                _ => EventStatus.Failed,
            };
            return Collected<IReadOnlyList<ListenerObservation>>.Unavailable(
                status,
                "监听端点列表不可读。",
                new[] { exception.GetType().Name });
        }
    }
}

/// <summary>
/// Reads the local IPv4 TCP owner listener table through a read-only Windows API. The owner-module
/// listener table is tried first because it also carries the endpoint creation time; the classic
/// owner-PID table is the fallback, and then the instance time stays unknown.
/// </summary>
internal static class NativeTcpOwners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpTableOwnerModuleListener = 6;
    private const int OwnerModuleRowSize = 160;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    internal sealed record Owner(int ProcessId, DateTimeOffset? CreatedAt);

    public static IReadOnlyDictionary<(string Host, int Port), Owner>? TryRead() =>
        TryReadOwnerModule() ?? TryReadOwnerPid();

    private static IReadOnlyDictionary<(string Host, int Port), Owner>? TryReadOwnerModule()
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerModuleListener, 0);
        if (result != ErrorInsufficientBuffer && result != NoError)
        {
            return null;
        }

        if (size <= 0 || size > 64 * 1024 * 1024)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerModuleListener, 0);
            if (result != NoError)
            {
                return null;
            }

            var count = Marshal.ReadInt32(buffer);
            // The owner-module row starts with a LARGE_INTEGER, so some Windows versions align the
            // row array to 8 bytes (header 8) while others keep it at 4. Both layouts are detected
            // from the returned buffer size instead of being assumed.
            var tableStart = size >= 8 + (long)count * OwnerModuleRowSize ? 8 : 4;
            if (count <= 0 || count > (size - tableStart) / OwnerModuleRowSize)
            {
                return new Dictionary<(string, int), Owner>();
            }

            var rows = IntPtr.Add(buffer, tableStart);
            var table = new Dictionary<(string Host, int Port), Owner>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerModule>(IntPtr.Add(rows, index * OwnerModuleRowSize));
                var address = new IPAddress(row.LocalAddr).ToString();
                var port = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                table[(address, port)] = new Owner((int)row.OwningPid, FromFileTime(row.CreateTimestamp));
            }

            return table;
        }
        catch (Exception)
        {
            // Any layout/access surprise falls back to the classic owner-PID table.
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyDictionary<(string Host, int Port), Owner>? TryReadOwnerPid()
    {
        try
        {
            var size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (result != ErrorInsufficientBuffer && result != NoError)
            {
                return null;
            }

            if (size <= 0 || size > 64 * 1024 * 1024)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result != NoError)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                if (rowSize <= 0 || count <= 0 || count > size / rowSize)
                {
                    return new Dictionary<(string, int), Owner>();
                }

                var rows = IntPtr.Add(buffer, sizeof(int));
                var table = new Dictionary<(string Host, int Port), Owner>();
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rows, index * rowSize));
                    var address = new IPAddress(row.LocalAddr).ToString();
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                    table[(address, port)] = new Owner((int)row.OwningPid, null);
                }

                return table;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or MarshalDirectiveException
            or SEHException
            or ArgumentException)
        {
            return null;
        }
    }

    private static DateTimeOffset? FromFileTime(long value)
    {
        if (value <= 0)
        {
            return null;
        }

        try
        {
            var time = DateTimeOffset.FromFileTime(value);
            // A listener cannot have been created in the future or before Windows existed.
            return time > DateTimeOffset.UtcNow.AddDays(1) ? null : time;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerModule
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
        public long CreateTimestamp;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ulong[] OwningModuleInfo;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}

/// <summary>Enumerates network adapters read-only. It never enables, disables or changes an adapter.</summary>
public sealed class SystemNetworkAdapterSource : INetworkAdapterSource
{
    public Collected<IReadOnlyList<NetworkAdapterObservation>> ReadAdapters()
    {
        var items = new List<NetworkAdapterObservation>();
        var errors = new List<string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    items.Add(Describe(nic));
                }
                catch (Exception exception) when (IsExpected(exception))
                {
                    errors.Add($"一个网络适配器不可读（{exception.GetType().Name}）。");
                }
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Collected<IReadOnlyList<NetworkAdapterObservation>>.Unavailable(
                StatusFor(exception),
                "网络适配器列表不可读。",
                new[] { exception.GetType().Name });
        }

        return errors.Count == 0
            ? Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(items)
            : Collected<IReadOnlyList<NetworkAdapterObservation>>.Partial(items, "部分网络适配器不可读。", errors);
    }

    private static NetworkAdapterObservation Describe(NetworkInterface nic)
    {
        var unreadable = new Dictionary<string, string>(StringComparer.Ordinal);

        string? identity = string.IsNullOrWhiteSpace(nic.Id) ? null : nic.Id.Trim();
        if (identity is null)
        {
            unreadable[ObservationFieldNames.InterfaceGuid] = "not_reported";
        }

        var properties = nic.GetIPProperties();
        int? index = null;
        try
        {
            index = properties.GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
        }

        if (index is null)
        {
            try
            {
                index = properties.GetIPv6Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
            }
        }

        if (index is null)
        {
            unreadable[ObservationFieldNames.InterfaceIndex] = "interface_index_unavailable";
        }

        long? luid = null;
        if (index is int knownIndex)
        {
            try
            {
                if (NativeInterfaceIdentity.TryIndexToLuid((uint)knownIndex, out var value))
                {
                    luid = value;
                }
                else
                {
                    unreadable[ObservationFieldNames.InterfaceLuid] = "convert_failed";
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                unreadable[ObservationFieldNames.InterfaceLuid] = "not_supported";
            }
        }
        else
        {
            unreadable[ObservationFieldNames.InterfaceLuid] = "interface_index_unavailable";
        }

        var unicast = properties.UnicastAddresses;
        static string Format(UnicastIPAddressInformation address) => $"{address.Address}/{address.PrefixLength}";
        var addresses = unicast.Select(Format).ToArray();
        var ipv4 = unicast
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(Format)
            .ToArray();
        var ipv6 = unicast
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(Format)
            .ToArray();

        return new NetworkAdapterObservation
        {
            Name = nic.Name,
            Description = nic.Description,
            Kind = KindOf(nic.NetworkInterfaceType),
            OperationalStatus = nic.OperationalStatus.ToString().ToLowerInvariant(),
            InterfaceId = identity,
            InterfaceIndex = index,
            Luid = luid,
            Addresses = addresses,
            Ipv4Addresses = ipv4,
            Ipv6Addresses = ipv6,
            UnreadableFields = unreadable,
        };
    }

    /// <summary>Read-only conversion of an interface index to its LUID; the index itself is not identity.</summary>
    private static class NativeInterfaceIdentity
    {
        public static bool TryIndexToLuid(uint index, out long luid)
        {
            var result = ConvertInterfaceIndexToLuid(index, out var value);
            // Preserve all 64 bits; NET_LUID is not a signed number.
            luid = unchecked((long)value);
            return result == 0 && value != 0;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);
    }

    internal static string KindOf(NetworkInterfaceType type) => type switch
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

    private static bool IsExpected(Exception exception) => exception is NetworkInformationException
        or PlatformNotSupportedException
        or SecurityException
        or UnauthorizedAccessException;

    private static EventStatus StatusFor(Exception exception) => exception switch
    {
        PlatformNotSupportedException => EventStatus.Unsupported,
        SecurityException or UnauthorizedAccessException => EventStatus.AccessDenied,
        _ => EventStatus.Failed,
    };
}

/// <summary>
/// Reads the five proxy layers without any value: variable names only, never a proxy URL, PAC body
/// or credential. It never writes an environment variable or registry value.
/// </summary>
public sealed class SystemProxyLayerSource : IProxyLayerSource
{
    private static readonly string[] Variables =
    [
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "all_proxy", "no_proxy",
    ];

    public Collected<IReadOnlyList<ProxyLayerObservation>> ReadLayers()
    {
        var layers = new List<ProxyLayerObservation>
        {
            ReadEnvironment(EnvironmentVariableTarget.Process, "tool_process_environment", "process_environment"),
            ReadEnvironment(EnvironmentVariableTarget.User, "user_environment", @"registry:HKCU\Environment"),
            ReadEnvironment(EnvironmentVariableTarget.Machine, "machine_environment", @"registry:HKLM\Session Manager\Environment"),
            ReadWinInet(),
            new()
            {
                Layer = "winhttp",
                Provenance = "unsupported",
                Enabled = null,
                Detail = "M1 不读取 WinHTTP 代理存储；不假设 WinHTTP 值。",
            },
        };

        return Collected<IReadOnlyList<ProxyLayerObservation>>.Ok(layers);
    }

    private static ProxyLayerObservation ReadEnvironment(
        EnvironmentVariableTarget target,
        string layer,
        string provenance)
    {
        try
        {
            var names = Variables
                .Where(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, target)))
                .ToArray();
            return new ProxyLayerObservation
            {
                Layer = layer,
                Provenance = provenance,
                Enabled = names.Length > 0,
                Detail = names.Length == 0
                    ? "未设置代理变量（值不采集）。"
                    : $"已设置 {names.Length} 个代理变量：{string.Join(", ", names)}（值不采集）。",
                VariableNames = names,
            };
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
        {
            return new ProxyLayerObservation
            {
                Layer = layer,
                Provenance = provenance,
                Enabled = null,
                Detail = "该层不可读（值不采集）。",
            };
        }
    }

    private static ProxyLayerObservation ReadWinInet()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                return new ProxyLayerObservation
                {
                    Layer = "wininet",
                    Provenance = @"registry:HKCU\...\Internet Settings",
                    Enabled = null,
                    Detail = "WinINET 设置键未找到。",
                };
            }

            var enable = key.GetValue("ProxyEnable") is int value ? value : (int?)null;
            var hasServer = key.GetValue("ProxyServer") is string server && !string.IsNullOrWhiteSpace(server);
            var hasPac = key.GetValue("AutoConfigURL") is string pac && !string.IsNullOrWhiteSpace(pac);
            return new ProxyLayerObservation
            {
                Layer = "wininet",
                Provenance = @"registry:HKCU\...\Internet Settings",
                Enabled = enable is not null and not 0,
                Detail = "系统代理存储值已读取（值不采集）。"
                    + (hasServer ? " 存在代理服务器字段。" : string.Empty)
                    + (hasPac ? " 存在 PAC 地址字段。" : string.Empty),
            };
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new ProxyLayerObservation
            {
                Layer = "wininet",
                Provenance = @"registry:HKCU\...\Internet Settings",
                Enabled = null,
                Detail = "WinINET 配置不可读。",
            };
        }
    }
}
