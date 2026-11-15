using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Each of the three fields gets one plan-forming positive, two policy/semantics negatives and
/// one missing-evidence case. Every case uses synthetic facts only.
/// </summary>
public sealed class RepairPlanActionTests
{
    [Fact]
    public void Interface_metric_positive_plan_is_blocked_only_by_capability()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"));

        Assert.Equal(RepairPlanDecision.Planned, plan.Decision);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(RepairActionKind.SetTunIpv4InterfaceMetric, action.Kind);
        Assert.Equal(RepairFixtures.Metric(35), action.Before);
        Assert.Equal(RepairFixtures.Metric(20), action.Desired);
        Assert.Equal(RepairActionEligibility.Blocked, action.Eligibility);
        var blocker = Assert.Single(action.BlockingReasons);
        Assert.Equal(RepairRefusalCode.CapabilityUnavailable, blocker.Code);
        Assert.Contains("隔离 VM", blocker.Detail);
        Assert.False(plan.ExecutionAllowed);
    }

    [Fact]
    public void Interface_metric_negative_with_automatic_enabled_refuses_without_the_combination()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(true),
                ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.DependencyMissing);
    }

    [Fact]
    public void Interface_metric_negative_derived_value_is_not_a_restore_baseline()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(20, MetricValueKind.EffectiveDerived),
                automatic: RepairFixtures.Automatic(true),
                ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.ValueNotManual);
        Assert.Empty(plan.NoOpFields);
    }

    [Fact]
    public void Interface_metric_missing_active_store_fact_is_a_critical_unknown()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(omit: RepairField.InterfaceMetric)));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.CriticalUnknown);
        Assert.Contains("field:interface_metric:active_store", plan.MissingEvidence);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Automatic_metric_positive_plan_is_blocked_only_by_capability()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "enabled"));

        Assert.Equal(RepairPlanDecision.Planned, plan.Decision);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(new AutomaticMetricValue { Enabled = false }, action.Before);
        Assert.Equal(new AutomaticMetricValue { Enabled = true }, action.Desired);
        Assert.Equal(RepairActionEligibility.Blocked, action.Eligibility);
        Assert.Equal(RepairRefusalCode.CapabilityUnavailable, Assert.Single(action.BlockingReasons).Code);
        Assert.Contains("耦合", Assert.Single(action.BlockingReasons).Detail);
    }

    [Fact]
    public void Automatic_metric_negative_persistent_store_is_refused()
    {
        var plan = RepairFixtures.Build(
            RepairFixtures.Preflight(),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "enabled", "persistent_store"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.StoreNotSupported);
    }

    [Fact]
    public void Automatic_metric_negative_unknown_before_state_is_refused()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(automaticStatus: NetScope.Domain.EventStatus.AccessDenied)));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "enabled"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.AutomaticMetricUnknown);
    }

    [Fact]
    public void Automatic_metric_missing_fact_is_a_critical_unknown()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(omit: RepairField.AutomaticMetric)));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "enabled"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.CriticalUnknown);
        Assert.Contains("field:automatic_metric:active_store", plan.MissingEvidence);
    }

    [Fact]
    public void Ignore_default_routes_positive_plan_is_blocked_only_by_capability()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        Assert.Equal(RepairPlanDecision.Planned, plan.Decision);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(new IgnoreDefaultRoutesValue { Ignore = false }, action.Before);
        Assert.Equal(new IgnoreDefaultRoutesValue { Ignore = true }, action.Desired);
        Assert.Equal(RepairActionEligibility.Blocked, action.Eligibility);
        Assert.Equal(RepairRefusalCode.CapabilityUnavailable, Assert.Single(action.BlockingReasons).Code);
    }

    [Fact]
    public void Ignore_default_routes_negative_ipv6_family_is_refused()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            identity: RepairFixtures.Identity(family: RepairAddressFamily.Ipv6)));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.FamilyNotSupported);
    }

    [Fact]
    public void Ignore_default_routes_negative_invalid_value_is_refused()
    {
        var plan = RepairFixtures.Build(
            RepairFixtures.Preflight(),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "yes"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.ActionValueInvalid);
    }

    [Fact]
    public void Ignore_default_routes_missing_fact_is_a_critical_unknown()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(omit: RepairField.IgnoreDefaultRoutes)));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.CriticalUnknown);
        Assert.Contains("field:ignore_default_routes:active_store", plan.MissingEvidence);
    }
}
