using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T06-R3 pre-fix reproducers. They use only the API that existed before this task, so they run
/// (and fail) on the T09-R2 worktree. The first group proves the local-sampling entry never ran
/// local rules; the second group proves the storage writer accepted a record that belongs to a
/// different observation window than the probes of the same snapshot scope.
/// </summary>
public sealed class T06R3PreFixReproducerTests
{
    [Fact]
    public async Task Local_sampling_currently_returns_no_diagnosis_even_when_a_local_rule_could_conclude()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new ProxyConfiguredCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-prefix-local-rules", bootId: "boot-test");

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        // DX-001's configuration-only branch is a candidate conclusion that needs no external
        // probe. A local sample must evaluate the local rules and keep that as a candidate.
        var diagnosis = Assert.Single(cycle.Diagnoses, item => item.RuleId == "DX-001");
        Assert.Equal(EvidenceGrade.Candidate, diagnosis.Level);
        Assert.Contains("probe_on_explicit_proxy_path", diagnosis.MissingEvidence);
        await session.StopAsync();
    }

    [Fact]
    public async Task Manual_diagnosis_persists_the_composed_evidence_source_records()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new ProxyConfiguredCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-prefix-manual-records", bootId: "boot-test",
            evidenceSource: new SingleRecordSource());

        var cycle = await session.RunDiagnosisAsync(new ProbeTarget
        {
            TargetId = "target-prefix",
            Endpoint = "https://example.invalid/health",
        }, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);

        // The two real Desktop entries used to write Records = Array.Empty unconditionally.
        var record = Assert.Single(cycle.Evidence.Records);
        Assert.Equal("client_process", record.Kind);
        Assert.Equal(cycle.Snapshot.SnapshotId, record.Facts["snapshot_id"]);
        await session.StopAsync();
    }

    private sealed class SingleRecordSource : IObservationEvidenceSource
    {
        public Task<ObservationEvidenceResult> CollectAsync(
            ObservationEvidenceContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationEvidenceResult
            {
                Records = new[]
                {
                    new EvidenceRecord
                    {
                        EvidenceId = "record-" + context.SnapshotId,
                        Kind = "client_process",
                        Summary = "测试进程观测",
                        Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["snapshot_id"] = context.SnapshotId,
                            ["client"] = "nice",
                            ["pid"] = "100",
                        },
                        ObservedAt = context.ObservedAt,
                        MonotonicMs = context.MonotonicMs,
                        BootId = context.BootId,
                        ObservationWindowId = context.ObservationWindowId,
                        Source = "test.evidence-source",
                        Provenance = "adapter_read",
                        Status = EventStatus.Ok,
                    },
                },
            });
    }

    private sealed class ProxyConfiguredCollector : ISnapshotCollector
    {
        public string Source => "test.proxy-configured";

        public string SourceVersion => "1.0";

        public Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
        {
            Collected<ProxyLayer> Layer(string name, bool configured) => Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = name,
                Provenance = "test",
                Enabled = configured,
                ProxyServer = configured ? "http://127.0.0.1:7890" : null,
                Variables = configured ? new[] { "HTTPS_PROXY" } : Array.Empty<string>(),
            });

            var snapshot = new NetworkSnapshot
            {
                SnapshotId = context.SnapshotId,
                ObservedAt = context.ObservedAt,
                MonotonicMs = context.MonotonicMs,
                Synthetic = context.Synthetic,
                Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(Array.Empty<InterfaceState>()),
                Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(Array.Empty<RouteEntry>()),
                Dns = Collected<DnsState>.Ok(new DnsState()),
                Proxies = Collected<ProxyState>.Ok(new ProxyState
                {
                    ToolProcessEnvironment = Layer("tool_process_environment", true),
                    UserEnvironment = Layer("user_environment", true),
                    MachineEnvironment = Layer("machine_environment", false),
                    WinInet = Layer("wininet", false),
                    WinHttp = Layer("winhttp", false),
                }),
            };
            return Task.FromResult(snapshot);
        }
    }
}
