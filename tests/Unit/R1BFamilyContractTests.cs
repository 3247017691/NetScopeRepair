using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Diagnostics.Rules;
using NetScope.Domain;
using NetScope.Probes;
using NetScope.Storage;

namespace NetScope.Unit.Tests;

/// <summary>
/// R1-B contract coverage: the three family facts stay separate, diagnostics consume the requested
/// policy plus the proven connection family, DX-011 gets compatible input, and legacy events still
/// replay through the same strict reader.
/// </summary>
public sealed class R1BFamilyContractTests
{
    [Theory]
    [InlineData(null, "any")]
    [InlineData("", "any")]
    [InlineData("any", "any")]
    [InlineData("dual", "any")]
    [InlineData("unspecified", "any")]
    [InlineData("ipv4", "ipv4")]
    [InlineData("V4", "ipv4")]
    [InlineData("ipv6", "ipv6")]
    [InlineData("v6", "ipv6")]
    [InlineData("carrier-pigeon", "carrier-pigeon")]
    public void The_requested_policy_is_normalized_without_guessing_unknown_values(string? raw, string expected)
    {
        Assert.Equal(expected, ProbeFamilyPolicy.Normalize(raw));
        Assert.Equal(expected != "carrier-pigeon", ProbeFamilyPolicy.IsKnown(raw));
    }

    [Fact]
    public async Task Runner_preserves_the_three_family_facts_from_the_transport()
    {
        var transport = new FakeTransport((request, _) => Task.FromResult(request.Path switch
        {
            ProbePath.NoExplicitProxy => TransportOutcome.Reachable(204) with
            {
                RequestedAddressFamily = "ipv4",
                LocalConnectionAddressFamily = "ipv4",
                TargetAddressFamily = "ipv4",
                TargetAddressFamilyStatus = ProbeTargetFamilyStatus.Observed,
            },
            ProbePath.ExplicitHttpProxy => TransportOutcome.Reachable(204) with
            {
                RequestedAddressFamily = "ipv4",
                LocalConnectionAddressFamily = "ipv4",
                TargetAddressFamily = null,
                TargetAddressFamilyStatus = ProbeTargetFamilyStatus.UnknownThroughProxy,
            },
            _ => TransportOutcome.Reachable(204) with
            {
                RequestedAddressFamily = "ipv4",
                LocalConnectionAddressFamily = null,
                TargetAddressFamily = null,
                TargetAddressFamilyStatus = ProbeTargetFamilyStatus.UnknownSystemDefault,
            },
        }));
        var plan = ProbePlans.Default() with { AddressFamily = "ipv4" };

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        var local = result.Results.Single(probe => probe.Path == ProbePath.NoExplicitProxy);
        Assert.Equal("ipv4", local.RequestedAddressFamily);
        Assert.Equal("ipv4", local.LocalConnectionAddressFamily);
        Assert.Equal("ipv4", local.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.Observed, local.TargetAddressFamilyStatus);

        var proxy = result.Results.Single(probe => probe.Path == ProbePath.ExplicitHttpProxy);
        Assert.Equal("ipv4", proxy.RequestedAddressFamily);
        Assert.Equal("ipv4", proxy.LocalConnectionAddressFamily);
        Assert.Null(proxy.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.UnknownThroughProxy, proxy.TargetAddressFamilyStatus);

        var system = result.Results.Single(probe => probe.Path == ProbePath.SystemDefault);
        Assert.Null(system.LocalConnectionAddressFamily);
        Assert.Null(system.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.UnknownSystemDefault, system.TargetAddressFamilyStatus);

        // The legacy field stays the raw request value for old readers.
        Assert.All(result.Results, probe => Assert.Equal("ipv4", probe.AddressFamily));
    }

    [Fact]
    public async Task An_unsupported_path_gap_does_not_make_the_whole_group_incomparable()
    {
        var outcomes = new Queue<TransportOutcome>(new[]
        {
            TransportOutcome.Reachable(204) with
            {
                RequestedAddressFamily = "any",
                TargetAddressFamilyStatus = ProbeTargetFamilyStatus.Observed,
                LocalConnectionAddressFamily = "ipv4",
                TargetAddressFamily = "ipv4",
            },
            TransportOutcome.Failure(EventStatus.Unsupported, "no_address_for_requested_family", "resolve"),
            TransportOutcome.Reachable(204) with
            {
                RequestedAddressFamily = "any",
                TargetAddressFamilyStatus = ProbeTargetFamilyStatus.UnknownThroughProxy,
            },
        });
        var transport = new FakeTransport((_, _) => Task.FromResult(outcomes.Dequeue()));
        var plan = ProbePlans.Default() with { AddressFamily = null };

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.True(result.TimingComparable);
        Assert.All(result.Results, probe => Assert.True(probe.TimingComparable));
    }

