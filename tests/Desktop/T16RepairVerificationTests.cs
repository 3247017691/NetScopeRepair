using NetScope.Application;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Desktop.Tests;

/// <summary>
/// RepairVerifier (planned-delta technical acceptance) and DiagnosticComparabilityGate
/// (TopologyChanged rejection without clearing flags). Pure functions; no machine access.
/// </summary>
public sealed class T16RepairVerificationTests
{
    private static readonly RepairVerificationDelta InterfaceMetric = new()
    {
        Field = RepairField.InterfaceMetric,
        Store = RepairStore.ActiveStore,
        BeforeText = "interface_metric:uint:35:manual_configured",
        DesiredText = "interface_metric:uint:20:manual_configured",
    };

    private static RepairVerificationSnapshot Snapshot(
        string target = "tun|ipv4|7d4a2f0e",
        string method = "GET",
        string family = "ipv4",
        string options = "opts-1",
        string? route = "route|tun",
        bool protectedUplink = false,
        string metric = "interface_metric:uint:35:manual_configured",
        string automatic = "automatic_metric:bool:enabled",
        string ignore = "ignore_default_routes:bool:false") => new()
    {
        TargetFingerprint = target,
        Method = method,
        AddressFamily = family,
        OptionsKey = options,
        RouteInterfaceFingerprint = route,
        IsProtectedUplink = protectedUplink,
        FieldValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["interface_metric|active_store"] = metric,
            ["automatic_metric|active_store"] = automatic,
            ["ignore_default_routes|active_store"] = ignore,
        },
    };

    private static RepairVerificationSnapshot After(
        string metric = "interface_metric:uint:20:manual_configured") => Snapshot(metric: metric);

    [Fact]
    public void A_planned_delta_with_unchanged_target_route_and_non_target_fields_passes()
    {
        var result = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), After());

        Assert.Equal(RepairVerificationStatus.Passed, result.Status);
        Assert.Empty(result.Refusals);
        Assert.Contains(result.Checks, check => check.Name == "route_unchanged" && check.Passed);
        Assert.Contains(result.Checks, check => check.Name == "non_target_unchanged" && check.Passed);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("method")]
    [InlineData("family")]
    [InlineData("options")]
    public void A_changed_target_method_family_or_options_fails_even_when_the_field_matches(string dimension)
    {
        var after = dimension switch
        {
            "target" => Snapshot(target: "other-target", metric: "interface_metric:uint:20:manual_configured"),
            "method" => Snapshot(method: "HEAD", metric: "interface_metric:uint:20:manual_configured"),
            "family" => Snapshot(family: "ipv6", metric: "interface_metric:uint:20:manual_configured"),
            _ => Snapshot(options: "opts-2", metric: "interface_metric:uint:20:manual_configured"),
        };
        var result = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), after);

        Assert.Equal(RepairVerificationStatus.Failed, result.Status);
        Assert.NotEmpty(result.Refusals);
    }

    [Fact]
    public void A_changed_route_or_protected_uplink_fails_the_technical_verification()
    {
        var routeChanged = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), Snapshot(
            route: "route|ethernet", metric: "interface_metric:uint:20:manual_configured"));
        Assert.Equal(RepairVerificationStatus.Failed, routeChanged.Status);
        Assert.Contains(routeChanged.Refusals, refusal => refusal.StartsWith("route_unchanged", StringComparison.Ordinal));

        var protectedUplink = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), Snapshot(
            protectedUplink: true, metric: "interface_metric:uint:20:manual_configured"));
        Assert.Equal(RepairVerificationStatus.Failed, protectedUplink.Status);
        Assert.Contains(protectedUplink.Refusals, refusal =>
            refusal.StartsWith("protected_uplink_not_written", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unplanned_non_target_change_fails_instead_of_being_silently_accepted()
    {
        var after = Snapshot(metric: "interface_metric:uint:20:manual_configured");
        after = after with
        {
            FieldValues = after.FieldValues.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == "automatic_metric|active_store" ? "automatic_metric:bool:disabled" : pair.Value,
                StringComparer.Ordinal),
        };

        var result = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), after);

        Assert.Equal(RepairVerificationStatus.Failed, result.Status);
        Assert.Contains(result.Refusals, refusal => refusal.Contains("non_target_unchanged", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_after_value_is_insufficient_not_a_pass()
    {
        var result = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), null);

        Assert.Equal(RepairVerificationStatus.Insufficient, result.Status);
        Assert.Contains(result.Refusals, refusal => refusal.StartsWith("observation_complete", StringComparison.Ordinal));
    }

    [Fact]
    public void An_after_value_that_does_not_match_the_planned_desired_value_fails()
    {
        var result = RepairVerifier.Verify(new[] { InterfaceMetric }, Snapshot(), After("interface_metric:uint:21:manual_configured"));

        Assert.Equal(RepairVerificationStatus.Failed, result.Status);
        Assert.Contains(result.Refusals, refusal => refusal.Contains("delta:interface_metric|active_store", StringComparison.Ordinal));
    }

    // ---- DiagnosticComparabilityGate ----

    private static ProbeResult Probe(
        string target = "target-1",
        string method = "GET",
        string family = "ipv4",
        string options = "opts-1",
        bool topologyChanged = false,
        bool timingComparable = true,
        string group = "group-1",
        string window = "window-1") => new()
    {
        ProbeId = "probe-" + Guid.NewGuid().ToString("N")[..6],
        ProbeGroupId = group,
        TargetId = target,
        Path = ProbePath.NoExplicitProxy,
        Status = EventStatus.Ok,
        StartedAt = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero),
        DurationMs = 100,
        Classification = ProbeClassification.ReachableExpected,
        Method = method,
        AddressFamily = family,
        RequestOptionsKey = options,
        ObservationWindowId = window,
        TimingComparable = timingComparable,
        TopologyChanged = topologyChanged,
    };

    [Fact]
    public void TopologyChanged_makes_the_probes_incomparable_and_the_flag_is_not_cleared()
    {
        var before = Probe(topologyChanged: true);
        var after = Probe();

        var result = DiagnosticComparabilityGate.Evaluate(before, after);

        Assert.Equal(DiagnosticComparabilityStatus.Incomparable, result.Status);
        Assert.Contains("topology_changed", result.Unmet);
        Assert.Contains("不清零", result.Text, StringComparison.Ordinal);
        Assert.True(before.TopologyChanged); // the gate never mutates the producer's flag
        Assert.True(Probe(topologyChanged: true).TopologyChanged);
    }

    [Fact]
    public void The_same_target_method_family_options_and_timing_are_comparable()
    {
        var result = DiagnosticComparabilityGate.Evaluate(Probe(), Probe());

        Assert.True(result.Comparable);
        Assert.Empty(result.Unmet);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("method")]
    [InlineData("family")]
    [InlineData("options")]
    public void Changed_target_method_family_or_options_is_incomparable(string dimension)
    {
        var after = dimension switch
        {
            "target" => Probe(target: "target-2"),
            "method" => Probe(method: "HEAD"),
            "family" => Probe(family: "ipv6"),
            _ => Probe(options: "opts-2"),
        };

        var result = DiagnosticComparabilityGate.Evaluate(Probe(), after);

        Assert.Equal(DiagnosticComparabilityStatus.Incomparable, result.Status);
        Assert.Contains(result.Unmet, item => item == dimension + "_changed");
    }

    [Fact]
    public void Missing_probe_or_unproven_timing_is_not_comparable()
    {
        Assert.Equal(
            DiagnosticComparabilityStatus.Insufficient,
            DiagnosticComparabilityGate.Evaluate(null, Probe()).Status);

        var timing = DiagnosticComparabilityGate.Evaluate(Probe(), Probe(timingComparable: false));
        Assert.Equal(DiagnosticComparabilityStatus.Incomparable, timing.Status);
        Assert.Contains("timing_not_comparable", timing.Unmet);
    }

    [Fact]
    public void Probe_flags_are_immutable_and_comparability_does_not_rewrite_them()
    {
        var changed = Probe(topologyChanged: true);
        _ = DiagnosticComparabilityGate.Evaluate(changed, changed);

        Assert.True(changed.TopologyChanged);
        Assert.True(changed.TimingComparable);
    }
}
