using NetScope.Broker;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Synthetic broker fixtures. They describe a would-be trusted deployment and a typed T13 plan so
/// the protocol/whitelist/budget logic can be exercised. None of them is a production capability:
/// the delivery policy has no trusted publisher, and T14 never executes a transaction.
/// </summary>
internal static class BrokerPlanFixtures
{
    public const string UserSid = "S-1-5-21-1000-2000-3000-1001";
    public const string LogonSession = "luid:0x0000000012345678";
    public const int SessionId = 7;
    public const string TrustedThumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    public const string ClientExecutableName = "NetScopeRepair.Desktop.exe";
    public const string BrokerExecutableName = "NetScopeRepair.Broker.exe";

    public static string InstallDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "NetScopeRepair-install-synthetic");

    public static string SystemDirectory { get; } = @"C:\Windows\System32";

    public static DateTimeOffset Now { get; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset PeerCreation { get; } = Now.AddMinutes(-30);

    public static BrokerPeerEvidence TrustedPeer() => new()
    {
        RemoteClient = false,
        FirstPipeInstance = true,
        PipeNameRandom = true,
        PeerProcessId = 4242,
        PeerProcessCreationUtc = PeerCreation,
        PeerImagePath = Path.Combine(InstallDirectory, ClientExecutableName),
        PeerSignature = BrokerSignatureState.ValidTrustedPublisher,
        PeerUserSid = UserSid,
        PeerLogonSessionId = LogonSession,
        PeerSessionId = SessionId,
        ClaimedProcessId = null,
        ClaimedProcessCreationUtc = null,
        ParentProcessId = 1000,
        ParentProcessCreationUtc = PeerCreation.AddMinutes(-10),
        ParentImagePath = Path.Combine(SystemDirectory, "consent.exe"),
        ParentSignature = BrokerSignatureState.ValidTrustedPublisher,
        BrokerSessionId = SessionId,
        InitiatingUserSid = UserSid,
        TrustedInstallDirectory = InstallDirectory,
        TrustedSystemDirectories = new[] { SystemDirectory },
        ProcessHandleHeld = true,
    };

    /// <summary>A signed, non-user-writable, elevated deployment; only used with a synthetic policy.</summary>
    public static BrokerDeploymentFacts TrustedDeployment() => new()
    {
        BrokerExecutablePath = Path.Combine(InstallDirectory, BrokerExecutableName),
        TrustedInstallDirectory = InstallDirectory,
        InstallDirectoryUserWritable = false,
        Signature = BrokerSignatureState.ValidTrustedPublisher,
        SignerThumbprints = new[] { TrustedThumbprint },
        RunningElevated = true,
        DevelopmentIsolation = false,
    };

    /// <summary>The real development tree: unsigned, user-writable, not elevated.</summary>
    public static BrokerDeploymentFacts DevelopmentDeployment() => new()
    {
        BrokerExecutablePath = Path.Combine(InstallDirectory, BrokerExecutableName),
        TrustedInstallDirectory = InstallDirectory,
        InstallDirectoryUserWritable = true,
        Signature = BrokerSignatureState.Unsigned,
        SignerThumbprints = Array.Empty<string>(),
        RunningElevated = false,
        DevelopmentIsolation = true,
    };

    public static PlatformCapabilityMatrix SyntheticMatrix(RepairActionKind kind = RepairActionKind.SetTunIpv4InterfaceMetric) =>
        RepairFixtures.SyntheticMatrix(RepairFixtures.VerifiedCapability(kind));

    public static (RepairPlan Plan, RepairAuthorizationBinding Authorization) ValidPlan(
        DateTimeOffset? now = null,
        RepairActionKind kind = RepairActionKind.SetTunIpv4InterfaceMetric,
        uint before = 35,
        uint desired = 20,
        DateTimeOffset? expiresAt = null,
        bool synthetic = false)
    {
        var clockNow = now ?? Now;
        var identity = RepairFixtures.Identity();
        var capability = RepairFixtures.VerifiedCapability(kind);
        var actionId = RepairActionRules.ActionIdOf(kind);
        var expires = expiresAt ?? clockNow.AddMinutes(5);

        var action = new RepairAction
        {
            ActionId = actionId,
            Kind = kind,
            Field = RepairActionRules.FieldOf(kind),
            Store = RepairStore.ActiveStore,
            Family = RepairAddressFamily.Ipv4,
            TargetFingerprint = identity.TargetFingerprint,
            Identity = identity,
            Before = new InterfaceMetricValue { RawValue = before, Kind = MetricValueKind.ManualConfigured },
            Desired = new InterfaceMetricValue { RawValue = desired, Kind = MetricValueKind.ManualConfigured },
            BeforeSource = "synthetic-t14",
            BeforeSourceVersion = "1.0.0",
            BeforeStatus = EventStatus.Ok,
            BeforeObservedAtUtc = clockNow.AddSeconds(-5),
            BeforeTtl = TimeSpan.FromSeconds(30),
            DependsOnActionIds = Array.Empty<string>(),
            EvidenceIds = new[] { "ev:t14" },
            Capability = capability,
            Eligibility = RepairActionEligibility.Allowed,
            BlockingReasons = Array.Empty<RepairRefusal>(),
            Risk = RepairRiskLevel.Low,
            RollbackCondition = "synthetic rollback condition",
            ObservationPlan = "synthetic observation plan",
            ObservationWindow = TimeSpan.FromSeconds(60),
            ValidUntilUtc = expires,
            IsNoOp = false,
        };

        var plan = new RepairPlan
        {
            PlanId = "plan-t14",
            SchemaVersion = RepairPlan.CurrentSchemaVersion,
            CreatedAtUtc = clockNow,
            ExpiresAtUtc = expires,
            SnapshotId = "snapshot-t14",
            PreflightId = "preflight-t14",
            DiagnosisId = "diagnosis-t14",
            IncidentId = "incident-t14",
            BootId = RepairFixtures.BootId,
            PlannerClock = RepairFixtures.DesktopClock,
            ClockNotice = "synthetic broker fixture clock",
            Decision = RepairPlanDecision.Planned,
            Digest = string.Empty,
            TargetFingerprint = identity.TargetFingerprint,
            Actions = new[] { action },
            NoOpFields = Array.Empty<RepairNoOpField>(),
            Refusals = Array.Empty<RepairRefusal>(),
            EvidenceIds = new[] { "ev:t14" },
            MissingEvidence = Array.Empty<string>(),
            Authorization = new RepairAuthorizationTerms
            {
                PlanDigest = string.Empty,
                ExpiresAtUtc = expires,
                ActionIds = new[] { actionId },
                DependencyGroups = Array.Empty<RepairDependencyGroup>(),
                PerActionConsentRequired = true,
                DenyByDefault = true,
                UacIsNotConsent = true,
                BindingIssued = false,
            },
            ExecutionAllowed = true,
            Synthetic = synthetic,
        };

        var digest = RepairPlanDigest.Compute(plan);
        var digested = plan with
        {
            Digest = digest,
            Authorization = plan.Authorization with { PlanDigest = digest },
        };

        return (digested, Authorization(digested, clockNow));
    }

    /// <summary>Builds the typed submit payload the protocol accepts (never an anonymous object).</summary>
    public static BrokerSubmitTransactionMessage Submit(
        string transactionId,
        RepairPlan plan,
        RepairAuthorizationBinding authorization,
        string? planDigest = null,
        string? targetFingerprint = null) => new()
    {
        TransactionId = transactionId,
        PlanDigest = planDigest ?? plan.Digest,
        TargetFingerprint = targetFingerprint ?? plan.TargetFingerprint,
        Plan = plan,
        Authorization = authorization,
    };

    public static RepairAuthorizationBinding Authorization(
        RepairPlan plan,
        DateTimeOffset? now = null,
        string userSid = UserSid,
        string logonSession = LogonSession,
        string? planDigest = null,
        string? targetFingerprint = null,
        DateTimeOffset? expiresAt = null,
        IEnumerable<string>? confirmedActionIds = null,
        bool uacIsNotConsent = true) => new()
        {
            BindingId = "binding-t14",
            PlanId = plan.PlanId,
            PlanDigest = planDigest ?? plan.Digest,
            TargetFingerprint = targetFingerprint ?? plan.TargetFingerprint,
            UserSid = userSid,
            LogonSessionId = logonSession,
            IssuedAtUtc = now ?? Now,
            ExpiresAtUtc = expiresAt ?? plan.ExpiresAtUtc,
            ConfirmedActionIds = confirmedActionIds?.ToArray()
                ?? plan.ChangedActions.Select(action => action.ActionId).ToArray(),
            Nonce = "synthetic-test-nonce-never-truncated",
            UacIsNotConsent = uacIsNotConsent,
        };

    /// <summary>Replaces the action list and re-derives the digest, as a preview would after a change.</summary>
    public static (RepairPlan Plan, RepairAuthorizationBinding Authorization) Rebuild(
        RepairPlan template, params RepairAction[] actions)
    {
        var updated = template with
        {
            Actions = actions,
            Authorization = template.Authorization with
            {
                ActionIds = actions.Select(action => action.ActionId).ToArray(),
            },
        };
        var digest = RepairPlanDigest.Compute(updated);
        updated = updated with
        {
            Digest = digest,
            Authorization = updated.Authorization with { PlanDigest = digest },
        };
        return (updated, Authorization(updated));
    }

    public static BrokerPreflightSnapshot MatchingPreflight(RepairPlan plan, DateTimeOffset? observedAt = null)    {
        var values = new Dictionary<RepairFieldKey, RepairFieldValue>();
        foreach (var action in plan.Actions)
        {
            values[RepairFieldKeys.For(action.Identity, action.Store, action.Field)] = action.Before;
        }

        return new BrokerPreflightSnapshot
        {
            Availability = BrokerPreflightAvailability.Available,
            TargetFingerprint = plan.TargetFingerprint,
            BootId = plan.BootId,
            ObservedAtUtc = observedAt ?? Now.AddSeconds(-2),
            FieldValues = values,
        };
    }

    public static BrokerTransactionValidationRequest ValidationRequest(
        RepairPlan plan,
        RepairAuthorizationBinding authorization,
        PlatformCapabilityMatrix? capabilities = null,
        BrokerPreflightSnapshot? preflight = null,
        DateTimeOffset? now = null,
        string userSid = UserSid,
        string logonSession = LogonSession,
        string transactionId = "tx-t14",
        string? planDigest = null,
        string? targetFingerprint = null) => new()
        {
            Submit = new BrokerSubmitTransactionMessage
            {
                TransactionId = transactionId,
                PlanDigest = planDigest ?? plan.Digest,
                TargetFingerprint = targetFingerprint ?? plan.TargetFingerprint,
                Plan = plan,
                Authorization = authorization,
            },
            InitiatingUserSid = userSid,
            LogonSessionId = logonSession,
            NowUtc = now ?? Now,
            Capabilities = capabilities ?? SyntheticMatrix(),
            Preflight = preflight ?? MatchingPreflight(plan),
        };

    public static BrokerSessionOptions SessionOptions(
        Stream? serverStream = null,
        BrokerPeerEvidence? evidence = null,
        PlatformCapabilityMatrix? capabilities = null,
        BrokerPreflightSnapshot? preflight = null,
        BrokerAuditTrail? audit = null,
        BrokerTrustDecision? trust = null,
        IBrokerNonceSource? nonceSource = null,
        FakeBrokerTimeProvider? clock = null,
        BrokerNonceLedger? ledger = null,
        string userSid = UserSid,
        string logonSession = LogonSession)
    {
        _ = serverStream;
        return new BrokerSessionOptions
        {
            Trust = trust ?? DevelopmentTrust(),
            Capabilities = capabilities ?? SyntheticMatrix(),
            Preflight = new FixedPreflightSource(preflight ?? BrokerPreflightSnapshot.Unavailable("test_default")),
            NonceSource = nonceSource ?? new FixedBrokerNonceSource(),
            InitiatingUserSid = userSid,
            LogonSessionId = logonSession,
            BrokerSessionId = SessionId,
            Clock = clock ?? new FakeBrokerTimeProvider(),
            Audit = audit ?? new BrokerAuditTrail(),
            NonceLedger = ledger,
        };
    }

    public static BrokerTrustDecision DevelopmentTrust() => new()
    {
        RealElevationEnabled = false,
        DevelopmentIsolationAllowed = true,
        IsSynthetic = true,
        Refusals = new[] { BrokerRefusal.Of(BrokerRefusalCode.RealElevationDisabled, "development isolation only") },
    };

    internal sealed class FixedPreflightSource : IBrokerPreflightSource
    {
        private readonly BrokerPreflightSnapshot _snapshot;

        public FixedPreflightSource(BrokerPreflightSnapshot snapshot) => _snapshot = snapshot;

        public Task<BrokerPreflightSnapshot> CollectAsync(RepairPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);
    }
}

