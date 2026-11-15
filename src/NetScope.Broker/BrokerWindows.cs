using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NetScope.Broker;

/// <summary>
/// Authenticode check. The delivery default has no trusted publisher thumbprint, so any signature
/// is reported as <see cref="BrokerSignatureState.ValidUntrustedPublisher"/> and real elevation
/// stays closed. Validation never downloads revocation data.
/// </summary>
public sealed class BrokerAuthenticodeVerifier
{
    private readonly HashSet<string> _trustedThumbprints;

    public BrokerAuthenticodeVerifier(IEnumerable<string>? trustedThumbprints = null)
    {
        _trustedThumbprints = (trustedThumbprints ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public BrokerSignatureVerification Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new BrokerSignatureVerification { State = BrokerSignatureState.Unreadable, Thumbprints = Array.Empty<string>() };

        try
        {
            var trusted = WinVerifyTrustFile(path);
            var thumbprints = SignerThumbprints(path);
            if (!trusted)
            {
                return new BrokerSignatureVerification
                {
                    State = thumbprints.Count == 0 ? BrokerSignatureState.Unsigned : BrokerSignatureState.InvalidSignature,
                    Thumbprints = thumbprints,
                };
            }

            var state = thumbprints.Any(_trustedThumbprints.Contains)
                ? BrokerSignatureState.ValidTrustedPublisher
                : BrokerSignatureState.ValidUntrustedPublisher;
            return new BrokerSignatureVerification { State = state, Thumbprints = thumbprints };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            return new BrokerSignatureVerification { State = BrokerSignatureState.Unreadable, Thumbprints = Array.Empty<string>() };
        }
    }

    private static IReadOnlyList<string> SignerThumbprints(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only built-in way to read the PE signer.
            var certificate = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return new[] { Normalize(certificate.GetCertHashString()) };
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Normalize(string thumbprint) => thumbprint.Replace(" ", string.Empty).ToUpperInvariant();

    private static bool WinVerifyTrustFile(string path)
    {
        var fileInfo = new BrokerNativeMethods.WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<BrokerNativeMethods.WinTrustFileInfo>(),
            FilePath = path,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero,
        };

        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BrokerNativeMethods.WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new BrokerNativeMethods.WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<BrokerNativeMethods.WinTrustData>(),
                UiChoice = BrokerNativeMethods.WtdUiNone,
                RevocationChecks = BrokerNativeMethods.WtdRevokeNone,
                UnionChoice = BrokerNativeMethods.WtdChoiceFile,
                File = fileInfoPointer,
                StateAction = BrokerNativeMethods.WtdStateActionVerify,
                ProviderFlags = BrokerNativeMethods.WtdCacheOnlyUrlRetrieval
                    | BrokerNativeMethods.WtdRevocationCheckNone
                    | BrokerNativeMethods.WtdDisableMd2Md4,
            };

            var trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BrokerNativeMethods.WinTrustData>());
            try
            {
                Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
                var action = BrokerNativeMethods.WinTrustActionGenericVerifyV2;
                var result = BrokerNativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);

                // The API writes its state handle into the structure; read it back before closing.
                var closeData = Marshal.PtrToStructure<BrokerNativeMethods.WinTrustData>(trustDataPointer);
                closeData.StateAction = BrokerNativeMethods.WtdStateActionClose;
                Marshal.StructureToPtr(closeData, trustDataPointer, fDeleteOld: true);
                _ = BrokerNativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(trustDataPointer);
            }
        }
        finally
        {
            Marshal.DestroyStructure<BrokerNativeMethods.WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }
}

public sealed record BrokerSignatureVerification
{
    public required BrokerSignatureState State { get; init; }

    public required IReadOnlyList<string> Thumbprints { get; init; }
}

/// <summary>
/// Reads the real deployment facts: executable path, install-directory ACL, Authenticode state and
/// elevation. An inspection failure is reported as untrusted (fail closed), never as trusted.
/// </summary>
public sealed class WindowsBrokerDeploymentVerifier : IBrokerDeploymentVerifier
{
    private readonly BrokerAuthenticodeVerifier _signature;
    private readonly Func<bool> _isElevated;

    public WindowsBrokerDeploymentVerifier(
        BrokerAuthenticodeVerifier? signature = null, Func<bool>? isElevated = null)
    {
        _signature = signature ?? new BrokerAuthenticodeVerifier();
        _isElevated = isElevated ?? WindowsTokenInspector.IsCurrentProcessElevated;
    }

