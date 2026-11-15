using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Preflight acceptance is deliberately strict: one critical unknown, one untrusted attribution,
/// one ambiguous candidate, one stale or cross-boot fact stops the target.
/// </summary>
public sealed class RepairPreflightTests
{
    [Fact]
    public void Complete_preflight_carries_identity_fields_store_fields_provenance_and_ttl()
    {
        var preflight = RepairFixtures.Preflight();

        Assert.True(preflight.IsComplete);
        Assert.Empty(preflight.Refusals);
        Assert.Equal(RepairPreflightStatus.Complete, preflight.Status);
        Assert.Equal(RepairFixtures.TunGuid, preflight.Interface.Value!.InterfaceGuid);
        Assert.Equal(0x0000000100000001UL, preflight.Interface.Value!.Luid);
        Assert.Equal(42, preflight.Interface.Value!.IfIndex);
        Assert.Equal(0, preflight.Interface.Value!.CompartmentId);
        Assert.Equal(RepairAddressFamily.Ipv4, preflight.Interface.Value!.Family);
        Assert.NotNull(preflight.Interface.Value!.DeviceInstanceId);
        Assert.Equal(3, preflight.Fields.Count);
        Assert.All(preflight.Fields, fact =>
        {
            Assert.True(fact.Ttl > TimeSpan.Zero);
            Assert.Equal(RepairFixtures.Observed, fact.ObservedAtUtc);
            Assert.Equal("synthetic-t13", fact.Source);
            Assert.NotEmpty(fact.EvidenceIds);
        });
        Assert.Contains("Broker", preflight.ClockNotice, StringComparison.Ordinal);
        Assert.NotEmpty(preflight.EvidenceIds);
    }

