using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// The primary chain: good typed facts, a trusted attribution, one unambiguous TUN target and a
/// request for all three fields. The plan must be complete and explain exactly why no write can
/// execute in this delivery.
/// </summary>
public sealed class RepairPlanMainChainTests
{
    private static RepairPlan BuildMainChain()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(true),
                ignore: RepairFixtures.Ignore(false))));

        return RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));
    }

    [Fact]
    public void Main_chain_forms_three_actions_without_making_them_executable()
    {
        var plan = BuildMainChain();

        Assert.Equal(RepairPlanDecision.Planned, plan.Decision);
        Assert.Equal(3, plan.Actions.Count);
        Assert.Equal(
            new[]
            {
                RepairActionKind.SetTunIpv4InterfaceMetric,
                RepairActionKind.SetTunIpv4AutomaticMetric,
                RepairActionKind.SetTunIpv4IgnoreDefaultRoutes,
            },
            plan.Actions.Select(action => action.Kind));

        Assert.All(plan.Actions, action => Assert.Equal(RepairActionEligibility.Blocked, action.Eligibility));
        Assert.All(plan.Actions, action => Assert.NotEmpty(action.BlockingReasons));
        Assert.All(plan.Actions, action => Assert.Equal(
            RepairRefusalCode.CapabilityUnavailable,
            action.BlockingReasons[0].Code));

        Assert.False(plan.ExecutionAllowed);
        Assert.False(plan.CanAuthorize);
        Assert.False(plan.Authorization.BindingIssued);
        Assert.True(plan.CountsAsRepair);
    }

    [Fact]
    public void Main_chain_keeps_typed_before_and_desired_per_action()
    {
        var plan = BuildMainChain();

        var metric = plan.Actions.Single(action => action.Kind == RepairActionKind.SetTunIpv4InterfaceMetric);
        var automatic = plan.Actions.Single(action => action.Kind == RepairActionKind.SetTunIpv4AutomaticMetric);
        var ignore = plan.Actions.Single(action => action.Kind == RepairActionKind.SetTunIpv4IgnoreDefaultRoutes);

        Assert.Equal(RepairFixtures.TunGuid, metric.Identity.InterfaceGuid);
        Assert.Equal(RepairAddressFamily.Ipv4, metric.Family);
        Assert.Equal(RepairStore.ActiveStore, metric.Store);
        Assert.Equal(new InterfaceMetricValue { RawValue = 35, Kind = MetricValueKind.ManualConfigured }, metric.Before);
        Assert.Equal(new InterfaceMetricValue { RawValue = 20, Kind = MetricValueKind.ManualConfigured }, metric.Desired);

        Assert.Equal(new AutomaticMetricValue { Enabled = true }, automatic.Before);
        Assert.Equal(new AutomaticMetricValue { Enabled = false }, automatic.Desired);

        Assert.Equal(new IgnoreDefaultRoutesValue { Ignore = false }, ignore.Before);
        Assert.Equal(new IgnoreDefaultRoutesValue { Ignore = true }, ignore.Desired);
        Assert.All(plan.Actions, action =>
        {
            Assert.InRange(action.ObservationWindow, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            Assert.Equal(plan.ExpiresAtUtc, action.ValidUntilUtc);
            Assert.Equal("synthetic-t13", action.BeforeSource);
            Assert.Equal(NetScope.Domain.EventStatus.Ok, action.BeforeStatus);
            Assert.Equal(RepairFixtures.Observed, action.BeforeObservedAtUtc);
            Assert.True(action.BeforeTtl > TimeSpan.Zero);
        });
        Assert.Equal("preflight-t13", plan.PreflightId);
    }

    [Fact]
    public void Automatic_metric_is_a_stated_dependency_of_interface_metric()
    {
        var plan = BuildMainChain();

        var metric = plan.Actions.Single(action => action.Kind == RepairActionKind.SetTunIpv4InterfaceMetric);
        var automaticId = RepairActionRules.ActionIdOf(RepairActionKind.SetTunIpv4AutomaticMetric);

        Assert.Equal(new[] { automaticId }, metric.DependsOnActionIds);
        var group = Assert.Single(plan.Authorization.DependencyGroups);
        Assert.True(group.RequiredTogether);
        Assert.Contains(automaticId, group.ActionIds);
        Assert.Contains(metric.ActionId, group.ActionIds);
    }

    [Fact]
    public void Plan_digest_is_stable_and_changes_when_a_desired_value_changes()
    {
        var first = BuildMainChain();
        var second = BuildMainChain();
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.Digest, first.Authorization.PlanDigest);
        Assert.StartsWith("sha256:", first.Digest, StringComparison.Ordinal);

        var preflight = first.Actions[0].Identity;
        var changedPreflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            identity: preflight,
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(true),
                ignore: RepairFixtures.Ignore(false))));
        var changed = RepairFixtures.Build(
            changedPreflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "21"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        Assert.NotEqual(first.Digest, changed.Digest);
    }

    [Fact]
    public void Request_order_does_not_change_the_plan_or_its_digest()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(true),
                ignore: RepairFixtures.Ignore(false))));

        var forward = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        var reversed = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"));

        Assert.Equal(forward.Digest, reversed.Digest);
        Assert.Equal(
            forward.Actions.Select(action => action.ActionId),
            reversed.Actions.Select(action => action.ActionId));
    }

    [Fact]
    public void Authorization_terms_expose_every_binding_dimension_and_deny_by_default()
    {
        var plan = BuildMainChain();
        var terms = plan.Authorization;

        Assert.True(terms.PerActionConsentRequired);
        Assert.True(terms.DenyByDefault);
        Assert.True(terms.UacIsNotConsent);
        Assert.False(terms.BindingIssued);
        Assert.Contains("user_sid", terms.RequiredBindingFields);
        Assert.Contains("logon_session", terms.RequiredBindingFields);
        Assert.Contains("plan_digest", terms.RequiredBindingFields);
        Assert.Contains("target_fingerprint", terms.RequiredBindingFields);
        Assert.Contains("expires_at_utc", terms.RequiredBindingFields);
        Assert.Contains("nonce", terms.RequiredBindingFields);
        Assert.InRange(terms.ExpiresAtUtc - plan.CreatedAtUtc, TimeSpan.Zero, RepairLimits.MaxPlanTtl);
        Assert.Equal(plan.Actions.Select(action => action.ActionId), terms.ActionIds);
        Assert.NotEmpty(plan.ClockNotice);
    }
}
