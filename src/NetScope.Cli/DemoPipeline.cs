using NetScope.Application;
using NetScope.Application.Mock;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Cli;

/// <summary>Entirely synthetic evidence-to-disk demonstration. No sockets or system settings are used.</summary>
internal static class DemoPipeline
{
    private const string SessionId = "session-demo";
    private const string BootId = "boot-demo";
    private const string SnapshotId = "snapshot-demo-001";
    private const string WindowId = "window-demo";

    public static async Task<int> RunAsync(string outputDirectory, TextWriter output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var sessionPath = Path.Combine(outputDirectory, "session-demo.ndjson");
        var indexPath = Path.Combine(outputDirectory, "session-demo.db");
        if (File.Exists(sessionPath) || File.Exists(indexPath))
            throw new IOException("Demo output already exists. Select an empty output directory to preserve the earlier session.");
        var observedAt = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8));
        var snapshot = await new SyntheticSnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = SessionId, BootId = BootId, SnapshotId = SnapshotId,
            ObservedAt = observedAt, MonotonicMs = 0, Synthetic = true,
        }, cancellationToken);
        var scenario = SyntheticScenario.Baseline with { LocalResolvedAddresses = new[] { "198.18.0.11" } };
        var firstGroup = await new SyntheticProbeRunner(scenario).RunAsync(Plan("group-demo-001", observedAt.AddSeconds(1)), cancellationToken);
        var secondGroup = await new SyntheticProbeRunner(SyntheticScenario.AllReachable with
            { LocalResolvedAddresses = new[] { "198.18.0.11" } }).RunAsync(Plan("group-demo-002", observedAt.AddSeconds(20)), cancellationToken);
        var probes = firstGroup.Results.Concat(secondGroup.Results)
            .Select(probe => probe with { StartedMonotonicMs = (probe.StartedAt - observedAt).TotalMilliseconds }).ToArray();
        var records = new[] { RuntimeEvidence("config-demo-001", observedAt.AddSeconds(1), 1000),
            RuntimeEvidence("config-demo-002", observedAt.AddSeconds(20), 20000) };
        var evidence = new EvidenceSet { Records = records, Probes = probes, Snapshot = snapshot };
        var evaluation = DiagnosticEngine.CreateDefault().EvaluateDetailed(evidence);
        foreach (var failure in evaluation.Failures) output.WriteLine($"rule_error: {failure.RuleId} {failure.ErrorType}");
        if (evaluation.Failures.Count != 0) return 1;
        var events = EvidenceSessionEvents.Create(evidence, evaluation.Diagnoses, SessionId, BootId, true,
            observedAt.AddSeconds(30), 30000);
        using (var log = new NdjsonEventLog(sessionPath))
            foreach (var envelope in events) await log.AppendAsync(envelope, cancellationToken);
        using (var index = SqliteEventIndex.Open(indexPath)) index.ProjectAll(events);
        var replay = new ReplayService().Replay(sessionPath);
        if (!replay.IsClean || replay.Evidence is null) return 1;
        var rerun = DiagnosticEngine.CreateDefault().EvaluateDetailed(replay.Evidence);
        if (rerun.Failures.Count != 0 || !Fingerprint(rerun.Diagnoses).SequenceEqual(Fingerprint(replay.Diagnoses)))
            throw new InvalidDataException("The persisted evidence did not reproduce the original diagnoses.");

        output.WriteLine("NetScope Repair - synthetic M0 evidence demonstration");
        output.WriteLine("DATA SOURCE: synthetic mock only; no network request or system setting change.");
        foreach (var probe in probes)
            output.WriteLine($"  {probe.ProbeGroupId} {WireNames.Of(probe.Path)} {WireNames.Of(probe.Status)} started={probe.StartedAt:O}");
        foreach (var diagnosis in evaluation.Diagnoses)
        {
            output.WriteLine($"  {diagnosis.RuleId} [{WireNames.Of(diagnosis.Level)}] {diagnosis.Summary}");
            output.WriteLine($"    evidence: {string.Join(", ", diagnosis.EvidenceIds)}");
        }
        output.WriteLine($"log: {sessionPath}");
        output.WriteLine($"index: {indexPath}");
        output.WriteLine($"events={replay.Summary.EventCount} synthetic={replay.Summary.SyntheticCount} problems={replay.Log.Problems.Count} evidence_problems={replay.EvidenceProblems.Count}");
        output.WriteLine($"offline_diagnosis_match=yes diagnoses={replay.Diagnoses.Count}");
        return 0;
    }

    private static IEnumerable<string> Fingerprint(IEnumerable<Diagnosis> values) => values.Select(value =>
        $"{value.RuleId}|{value.RuleVersion}|{value.Level}|{value.Summary}|{string.Join(",", value.EvidenceIds)}|{string.Join(",", value.CounterEvidenceIds)}");

    private static ProbeGroupPlan Plan(string groupId, DateTimeOffset startedAt) => new()
    {
        ProbeGroupId = groupId,
        Target = new ProbeTarget { TargetId = "target-demo", Endpoint = "https://example.invalid/health" },
        ExplicitProxyEndpoint = "http://127.0.0.1:7890", ProxyEndpointId = "proxy-demo",
        AddressFamily = "ipv4", ObservationWindowId = WindowId, BootId = BootId, StartedAt = startedAt,
    };

    private static EvidenceRecord RuntimeEvidence(string id, DateTimeOffset at, double monotonic) => new()
    {
        EvidenceId = id, Kind = "client_runtime_config", Summary = "Invented running fake-IP configuration for the synthetic scenario.",
        Status = EventStatus.Ok, Source = "synthetic.runtime-api", Provenance = "runtime_api",
        ObservedAt = at, MonotonicMs = monotonic, BootId = BootId, ObservationWindowId = WindowId,
        TargetId = "target-demo", AddressFamily = "ipv4",
        Facts = new Dictionary<string, string>
        {
            ["client"] = "synthetic", ["running"] = "true", ["dns_mode"] = "fake-ip",
            ["config_path"] = "synthetic/runtime", ["config_hash"] = "synthetic-config-hash",
        },
    };
}