    public BrokerDeploymentFacts Inspect(bool developmentIsolation)
    {
        var executable = Environment.ProcessPath ?? string.Empty;
        var directory = string.IsNullOrWhiteSpace(executable)
            ? string.Empty
            : Path.GetDirectoryName(executable) ?? string.Empty;
        var signature = _signature.Inspect(executable);

        return new BrokerDeploymentFacts
        {
            BrokerExecutablePath = executable,
            TrustedInstallDirectory = directory,
            InstallDirectoryUserWritable = InstallDirectoryWritable(directory),
            Signature = signature.State,
            SignerThumbprints = signature.Thumbprints,
            RunningElevated = _isElevated(),
            DevelopmentIsolation = developmentIsolation,
        };
    }

    /// <summary>
    /// Conservative ACL check: if the current user, or a group the user belongs to, has write,
    /// modify, full control or ACL-edit rights on the install directory it is not a boundary.
    /// Any inspection failure counts as writable (closed).
    /// </summary>
    public static bool InstallDirectoryWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return true;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null) return true;

            var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid) continue;
                if (!AppliesTo(sid, user, identity.Groups)) continue;

                const FileSystemRights dangerous = FileSystemRights.Write
                    | FileSystemRights.Modify
                    | FileSystemRights.FullControl
                    | FileSystemRights.ChangePermissions
                    | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & dangerous) != 0) return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
            or InvalidOperationException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    private static bool AppliesTo(
        SecurityIdentifier sid, SecurityIdentifier user, IdentityReferenceCollection? groups)
    {
        if (sid.Equals(user)) return true;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (sid.Equals(everyone)) return true;
        if (groups is null) return false;
        foreach (var group in groups)
        {
            if (group is SecurityIdentifier groupSid && groupSid.Equals(sid)) return true;
        }

        return false;
    }
}

/// <summary>Token elevation probe through the Windows token, not through a spoofable environment flag.</summary>
public static class WindowsTokenInspector
{
    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var process = Process.GetCurrentProcess();
            if (!BrokerNativeMethods.OpenProcessToken(
                    process.Handle, BrokerNativeMethods.TokenQuery, out var token)) return false;
            using (token)
            {
                var elevation = new BrokerNativeMethods.TokenElevation { TokenIsElevated = 0 };
                var size = Marshal.SizeOf<BrokerNativeMethods.TokenElevation>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!BrokerNativeMethods.GetTokenInformation(
                            token, BrokerNativeMethods.TokenElevationClass, buffer, (uint)size, out _))
                        return false;
                    elevation = Marshal.PtrToStructure<BrokerNativeMethods.TokenElevation>(buffer);
                    return elevation.TokenIsElevated != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class WindowsBrokerElevationProbe : IBrokerElevationProbe
{
    public BrokerElevationState Probe() => WindowsTokenInspector.IsCurrentProcessElevated()
        ? BrokerElevationState.Elevated
        : BrokerElevationState.NotElevated;
}

/// <summary>
/// Random ACL-protected local pipe. Only the initiating user, administrators and SYSTEM get
/// access; the name contains fresh random material and the first-instance flag prevents a
/// pre-created pipe from squatting the name. A remote client (whose process id cannot be read
/// locally) is refused by the identity policy.
/// </summary>
public sealed class WindowsNamedPipeBrokerListener : IBrokerListener
{
    private readonly NamedPipeServerStream _server;
    private readonly WindowsPeerEvidenceCollector _collector;

    private WindowsNamedPipeBrokerListener(
        NamedPipeServerStream server, string pipeName, WindowsPeerEvidenceCollector collector)
    {
        _server = server;
        PipeName = pipeName;
        _collector = collector;
    }

    public string PipeName { get; }

    /// <summary>True because <see cref="PipeOptions.FirstPipeInstance"/> rejects a pre-existing name.</summary>
    public bool FirstPipeInstance => true;

    /// <summary>32 hex characters of fresh random material in the name.</summary>
    public bool PipeNameRandom => IsRandomPipeName(PipeName);

    public static WindowsNamedPipeBrokerListener Create(
        IReadOnlyList<string> trustedSystemDirectories,
        BrokerAuthenticodeVerifier? signature = null,
        Func<bool>? isElevated = null,
        int bufferSize = BrokerProtocol.MaxFrameBytes)
    {
        var pipeName = NewRandomPipeName();
        var security = NewPipeSecurity();
        var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.WriteThrough,
            inBufferSize: bufferSize,
            outBufferSize: bufferSize,
            pipeSecurity: security);