    [Fact]
    public void DX011_uses_the_local_connection_family_not_the_unknown_proxy_target_family()
    {
        var probe = EvidenceBuilder.Probe(
                ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 407, proxyIsLocal: true)
            with
        {
            AddressFamily = "any",
            RequestedAddressFamily = "any",
            LocalConnectionAddressFamily = "ipv4",
            TargetAddressFamily = null,
            TargetAddressFamilyStatus = ProbeTargetFamilyStatus.UnknownThroughProxy,
            TransportError = "proxy_authentication_required",
        };
        var listening = EvidenceBuilder.Record("listen-empty", "listening_ports", ("count", "0"));

        var diagnosis = new Dx011CollectionContradiction().Evaluate(
            EvidenceBuilder.Evidence(probes: new[] { probe }, records: new[] { listening }));

        Assert.NotNull(diagnosis);
        Assert.Equal(EvidenceGrade.Insufficient, diagnosis.Level);
        Assert.Contains(probe.ProbeId, diagnosis.EvidenceIds);
        Assert.Contains("同一地址族", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ipv6")]
    [InlineData(null)]
    public void DX011_rejects_a_probe_whose_local_connection_family_is_unknown_or_different(string? localFamily)
    {
        var probe = EvidenceBuilder.Probe(
                ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 407, proxyIsLocal: true)
            with
        {
            AddressFamily = "any",
            RequestedAddressFamily = "any",
            LocalConnectionAddressFamily = localFamily,
            TargetAddressFamily = null,
            TargetAddressFamilyStatus = ProbeTargetFamilyStatus.UnknownThroughProxy,
        };
        var listening = EvidenceBuilder.Record("listen-empty", "listening_ports", ("count", "0"));

        Assert.Null(new Dx011CollectionContradiction().Evaluate(
            EvidenceBuilder.Evidence(probes: new[] { probe }, records: new[] { listening })));
    }

    [Fact]
    public void DX008_merges_unspecified_and_any_policy_samples_but_not_ipv4_and_ipv6()
    {
        var unspecified = EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok)
            with { AddressFamily = null, RequestedAddressFamily = null, TargetAddressFamilyStatus = null };
        var any = EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout, transportError: "Timeout")
            with { AddressFamily = "any", RequestedAddressFamily = "any", TargetAddressFamilyStatus = null };

        var merged = new Dx008IntermittentFailure().Evaluate(
            EvidenceBuilder.Evidence(probes: new[] { unspecified, any }));
        Assert.NotNull(merged);
        Assert.Equal(EvidenceGrade.Observed, merged.Level);

        var ipv4 = EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok)
            with { AddressFamily = "ipv4", RequestedAddressFamily = "ipv4", TargetAddressFamilyStatus = null };
        var ipv6 = EvidenceBuilder.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Timeout, transportError: "Timeout")
            with { AddressFamily = "ipv6", RequestedAddressFamily = "ipv6", TargetAddressFamilyStatus = null };
        Assert.Null(new Dx008IntermittentFailure().Evaluate(
            EvidenceBuilder.Evidence(probes: new[] { ipv4, ipv6 })));
    }

    [Fact]
    public void Legacy_probe_events_without_the_new_fields_still_replay_through_the_strict_reader()
    {
        var modern = EvidenceBuilder.Probe(
            ProbePath.ExplicitHttpProxy, EventStatus.Ok, httpStatus: 407, proxyIsLocal: true);
        var codecPayload = EvidenceEventCodec.Probe(modern, "event-r1b-legacy");
        // Strip the R1-B fields to simulate a package written by the old producer.
        var probeNode = codecPayload["probe_result"]!.AsObject();
        probeNode.Remove("requested_address_family");
        probeNode.Remove("local_connection_address_family");
        probeNode.Remove("target_address_family");
        probeNode.Remove("target_address_family_status");

        var envelope = new EventEnvelope
        {
            SchemaVersion = EventEnvelope.SupportedSchemaVersion,
            EventId = "event-r1b-legacy",
            SessionId = "session-r1b-legacy",
            BootId = "boot-1",
            Sequence = 1,
            ObservedAt = modern.StartedAt,
            MonotonicMs = modern.StartedMonotonicMs!.Value,
            EventType = EventType.ProbeResult,
            Status = modern.Status,
            Source = "probe-runner",
            SourceVersion = "1.1",
            Synthetic = true,
            Refs = new EventRefs { ProbeId = modern.ProbeId, EvidenceId = modern.ProbeId, ProbeGroupId = modern.ProbeGroupId },
            Payload = codecPayload,
        };

        var replay = new ReplayService().ReplayText(EnvelopeJson.Serialize(envelope) + "\n");

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var replayed = Assert.Single(replay.UnscopedEvidence!.Probes);
        Assert.Equal(modern.ProbeId, replayed.ProbeId);
        Assert.Equal("ipv4", replayed.AddressFamily);
        Assert.Null(replayed.RequestedAddressFamily);
        Assert.Null(replayed.LocalConnectionAddressFamily);
        Assert.Null(replayed.TargetAddressFamily);
        Assert.Null(replayed.TargetAddressFamilyStatus);

        // The legacy record still reaches DX-011 through the documented fallback path.
        var listening = EvidenceBuilder.Record("listen-empty", "listening_ports", ("count", "0"));
        var diagnosis = new Dx011CollectionContradiction().Evaluate(
            EvidenceBuilder.Evidence(probes: new[] { replayed }, records: new[] { listening }));
        Assert.NotNull(diagnosis);
    }
}

