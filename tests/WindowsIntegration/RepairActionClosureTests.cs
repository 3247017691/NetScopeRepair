using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// The action surface is a closed set. There is no generic property write, no script and no
/// string-to-field guessing.
/// </summary>
public sealed class RepairActionClosureTests
{
    [Theory]
    [InlineData("set_tun_ipv4_interface_metric", RepairActionKind.SetTunIpv4InterfaceMetric)]
    [InlineData("set_tun_ipv4_automatic_metric", RepairActionKind.SetTunIpv4AutomaticMetric)]
    [InlineData("set_tun_ipv4_ignore_default_routes", RepairActionKind.SetTunIpv4IgnoreDefaultRoutes)]
    [InlineData("recover_transaction", RepairActionKind.RecoverTransaction)]
    public void Closed_set_round_trips_each_declared_action(string wire, RepairActionKind expected)
    {
        Assert.True(RepairActionParser.TryParseKind(wire, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(wire, WireNames.Of(parsed));
    }

    [Theory]
    [InlineData("set_property")]
    [InlineData("SetProperty")]
    [InlineData("set_any_property")]
    [InlineData("set_interface_metric")]
    [InlineData("set_tun_metric")]
    [InlineData("recover_anything")]
    [InlineData("")]
    public void Generic_or_unknown_action_tokens_are_rejected(string wire)
    {
        Assert.False(RepairActionParser.TryParseKind(wire, out _));
    }

    [Fact]
    public void Generic_action_in_a_plan_is_refused_without_creating_an_action()
    {
        var plan = RepairFixtures.Build(
            RepairFixtures.Preflight(),
            RepairFixtures.Action("set_property", "35"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.ActionNotAllowed);
    }

    [Fact]
    public void Recover_transaction_cannot_be_requested_by_a_diagnostic_plan()
    {
        var plan = RepairFixtures.Build(
            RepairFixtures.Preflight(),
            RepairFixtures.Action("recover_transaction", "0"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.RecoverRequiresJournal);
    }

    [Theory]
    [InlineData("4294967295", true)]
    [InlineData("0", true)]
    [InlineData("4294967296", false)]
    [InlineData("-1", false)]
    [InlineData("+20", false)]
    [InlineData("0x14", false)]
    [InlineData(" 20 ", false)]
    [InlineData("20.0", false)]
    [InlineData("", false)]
    public void Interface_metric_keeps_raw_uint_range_and_rejects_truncation(string text, bool accepted)
    {
        var parsed = RepairActionParser.TryParseDesired(
            RepairActionKind.SetTunIpv4InterfaceMetric,
            text,
            out var value,
            out var error);

        Assert.Equal(accepted, parsed);
        if (accepted)
        {
            var metric = Assert.IsType<InterfaceMetricValue>(value);
            Assert.Equal(MetricValueKind.ManualConfigured, metric.Kind);
            Assert.Equal(text, metric.RawValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(error);
        }
        else
        {
            Assert.Null(value);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Theory]
    [InlineData("enabled", true)]
    [InlineData("disabled", false)]
    [InlineData("Enabled", null)]
    [InlineData("true", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    public void Automatic_metric_accepts_only_declared_tokens(string text, bool? enabled)
    {
        var parsed = RepairActionParser.TryParseDesired(
            RepairActionKind.SetTunIpv4AutomaticMetric,
            text,
            out var value,
            out _);

        if (enabled is null)
        {
            Assert.False(parsed);
            Assert.Null(value);
            return;
        }

        Assert.True(parsed);
        Assert.Equal(new AutomaticMetricValue { Enabled = enabled.Value }, value);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("enabled", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    public void Ignore_default_routes_accepts_only_booleans(string text, bool? ignore)
    {
        var parsed = RepairActionParser.TryParseDesired(
            RepairActionKind.SetTunIpv4IgnoreDefaultRoutes,
            text,
            out var value,
            out _);

        if (ignore is null)
        {
            Assert.False(parsed);
            Assert.Null(value);
            return;
        }

        Assert.True(parsed);
        Assert.Equal(new IgnoreDefaultRoutesValue { Ignore = ignore.Value }, value);
    }

    [Fact]
    public void Persistent_store_action_is_refused_by_the_plan()
    {
        var plan = RepairFixtures.Build(
            RepairFixtures.Preflight(),
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20", "persistent_store"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.StoreNotSupported);
    }

    [Fact]
    public void Duplicate_action_for_the_same_field_is_refused()
    {
        var plan = RepairFixtures.Build(
            RepairFixtures.Preflight(),
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "20"),
            RepairFixtures.Action("set_tun_ipv4_interface_metric", "21"));

        Assert.Equal(RepairPlanDecision.Refused, plan.Decision);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == RepairRefusalCode.DuplicateAction);
    }
}
