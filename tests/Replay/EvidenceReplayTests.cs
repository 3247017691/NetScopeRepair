using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Application.Mock;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

public sealed class EvidenceReplayTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-04T00:00:00Z");
    private const string Boot = "boot-codec";
    private const string Window = "window-codec";

    [Fact]
    public async Task Saved_facts_reproduce_diagnoses_and_preserve_times_and_canonical_evidence_links()
    {
        var input = await Scenario();
        var original = DiagnosticEngine.CreateDefault().EvaluateDetailed(input);
        Assert.Empty(original.Failures);
        Assert.Contains(original.Diagnoses, d => d.RuleId == "DX-002" && d.Level == EvidenceGrade.Supported);
        Assert.Contains(original.Diagnoses, d => d.RuleId == "DX-008" && d.Level == EvidenceGrade.Observed);
        var events = Bundle(input, original.Diagnoses);
        using var workspace = new TempWorkspace();
        var path = workspace.File("evidence.ndjson");
        await Save(path, events);
        var replay = new ReplayService().Replay(path);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.NotNull(replay.Evidence);
        Assert.NotEmpty(replay.Evidence!.Snapshot!.Routes.Value!);
        Assert.Equal("198.18.0.2", replay.Evidence.Snapshot.Dns.Value!.ServersByInterface[40][0]);
        Assert.Equal(input.Probes.Select(p => p.StartedAt), replay.Evidence.Probes.Select(p => p.StartedAt));
        Assert.Equal(input.Probes.Select(p => p.StartedMonotonicMs), replay.Evidence.Probes.Select(p => p.StartedMonotonicMs));
        var secondProbe = replay.Log.Events.Single(e => e.Refs.ProbeId == "probe-2");
        Assert.Equal(Origin.AddSeconds(20), secondProbe.ObservedAt);
        Assert.Equal(20000, secondProbe.MonotonicMs);
        var definitions = replay.Log.Events.Where(e => e.Refs.EvidenceId is not null).Select(e => e.Refs.EvidenceId).ToHashSet();
        Assert.All(replay.Diagnoses.SelectMany(d => d.EvidenceIds.Concat(d.CounterEvidenceIds)), id => Assert.Contains(id, definitions));
        var recomputed = DiagnosticEngine.CreateDefault().EvaluateDetailed(replay.Evidence);
        Assert.Empty(recomputed.Failures);
        Assert.Equal(Fingerprint(original.Diagnoses), Fingerprint(recomputed.Diagnoses));
        Assert.Equal(Fingerprint(original.Diagnoses), Fingerprint(replay.Diagnoses));
        Assert.All(replay.Evidence.Records, r => Assert.All(r.SourceEventIds,
            source => Assert.Contains(replay.Log.Events, e => e.EventId == source)));
    }

    [Theory]
    [InlineData("missing_diagnosis_reference")]
    [InlineData("missing_source_event")]
    [InlineData("mismatched_canonical_id")]
    [InlineData("rewritten_start")]
    [InlineData("ok_domain_without_data")]
    [InlineData("unavailable_domain_with_data")]
    [InlineData("incomplete_domain_without_reason")]
    public async Task Semantically_corrupt_evidence_is_reported_even_when_the_envelope_is_valid(string fault)
    {
        var input = await Scenario();
        var diagnoses = DiagnosticEngine.CreateDefault().Evaluate(input);
        var events = Bundle(input, diagnoses).Select(e => e with { Payload = e.Payload.DeepClone().AsObject() }).ToArray();
        switch (fault)
        {
            case "missing_diagnosis_reference":
                events.First(e => e.EventType == EventType.Diagnosis).Payload["diagnosis"]!["evidence_ids"] = new JsonArray("absent-evidence");
                break;
            case "missing_source_event":
                events.First(e => e.Refs.EvidenceId is not null).Payload["evidence_metadata"]!["source_event_ids"] = new JsonArray("absent-event");
                break;
            case "mismatched_canonical_id":
                events[0] = events[0] with { Refs = events[0].Refs with { EvidenceId = "wrong-id" } };
                break;
            case "rewritten_start":
                events.First(e => e.Refs.ProbeId == "probe-2").Payload["probe_result"]!["started_at"] = Origin.AddSeconds(4).ToString("O");
                break;
            case "ok_domain_without_data":
                events.First(e => e.Payload.ContainsKey("snapshot")).Payload["snapshot"]!["interfaces"]!["value"] = null;
                break;
            case "unavailable_domain_with_data":
                var unavailable = events.First(e => e.Payload.ContainsKey("snapshot")).Payload["snapshot"]!["routes"]!;
                unavailable["status"] = "access_denied";
                unavailable["reason"] = "synthetic denial";
                break;
            case "incomplete_domain_without_reason":
                events.First(e => e.Payload.ContainsKey("snapshot")).Payload["snapshot"]!["dns"]!["status"] = "partial";
                break;
        }
        using var workspace = new TempWorkspace();
        var path = workspace.File("bad-evidence.ndjson");
        await Save(path, events);
        var replay = new ReplayService().Replay(path);
        Assert.True(replay.Log.IsClean); // Envelope validation alone cannot establish semantic consistency.
        Assert.False(replay.IsClean);
        Assert.NotEmpty(replay.EvidenceProblems);
    }

    [Fact]
    public async Task Removing_a_referenced_record_is_detected_without_relying_on_sequence_gaps()
    {
        var input = await Scenario();
        var events = Bundle(input, DiagnosticEngine.CreateDefault().Evaluate(input));
        var referenced = events.Where(e => e.EventType == EventType.Diagnosis)
            .SelectMany(e => e.Payload["diagnosis"]!["evidence_ids"]!.AsArray().Select(x => x!.GetValue<string>())).ToHashSet();
        var remove = events.First(e => e.Payload.ContainsKey("evidence_record") && referenced.Contains(e.Refs.EvidenceId!));
        var missing = events.Where(e => e.EventId != remove.EventId).Select((e, index) => e with { Sequence = index + 1 }).ToArray();
        using var workspace = new TempWorkspace();
        var path = workspace.File("missing-record.ndjson");
        await Save(path, missing);
        var replay = new ReplayService().Replay(path);
        Assert.True(replay.Log.IsClean);
        Assert.Contains(replay.EvidenceProblems, p => p.Contains("missing evidence reference", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QA020_ipv6_failure_survives_replay_with_or_without_successful_ipv4(bool includeIpv4)
    {
        var probes = new List<ProbeResult>();
        if (includeIpv4) probes.Add(Probe("v4", "ipv4", EventStatus.Ok, 1000));
        probes.Add(Probe("v6", "ipv6", EventStatus.Timeout, 1100));
        var input = new EvidenceSet { Records = Array.Empty<EvidenceRecord>(), Probes = probes };
        var diagnoses = DiagnosticEngine.CreateDefault().Evaluate(input);
        Assert.DoesNotContain(diagnoses, d => d.RuleId == "DX-008" && d.Level == EvidenceGrade.Observed);
        using var workspace = new TempWorkspace();
        var path = workspace.File("dual-stack.ndjson");
        await Save(path, Bundle(input, diagnoses));
        var replay = new ReplayService().Replay(path);
        Assert.True(replay.IsClean);
        var ipv6 = Assert.Single(replay.Evidence!.Probes, p => p.AddressFamily == "ipv6");
        Assert.Equal(EventStatus.Timeout, ipv6.Status);
        Assert.False(ipv6.IsSuccess);
        Assert.Equal(1, replay.Summary.ByStatus[EventStatus.Timeout]);
        if (includeIpv4) Assert.True(Assert.Single(replay.Evidence.Probes, p => p.AddressFamily == "ipv4").IsSuccess);
    }

    [Fact]
    public void Legacy_events_remain_readable_without_claiming_missing_diagnostic_inputs_were_reconstructed()
    {
        var replay = new ReplayService().Replay(Fixtures.SyntheticPath);
        Assert.True(replay.IsClean);
        Assert.Null(replay.Evidence);
        Assert.Empty(replay.Diagnoses);
    }

    private static IReadOnlyList<EventEnvelope> Bundle(EvidenceSet input, IReadOnlyList<Diagnosis> diagnoses) =>
        EvidenceSessionEvents.Create(input, diagnoses, "codec", Boot, true, Origin.AddSeconds(30), 30000);

    private static async Task Save(string path, IEnumerable<EventEnvelope> events)
    {
        using var log = new NdjsonEventLog(path);
        foreach (var item in events) await log.AppendAsync(item, CancellationToken.None);
    }

    private static string[] Fingerprint(IEnumerable<Diagnosis> diagnoses) => diagnoses.Select(d => JsonSerializer.Serialize(d)).ToArray();

    private static async Task<EvidenceSet> Scenario()
    {
        var snapshot = await new SyntheticSnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = "codec", BootId = Boot, SnapshotId = "snapshot-codec", ObservedAt = Origin,
            MonotonicMs = 0, Synthetic = true,
        }, CancellationToken.None);
        return new EvidenceSet
        {
            Snapshot = snapshot,
            Probes = new[] { Probe("probe-1", "ipv4", EventStatus.Timeout, 1000), Probe("probe-2", "ipv4", EventStatus.Ok, 20000) },
            Records = new[] { Runtime("runtime-1", 1000), Runtime("runtime-2", 20000) },
        };
    }

    private static ProbeResult Probe(string id, string family, EventStatus status, double monotonic) => new()
    {
        ProbeId = id, ProbeGroupId = "group-" + id, TargetId = "target-codec", Path = ProbePath.NoExplicitProxy,
        Status = status, StartedAt = Origin.AddMilliseconds(monotonic), StartedMonotonicMs = monotonic,
        DurationMs = 100, HttpStatus = status == EventStatus.Ok ? 204 : null,
        Classification = status == EventStatus.Ok ? ProbeClassification.ReachableExpected : ProbeClassification.TransportFailure,
        AddressFamily = family, Method = "GET", RequestOptionsKey = "options-" + family,
        BootId = Boot, ObservationWindowId = Window, TimingComparable = true,
        FailedStage = status == EventStatus.Ok ? null : "connect", FailedEndpoint = "target",
        ResolvedAddresses = new[] { "198.18.0.11" }, ResolutionLocation = "local",
    };

    private static EvidenceRecord Runtime(string id, double monotonic) => new()
    {
        EvidenceId = id, Kind = "client_runtime_config", Summary = "Synthetic running fake-IP mode",
        ObservedAt = Origin.AddMilliseconds(monotonic), MonotonicMs = monotonic, BootId = Boot,
        ObservationWindowId = Window, Source = "synthetic.runtime", Provenance = "runtime_api", Status = EventStatus.Ok,
        TargetId = "target-codec", AddressFamily = "ipv4",
        Facts = new Dictionary<string, string> { ["client"] = "synthetic", ["running"] = "true", ["dns_mode"] = "fake-ip" },
    };
}
