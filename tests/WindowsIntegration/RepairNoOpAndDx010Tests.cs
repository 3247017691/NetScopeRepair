using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// No-op is scoped by identity + family + store + field and uses typed values. This includes the
/// DX-010 counterexample: an untyped ProposedChange with only field/GUID/desired string is
/// structurally unable to express those constraints.
/// </summary>
public sealed class RepairNoOpAndDx010Tests
{
    [Fact]
    public void All_values_already_equal_produce_no_op_and_are_not_counted_as_repair()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
            metric: RepairFixtures.Metric(35),
            automatic: RepairFixtures.Automatic(false),
            ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "35"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "false"));

        Assert.Equal(RepairPlanDecision.NoOp, plan.Decision);
        Assert.Empty(plan.Actions);
        Assert.Equal(3, plan.NoOpFields.Count);
        Assert.False(plan.CountsAsRepair);
        Assert.False(plan.ExecutionAllowed);
        Assert.False(plan.CanAuthorize);
        Assert.Empty(plan.Authorization.ActionIds);
    }

    [Fact]
    public void Partial_no_op_records_only_the_field_that_is_already_equal()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
            metric: RepairFixtures.Metric(35),
            automatic: RepairFixtures.Automatic(false),
            ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "true"));

        Assert.Equal(RepairPlanDecision.Planned, plan.Decision);
        Assert.Equal(2, plan.Actions.Count);
        var noOp = Assert.Single(plan.NoOpFields);
        Assert.Equal(RepairField.AutomaticMetric, noOp.Field);
    }

    [Fact]
    public void A_matching_persistent_store_value_does_not_make_the_active_store_a_no_op()
    {
        var fields = RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false))
            .Concat(new[]
            {
                RepairFixtures.FieldFact(
                    RepairField.InterfaceMetric,
                    RepairFixtures.Metric(20),
                    RepairStore.PersistentStore),
            })
            .ToArray();

        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: fields));
        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"));

        Assert.Equal(RepairPlanDecision.Planned, plan.Decision);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(RepairStore.ActiveStore, action.Store);
        Assert.Empty(plan.NoOpFields);
    }

    [Fact]
    public void Metric_zero_with_automatic_disabled_and_default_routes_allowed_is_already_a_no_op()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
            metric: RepairFixtures.Metric(0),
            automatic: RepairFixtures.Automatic(false),
            ignore: RepairFixtures.Ignore(false))));

        var plan = RepairFixtures.Build(
            preflight,
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "0"),
            RepairFixtures.Action("set_tun_ipv4_automatic_metric", "disabled"),
            RepairFixtures.Action("set_tun_ipv4_ignore_default_routes", "false"));

        Assert.Equal(RepairPlanDecision.NoOp, plan.Decision);
        Assert.False(plan.CountsAsRepair);

        // The closed set has no route-creation and no generic metric-to-1 action. Nothing in the
        // planner can invent "set metric 1" or add a default route.
        Assert.DoesNotContain(
            Enum.GetNames<RepairActionKind>(),
            name => name.Contains("Route", StringComparison.OrdinalIgnoreCase)
                && name != nameof(RepairActionKind.SetTunIpv4IgnoreDefaultRoutes));
    }

    [Fact]
    public void Typed_no_op_comparison_rejects_other_identity_family_and_store()
    {
        var desired = RepairFixtures.Metric(20);
        var before = RepairFixtures.Metric(20);

        var identityMismatch = RepairNoOpEvaluator.Assess(
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            new RepairFieldKey(RepairFixtures.OtherTunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            before,
            desired);
        Assert.False(identityMismatch.IsNoOp);
        Assert.Equal("identity_mismatch", identityMismatch.ReasonCode);

        var familyMismatch = RepairNoOpEvaluator.Assess(
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv6, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            before,
            desired);
        Assert.False(familyMismatch.IsNoOp);
        Assert.Equal("family_mismatch", familyMismatch.ReasonCode);

        var storeMismatch = RepairNoOpEvaluator.Assess(
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.PersistentStore, RepairField.InterfaceMetric),
            before,
            desired);
        Assert.False(storeMismatch.IsNoOp);
        Assert.Equal("store_mismatch", storeMismatch.ReasonCode);
    }

    [Fact]
    public void A_derived_metric_equal_to_the_desired_value_is_not_a_no_op()
    {
        var derived = RepairFixtures.Metric(20, MetricValueKind.EffectiveDerived);

        var assessment = RepairNoOpEvaluator.Assess(
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            derived,
            RepairFixtures.Metric(20));

        Assert.False(assessment.IsNoOp);
        Assert.Equal("value_not_manual", assessment.ReasonCode);
    }

    [Fact]
    public void Dx010_untyped_proposal_is_structurally_insufficient_for_a_plan()
    {
        var proposal = new UntypedRepairProposal
        {
            Field = "disable_default_routes",
            TargetInterfaceGuid = RepairFixtures.TunGuid.ToString("D"),
            DesiredValue = "true",
            Rationale = "DX-010 fixture: the old shape has no store/family/type/provenance.",
        };

        var issues = RepairProposalGuard.Validate(proposal);

        Assert.True(issues.Count >= 6);
        Assert.All(issues, issue => Assert.Equal(RepairRefusalCode.ProposalUntyped, issue.Code));
        Assert.Contains(issues, issue => issue.Detail.Contains("disable_default_routes", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Detail.Contains("store", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Detail.Contains("地址族", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Detail.Contains("uint", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Detail.Contains("LUID", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Detail.Contains("198.18", StringComparison.Ordinal));
        Assert.False(RepairProposalGuard.CanBecomePlan(proposal));
    }

    [Fact]
    public void Dx010_alias_and_untyped_string_are_not_silently_mapped()
    {
        Assert.False(RepairActionParser.TryParseKind("disable_default_routes", out _));

        var alias = new UntypedRepairProposal
        {
            Field = "disable_default_routes",
            TargetInterfaceGuid = RepairFixtures.TunGuid.ToString("D"),
            DesiredValue = "true",
        };
        Assert.Contains(
            RepairProposalGuard.Validate(alias),
            issue => issue.Detail.Contains("canonical", StringComparison.Ordinal));
    }

    [Fact]
    public void A_naive_dx010_string_comparison_would_wrongly_claim_no_op_on_a_derived_value()
    {
        var proposal = new UntypedRepairProposal
        {
            Field = "interface_metric",
            TargetInterfaceGuid = RepairFixtures.TunGuid.ToString("D"),
            DesiredValue = "20",
        };

        // DX-010 currently compares a formatted current value with the desired string; this is how
        // an effective derived 20 would look identical to a desired manual 20.
        var derived = RepairFixtures.Metric(20, MetricValueKind.EffectiveDerived);
        var naiveMatch = derived.RawValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            == proposal.DesiredValue;
        Assert.True(naiveMatch);

        var typed = RepairNoOpEvaluator.Assess(
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            new RepairFieldKey(RepairFixtures.TunGuid, RepairAddressFamily.Ipv4, RepairStore.ActiveStore, RepairField.InterfaceMetric),
            derived,
            RepairFixtures.Metric(20));

        Assert.False(typed.IsNoOp);
        Assert.Equal("value_not_manual", typed.ReasonCode);
    }
}
