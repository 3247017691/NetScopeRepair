using NetScope.Broker;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Identity, deployment trust, closed action whitelist and independent transaction validation.
/// Every fixture is synthetic; no pipe is opened and nothing is elevated.
/// </summary>
public sealed class BrokerIdentityAndValidationTests
{
    [Fact]
    public void A_complete_kernel_verified_peer_is_allowed()
    {
        var peer = BrokerPlanFixtures.TrustedPeer() with
        {
            ClaimedProcessId = BrokerPlanFixtures.TrustedPeer().PeerProcessId,
            ClaimedProcessCreationUtc = BrokerPlanFixtures.PeerCreation,
        };
        var decision = BrokerPeerIdentityPolicy.Evaluate(peer);
        Assert.True(decision.Allowed);
        Assert.Empty(decision.Refusals);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("not_first_instance")]
    [InlineData("predictable_pipe")]
    [InlineData("wrong_user")]
    [InlineData("wrong_session")]
    [InlineData("pid_reuse")]
    [InlineData("creation_time_mismatch")]
    [InlineData("path_replaced")]
    [InlineData("unsigned_peer")]
    [InlineData("fake_parent")]
    [InlineData("unknown_parent")]
    [InlineData("no_handle")]
    [InlineData("missing_creation")]
    [InlineData("missing_user")]
    public void Each_identity_defect_is_refused_with_its_own_code(string defect)
    {
        var peer = BrokerPlanFixtures.TrustedPeer();
        var mutation = defect switch
        {
            "remote" => peer with { RemoteClient = true },
            "not_first_instance" => peer with { FirstPipeInstance = false },
            "predictable_pipe" => peer with { PipeNameRandom = false },
            "wrong_user" => peer with { PeerUserSid = "S-1-5-21-9999" },
            "wrong_session" => peer with { PeerSessionId = 99 },
            "pid_reuse" => peer with { ClaimedProcessId = 9999 },
            "creation_time_mismatch" => peer with
            {
                ClaimedProcessCreationUtc = peer.PeerProcessCreationUtc!.Value.AddHours(1),
            },
            "path_replaced" => peer with { PeerImagePath = Path.Combine(Path.GetTempPath(), "evil.exe") },
            "unsigned_peer" => peer with { PeerSignature = BrokerSignatureState.Unsigned },
            "fake_parent" => peer with
            {
                ParentProcessCreationUtc = peer.PeerProcessCreationUtc!.Value.AddMinutes(1),
            },
            "unknown_parent" => peer with { ParentProcessId = null, ParentImagePath = null },
            "no_handle" => peer with { ProcessHandleHeld = false },
            "missing_creation" => peer with { PeerProcessCreationUtc = null },
            "missing_user" => peer with { PeerUserSid = null },
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown identity defect."),
        };

        var decision = BrokerPeerIdentityPolicy.Evaluate(mutation);

        Assert.False(decision.Allowed);
        Assert.NotEmpty(decision.Refusals);
        var expected = defect switch
        {
            "remote" => BrokerRefusalCode.PeerRemoteClient,
            "not_first_instance" => BrokerRefusalCode.PipeNotFirstInstance,
            "predictable_pipe" => BrokerRefusalCode.PipeNameNotRandom,
            "wrong_user" => BrokerRefusalCode.PeerUserMismatch,
            "wrong_session" => BrokerRefusalCode.PeerSessionMismatch,
            "pid_reuse" => BrokerRefusalCode.PeerPidMismatch,
            "creation_time_mismatch" => BrokerRefusalCode.PeerCreationTimeMismatch,
            "path_replaced" => BrokerRefusalCode.PeerPathUntrusted,
            "unsigned_peer" => BrokerRefusalCode.PeerSignatureUntrusted,
            "fake_parent" => BrokerRefusalCode.PeerParentUntrusted,
            "unknown_parent" => BrokerRefusalCode.PeerParentUntrusted,
            "no_handle" => BrokerRefusalCode.PeerIdentityUnknown,
            "missing_creation" => BrokerRefusalCode.PeerIdentityUnknown,
            _ => BrokerRefusalCode.PeerIdentityUnknown,
        };
        Assert.Contains(decision.Refusals, refusal => refusal.Code == expected);
    }

