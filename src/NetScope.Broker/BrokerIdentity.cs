namespace NetScope.Broker;

/// <summary>
/// Result of one signature check. "Valid but untrusted publisher" is a distinct state from an
/// invalid signature: both are refusals, but only the first could ever be fixed by a trust policy.
/// </summary>
public enum BrokerSignatureState
{
    /// <summary>Nothing was checked (for example a remote peer whose process could not be opened).</summary>
    NotChecked,

    /// <summary>A valid Authenticode signature whose signer is in the configured trust allowlist.</summary>
    ValidTrustedPublisher,

    /// <summary>A valid Authenticode signature, but the signer is not trusted by this deployment.</summary>
    ValidUntrustedPublisher,

    /// <summary>The file is signed but the signature does not validate.</summary>
    InvalidSignature,

    /// <summary>The file carries no Authenticode signature.</summary>
    Unsigned,

    /// <summary>The file could not be opened or read.</summary>
    Unreadable,
}

/// <summary>
/// Everything the broker verified about the connecting peer. It is assembled only from kernel
/// facts (pipe client process id, session, process creation time, token SID/logon session, image
/// path, signature) plus the values the client claimed in its hello, which are compared against
/// the kernel facts rather than trusted.
/// </summary>
public sealed record BrokerPeerEvidence
{
    public required bool RemoteClient { get; init; }

    /// <summary>True only when this broker created the first instance of its random pipe name.</summary>
    public required bool FirstPipeInstance { get; init; }

    /// <summary>True only when the pipe name contains fresh random material, not a fixed/readable name.</summary>
    public required bool PipeNameRandom { get; init; }

    public required int PeerProcessId { get; init; }

    public required DateTimeOffset? PeerProcessCreationUtc { get; init; }

    public required string? PeerImagePath { get; init; }

    public required BrokerSignatureState PeerSignature { get; init; }

    public required string? PeerUserSid { get; init; }

    public required string? PeerLogonSessionId { get; init; }

    public required int? PeerSessionId { get; init; }

    /// <summary>Process id the peer claimed in its hello; must match the kernel-reported pid.</summary>
    public required int? ClaimedProcessId { get; init; }

    /// <summary>Creation time the peer claimed; must match the kernel-reported creation time (PID reuse).</summary>
    public required DateTimeOffset? ClaimedProcessCreationUtc { get; init; }

    public required int? ParentProcessId { get; init; }

    public required DateTimeOffset? ParentProcessCreationUtc { get; init; }

    public required string? ParentImagePath { get; init; }

    public required BrokerSignatureState ParentSignature { get; init; }

    /// <summary>The broker's own session; the peer must run in the same session.</summary>
    public required int BrokerSessionId { get; init; }

    /// <summary>The user who started the broker (the only user allowed to connect).</summary>
    public required string InitiatingUserSid { get; init; }

    /// <summary>The exact directory the client executable must live in.</summary>
    public required string TrustedInstallDirectory { get; init; }

    /// <summary>Trusted OS directories allowed for the parent process (for example System32).</summary>
    public required IReadOnlyList<string> TrustedSystemDirectories { get; init; }

    /// <summary>True only while the broker holds a process handle for the peer until transaction end.</summary>
    public required bool ProcessHandleHeld { get; init; }
}

public sealed record BrokerPeerIdentityDecision
{
    public required bool Allowed { get; init; }

    public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }

    public static BrokerPeerIdentityDecision Allow() => new() { Allowed = true, Refusals = Array.Empty<BrokerRefusal>() };

    public static BrokerPeerIdentityDecision Deny(IEnumerable<BrokerRefusal> refusals) => new()
    {
        Allowed = false,
        Refusals = refusals.ToArray(),
    };
}

/// <summary>
/// Pure identity policy. It intentionally fails closed: a missing fact is a refusal, never an
/// "assume trusted". Remote clients, PID reuse, a swapped image path, an untrusted signature and
/// a spoofed parent are all refused with distinct codes.
/// </summary>
public static class BrokerPeerIdentityPolicy
{
    /// <summary>Creation times from different Windows APIs may differ slightly; reuse differs by far more.</summary>
    public static TimeSpan CreationTimeTolerance { get; } = TimeSpan.FromSeconds(1);

