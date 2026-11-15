using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;
using NetScope.Probes;
using NetScope.Storage;

namespace NetScope.Desktop.Tests;

public sealed class ReviewRegressionTests
{
    private static ProbeTarget Target() => new()
    {
        TargetId = "target-review", Endpoint = "https://example.invalid/health", ExpectedStatusCodes = new[] { 204 },
    };

    [Fact]
    public async Task Samples_collected_with_the_same_clock_tick_have_unique_evidence_ids()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-same-tick", bootId: "boot-test");
        var first = await session.CollectOnceAsync(CancellationToken.None);
        var second = await session.CollectOnceAsync(CancellationToken.None);
        Assert.True(first.Persisted);
        Assert.True(second.Persisted);
        Assert.NotEqual(first.Snapshot.SnapshotId, second.Snapshot.SnapshotId);
        Assert.Null(session.PersistenceError);
    }

    [Fact]
    public async Task Repeated_diagnoses_in_one_second_have_unique_groups_and_probe_ids()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-fast-probes", bootId: "boot-test");
        var first = await session.RunDiagnosisAsync(Target(), null, null, CancellationToken.None);
        var second = await session.RunDiagnosisAsync(Target(), null, null, CancellationToken.None);
        Assert.True(first.Persisted);
        Assert.True(second.Persisted);
        Assert.NotEqual(first.Evidence.Probes[0].ProbeGroupId, second.Evidence.Probes[0].ProbeGroupId);
        Assert.Empty(first.Evidence.Probes.Select(x => x.ProbeId).Intersect(second.Evidence.Probes.Select(x => x.ProbeId)));
    }

    [Fact]
    public async Task Stopped_sessions_reject_manual_collection_and_diagnosis()
    {
        using var temp = new TempAppPaths();
        var runner = new StubProbeRunner();
        await using var session = new ObservationSession(new StubSnapshotCollector(), runner,
            new TestClock(), temp.Paths, sessionId: "session-stopped", bootId: "boot-test");
        await session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CollectOnceAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunDiagnosisAsync(Target(), null, null, CancellationToken.None));
        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(session.SessionPath));
    }

    [Fact]
    public async Task Stop_waits_for_an_inflight_manual_sample_and_does_not_persist_after_stopping()
    {
        using var temp = new TempAppPaths();
        var collector = new HeldCollector();
        await using var session = new ObservationSession(collector, new StubProbeRunner(), new TestClock(),
            temp.Paths, sessionId: "session-exit-race", bootId: "boot-test");
        var collecting = session.CollectOnceAsync(CancellationToken.None);
        await collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.StopAsync();
        var returnedBeforeCollection = stopping.IsCompleted;
        collector.Release.TrySetResult();
        try { await collecting; } catch (OperationCanceledException) { }
        await stopping;
        Assert.False(returnedBeforeCollection);
        Assert.Equal(0, session.EventCount);
        Assert.False(File.Exists(session.SessionPath));
    }

    [Fact]
    public void Saving_settings_updates_the_values_used_when_remembering_the_close_choice()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var vm = new MainViewModel(services, new InlineUiDispatcher());
        vm.Settings.IntervalSeconds = "17";
        vm.Settings.TargetEndpoint = "https://example.invalid/updated";
        vm.Settings.ExplicitProxy = "http://127.0.0.1:8080";
        vm.Settings.Save();
        // Same operation as App.HandleCloseRequest when the user remembers the tray choice.
        services.SettingsStore.Save(services.Settings with { HideOnClose = true });
        var saved = services.SettingsStore.Load();
        Assert.Equal("https://example.invalid/updated", saved.ProbeTargetEndpoint);
        Assert.Equal(TimeSpan.FromSeconds(17), saved.ObservationInterval);
        Assert.Equal("http://127.0.0.1:8080", saved.ExplicitProxyEndpoint);
    }

    [Fact]
    public async Task Local_snapshot_does_not_erase_the_last_manual_diagnosis()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(EventStatus.Failed),
            clock, temp.Paths, sessionId: "session-retain-diagnosis", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings
        {
            ProbeTargetEndpoint = Target().Endpoint,
        }, clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());
        await vm.DiagnoseAsync(CancellationToken.None);
        Assert.NotEmpty(vm.Diagnoses.Rows);
        var diagnosisId = vm.Diagnoses.Rows[0].RuleId;
        clock.Advance(TimeSpan.FromSeconds(10));
        await session.CollectOnceAsync(CancellationToken.None);
        Assert.Contains(vm.Diagnoses.Rows, row => row.RuleId == diagnosisId);
    }

    [Fact]
    public async Task Demo_mode_cannot_launch_a_real_probe_even_with_a_saved_target()
    {
        using var temp = new TempAppPaths();
        var runner = new StubProbeRunner();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), runner, clock, temp.Paths,
            sessionId: "session-demo-no-probe", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings
        {
            ProbeTargetEndpoint = Target().Endpoint,
        }, clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());
        await vm.LoadDemoAsync();
        var result = await vm.DiagnoseAsync(CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(0, runner.Calls);
        Assert.True(vm.IsDemo);
        Assert.False(vm.Home.CanDiagnose);
    }

    [Fact]
    public async Task Default_sessions_created_together_do_not_share_a_log_path()
    {
        using var temp = new TempAppPaths();
        await using var first = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), new TestClock(), temp.Paths);
        await using var second = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), new TestClock(), temp.Paths);
        Assert.NotEqual(first.SessionPath, second.SessionPath);
    }

    [Fact]
    public async Task Synthetic_sessions_keep_unique_event_ids_across_multiple_batches()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, synthetic: true, sessionId: "session-synthetic-batches", bootId: "boot-test");
        await session.CollectOnceAsync(CancellationToken.None);
        await session.CollectOnceAsync(CancellationToken.None);
        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.Log.Events.Count);
        Assert.Equal(2, replay.SnapshotScopes.Count);
    }

    [Fact]
    public async Task Multiple_manual_diagnoses_do_not_reuse_diagnosis_ids()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(EventStatus.Failed),
            new TestClock(), temp.Paths, sessionId: "session-diagnosis-ids", bootId: "boot-test");
        var first = await session.RunDiagnosisAsync(Target(), null, null, CancellationToken.None);
        var second = await session.RunDiagnosisAsync(Target(), null, null, CancellationToken.None);
        Assert.NotEmpty(first.Diagnoses);
        Assert.Empty(first.Diagnoses.Select(x => x.DiagnosisId).Intersect(second.Diagnoses.Select(x => x.DiagnosisId)));
        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(first.Diagnoses.Count + second.Diagnoses.Count, replay.Diagnoses.Count);
    }

    [Fact]
    public async Task Switching_to_demo_cancels_and_joins_an_inflight_real_diagnosis()
    {
        using var temp = new TempAppPaths();
        var runner = new HeldProbeRunner();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), runner, clock, temp.Paths,
            sessionId: "session-demo-inflight", bootId: "boot-test");
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings
        {
            ProbeTargetEndpoint = Target().Endpoint,
        }, clock, session, new HttpProbeTransport());
        var vm = new MainViewModel(services, new InlineUiDispatcher());
        var diagnosing = vm.DiagnoseAsync(CancellationToken.None);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.LoadDemoAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnosing);
        Assert.True(vm.IsDemo);
        Assert.True(vm.Diagnoses.IsSynthetic);
        Assert.True(vm.Timeline.IsSynthetic);
        Assert.Equal(0, session.EventCount);
    }

    [Fact]
    public async Task Demo_clients_use_mock_provider_and_do_not_refresh_the_real_provider()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var provider = new CountedClientProvider();
        services.Clients = provider;
        var vm = new MainViewModel(services, new InlineUiDispatcher());
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.LoadDemoAsync();
        var calls = provider.Calls;
        await vm.Clients.RefreshAsync(CancellationToken.None);
        Assert.Equal(calls, provider.Calls);
        Assert.NotEmpty(vm.Clients.Rows);
        Assert.Contains("合成", vm.Clients.Notice);
    }

    [Fact]
    public async Task Proxy_subdomain_gap_is_not_displayed_as_a_complete_sample()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var snapshot = await new StubSnapshotCollector().CollectAsync(new CollectionContext
        {
            SessionId = "session-proxy-gap", BootId = "boot-test", SnapshotId = "snapshot-proxy-gap",
            ObservedAt = clock.UtcNow, MonotonicMs = clock.MonotonicMs, Synthetic = false,
        }, CancellationToken.None);
        snapshot = snapshot with { Proxies = Collected<ProxyState>.Ok(snapshot.Proxies.Value! with
        {
            WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "test unsupported"),
        }) };
        Assert.Equal(EventStatus.Partial, snapshot.Completeness()["proxy"]);
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), clock, temp.Paths,
            sessionId: "session-proxy-ui", bootId: "boot-test");
        var home = new HomeViewModel(session, () => new AppSettings(), _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        home.ApplyCycle(new SessionCycle
        {
            Snapshot = snapshot, Evidence = new EvidenceSet { Snapshot = snapshot, Records = Array.Empty<EvidenceRecord>(), Probes = Array.Empty<ProbeResult>() },
            Diagnoses = Array.Empty<Diagnosis>(), RuleFailures = Array.Empty<RuleEvaluationFailure>(),
            CompletedAt = clock.UtcNow, Persisted = true, AppendedEvents = 1,
        });
        Assert.Equal("采集不足", home.HealthTitle);
        Assert.Contains(home.Domains, row => row.Name == "proxy" && row.Detail.Contains("WinHTTP"));
        await session.StopAsync();
    }

    [Fact]
    public void GoRepair_command_is_disabled_until_a_diagnosis_is_selected()
    {
        var vm = new DiagnosesViewModel();
        Assert.False(vm.GoRepairCommand.CanExecute(null));

        var raised = 0;
        vm.GoRepairCommand.CanExecuteChanged += (_, _) => raised++;

        vm.Apply(null, new[] { T07R2Data.Diagnosis("d-gorepair", Array.Empty<string>()) },
            Array.Empty<RuleEvaluationFailure>());
        Assert.NotNull(vm.Selected);
        Assert.True(vm.GoRepairCommand.CanExecute(null));

        vm.Selected = null;
        Assert.False(vm.GoRepairCommand.CanExecute(null));
        Assert.True(raised >= 2);
    }

    private sealed class HeldProbeRunner : IProbeRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProbeGroupResult> RunAsync(ProbeGroupPlan plan, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled test probe must not return a result.");
        }
    }

    private sealed class CountedClientProvider : IClientStatusProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<IReadOnlyList<ClientStatus>> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            return Task.FromResult<IReadOnlyList<ClientStatus>>(Array.Empty<ClientStatus>());
        }
    }

    private sealed class HeldCollector : ISnapshotCollector
    {
        private readonly StubSnapshotCollector _inner = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Source => "test.held";
        public string SourceVersion => "1";
        public async Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await _inner.CollectAsync(context, CancellationToken.None);
        }
    }
}
