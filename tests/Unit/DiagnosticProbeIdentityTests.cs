using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

public sealed class DiagnosticProbeIdentityTests
{
    [Fact]
    public void Fingerprint_is_stable_across_groups_paths_proxy_identity_and_status_order()
    {
        var plan = EvidenceBuilder.Plan() with
        {
            Target = EvidenceBuilder.Plan().Target with { ExpectedStatusCodes = new[] { 204, 200 } },
        };
        var another = plan with
        {
            ProbeGroupId = "other-group",
            Paths = new[] { ProbePath.NoExplicitProxy },
            ProxyEndpointId = "another-proxy",
            ExplicitProxyEndpoint = "http://127.0.0.1:9999",
            BootId = "another-boot",
            ObservationWindowId = "another-window",
            StartedAt = plan.StartedAt.AddDays(1),
            MaxConcurrency = 1,
            Target = plan.Target with { TargetId = "another-logical-name", ExpectedStatusCodes = new[] { 200, 204, 200 } },
        };
        Assert.Equal(ProbeIdentity.OptionsKey(plan), ProbeIdentity.OptionsKey(another));
        Assert.Matches("^[a-f0-9]{64}$", ProbeIdentity.OptionsKey(plan));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("method")]
    [InlineData("statuses")]
    [InlineData("family")]
    [InlineData("probe_timeout")]
    [InlineData("group_timeout")]
    [InlineData("body_limit")]
    public void Every_fixed_request_option_changes_the_fingerprint(string option)
    {
        var plan = EvidenceBuilder.Plan();
        var changed = option switch
        {
            "endpoint" => plan with { Target = plan.Target with { Endpoint = "https://example.invalid/another" } },
            "method" => plan with { Target = plan.Target with { Method = "HEAD" } },
            "statuses" => plan with { Target = plan.Target with { ExpectedStatusCodes = new[] { 200 } } },
            "family" => plan with { AddressFamily = "ipv6" },
            "probe_timeout" => plan with { PerProbeTimeout = TimeSpan.FromSeconds(1) },
            "group_timeout" => plan with { GroupBudget = TimeSpan.FromSeconds(1) },
            _ => plan with { MaxResponseBytes = 4096 },
        };
        Assert.NotEqual(ProbeIdentity.OptionsKey(plan), ProbeIdentity.OptionsKey(changed));
    }

    [Fact]
    public void New_evidence_metadata_defaults_to_unknown_and_unproven_comparability()
    {
        var record = new EvidenceRecord { EvidenceId = "record", Kind = "test", Summary = "test" };
        Assert.Equal(EventStatus.Unknown, record.Status);
        Assert.Empty(record.SourceEventIds);
        Assert.Equal("1", record.TransformationVersion);
        var probe = EvidenceBuilder.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok) with
        {
            TimingComparable = false,
            StartedMonotonicMs = null,
            ObservationWindowId = null,
            BootId = null,
        };
        Assert.False(probe.TimingComparable);
        var group = new ProbeGroupResult
        {
            ProbeGroupId = "group", StartedAt = DateTimeOffset.UnixEpoch, DurationMs = 0, Results = new[] { probe },
        };
        Assert.False(group.TimingComparable);
    }
}