    [Theory]
    [InlineData(@"C:\Install\NetScopeRepair.Desktop.exe", @"C:\Install", true)]
    [InlineData(@"C:\Install\sub\client.exe", @"C:\Install", true)]
    [InlineData(@"C:\InstallEvil\client.exe", @"C:\Install", false)]
    [InlineData(@"C:\Install\..\Windows\client.exe", @"C:\Install", false)]
    [InlineData(@"C:\Install", @"C:\Install", false)]
    public void Install_directory_containment_is_normalized(string candidate, string directory, bool expected)
    {
        Assert.Equal(expected, BrokerPeerIdentityPolicy.IsUnder(candidate, directory));
    }

    [Fact]
    public void The_delivery_trust_policy_cannot_enable_real_elevation()
    {
        var trusted = BrokerPlanFixtures.TrustedDeployment();
        var decision = BrokerTrustPolicy.DeliveryDefault.Evaluate(trusted);

        Assert.False(decision.RealElevationEnabled);
        Assert.False(decision.DevelopmentIsolationAllowed);
        Assert.False(decision.CanServe);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.SignatureNotTrusted);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.RealElevationDisabled);
        Assert.Empty(BrokerTrustPolicy.DeliveryDefault.TrustedPublisherThumbprints);
        Assert.False(BrokerTrustPolicy.DeliveryDefault.AllowDevelopmentIsolation);
    }

    [Fact]
    public void Real_elevation_would_require_signature_allowlist_acl_and_token()
    {
        var policy = BrokerTrustPolicy.ForSyntheticTests(new[] { BrokerPlanFixtures.TrustedThumbprint });
        var trusted = BrokerPlanFixtures.TrustedDeployment();

        var enabled = policy.Evaluate(trusted);
        Assert.True(enabled.RealElevationEnabled);
        Assert.True(enabled.IsSynthetic);
        Assert.True(enabled.CanServe);

        var userWritable = policy.Evaluate(trusted with { InstallDirectoryUserWritable = true });
        Assert.False(userWritable.RealElevationEnabled);
        Assert.Contains(userWritable.Refusals, refusal => refusal.Code == BrokerRefusalCode.InstallDirectoryUserWritable);

        var notElevated = policy.Evaluate(trusted with { RunningElevated = false });
        Assert.False(notElevated.RealElevationEnabled);
        Assert.Contains(notElevated.Refusals, refusal => refusal.Code == BrokerRefusalCode.BrokerNotElevated);

        var unsigned = policy.Evaluate(trusted with { Signature = BrokerSignatureState.Unsigned, SignerThumbprints = Array.Empty<string>() });
        Assert.False(unsigned.RealElevationEnabled);
        Assert.Contains(unsigned.Refusals, refusal => refusal.Code == BrokerRefusalCode.SignatureNotTrusted);

        var wrongDirectory = policy.Evaluate(trusted with { BrokerExecutablePath = @"C:\Temp\broker.exe" });
        Assert.False(wrongDirectory.RealElevationEnabled);
        Assert.Contains(wrongDirectory.Refusals, refusal => refusal.Code == BrokerRefusalCode.TrustedDeploymentUnavailable);
    }

    [Fact]
    public void Development_isolation_never_implies_real_elevation()
    {
        var policy = BrokerTrustPolicy.ForSyntheticTests();
        var decision = policy.Evaluate(BrokerPlanFixtures.DevelopmentDeployment() with { DevelopmentIsolation = true });

        Assert.False(decision.RealElevationEnabled);
        Assert.True(decision.DevelopmentIsolationAllowed);
        Assert.True(decision.IsSynthetic);

        var disabled = new BrokerTrustPolicy
        {
            TrustedPublisherThumbprints = Array.Empty<string>(),
            AllowDevelopmentIsolation = false,
        }.Evaluate(BrokerPlanFixtures.DevelopmentDeployment() with { DevelopmentIsolation = true });
        Assert.False(disabled.CanServe);
        Assert.Contains(disabled.Refusals, refusal => refusal.Code == BrokerRefusalCode.RealElevationDisabled);
    }

    [Fact]
    public void A_complete_synthetic_transaction_passes_independent_validation()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var request = BrokerPlanFixtures.ValidationRequest(plan, authorization);

        var decision = BrokerTransactionValidator.Validate(request);

        Assert.True(decision.Accepted);
        Assert.Empty(decision.Refusals);
        Assert.False(decision.RequiresNewPreview);
        Assert.Equal(plan.Digest, decision.PlanDigest);
    }

    [Fact]
    public void The_delivery_capability_matrix_refuses_the_same_transaction()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var request = BrokerPlanFixtures.ValidationRequest(
            plan, authorization, capabilities: PlatformCapabilityMatrix.UnverifiedDefault);

        var decision = BrokerTransactionValidator.Validate(request);

        Assert.False(decision.Accepted);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.CapabilityUnavailable);
        Assert.False(decision.RequiresNewPreview);
    }

    [Fact]
    public void Changing_a_value_after_the_authorized_digest_requires_a_new_preview()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var changed = plan with
        {
            Actions = new[]
            {
                plan.Actions[0] with
                {
                    Desired = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
                },
            },
        };

        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(changed, authorization));

        Assert.False(decision.Accepted);
        Assert.True(decision.RequiresNewPreview);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.PlanDigestMismatch);
    }

    [Fact]
    public void Changing_the_target_requires_a_new_preview()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(plan, authorization, targetFingerprint: "other-target|ipv4|0"));

        Assert.False(decision.Accepted);
        Assert.True(decision.RequiresNewPreview);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.TargetMismatch);
    }

    [Fact]
    public void An_expired_plan_or_authorization_is_refused()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var afterExpiry = plan.ExpiresAtUtc.AddSeconds(1);

        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(plan, authorization, now: afterExpiry));

        Assert.False(decision.Accepted);
        Assert.True(decision.RequiresNewPreview);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.PlanExpired);
    }

    [Fact]
    public void Wrong_user_wrong_session_or_missing_consent_is_refused()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();

        var wrongUser = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(plan, authorization, userSid: "S-1-5-21-9999"));
        Assert.Contains(wrongUser.Refusals, refusal => refusal.Code == BrokerRefusalCode.AuthorizationMismatch);

        var wrongSession = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(plan, authorization, logonSession: "luid:0x9999"));
        Assert.Contains(wrongSession.Refusals, refusal => refusal.Code == BrokerRefusalCode.AuthorizationMismatch);

        var noConsent = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(
                plan,
                BrokerPlanFixtures.Authorization(plan, confirmedActionIds: Array.Empty<string>())));
        Assert.Contains(noConsent.Refusals, refusal => refusal.Code == BrokerRefusalCode.ConsentMissing);

        var uacNotConsent = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(
                plan,
                BrokerPlanFixtures.Authorization(plan, uacIsNotConsent: false)));
        Assert.Contains(uacNotConsent.Refusals, refusal => refusal.Code == BrokerRefusalCode.AuthorizationMismatch);
    }

    [Fact]
    public void A_stale_or_changed_broker_preflight_requires_a_new_preview()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();

        var changedBefore = new Dictionary<RepairFieldKey, RepairFieldValue>
        {
            [RepairFieldKeys.For(plan.Actions[0].Identity, plan.Actions[0].Store, plan.Actions[0].Field)]
                = new InterfaceMetricValue { RawValue = 40, Kind = MetricValueKind.ManualConfigured },
        };
        var changed = BrokerTransactionValidator.Validate(BrokerPlanFixtures.ValidationRequest(
            plan,
            authorization,
            preflight: new BrokerPreflightSnapshot
            {
                Availability = BrokerPreflightAvailability.Available,
                TargetFingerprint = plan.TargetFingerprint,
                BootId = plan.BootId,
                ObservedAtUtc = BrokerPlanFixtures.Now.AddSeconds(-2),
                FieldValues = changedBefore,
            }));
        Assert.True(changed.RequiresNewPreview);
        Assert.Contains(changed.Refusals, refusal => refusal.Code == BrokerRefusalCode.RequiresNewPreview);

        var stale = BrokerTransactionValidator.Validate(BrokerPlanFixtures.ValidationRequest(
            plan,
            authorization,
            preflight: BrokerPlanFixtures.MatchingPreflight(plan, BrokerPlanFixtures.Now.AddMinutes(-5))));
        Assert.True(stale.RequiresNewPreview);
        Assert.Contains(stale.Refusals, refusal => refusal.Code == BrokerRefusalCode.PreflightMismatch);

        var unavailable = BrokerTransactionValidator.Validate(BrokerPlanFixtures.ValidationRequest(
            plan,
            authorization,
            preflight: BrokerPreflightSnapshot.Unavailable("no_source")));
        Assert.Contains(unavailable.Refusals, refusal => refusal.Code == BrokerRefusalCode.PreflightUnavailable);
    }

    [Fact]
    public void Recover_transaction_is_refused_without_a_journal()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var template = plan.Actions[0];
        var recover = template with
        {
            Kind = RepairActionKind.RecoverTransaction,
            ActionId = RepairActionRules.ActionIdOf(RepairActionKind.RecoverTransaction),
        };
        var (changed, authorization) = BrokerPlanFixtures.Rebuild(plan, recover);

        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(changed, authorization));

        Assert.False(decision.Accepted);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.RecoverRequiresJournal);
    }

    [Fact]
    public void Store_family_field_and_value_violations_are_refused()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var template = plan.Actions[0];

        AssertRefused(template with { Store = RepairStore.PersistentStore }, BrokerRefusalCode.StoreNotSupported);
        AssertRefused(template with { Family = RepairAddressFamily.Ipv6 }, BrokerRefusalCode.FamilyNotSupported);
        AssertRefused(template with { Field = RepairField.AutomaticMetric }, BrokerRefusalCode.ActionFieldMismatch);
        AssertRefused(
            template with
            {
                Before = new InterfaceMetricValue { RawValue = 35, Kind = MetricValueKind.EffectiveDerived },
            },
            BrokerRefusalCode.ActionValueInvalid);

        // An out-of-set enum has no wire name, so it is rejected by the whitelist itself before a
        // digest or a capability could ever be computed for it.
        var unknownKind = BrokerActionWhitelist.ValidateShape(template with { Kind = (RepairActionKind)99 });
        Assert.Contains(unknownKind, refusal => refusal.Code == BrokerRefusalCode.ActionNotAllowed);
    }

    [Fact]
    public void The_sixteen_action_limit_and_duplicate_actions_are_enforced()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var action = plan.Actions[0];

        var duplicate = Enumerable.Range(0, 16).Select(_ => action).ToArray();
        var (duplicatePlan, duplicateAuthorization) = BrokerPlanFixtures.Rebuild(plan, duplicate);
        var duplicateDecision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(duplicatePlan, duplicateAuthorization));
        Assert.Contains(duplicateDecision.Refusals, refusal => refusal.Code == BrokerRefusalCode.DuplicateAction);

        var tooMany = Enumerable.Range(0, 17).Select(_ => action).ToArray();
        var (tooManyPlan, tooManyAuthorization) = BrokerPlanFixtures.Rebuild(plan, tooMany);
        var tooManyDecision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(tooManyPlan, tooManyAuthorization));
        Assert.Contains(tooManyDecision.Refusals, refusal => refusal.Code == BrokerRefusalCode.ActionLimitExceeded);
    }

    [Fact]
    public void A_synthetic_plan_can_never_be_executed_as_a_privileged_repair()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan(synthetic: true);
        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(plan, authorization));

        Assert.False(decision.Accepted);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.SyntheticPlanNotExecutable);
    }

    [Fact]
    public void Path_like_transaction_identifiers_are_refused()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(plan, authorization, transactionId: @"C:\Windows\cmd.exe"));

        Assert.False(decision.Accepted);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == BrokerRefusalCode.InvalidIdentifier);
    }

    private static void AssertRefused(RepairAction action, BrokerRefusalCode expected)
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var (changed, authorization) = BrokerPlanFixtures.Rebuild(plan, action);
        var decision = BrokerTransactionValidator.Validate(
            BrokerPlanFixtures.ValidationRequest(changed, authorization));
        Assert.False(decision.Accepted);
        Assert.Contains(decision.Refusals, refusal => refusal.Code == expected);
    }
}

