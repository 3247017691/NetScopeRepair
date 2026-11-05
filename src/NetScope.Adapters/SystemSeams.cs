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

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        items.Add(Describe(process));
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

    private static ProcessObservation Describe(Process process)
    {
        var id = process.Id;

        string? name = null;
        try
        {
            name = process.ProcessName;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
        }

        string? path = null;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
        }

        DateTimeOffset? startTime = null;
        try
        {
            startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
        }

        return new ProcessObservation
        {
            ProcessId = id,
            Name = name,
            ExecutablePath = path,
            Version = ReadVersion(path),
            StartTime = startTime,
        };
    }

    private static string? ReadVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

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

/// <summary>
/// Enumerates TCP listening endpoints read-only. Owner PID is filled only where the local IPv4
/// owner table could be read; otherwise the endpoint is still reported with owner unknown. It never
/// connects to a listener.
/// </summary>
public sealed class SystemListenerSource : IListenerSource
{
    public Collected<IReadOnlyList<ListenerObservation>> ReadListeners()
    {
        try
        {
            var owners = NativeTcpOwners.TryRead();
            var items = new List<ListenerObservation>();
            var ownerGaps = 0;

            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                int? owner = null;
                if (owners is not null
                    && endpoint.AddressFamily == AddressFamily.InterNetwork
                    && owners.TryGetValue((endpoint.Address.ToString(), endpoint.Port), out var pid))
                {
                    owner = pid;
                }

                if (owner is null && endpoint.AddressFamily != AddressFamily.InterNetwork)
                {
                    ownerGaps++;
                }

                items.Add(new ListenerObservation
                {
                    Protocol = "tcp",
                    Host = endpoint.Address.ToString(),
                    Port = endpoint.Port,
                    OwnerProcessId = owner,
                });
            }

            var errors = new List<string>();
            if (owners is null)
            {
                errors.Add("监听端点所有者 PID 表不可读。");
            }

            if (ownerGaps > 0)
            {
                errors.Add($"{ownerGaps} 个非 IPv4 监听端点的所有者 PID 未读取。");
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

/// <summary>Reads the local IPv4 TCP owner-PID listener table through a read-only Windows API.</summary>
internal static class NativeTcpOwners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    public static IReadOnlyDictionary<(string Host, int Port), int>? TryRead()
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
                if (count <= 0 || count > size / rowSize)
                {
                    return new Dictionary<(string, int), int>();
                }

                var rows = IntPtr.Add(buffer, sizeof(int));
                var table = new Dictionary<(string Host, int Port), int>();
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rows, index * rowSize));
                    var address = new IPAddress(row.LocalAddr).ToString();
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                    table[(address, port)] = (int)row.OwningPid;
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

    private static NetworkAdapterObservation Describe(NetworkInterface nic) => new()
    {
        Name = nic.Name,
        Description = nic.Description,
        Kind = KindOf(nic.NetworkInterfaceType),
        OperationalStatus = nic.OperationalStatus.ToString().ToLowerInvariant(),
        Addresses = nic.GetIPProperties().UnicastAddresses
            .Select(address => $"{address.Address}/{address.PrefixLength}")
            .ToArray(),
    };

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
