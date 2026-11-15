using System.Text.Json;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// The capability matrix is the only gate that can ever permit a write. In this delivery every
/// real field action remains unavailable or disallowed, with a written reason.
/// </summary>
public sealed class PlatformCapabilityTests
{
    [Theory]
    [InlineData(RepairActionKind.SetTunIpv4InterfaceMetric)]
    [InlineData(RepairActionKind.SetTunIpv4AutomaticMetric)]
    [InlineData(RepairActionKind.SetTunIpv4IgnoreDefaultRoutes)]
    public void Default_matrix_keeps_every_field_action_unavailable_with_a_reason(RepairActionKind action)
    {
        var capability = PlatformCapabilityMatrix.UnverifiedDefault.Resolve(
            action,
            RepairStore.ActiveStore,
            RepairAddressFamily.Ipv4);

        Assert.Equal(CapabilityStatus.Unavailable, capability.Status);
        Assert.False(capability.IsAvailable);
        Assert.False(capability.HasCompleteVerification);
        Assert.Equal("no_verified_platform_experiment", capability.ReasonCode);
        Assert.Contains("未", capability.Reason, StringComparison.Ordinal);
        Assert.Empty(capability.EvidenceIds);
        Assert.Null(capability.ExperimentId);
    }

    [Fact]
    public void Recover_transaction_is_unavailable_because_the_journal_does_not_exist_yet()
    {
        var capability = PlatformCapabilityMatrix.UnverifiedDefault.Resolve(
            RepairActionKind.RecoverTransaction,
            RepairStore.ActiveStore,
            RepairAddressFamily.Ipv4);

        Assert.Equal(CapabilityStatus.Unavailable, capability.Status);
        Assert.Equal("transaction_journal_not_implemented", capability.ReasonCode);
        Assert.Contains("journal", capability.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(capability.IsAvailable);
    }

    [Fact]
    public void Persistent_store_is_disallowed_even_for_a_verified_action()
    {
        var capability = PlatformCapabilityMatrix.UnverifiedDefault.Resolve(
            RepairActionKind.SetTunIpv4InterfaceMetric,
            RepairStore.PersistentStore,
            RepairAddressFamily.Ipv4);

        Assert.Equal(CapabilityStatus.Disallowed, capability.Status);
        Assert.Equal("persistent_store_not_in_v1", capability.ReasonCode);
        Assert.Contains("PersistentStore", capability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ipv6_is_disallowed_in_v1()
    {
        var capability = PlatformCapabilityMatrix.UnverifiedDefault.Resolve(
            RepairActionKind.SetTunIpv4InterfaceMetric,
            RepairStore.ActiveStore,
            RepairAddressFamily.Ipv6);

        Assert.Equal(CapabilityStatus.Disallowed, capability.Status);
        Assert.Equal("family_not_in_v1", capability.ReasonCode);
    }

    [Fact]
    public void A_complete_synthetic_verification_resolves_available_only_in_a_synthetic_matrix()
    {
        var capability = RepairFixtures.VerifiedCapability(RepairActionKind.SetTunIpv4InterfaceMetric);
        var matrix = RepairFixtures.SyntheticMatrix(capability);

        var resolved = matrix.Resolve(
            RepairActionKind.SetTunIpv4InterfaceMetric,
            RepairStore.ActiveStore,
            RepairAddressFamily.Ipv4);

        Assert.True(matrix.IsSynthetic);
        Assert.Equal(1, matrix.VerifiedCount);
        Assert.True(resolved.IsAvailable);
        Assert.Equal(CapabilityStatus.Available, resolved.Status);
        Assert.NotNull(resolved.ExperimentId);
        Assert.NotEmpty(resolved.EvidenceIds);
    }

    [Fact]
    public void A_verified_record_missing_the_coupling_check_is_downgraded_to_unavailable()
    {
        var capability = RepairFixtures.VerifiedCapability(
            RepairActionKind.SetTunIpv4AutomaticMetric,
            includeRequiredChecks: false);
        var matrix = RepairFixtures.SyntheticMatrix(capability);

        var resolved = matrix.Resolve(
            RepairActionKind.SetTunIpv4AutomaticMetric,
            RepairStore.ActiveStore,
            RepairAddressFamily.Ipv4);

        Assert.Equal(CapabilityStatus.Unavailable, resolved.Status);
        Assert.Equal("capability_record_incomplete", resolved.ReasonCode);
        Assert.False(resolved.IsAvailable);
    }

    [Fact]
    public void A_verified_record_cannot_exist_without_evidence_checks_and_an_experiment()
    {
        var matrix = PlatformCapabilityMatrix.ForSyntheticTests(
            RepairFixtures.TestEnvironment,
            Array.Empty<PlatformCapability>());
        var signature = matrix.SignatureFor(
            RepairActionKind.SetTunIpv4InterfaceMetric,
            RepairStore.ActiveStore,
            RepairAddressFamily.Ipv4);

        Assert.Throws<ArgumentException>(() =>
        {
            _ = PlatformCapability.Verified(
                signature,
                "cap-missing-evidence",
                "experiment",
                RepairFixtures.Now,
                Array.Empty<string>(),
                RepairExperimentChecks.RequiredFor(RepairActionKind.SetTunIpv4InterfaceMetric));
        });
    }

    [Fact]
    public void A_synthetic_verified_capability_cannot_make_a_plan_executable()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
            metric: RepairFixtures.Metric(35),
            automatic: RepairFixtures.Automatic(false),
            ignore: RepairFixtures.Ignore(false))));
        var capability = RepairFixtures.VerifiedCapability(RepairActionKind.SetTunIpv4InterfaceMetric);
        var matrix = RepairFixtures.SyntheticMatrix(capability);

