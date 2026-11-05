using NetScope.Application;
using NetScope.Application.Mock;
using NetScope.Diagnostics;
using NetScope.Storage;

namespace NetScope.Desktop.Services;

/// <summary>
/// Produces one clearly synthetic session so the UI can be exercised and reviewed without any
/// real network request. It is the GUI counterpart of the CLI demo, not a measurement.
/// </summary>
public static class SyntheticShowcase
{
    public const string SessionId = "session-demo-view";

    public static async Task<string> CreateAsync(AppPaths paths, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var path = Path.Combine(paths.SessionsDirectory, "demo-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".ndjson");
        var observedAt = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8));
        var snapshot = await new SyntheticSnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = SessionId,
            BootId = "boot-demo-view",
            SnapshotId = "snapshot-demo-001",
            ObservedAt = observedAt,
            MonotonicMs = 0,
            Synthetic = true,
        }, cancellationToken).ConfigureAwait(false);

        var scenario = SyntheticScenario.Baseline with { LocalResolvedAddresses = new[] { "198.18.0.11" } };
        var first = await new SyntheticProbeRunner(scenario).RunAsync(Plan("group-demo-001", observedAt.AddSeconds(1)), cancellationToken).ConfigureAwait(false);
        var secondScenario = SyntheticScenario.AllReachable with { LocalResolvedAddresses = new[] { "198.18.0.11" } };
        var second = await new SyntheticProbeRunner(secondScenario).RunAsync(Plan("group-demo-002", observedAt.AddSeconds(20)), cancellationToken).ConfigureAwait(false);
        var probes = first.Results.Concat(second.Results)
            .Select(probe => probe with { StartedMonotonicMs = (probe.StartedAt - observedAt).TotalMilliseconds })
            .ToArray();
        var records = new[]
        {
            RuntimeEvidence("config-demo-001", observedAt.AddSeconds(1), 1000),
            RuntimeEvidence("config-demo-002", observedAt.AddSeconds(20), 20000),
        };
        var evidence = new EvidenceSet { Records = records, Probes = probes, Snapshot = snapshot };
        var evaluation = DiagnosticEngine.CreateDefault().EvaluateDetailed(evidence);
        if (evaluation.Failures.Count != 0)
            throw new InvalidOperationException("A diagnostic rule failed on the synthetic scenario.");

        var events = EvidenceSessionEvents.Create(evidence, evaluation.Diagnoses, SessionId, "boot-demo-view", true,
            observedAt.AddSeconds(30), 30000);
        using (var log = new NdjsonEventLog(path))
        {
            foreach (var envelope in events)
                await log.AppendAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        using (var index = SqliteEventIndex.Open(Path.ChangeExtension(path, ".db")))
            index.ProjectAll(events);
        return path;
    }

    private static ProbeGroupPlan Plan(string groupId, DateTimeOffset startedAt) => new()
    {
        ProbeGroupId = groupId,
        Target = new ProbeTarget { TargetId = "target-demo", Endpoint = "https://example.invalid/health" },
        ExplicitProxyEndpoint = "http://127.0.0.1:7890",
        ProxyEndpointId = "proxy-demo",
        AddressFamily = "ipv4",
        ObservationWindowId = "window-demo",
        BootId = "boot-demo-view",
        StartedAt = startedAt,
    };

    private static EvidenceRecord RuntimeEvidence(string id, DateTimeOffset at, double monotonic) => new()
    {
        EvidenceId = id,
        Kind = "client_runtime_config",
        Summary = "合成场景使用的运行态 fake-IP 配置（非真实采集）。",
        Status = NetScope.Domain.EventStatus.Ok,
        Source = "synthetic.runtime-api",
        Provenance = "runtime_api",
        ObservedAt = at,
        MonotonicMs = monotonic,
        BootId = "boot-demo-view",
        ObservationWindowId = "window-demo",
        TargetId = "target-demo",
        AddressFamily = "ipv4",
        Facts = new Dictionary<string, string>
        {
            ["client"] = "synthetic",
            ["running"] = "true",
            ["dns_mode"] = "fake-ip",
            ["config_path"] = "synthetic/runtime",
            ["config_hash"] = "synthetic-config-hash",
        },
    };
}
