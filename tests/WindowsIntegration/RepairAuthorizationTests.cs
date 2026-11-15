using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Authorization binding fields and invalidation. No binding is actually issued in this
/// delivery; these tests build synthetic records only to prove the comparison logic.
/// </summary>
public sealed class RepairAuthorizationTests
{
    private const string UserSid = "S-1-5-21-1000-2000-3000-1001";
    private const string LogonSession = "logon-session-7";

    private static RepairPlan MainChainPlan() => RepairFixtures.Build(
        RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
            metric: RepairFixtures.Metric(35),
            automatic: RepairFixtures.Automatic(true),
            ignore: RepairFixtures.Ignore(false)))),
        RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"),
        RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
        RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

    private static RepairAuthorizationBinding BindingFor(
        RepairPlan plan,
        DateTimeOffset? expiresAt = null,
        IEnumerable<string>? confirmed = null,
        string userSid = UserSid,
        string logonSession = LogonSession,
        string? target = null,
        bool uacIsNotConsent = true) => new()
        {
            BindingId = "binding-t13",
            PlanId = plan.PlanId,
            PlanDigest = plan.Digest,
            TargetFingerprint = target ?? plan.TargetFingerprint,
            UserSid = userSid,
            LogonSessionId = logonSession,
            IssuedAtUtc = RepairFixtures.Now,
            ExpiresAtUtc = expiresAt ?? plan.ExpiresAtUtc,
            ConfirmedActionIds = confirmed?.ToArray()
                ?? plan.ChangedActions.Select(action => action.ActionId).ToArray(),
            Nonce = "synthetic-nonce-not-used-for-real-auth",
            UacIsNotConsent = uacIsNotConsent,
        };

    [Fact]
    public void A_real_plan_cannot_have_a_usable_binding_in_this_delivery()
    {
        var plan = MainChainPlan();
        var binding = BindingFor(plan);

        var refusals = RepairAuthorizationBindingValidator.Validate(
            binding,
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);

        Assert.Contains(refusals, refusal => refusal.Code == RepairRefusalCode.PlanNotExecutable);
        Assert.False(plan.Authorization.BindingIssued);
    }

    [Fact]
    public void Changing_a_desired_value_changes_the_digest_and_invalidates_the_binding()
    {
        var original = MainChainPlan();
        var changed = RepairFixtures.Build(
            RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(true),
                ignore: RepairFixtures.Ignore(false)))),
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "21"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        var refusals = RepairAuthorizationBindingValidator.Validate(
            BindingFor(original),
            changed,
            RepairFixtures.Now,
            UserSid,
            LogonSession);

        Assert.NotEqual(original.Digest, changed.Digest);
        Assert.Contains(refusals, refusal => refusal.Code == RepairRefusalCode.PlanDigestMismatch);
    }

    [Fact]
    public void An_expired_binding_is_refused()
    {
        var plan = MainChainPlan();
        var binding = BindingFor(plan, expiresAt: RepairFixtures.Now.AddSeconds(-1));

        var refusals = RepairAuthorizationBindingValidator.Validate(
            binding,
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);

        Assert.Contains(refusals, refusal => refusal.Code == RepairRefusalCode.PlanExpired);
    }

    [Fact]
    public void A_different_user_session_or_target_is_refused()
    {
        var plan = MainChainPlan();

        var userMismatch = RepairAuthorizationBindingValidator.Validate(
            BindingFor(plan, userSid: "S-1-5-21-9999"),
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);
        Assert.Contains(userMismatch, refusal => refusal.Code == RepairRefusalCode.AuthorizationMismatch);

        var sessionMismatch = RepairAuthorizationBindingValidator.Validate(
            BindingFor(plan, logonSession: "logon-session-other"),
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);
        Assert.Contains(sessionMismatch, refusal => refusal.Code == RepairRefusalCode.AuthorizationMismatch);

        var targetMismatch = RepairAuthorizationBindingValidator.Validate(
            BindingFor(plan, target: "other-target"),
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);
        Assert.Contains(targetMismatch, refusal => refusal.Code == RepairRefusalCode.AuthorizationMismatch);
    }

    [Fact]
    public void Each_changed_action_needs_consent()
    {
        var plan = MainChainPlan();
        var missingIgnore = plan.ChangedActions
            .Where(action => action.Kind != RepairActionKind.SetTunIpv4IgnoreDefaultRoutes)
            .Select(action => action.ActionId)
            .ToArray();

        var refusals = RepairAuthorizationBindingValidator.Validate(
            BindingFor(plan, confirmed: missingIgnore),
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);

        Assert.Contains(refusals, refusal => refusal.Code == RepairRefusalCode.ConsentMissing);
    }

    [Fact]
    public void A_dependency_group_cannot_be_partially_confirmed()
    {
        var plan = MainChainPlan();
        var automaticId = RepairActionRules.ActionIdOf(RepairActionKind.SetTunIpv4AutomaticMetric);
        var ignoreId = RepairActionRules.ActionIdOf(RepairActionKind.SetTunIpv4IgnoreDefaultRoutes);

        var refusals = RepairAuthorizationBindingValidator.Validate(
            BindingFor(plan, confirmed: new[] { automaticId, ignoreId }),
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);

        Assert.Contains(
            refusals,
            refusal => refusal.Code == RepairRefusalCode.ConsentMissing
                && refusal.Detail.Contains("依赖组", StringComparison.Ordinal));
    }

    [Fact]
    public void Uac_is_not_action_consent()
    {
        var plan = MainChainPlan();

        var refusals = RepairAuthorizationBindingValidator.Validate(
            BindingFor(plan, uacIsNotConsent: false),
            plan,
            RepairFixtures.Now,
            UserSid,
            LogonSession);

        Assert.Contains(refusals, refusal => refusal.Code == RepairRefusalCode.AuthorizationMismatch);
    }
}