        var plan = new RepairPlanBuilder().Build(RepairFixtures.PlanRequest(
            preflight,
            new[] { RepairFixtures.Action("set_tun_ipv4_interface_metric", "20") },
            matrix));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(RepairActionEligibility.Allowed, action.Eligibility);
        Assert.Empty(action.BlockingReasons);
        Assert.True(matrix.IsSynthetic);
        Assert.False(plan.ExecutionAllowed);
        Assert.False(plan.CanAuthorize);
    }

    [Fact]
    public void The_experiment_asset_declares_every_action_as_not_run()
    {
        var manifest = LoadManifest();

        var issues = PlatformExperimentManifestValidator.Validate(manifest);

        Assert.Empty(issues);
        Assert.Equal("not_run", manifest.Status);
        Assert.All(manifest.Rows, row =>
        {
            Assert.False(row.Executed);
            Assert.Equal("not_run", row.Result);
        });
        Assert.Contains(manifest.Rows, row => row.Action == RepairActionKind.SetTunIpv4InterfaceMetric);
        Assert.Contains(manifest.Rows, row => row.Action == RepairActionKind.SetTunIpv4AutomaticMetric);
        Assert.Contains(manifest.Rows, row => row.Action == RepairActionKind.SetTunIpv4IgnoreDefaultRoutes);
        Assert.Contains(manifest.Rows, row => row.Action == RepairActionKind.RecoverTransaction);
    }

    [Fact]
    public void The_experiment_asset_cannot_claim_a_pass()
    {
        var manifest = LoadManifest();
        var claimed = manifest with
        {
            Rows = manifest.Rows
                .Select((row, index) => index == 0
                    ? row with { Executed = true, Result = "pass" }
                    : row)
                .ToArray(),
        };

        var issues = PlatformExperimentManifestValidator.Validate(claimed);

        Assert.Contains(issues, issue => issue.Contains("已执行", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("not_run", StringComparison.Ordinal));
    }

    [Fact]
    public void The_experiment_asset_rejects_a_missing_coupling_check()
    {
        var manifest = LoadManifest();
        var automatic = manifest.Rows.Single(row => row.Action == RepairActionKind.SetTunIpv4AutomaticMetric);
        var changed = manifest with
        {
            Rows = manifest.Rows
                .Select(row => row.ExperimentId == automatic.ExperimentId
                    ? row with
                    {
                        Checks = row.Checks
                            .Where(check => check != RepairExperimentChecks.CouplingClosed)
                            .ToArray(),
                    }
                    : row)
                .ToArray(),
        };

        var issues = PlatformExperimentManifestValidator.Validate(changed);

        Assert.Contains(issues, issue => issue.Contains("耦合", StringComparison.Ordinal));
    }

    private static PlatformExperimentManifest LoadManifest()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "experiments",
            "platform-experiment-manifest.json");
        Assert.True(File.Exists(path), "Experiment manifest was not copied to the test output: " + path);
        return JsonSerializer.Deserialize<PlatformExperimentManifest>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Experiment manifest failed to deserialize.");
    }
}
