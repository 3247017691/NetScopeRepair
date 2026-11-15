namespace NetScope.Broker;

/// <summary>
/// Facts about the deployment the broker is running from. They are inspected, not supplied by a
/// caller on a command line: a path, an ACL and a signature cannot be "declared" trusted.
/// </summary>
public sealed record BrokerDeploymentFacts
{
    public required string BrokerExecutablePath { get; init; }

    /// <summary>The install directory whose ACL and signature policy protect the broker binary.</summary>
    public required string TrustedInstallDirectory { get; init; }

    /// <summary>True when the initiating user (or any non-admin) can modify the install directory.</summary>
    public required bool InstallDirectoryUserWritable { get; init; }

    public required BrokerSignatureState Signature { get; init; }

    /// <summary>Thumbprints of the certificates that signed the executable, when it is signed.</summary>
    public required IReadOnlyList<string> SignerThumbprints { get; init; }

    public required bool RunningElevated { get; init; }

    /// <summary>Explicitly requested development isolation (unsigned, user-writable tree).</summary>
    public required bool DevelopmentIsolation { get; init; }
}

/// <summary>
/// Trust policy. The delivery default has an empty publisher allowlist and disables development
/// isolation, so no fact combination can enable real elevation: an unsigned or user-writable
/// deployment is always closed, and a signed development build still has no trusted publisher.
/// </summary>
public sealed record BrokerTrustPolicy
{
    public required IReadOnlyList<string> TrustedPublisherThumbprints { get; init; }

    public required bool AllowDevelopmentIsolation { get; init; }

    /// <summary>The shipped policy: no trusted publisher configured, no development elevation.</summary>
    public static BrokerTrustPolicy DeliveryDefault { get; } = new()
    {
        TrustedPublisherThumbprints = Array.Empty<string>(),
        AllowDevelopmentIsolation = false,
    };

    /// <summary>Test-only policy factory. Every decision made with it is marked synthetic.</summary>
    public static BrokerTrustPolicy ForSyntheticTests(IEnumerable<string>? trustedThumbprints = null) => new()
    {
        TrustedPublisherThumbprints = trustedThumbprints?.ToArray() ?? new[] { "synthetic-test-thumbprint" },
        AllowDevelopmentIsolation = true,
    };

    public BrokerTrustDecision Evaluate(BrokerDeploymentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var refusals = new List<BrokerRefusal>();

        var thumbprints = TrustedPublisherThumbprints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var signatureTrusted = facts.Signature == BrokerSignatureState.ValidTrustedPublisher
            && facts.SignerThumbprints.Any(thumbprints.Contains);

        if (facts.Signature == BrokerSignatureState.Unsigned)
            refusals.Add(Refuse(BrokerRefusalCode.SignatureNotTrusted, "The broker executable is not Authenticode signed."));
        else if (!signatureTrusted)
            refusals.Add(Refuse(BrokerRefusalCode.SignatureNotTrusted, "The broker signature is missing, invalid or from an untrusted publisher."));

        if (!BrokerPeerIdentityPolicy.IsUnder(facts.BrokerExecutablePath, facts.TrustedInstallDirectory))
            refusals.Add(Refuse(BrokerRefusalCode.TrustedDeploymentUnavailable, "The broker does not run from the trusted install directory."));

        if (facts.InstallDirectoryUserWritable)
            refusals.Add(Refuse(BrokerRefusalCode.InstallDirectoryUserWritable, "The install directory is writable by the initiating user; it is not a production boundary."));

        if (!facts.RunningElevated)
            refusals.Add(Refuse(BrokerRefusalCode.BrokerNotElevated, "The broker process is not running with an elevated token."));

        var realElevationEnabled = signatureTrusted
            && !facts.InstallDirectoryUserWritable
            && facts.RunningElevated
            && BrokerPeerIdentityPolicy.IsUnder(facts.BrokerExecutablePath, facts.TrustedInstallDirectory);

        var developmentAllowed = facts.DevelopmentIsolation
            && AllowDevelopmentIsolation
            && !realElevationEnabled;

        if (!realElevationEnabled && !developmentAllowed)
            refusals.Add(Refuse(BrokerRefusalCode.RealElevationDisabled, "Real elevation is disabled by the delivery trust policy."));

        return new BrokerTrustDecision
        {
            RealElevationEnabled = realElevationEnabled,
            DevelopmentIsolationAllowed = developmentAllowed,
            IsSynthetic = developmentAllowed || !IsDeliveryPolicy,
            Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
        };
    }

    private bool IsDeliveryPolicy =>
        TrustedPublisherThumbprints.Count == 0 && !AllowDevelopmentIsolation;

    private static BrokerRefusal Refuse(BrokerRefusalCode code, string detail) => BrokerRefusal.Of(code, detail);
}

public sealed record BrokerTrustDecision
{
    /// <summary>True only for a signed, trusted, non-user-writable, elevated deployment.</summary>
    public required bool RealElevationEnabled { get; init; }

    /// <summary>True only for an explicit development-isolation start; never implies elevation.</summary>
    public required bool DevelopmentIsolationAllowed { get; init; }

    /// <summary>True when this decision came from a test/dev policy rather than the delivery policy.</summary>
    public required bool IsSynthetic { get; init; }

    public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }

    public bool CanServe => RealElevationEnabled || DevelopmentIsolationAllowed;
}

/// <summary>Reads the deployment facts on Windows (path, ACL, signature, elevation).</summary>
public interface IBrokerDeploymentVerifier
{
    BrokerDeploymentFacts Inspect(bool developmentIsolation);
}

/// <summary>Whether the broker process actually holds an elevated token.</summary>
public enum BrokerElevationState
{
    Unknown,
    Elevated,
    NotElevated,
}

public interface IBrokerElevationProbe
{
    BrokerElevationState Probe();
}
