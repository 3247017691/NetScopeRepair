using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// A preflight is a point-in-time reading. Changing the target locator, the identity, a field
/// value or the protected uplink after preview must invalidate the authorization.
/// </summary>
public sealed class RepairRevalidationTests
{
    private static (RepairPreflight Planned, RepairPreflightRequest Request) PlannedPreflight()
    {
        var request = RepairFixtures.PreflightRequest(fields: RepairFixtures.Fields(
            metric: RepairFixtures.Metric(35),
            automatic: RepairFixtures.Automatic(false),
            ignore: RepairFixtures.Ignore(false)));
        return (RepairFixtures.Preflight(request), request);
    }

    [Fact]
    public void Unchanged_facts_revalidate_successfully()
    {
        var (planned, request) = PlannedPreflight();

        var result = RepairPreflightRevalidator.Revalidate(planned, request);

        Assert.True(result.IsValid);
        Assert.False(result.RequiresNewPreview);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void An_if_index_change_requires_a_new_preview_and_is_not_written()
    {
        var (planned, _) = PlannedPreflight();
        var current = RepairFixtures.PreflightRequest(
            identity: RepairFixtures.Identity(ifIndex: 43),
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false)));

        var result = RepairPreflightRevalidator.Revalidate(planned, current);

        Assert.False(result.IsValid);
        Assert.True(result.RequiresNewPreview);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairRefusalCode.TargetDrift);
    }

    [Theory]
    [InlineData("luid")]
    [InlineData("device")]
    public void A_rebuilt_interface_is_rejected(string change)
    {
        var (planned, _) = PlannedPreflight();
        var identity = change == "luid"
            ? RepairFixtures.Identity(luid: 0x0000000100000002UL)
            : RepairFixtures.Identity(deviceInstanceId: @"ROOT\NET\0002");
        var current = RepairFixtures.PreflightRequest(
            identity: identity,
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false)));

        var result = RepairPreflightRevalidator.Revalidate(planned, current);

        Assert.False(result.IsValid);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairRefusalCode.InterfaceRebuilt);
    }

    [Fact]
    public void An_external_field_value_change_is_a_target_drift()
    {
        var (planned, _) = PlannedPreflight();
        var current = RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(30),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false)));

        var result = RepairPreflightRevalidator.Revalidate(planned, current);

        Assert.False(result.IsValid);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairRefusalCode.TargetDrift);
        Assert.Contains(
            result.Refusals,
            refusal => refusal.Detail.Contains("interface_metric", StringComparison.Ordinal)
                && refusal.Detail.Contains("重新预览", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_protected_uplink_is_rejected()
    {
        var (planned, _) = PlannedPreflight();
        var current = RepairFixtures.PreflightRequest(
            uplink: RepairFixtures.UplinkFact(RepairFixtures.Uplink(Guid.Parse("2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e"))),
            fields: RepairFixtures.Fields(
                metric: RepairFixtures.Metric(35),
                automatic: RepairFixtures.Automatic(false),
                ignore: RepairFixtures.Ignore(false)));

        var result = RepairPreflightRevalidator.Revalidate(planned, current);

        Assert.False(result.IsValid);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairRefusalCode.UplinkChanged);
    }
}
