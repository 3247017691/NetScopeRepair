using System.IO.Compression;
using NetScope.Adapters;
using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T06-R3 evidence chain: fake adapter data enters the real Desktop entry points, a real rule
/// consumes it, storage keeps one explicit scope, and replay/export preserve the same facts.
/// No test reads a real process, listener or client configuration and none opens a socket.
/// </summary>
public sealed class T06R3EvidenceIntegrationTests
{
    [Fact]
    public async Task Fake_adapter_facts_reach_the_evidence_set_and_a_real_rule_consumes_them()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var adapter = T06R3Adapter.Create(
            new[] { T06R3Adapter.NiceUi() },
            new[] { T06R3Adapter.Loopback() },
            T06R3Adapter.NiceDiskAdapter());
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t06r3-adapter", bootId: "boot-test",
            evidenceSource: new AdapterObservationEvidenceSource(adapter));

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        Assert.True(cycle.Persisted);
        Assert.NotEmpty(cycle.Evidence.Records);
        var process = Assert.Single(cycle.Evidence.Records, record =>
            record.Kind == "client_process" && record.Facts["client"] == "nice");
        Assert.Equal("ui", process.Facts["role"]);
        Assert.True(process.Facts.ContainsKey("started_at"));

        var config = Assert.Single(cycle.Evidence.Records, record => record.Kind == "client_config_file");
        Assert.Equal("nice", config.Facts["client"]);
        Assert.Equal("disk_only", config.Provenance);
        Assert.Equal(64, config.Facts["config_hash"].Length);
        Assert.Equal("7890", config.Facts["mixed_port"]);
        Assert.True(DateTimeOffset.TryParse(config.Facts["mtime"], out _));
        Assert.DoesNotContain(cycle.Evidence.Records, record => record.Kind == "client_runtime_config");

        var window = cycle.ObservationWindowId;
        Assert.False(string.IsNullOrWhiteSpace(window));
        Assert.All(cycle.Evidence.Records, record =>
        {
            Assert.Equal(cycle.Snapshot.SnapshotId, record.Facts["snapshot_id"]);
            Assert.Equal("boot-test", record.BootId);
            Assert.Equal(window, record.ObservationWindowId);
            Assert.Equal(clock.UtcNow, record.ObservedAt);
        });

        // DX-006 is a real catalog rule fed only by the adapter facts above: the disk file is
        // older than the client process start, which stays a candidate without runtime evidence.
        var aging = Assert.Single(cycle.Diagnoses, diagnosis => diagnosis.RuleId == "DX-006");
        Assert.Equal(EvidenceGrade.Candidate, aging.Level);
        Assert.Contains("runtime_api_configuration", aging.MissingEvidence);
        Assert.All(aging.EvidenceIds, id =>
            Assert.Contains(cycle.Evidence.Records, record => record.EvidenceId == id));
        Assert.Empty(cycle.Evidence.Probes);
        await session.StopAsync();

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var scope = Assert.Single(replay.SnapshotScopes);
        Assert.Equal(cycle.Evidence.Records.Count, scope.Evidence.Records.Count);
        var rerun = DiagnosticEngine.CreateDefault().EvaluateDetailed(scope.Evidence);
        Assert.Empty(rerun.Failures);
        Assert.Equal(
            cycle.Diagnoses.Select(item => $"{item.RuleId}|{item.Level}"),
            rerun.Diagnoses.Select(item => $"{item.RuleId}|{item.Level}"));

        // Export then replay the package's own events: the adapter records and the rule result
        // must survive redaction without being re-invented.
        var destination = Path.Combine(temp.Root, "export");
        var result = await new IncidentExporter().ExportAsync(session.SessionPath, destination, CancellationToken.None);
        using (var archive = ZipFile.OpenRead(result.PackagePath))
        {
            var entry = archive.GetEntry("events.ndjson");
            Assert.NotNull(entry);
            using var stream = entry!.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var exportedPath = Path.Combine(temp.Root, "exported-events.ndjson");
            await File.WriteAllBytesAsync(exportedPath, memory.ToArray());

            var exported = new ReplayService().Replay(exportedPath);
            Assert.True(exported.IsClean, string.Join("; ", exported.EvidenceProblems));
            Assert.Contains(exported.Evidence!.Records, record => record.Kind == "client_config_file");
            Assert.Contains(exported.Evidence.Records, record => record.Kind == "client_process");
            Assert.Equal(cycle.Diagnoses.Count, exported.Diagnoses.Count);
        }
    }

    [Fact]
    public async Task Both_real_desktop_entries_consume_the_composed_evidence_source_in_their_own_window()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var source = new CountingEvidenceSource();
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t06r3-both-entries", bootId: "boot-test", evidenceSource: source);

        var local = await session.CollectOnceAsync(CancellationToken.None);
        var manual = await session.RunDiagnosisAsync(new ProbeTarget
        {
            TargetId = "target-t06r3",
            Endpoint = "https://example.invalid/health",
        }, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);

        Assert.Equal(2, source.Calls);
        Assert.NotEmpty(local.Evidence.Records);
        Assert.Empty(local.Evidence.Probes);
        Assert.NotEmpty(manual.Evidence.Records);
        Assert.Equal(3, manual.Evidence.Probes.Count);
        Assert.NotEqual(local.ObservationWindowId, manual.ObservationWindowId);
        Assert.All(local.Evidence.Records, record => Assert.Equal(local.ObservationWindowId, record.ObservationWindowId));
        Assert.All(manual.Evidence.Records, record => Assert.Equal(manual.ObservationWindowId, record.ObservationWindowId));
        Assert.Equal("proxy-configured", source.Contexts[1].ProxyEndpointId);
        Assert.Equal("target-t06r3", source.Contexts[1].TargetId);
        Assert.Null(source.Contexts[0].ProxyEndpointId);
        await session.StopAsync();
    }

    [Fact]
    public void Composition_injects_one_adapter_evidence_source_for_the_application()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths);

        Assert.IsType<AdapterObservationEvidenceSource>(services.Session.EvidenceSource);
        Assert.IsType<GenericAdapter>(services.Clients);
        Assert.NotNull(services.Explanations);
    }

    [Fact]
    public async Task Disk_whitelist_is_projected_as_client_config_file_and_never_as_runtime_config()
    {
        var adapter = T06R3Adapter.Create(
            new[] { T06R3Adapter.NiceUi() },
            new[] { T06R3Adapter.Loopback() },
            T06R3Adapter.NiceDiskAdapter());
        var read = await adapter.ReadObservationAsync(CancellationToken.None);
        var snapshot = await new T06R3SnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = "session-t06r3-builder",
            BootId = "boot-t06r3-builder",
            SnapshotId = "snapshot-t06r3-builder",
            ObservedAt = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8)),
            MonotonicMs = 1000,
            Synthetic = false,
        }, CancellationToken.None);

        var result = ObservationEvidenceBuilder.Build(read, T06R3Adapter.Context(snapshot));

        var kinds = result.Records.Select(record => record.Kind).Distinct().OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ObservationEvidenceBuilder.RecordKinds.OrderBy(kind => kind, StringComparer.Ordinal),
            kinds);
        Assert.DoesNotContain("client_runtime_config", kinds);
        Assert.DoesNotContain("client_configuration", kinds);
        Assert.DoesNotContain("port_bind_failure", kinds);
        Assert.DoesNotContain("fake_ip_adapter", kinds);
        var config = Assert.Single(result.Records, record => record.Kind == "client_config_file");
        Assert.Equal("disk_only", config.Provenance);
        Assert.Equal("true", config.Facts["valid"]);
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public async Task DX001_stays_a_candidate_when_only_local_configuration_is_visible()
    {
        using var temp = new TempAppPaths();
        var runner = new StubProbeRunner();
        await using var session = new ObservationSession(new T06R3SnapshotCollector(proxyConfigured: true), runner,
            new TestClock(), temp.Paths, sessionId: "session-t06r3-dx001", bootId: "boot-test",
            evidenceSource: new CountingEvidenceSource());

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        var diagnosis = Assert.Single(cycle.Diagnoses, item => item.RuleId == "DX-001");
        Assert.Equal(EvidenceGrade.Candidate, diagnosis.Level);
        Assert.Contains("probe_on_explicit_proxy_path", diagnosis.MissingEvidence);
        Assert.Empty(cycle.Evidence.Probes);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(cycle.Snapshot.SnapshotId, Assert.Single(diagnosis.EvidenceIds));
        await session.StopAsync();
    }

    [Fact]
    public async Task DX002_reserved_range_is_only_a_candidate_without_probes_or_runtime_context()
    {
        using var temp = new TempAppPaths();
        var reserved = new T06R3SnapshotCollector(
            routes: new[]
            {
                new RouteEntry
                {
                    DestinationPrefix = "198.18.0.0/15",
                    Family = "ipv4",
                    IfIndex = 12,
                    RouteMetric = 0,
                    InterfaceMetric = 25,
                    Protocol = "manual",
                },
            },
            dns: new DnsState
            {
                ServersByInterface = new Dictionary<int, IReadOnlyList<string>> { [12] = new[] { "198.18.0.2" } },
            });
        await using var session = new ObservationSession(reserved, new StubProbeRunner(), new TestClock(),
            temp.Paths, sessionId: "session-t06r3-dx002", bootId: "boot-test",
            evidenceSource: new CountingEvidenceSource());

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        var diagnosis = Assert.Single(cycle.Diagnoses, item => item.RuleId == "DX-002");
        Assert.Equal(EvidenceGrade.Candidate, diagnosis.Level);
        Assert.Contains("effective_fake_ip_configuration", diagnosis.MissingEvidence);
        Assert.DoesNotContain("不是污染证据", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Empty(cycle.Evidence.Probes);
        await session.StopAsync();

        await using var baseline = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-dx002-baseline", bootId: "boot-test",
            evidenceSource: new CountingEvidenceSource());
        var normal = await baseline.CollectOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(normal.Diagnoses, item => item.RuleId == "DX-002");
        await baseline.StopAsync();
    }

    [Fact]
    public async Task DX005_has_no_bind_failure_producer_so_no_conflict_is_invented()
    {
        using var temp = new TempAppPaths();
        var adapter = T06R3Adapter.Create(
            new[] { T06R3Adapter.NiceUi(), T06R3Adapter.ApexUi() },
            new[] { T06R3Adapter.Loopback() },
            T06R3Adapter.NiceDiskAdapter(),
            T06R3Adapter.ApexDiskAdapter());
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-dx005", bootId: "boot-test",
            evidenceSource: new AdapterObservationEvidenceSource(adapter));

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(cycle.Diagnoses, item => item.RuleId == "DX-005");
        Assert.Equal(2, cycle.Evidence.Records.Count(record => record.Kind == "client_config_file"));
        Assert.Contains(cycle.Evidence.Records, record => record.Kind == "port_ownership");
        Assert.DoesNotContain(cycle.Evidence.Records, record => record.Kind == "port_bind_failure");
        Assert.DoesNotContain(cycle.Evidence.Records, record => record.Kind == "client_configuration");
        await session.StopAsync();
    }

    [Fact]
    public async Task DX011_empty_listener_sample_with_a_successful_local_proxy_connection_is_insufficient()
    {
        using var temp = new TempAppPaths();
        var emptyAdapter = T06R3Adapter.Create(
            new[] { T06R3Adapter.NiceUi() },
            Array.Empty<ListenerObservation>(),
            T06R3Adapter.NiceDiskAdapter());
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-dx011", bootId: "boot-test",
            evidenceSource: new AdapterObservationEvidenceSource(emptyAdapter));

        var cycle = await session.RunDiagnosisAsync(new ProbeTarget
        {
            TargetId = "target-t06r3",
            Endpoint = "https://example.invalid/health",
        }, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);

        var diagnosis = Assert.Single(cycle.Diagnoses, item => item.RuleId == "DX-011");
        Assert.Equal(EvidenceGrade.Insufficient, diagnosis.Level);
        Assert.Contains("repeated_listening_sample", diagnosis.MissingEvidence);
        Assert.NotEmpty(diagnosis.CounterEvidenceIds);
        await session.StopAsync();

        var populatedAdapter = T06R3Adapter.Create(
            new[] { T06R3Adapter.NiceUi() },
            new[] { T06R3Adapter.Loopback() },
            T06R3Adapter.NiceDiskAdapter());
        await using var populated = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-dx011-ok", bootId: "boot-test",
            evidenceSource: new AdapterObservationEvidenceSource(populatedAdapter));
        var healthy = await populated.RunDiagnosisAsync(new ProbeTarget
        {
            TargetId = "target-t06r3",
            Endpoint = "https://example.invalid/health",
        }, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);
        Assert.DoesNotContain(healthy.Diagnoses, item => item.RuleId == "DX-011");
        await populated.StopAsync();
    }

    [Fact]
    public async Task DX006_stays_silent_when_the_file_is_newer_than_the_process()
    {
        using var temp = new TempAppPaths();
        var adapter = T06R3Adapter.Create(
            new[] { T06R3Adapter.NiceUi() },
            new[] { T06R3Adapter.Loopback() },
            T06R3Adapter.NiceDiskAdapter(modifiedAt: T06R3Adapter.ClientStartedAt.AddHours(1)));
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-dx006-quiet", bootId: "boot-test",
            evidenceSource: new AdapterObservationEvidenceSource(adapter));

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        Assert.Contains(cycle.Evidence.Records, record => record.Kind == "client_config_file");
        Assert.DoesNotContain(cycle.Diagnoses, item => item.RuleId == "DX-006");
        await session.StopAsync();
    }

    [Fact]
    public async Task A_page_refresh_read_is_not_spliced_into_the_next_sample_evidence()
    {
        using var temp = new TempAppPaths();
        var processes = new List<ProcessObservation> { T06R3Adapter.NiceUi() };
        var listeners = new List<ListenerObservation> { T06R3Adapter.Loopback(7890) };
        var adapter = new GenericAdapter(
            new FakeProcessSource(processes),
            new FakeListenerSource(listeners),
            new FakeAdapterSource(),
            new FakeProxyLayerSource(),
            new[] { T06R3Adapter.NiceDiskAdapter() });

        // The clients page refreshes first and sees 7890. The next observation cycle must read
        // its own data (9090) instead of reusing the page's earlier result.
        await adapter.ReadAsync(CancellationToken.None);
        listeners.Clear();
        listeners.Add(T06R3Adapter.Loopback(9090));

        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-page-refresh", bootId: "boot-test",
            evidenceSource: new AdapterObservationEvidenceSource(adapter));
        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        var ownership = Assert.Single(cycle.Evidence.Records, record => record.Kind == "port_ownership");
        Assert.Equal("127.0.0.1:9090", ownership.Facts["endpoint"]);
        var listening = Assert.Single(cycle.Evidence.Records, record =>
            record.Kind == "listening_ports" && record.AddressFamily == "ipv4");
        Assert.Equal("1", listening.Facts["count"]);
        Assert.DoesNotContain(cycle.Evidence.Records, record =>
            record.Facts.Values.Any(value => value == "127.0.0.1:7890"));
        await session.StopAsync();
    }

    [Fact]
    public async Task Two_samples_keep_each_record_in_its_own_snapshot_scope()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t06r3-multi-snapshot", bootId: "boot-test",
            evidenceSource: new CountingEvidenceSource());

        var first = await session.CollectOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = await session.CollectOnceAsync(CancellationToken.None);
        await session.StopAsync();

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.All(replay.SnapshotScopes, scope =>
        {
            var record = Assert.Single(scope.Evidence.Records);
            Assert.Equal(scope.Snapshot.SnapshotId, record.Facts["snapshot_id"]);
            Assert.Equal(scope.Snapshot.SnapshotId, record.EvidenceId["record-".Length..]);
        });
        Assert.Equal(first.Snapshot.SnapshotId, replay.SnapshotScopes[0].Snapshot.SnapshotId);
        Assert.Equal(second.Snapshot.SnapshotId, replay.SnapshotScopes[1].Snapshot.SnapshotId);
        Assert.Equal(first.ObservationWindowId, replay.SnapshotScopes[0].Evidence.Records[0].ObservationWindowId);
        Assert.Equal(second.ObservationWindowId, replay.SnapshotScopes[1].Evidence.Records[0].ObservationWindowId);
        Assert.Single(replay.Evidence!.Records);
        Assert.Equal(second.Snapshot.SnapshotId, replay.Evidence.Records[0].Facts["snapshot_id"]);
    }

    [Fact]
    public async Task A_runtime_api_evidence_item_is_not_downgraded_to_a_disk_config_record()
    {
        var runtimeEvidence = new ClientConfigEvidence
        {
            ClientId = "nice",
            ParserVersion = "runtime-test/1.0",
            Provenance = ConfigProvenance.RuntimeApi,
            Valid = true,
            Facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["dns_mode"] = "fake-ip" },
        };
        var read = new AdapterObservationRead(
            T06R3Adapter.ConfigModifiedAt,
            Collected<IReadOnlyList<ProcessObservation>>.Ok(new[] { T06R3Adapter.NiceUi() }),
            Collected<IReadOnlyList<ListenerObservation>>.Ok(new[] { T06R3Adapter.Loopback() }),
            Collected<IReadOnlyList<NetworkAdapterObservation>>.Ok(Array.Empty<NetworkAdapterObservation>()),
            Collected<IReadOnlyList<ProxyLayerObservation>>.Ok(Array.Empty<ProxyLayerObservation>()),
            new[]
            {
                new ClientStatus
                {
                    ClientId = "nice",
                    DisplayName = "Nice加速",
                    Running = true,
                    Evidence = new[] { runtimeEvidence },
                },
            });
        var snapshot = await new T06R3SnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = "session-t06r3-runtime",
            BootId = "boot-t06r3-runtime",
            SnapshotId = "snapshot-t06r3-runtime",
            ObservedAt = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8)),
            MonotonicMs = 1000,
            Synthetic = false,
        }, CancellationToken.None);

        var result = ObservationEvidenceBuilder.Build(read, T06R3Adapter.Context(snapshot));

        Assert.DoesNotContain(result.Records, record => record.Kind is "client_runtime_config" or "client_config_file");
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("client_runtime_config", gap.Source);
        Assert.Equal(EventStatus.Unsupported, gap.Status);
    }

    [Fact]
    public async Task A_failing_evidence_source_is_a_persisted_gap_not_an_empty_success()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new T06R3SnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t06r3-source-gap", bootId: "boot-test",
            evidenceSource: new ThrowingEvidenceSource());

        var cycle = await session.CollectOnceAsync(CancellationToken.None);

        Assert.True(cycle.Persisted);
        Assert.Empty(cycle.Evidence.Records);
        var gap = Assert.Single(cycle.EvidenceGaps);
        Assert.Contains("证据源读取失败", gap, StringComparison.Ordinal);
        await session.StopAsync();

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Empty(replay.Evidence!.Records);
        Assert.Contains(replay.Log.Events, item => item.EventType == EventType.Gap
            && item.Source == "observation_evidence" && item.Status == EventStatus.Failed);
    }
}