    public static BrokerPeerIdentityDecision Evaluate(BrokerPeerEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var refusals = new List<BrokerRefusal>();

        if (evidence.RemoteClient)
            refusals.Add(Refuse(BrokerRefusalCode.PeerRemoteClient, "The pipe client is remote; only local peers are accepted."));
        if (!evidence.FirstPipeInstance)
            refusals.Add(Refuse(BrokerRefusalCode.PipeNotFirstInstance, "The broker pipe is not the first instance of its random name."));
        if (!evidence.PipeNameRandom)
            refusals.Add(Refuse(BrokerRefusalCode.PipeNameNotRandom, "The pipe name is not backed by fresh random material."));

        if (evidence.PeerProcessId <= 0
            || evidence.PeerProcessCreationUtc is null
            || string.IsNullOrWhiteSpace(evidence.PeerImagePath)
            || string.IsNullOrWhiteSpace(evidence.PeerUserSid)
            || string.IsNullOrWhiteSpace(evidence.PeerLogonSessionId)
            || evidence.PeerSessionId is null)
        {
            refusals.Add(Refuse(BrokerRefusalCode.PeerIdentityUnknown, "The kernel did not report enough peer identity facts."));
        }

        if (evidence.ClaimedProcessId != evidence.PeerProcessId
            || evidence.ClaimedProcessId is null)
        {
            refusals.Add(Refuse(BrokerRefusalCode.PeerPidMismatch, "The claimed process id differs from the kernel-reported peer process id."));
        }

        if (!SameInstant(evidence.ClaimedProcessCreationUtc, evidence.PeerProcessCreationUtc))
        {
            refusals.Add(Refuse(BrokerRefusalCode.PeerCreationTimeMismatch, "The claimed process creation time does not match the kernel-reported process; possible pid reuse."));
        }

        if (evidence.PeerSessionId is { } peerSession && peerSession != evidence.BrokerSessionId)
            refusals.Add(Refuse(BrokerRefusalCode.PeerSessionMismatch, "The peer runs in a different Windows session."));

        if (!string.Equals(evidence.PeerUserSid, evidence.InitiatingUserSid, StringComparison.OrdinalIgnoreCase))
            refusals.Add(Refuse(BrokerRefusalCode.PeerUserMismatch, "The peer token user SID is not the initiating user."));

        if (!string.IsNullOrWhiteSpace(evidence.PeerImagePath)
            && !IsUnder(evidence.PeerImagePath, evidence.TrustedInstallDirectory))
        {
            refusals.Add(Refuse(BrokerRefusalCode.PeerPathUntrusted, "The peer executable is not inside the trusted install directory."));
        }

        if (evidence.PeerSignature != BrokerSignatureState.ValidTrustedPublisher)
            refusals.Add(Refuse(BrokerRefusalCode.PeerSignatureUntrusted, "The peer executable signature is not a trusted publisher signature."));

        if (!ParentTrusted(evidence))
            refusals.Add(Refuse(BrokerRefusalCode.PeerParentUntrusted, "The peer parent process could not be verified as a trusted local launcher."));

        if (!evidence.ProcessHandleHeld)
            refusals.Add(Refuse(BrokerRefusalCode.PeerIdentityUnknown, "The broker does not hold the peer process handle for the transaction."));

        return refusals.Count == 0
            ? BrokerPeerIdentityDecision.Allow()
            : BrokerPeerIdentityDecision.Deny(refusals);
    }

    private static bool ParentTrusted(BrokerPeerEvidence evidence)
    {
        if (evidence.ParentProcessId is not { } parentId || parentId <= 0) return false;
        if (evidence.ParentProcessCreationUtc is not { } parentCreation) return false;
        if (string.IsNullOrWhiteSpace(evidence.ParentImagePath)) return false;

        // A fake parent is a parent created after its child. The kernel creation times must show
        // the parent existed first.
        if (evidence.PeerProcessCreationUtc is { } peerCreation && parentCreation > peerCreation) return false;

        if (evidence.ParentSignature != BrokerSignatureState.ValidTrustedPublisher) return false;

        var inInstall = IsUnder(evidence.ParentImagePath, evidence.TrustedInstallDirectory);
        var inSystem = evidence.TrustedSystemDirectories.Any(directory => IsUnder(evidence.ParentImagePath, directory));
        return inInstall || inSystem;
    }

    private static bool SameInstant(DateTimeOffset? claimed, DateTimeOffset? kernel)
    {
        if (claimed is null || kernel is null) return false;
        return (claimed.Value.ToUniversalTime() - kernel.Value.ToUniversalTime()).Duration() <= CreationTimeTolerance;
    }

    /// <summary>Path containment after full normalization; a traversal cannot escape the prefix.</summary>
    public static bool IsUnder(string candidatePath, string directory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directory)) return false;
        string candidate;
        string root;
        try
        {
            candidate = Path.GetFullPath(candidatePath);
            root = Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (candidate.Length <= root.Length) return false;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static BrokerRefusal Refuse(BrokerRefusalCode code, string detail) => BrokerRefusal.Of(code, detail);
}

/// <summary>
/// A held OS handle to the verified peer process. The broker keeps it open for the whole
/// transaction so the process id cannot be reused behind its back.
/// </summary>
public interface IBrokerPeerLease : IDisposable
{
    int ProcessId { get; }

    bool IsHeld { get; }
}

public sealed class DisposedBrokerPeerLease : IBrokerPeerLease
{
    public static DisposedBrokerPeerLease Instance { get; } = new();

    public int ProcessId => 0;

    public bool IsHeld => false;

    public void Dispose()
    {
    }
}