    [Theory]
    [InlineData(TunAttributionBasis.NameToken)]
    [InlineData(TunAttributionBasis.FakeIpAddressRange)]
    [InlineData(TunAttributionBasis.DiskConfigEnabled)]
    [InlineData(TunAttributionBasis.UserSelection)]
    [InlineData(TunAttributionBasis.AdapterDescription)]
    public void Untrusted_attribution_basis_never_becomes_verified_ownership(TunAttributionBasis basis)
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            attribution: RepairFixtures.AttributionFact(RepairFixtures.UntrustedAttribution(basis))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.AttributionUntrusted);
        Assert.Contains("attribution:trusted_source", preflight.MissingEvidence);
    }

    [Fact]
    public void Candidate_attribution_without_a_source_or_evidence_is_downgraded()
    {
        var attribution = new TunAttribution
        {
            Verdict = TunAttributionVerdict.Verified,
            Basis = TunAttributionBasis.ClientRuntimeApi,
            SourceId = null,
            EvidenceIds = Array.Empty<string>(),
        };

        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            attribution: RepairFixtures.AttributionFact(attribution)));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.AttributionUntrusted);
    }

    [Fact]
    public void A_candidate_verdict_is_not_enough_for_a_plan()
    {
        var attribution = RepairFixtures.VerifiedAttribution() with
        {
            Verdict = TunAttributionVerdict.Candidate,
        };

        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            attribution: RepairFixtures.AttributionFact(attribution)));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.AttributionUnknown);
    }

    [Fact]
    public void An_empty_candidate_list_is_rejected_instead_of_assuming_a_single_target()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            candidates: Array.Empty<RepairTargetCandidate>()));

        Assert.False(preflight.IsComplete);
        Assert.Contains(
            preflight.Refusals,
            refusal => refusal.Code == RepairRefusalCode.CriticalUnknown
                && refusal.Detail.Contains("候选", StringComparison.Ordinal));
        Assert.Contains("tun_candidates", preflight.MissingEvidence);
    }

    [Fact]
    public void Two_verified_candidates_make_the_target_ambiguous()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            candidates: new[]
            {
                RepairFixtures.Candidate(RepairFixtures.Identity(RepairFixtures.TunGuid)),
                RepairFixtures.Candidate(RepairFixtures.Identity(RepairFixtures.OtherTunGuid)),
            }));

        Assert.Equal(RepairPreflightStatus.Ambiguous, preflight.Status);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.InterfaceAmbiguous);
    }

    [Fact]
    public void The_protected_uplink_itself_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            uplink: RepairFixtures.UplinkFact(RepairFixtures.Uplink(RepairFixtures.TunGuid))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.ProtectedUplink);
    }

    [Fact]
    public void An_unknown_protected_uplink_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            uplink: RepairFixtures.UplinkFact(RepairFixtures.Uplink(known: false))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.UplinkUnknown);
    }

    [Fact]
    public void Attribution_from_a_previous_boot_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            attribution: RepairFixtures.AttributionFact(bootId: "boot-previous")));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.CrossBoot);
        Assert.Contains("attribution:tun_owner", preflight.MissingEvidence);
    }

    [Fact]
    public void A_stale_attribution_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            attribution: RepairFixtures.AttributionFact(
                observedAt: RepairFixtures.Now.AddSeconds(-60),
                ttl: TimeSpan.FromSeconds(30))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.FactStale);
        Assert.Contains("attribution:tun_owner", preflight.MissingEvidence);
    }

    [Fact]
    public void A_stale_uplink_fact_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            uplink: RepairFixtures.UplinkFact(
                observedAt: RepairFixtures.Now.AddSeconds(-60),
                ttl: TimeSpan.FromSeconds(30))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.FactStale);
        Assert.Contains("uplink:protection", preflight.MissingEvidence);
    }

    [Fact]
    public void Duplicate_active_store_field_facts_are_rejected_as_ambiguous()
    {
        var fields = RepairFixtures.Fields()
            .Concat(new[]
            {
                RepairFixtures.FieldFact(
                    RepairField.InterfaceMetric,
                    RepairFixtures.Metric(40)),
            })
            .ToArray();

        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(fields: fields));

        Assert.False(preflight.IsComplete);
        Assert.Contains(
            preflight.Refusals,
            refusal => refusal.Code == RepairRefusalCode.CriticalUnknown
                && refusal.Field == "interface_metric"
                && refusal.Detail.Contains("多个观测", StringComparison.Ordinal));
        Assert.Contains("field:interface_metric:active_store:unique", preflight.MissingEvidence);
    }

    [Fact]
    public void An_unknown_default_route_owner_is_rejected()
    {
        var uplink = RepairFixtures.Uplink(targetOwnsDefaultRoute: null);
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            uplink: RepairFixtures.UplinkFact(uplink)));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.UplinkUnknown);
        Assert.Contains("uplink:target_default_route", preflight.MissingEvidence);
    }

    [Theory]
    [InlineData(InterfaceKind.Ethernet)]
    [InlineData(InterfaceKind.Wifi)]
    [InlineData(InterfaceKind.Rndis)]
    [InlineData(InterfaceKind.Pppoe)]
    public void Real_uplink_kinds_are_rejected(InterfaceKind kind)
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            identity: RepairFixtures.Identity(kind: kind)));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.ProtectedUplink);
    }

    [Fact]
    public void A_non_tun_non_uplink_interface_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            identity: RepairFixtures.Identity(kind: InterfaceKind.Virtual)));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.NotTun);
    }

    [Fact]
    public void Missing_critical_identity_fields_are_reported_individually()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            identity: RepairFixtures.Identity(
                luid: null,
                ifIndex: null,
                compartmentId: null,
                deviceInstanceId: null)));

        Assert.False(preflight.IsComplete);
        var incomplete = Assert.Single(
            preflight.Refusals,
            refusal => refusal.Code == RepairRefusalCode.IdentityIncomplete);
        Assert.Contains("luid", incomplete.Detail);
        Assert.Contains("identity:luid", preflight.MissingEvidence);
        Assert.Contains("identity:device_instance_id", preflight.MissingEvidence);
    }

    [Fact]
    public void A_partial_identity_source_is_not_a_complete_identity()
    {
        var request = RepairFixtures.PreflightRequest();
        var partialRequest = request with
        {
            Interface = RepairFixtures.InterfaceFact(
                request.Interface.Value,
                status: EventStatus.Partial),
        };
        var partialResult = RepairPreflightEvaluator.Evaluate(partialRequest);

        Assert.False(partialResult.IsComplete);
        Assert.Contains(partialResult.Refusals, refusal => refusal.Code == RepairRefusalCode.IdentityUnknown);
    }

    [Fact]
    public void Cross_boot_identity_is_rejected()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            identity: RepairFixtures.Identity(bootId: "boot-previous")));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.CrossBoot);
    }

    [Fact]
    public void Stale_field_fact_is_rejected_after_its_ttl()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(
                observedAt: RepairFixtures.Now.AddSeconds(-60),
                ttl: TimeSpan.FromSeconds(30))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.FactStale);
        Assert.Contains("field:interface_metric", preflight.MissingEvidence);
    }

    [Fact]
    public void A_fact_from_the_future_is_rejected_as_clock_skew()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(observedAt: RepairFixtures.Now.AddSeconds(30))));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.ClockSkew);
    }

    [Fact]
    public void Each_required_active_store_field_is_a_preflight_gate()
    {
        foreach (var field in new[]
        {
            RepairField.InterfaceMetric,
            RepairField.AutomaticMetric,
            RepairField.IgnoreDefaultRoutes,
        })
        {
            var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
                fields: RepairFixtures.Fields(omit: field)));

            Assert.False(preflight.IsComplete);
            Assert.Contains(
                preflight.Refusals,
                refusal => refusal.Field == WireNames.Of(field)
                    && refusal.Code is RepairRefusalCode.CriticalUnknown or RepairRefusalCode.AutomaticMetricUnknown);
        }
    }

    [Fact]
    public void Automatic_metric_read_failure_has_its_own_refusal_code()
    {
        var preflight = RepairFixtures.Preflight(RepairFixtures.PreflightRequest(
            fields: RepairFixtures.Fields(automaticStatus: EventStatus.AccessDenied)));

        Assert.False(preflight.IsComplete);
        Assert.Contains(preflight.Refusals, refusal => refusal.Code == RepairRefusalCode.AutomaticMetricUnknown);
    }
}