        var collector = new WindowsPeerEvidenceCollector(
            trustedSystemDirectories,
            signature ?? new BrokerAuthenticodeVerifier(),
            isElevated);
        return new WindowsNamedPipeBrokerListener(server, pipeName, collector);
    }

    public static string NewRandomPipeName() =>
        "NetScopeRepair.Broker." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsRandomPipeName(string? pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || !pipeName.StartsWith("NetScopeRepair.Broker.", StringComparison.Ordinal))
            return false;
        var suffix = pipeName["NetScopeRepair.Broker.".Length..];
        return BrokerIdentifiers.IsHex(suffix, 32);
    }

    /// <summary>The ACL: initiating user read/write, SYSTEM and Administrators full control, nobody else.</summary>
    public static PipeSecurity NewPipeSecurity(SecurityIdentifier? initiatingUser = null)
    {
        var user = initiatingUser;
        if (user is null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User ?? throw new InvalidOperationException("The current identity has no user SID.");
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public async Task<IBrokerConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        await _server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        var evidence = _collector.CollectForPipe(_server, PipeNameRandom, FirstPipeInstance);
        return new WindowsBrokerConnection(_server, evidence, _collector.Lease);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

internal sealed class WindowsBrokerConnection : IBrokerConnection
{
    private readonly NamedPipeServerStream _stream;

    public WindowsBrokerConnection(NamedPipeServerStream stream, BrokerPeerEvidence evidence, IBrokerPeerLease lease)
    {
        _stream = stream;
        PeerEvidence = evidence;
        Lease = lease;
        Stream = stream;
    }

    public Stream Stream { get; }

    public BrokerPeerEvidence PeerEvidence { get; }

    public IBrokerPeerLease Lease { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Lease.Dispose();
        }
    }
}

internal sealed class WindowsProcessLease : IBrokerPeerLease
{
    private readonly SafeProcessHandle _handle;
    private bool _disposed;

    public WindowsProcessLease(SafeProcessHandle handle, int processId)
    {
        _handle = handle;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public bool IsHeld => !_disposed && !_handle.IsInvalid && !_handle.IsClosed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

/// <summary>
/// Collects the kernel-reported peer facts from a connected pipe. Any missing fact is reported as
/// missing; the policy then refuses instead of assuming a trust level.
/// </summary>
public sealed class WindowsPeerEvidenceCollector
{
    private readonly IReadOnlyList<string> _trustedSystemDirectories;
    private readonly BrokerAuthenticodeVerifier _signature;
    private readonly Func<bool> _isElevated;

    public WindowsPeerEvidenceCollector(
        IReadOnlyList<string> trustedSystemDirectories,
        BrokerAuthenticodeVerifier? signature = null,
        Func<bool>? isElevated = null)
    {
        _trustedSystemDirectories = trustedSystemDirectories ?? Array.Empty<string>();
        _signature = signature ?? new BrokerAuthenticodeVerifier();
        _isElevated = isElevated ?? WindowsTokenInspector.IsCurrentProcessElevated;
    }

    /// <summary>The held peer process handle, valid until the connection is disposed.</summary>
    public IBrokerPeerLease Lease { get; private set; } = DisposedBrokerPeerLease.Instance;

    public BrokerPeerEvidence CollectForPipe(NamedPipeServerStream pipe, bool pipeNameRandom, bool firstPipeInstance)
    {
        try
        {
            return CollectCore(pipe, pipeNameRandom, firstPipeInstance);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Identity collection failures are refusals, never a pass-through.
            Lease = DisposedBrokerPeerLease.Instance;
            return BaseEvidence(
                remote: false, pipeNameRandom, firstPipeInstance, 0, string.Empty);
        }
    }

    private BrokerPeerEvidence CollectCore(NamedPipeServerStream pipe, bool pipeNameRandom, bool firstPipeInstance)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        var remote = !BrokerNativeMethods.GetNamedPipeClientProcessId(handle, out var clientProcessIdRaw);
        var clientProcessId = unchecked((int)clientProcessIdRaw);
        var sessionKnown = BrokerNativeMethods.GetNamedPipeClientSessionId(handle, out var clientSessionId);
        var brokerSessionId = CurrentSessionId();
        var initiatingUser = CurrentUserSid();

        if (remote)
        {
            Lease = DisposedBrokerPeerLease.Instance;
            return BaseEvidence(remote: true, pipeNameRandom, firstPipeInstance, brokerSessionId, initiatingUser);
        }

        var lease = OpenPeer(clientProcessId);
        Lease = lease;
        var peerKnown = ReadProcessFacts(clientProcessId, out var creation, out var imagePath, out _);
        int? parentId = null;
        DateTimeOffset? parentCreationTime = null;
        string? parentImage = null;
        if (peerKnown)
        {
            using var peerHandle = BrokerNativeMethods.OpenProcess(
                BrokerNativeMethods.ProcessQueryLimitedInformation, false, clientProcessId);
            if (!peerHandle.IsInvalid) parentId = ReadParentProcessId(peerHandle);
        }

        if (parentId is { } parentProcessId)
        {
            if (ReadProcessFacts(parentProcessId, out var parentCreation, out var parentPath, out _))
            {
                parentCreationTime = parentCreation;
                parentImage = parentPath;
            }
        }

        var parentSignature = parentImage is null ? BrokerSignatureState.NotChecked : _signature.Inspect(parentImage).State;

        return new BrokerPeerEvidence
        {
            RemoteClient = false,
            FirstPipeInstance = firstPipeInstance,
            PipeNameRandom = pipeNameRandom,
            PeerProcessId = clientProcessId,
            PeerProcessCreationUtc = creation,
            PeerImagePath = imagePath,
            PeerSignature = imagePath is null ? BrokerSignatureState.NotChecked : _signature.Inspect(imagePath).State,
            PeerUserSid = ReadPeerSid(clientProcessId),
            PeerLogonSessionId = ReadPeerLogonSession(clientProcessId),
            PeerSessionId = sessionKnown ? (int)clientSessionId : null,
            ClaimedProcessId = null,
            ClaimedProcessCreationUtc = null,
            ParentProcessId = parentId,
            ParentProcessCreationUtc = parentCreationTime,
            ParentImagePath = parentImage,
            ParentSignature = parentSignature,
            BrokerSessionId = brokerSessionId,
            InitiatingUserSid = initiatingUser,
            TrustedInstallDirectory = TrustedDirectory(),
            TrustedSystemDirectories = _trustedSystemDirectories,
            ProcessHandleHeld = lease.IsHeld,
        };
    }

    private BrokerPeerEvidence BaseEvidence(
        bool remote, bool pipeNameRandom, bool firstPipeInstance, int brokerSessionId, string initiatingUser) => new()
    {
        RemoteClient = remote,
        FirstPipeInstance = firstPipeInstance,
        PipeNameRandom = pipeNameRandom,
        PeerProcessId = 0,
        PeerProcessCreationUtc = null,
        PeerImagePath = null,
        PeerSignature = BrokerSignatureState.NotChecked,
        PeerUserSid = null,
        PeerLogonSessionId = null,
        PeerSessionId = null,
        ClaimedProcessId = null,
        ClaimedProcessCreationUtc = null,
        ParentProcessId = null,
        ParentProcessCreationUtc = null,
        ParentImagePath = null,
        ParentSignature = BrokerSignatureState.NotChecked,
        BrokerSessionId = brokerSessionId,
        InitiatingUserSid = initiatingUser,
        TrustedInstallDirectory = TrustedDirectory(),
        TrustedSystemDirectories = _trustedSystemDirectories,
        ProcessHandleHeld = false,
    };

    private static IBrokerPeerLease OpenPeer(int processId)
    {
        try
        {
            var handle = BrokerNativeMethods.OpenProcess(
                BrokerNativeMethods.ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return DisposedBrokerPeerLease.Instance;
            }

            return new WindowsProcessLease(handle, processId);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return DisposedBrokerPeerLease.Instance;
        }
    }

    private static bool ReadProcessFacts(
        int processId, out DateTimeOffset? creation, out string? imagePath, out int? parentId)
    {
        creation = null;
        imagePath = null;
        parentId = null;
        try
        {
            using var handle = BrokerNativeMethods.OpenProcess(
                BrokerNativeMethods.ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid) return false;

            creation = ReadCreationTime(processId);
            imagePath = ReadImagePath(processId);
            parentId = ReadParentProcessId(handle);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static DateTimeOffset? ReadCreationTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadImagePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static int? ReadParentProcessId(SafeProcessHandle handle)
    {
        var size = Marshal.SizeOf<BrokerNativeMethods.ProcessBasicInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = BrokerNativeMethods.NtQueryInformationProcess(
                handle, 0, buffer, (uint)size, out _);
            if (status != 0) return null;
            var information = Marshal.PtrToStructure<BrokerNativeMethods.ProcessBasicInformation>(buffer);
            var parent = unchecked((int)information.InheritedFromUniqueProcessId.ToInt64());
            return parent > 0 ? parent : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadPeerSid(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var token = process.Handle;
            if (!BrokerNativeMethods.OpenProcessToken(token, BrokerNativeMethods.TokenQuery, out var tokenHandle))
                return null;
            using (tokenHandle)
            {
                return ReadSid(tokenHandle);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadSid(SafeAccessTokenHandle tokenHandle)
    {
        BrokerNativeMethods.GetTokenInformation(tokenHandle, BrokerNativeMethods.TokenUserClass, IntPtr.Zero, 0, out var needed);
        if (needed == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!BrokerNativeMethods.GetTokenInformation(
                    tokenHandle, BrokerNativeMethods.TokenUserClass, buffer, needed, out _))
                return null;
            var sidPointer = Marshal.ReadIntPtr(buffer);
            return BrokerNativeMethods.SidToText(sidPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadPeerLogonSession(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!BrokerNativeMethods.OpenProcessToken(process.Handle, BrokerNativeMethods.TokenQuery, out var tokenHandle))
                return null;
            using (tokenHandle)
            {
                var size = Marshal.SizeOf<BrokerNativeMethods.TokenStatistics>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!BrokerNativeMethods.GetTokenInformation(
                            tokenHandle, BrokerNativeMethods.TokenStatisticsClass, buffer, (uint)size, out _))
                        return null;
                    var statistics = Marshal.PtrToStructure<BrokerNativeMethods.TokenStatistics>(buffer);
                    return "luid:" + statistics.AuthenticationId.ToString();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int CurrentSessionId()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or NotSupportedException)
        {
            return 0;
        }
    }

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? string.Empty;
    }

    private static string TrustedDirectory()
    {
        var executable = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executable) ? string.Empty : Path.GetDirectoryName(executable) ?? string.Empty;
    }
}

/// <summary>Minimal native surface: pipe peer identity, process/token facts and Authenticode.</summary>
internal static class BrokerNativeMethods
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const int TokenUserClass = 1;
    public const int TokenStatisticsClass = 10;
    public const int TokenElevationClass = 20;

    public const int WtdUiNone = 2;
    public const int WtdRevokeNone = 0;
    public const int WtdChoiceFile = 1;
    public const int WtdStateActionVerify = 1;
    public const int WtdStateActionClose = 2;
    public const int WtdCacheOnlyUrlRetrieval = 0x00001000;
    public const int WtdRevocationCheckNone = 0x00000010;
    public const int WtdDisableMd2Md4 = 0x00002000;

    public static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WinTrustFileInfo
    {
        public uint StructSize;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public int UiChoice;
        public int RevocationChecks;
        public int UnionChoice;
        public IntPtr File;
        public int StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public int ProviderFlags;
        public int UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public long Value => ((long)HighPart << 32) | LowPart;

        public override string ToString() => "0x" + Value.ToString("x16");
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenStatistics
    {
        public Luid AuthenticationId;
        public Luid TokenId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint clientSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle, int processInformationClass, IntPtr processInformation, uint processInformationLength, out uint returnLength);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern uint WinVerifyTrust(IntPtr parentWindow, ref Guid actionId, IntPtr trustData);

    /// <summary>Converts a SID pointer to its string form without trusting a fixed buffer size.</summary>
    public static string? SidToText(IntPtr sid)
    {
        if (sid == IntPtr.Zero) return null;
        try
        {
            var revision = Marshal.ReadByte(sid, 0);
            var subAuthorityCount = Marshal.ReadByte(sid, 1);
            var length = 8 + (4 * subAuthorityCount);
            var bytes = new byte[length];
            Marshal.Copy(sid, bytes, 0, length);
            if (revision != 1 || subAuthorityCount == 0) return null;
            return new SecurityIdentifier(bytes, 0).Value;
        }
        catch (Exception exception) when (exception is ArgumentException or SEHException)
        {
            return null;
        }
    }
}



