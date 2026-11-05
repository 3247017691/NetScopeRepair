using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Desktop.Tests;

public sealed class SessionLifecycleTests
{
    private static AppSettings Settings() => new();

    [Fact]
    public async Task Complete_sample_without_diagnoses_is_labelled_sample_normal_not_whole_network()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-normal", bootId: "boot-test");
        var home = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var cycle = await session.CollectOnceAsync(CancellationToken.None);
            home.ApplyCycle(cycle);
            Assert.Equal("当前样本正常", home.HealthTitle);
            Assert.Contains("不代表全网正常", home.HealthDetail);
            Assert.Contains(home.Domains, row => row.Name == "interfaces" && row.StatusText == "正常");
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Missing_domain_is_reported_as_insufficient_collection()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(EventStatus.AccessDenied),
            new StubProbeRunner(), new TestClock(), temp.Paths, sessionId: "session-gap", bootId: "boot-test");
        var home = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            home.ApplyCycle(await session.CollectOnceAsync(CancellationToken.None));
            Assert.Equal("采集不足", home.HealthTitle);
            Assert.Contains(home.Domains, row => row.Name == "interfaces" && row.StatusText == "权限不足");
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Evidence_backed_diagnosis_is_shown_as_anomaly_with_its_evidence()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var collector = new StubSnapshotCollector();
        var snapshot = await collector.CollectAsync(new CollectionContext
        {
            SessionId = "session-claim",
            BootId = "boot-test",
            SnapshotId = "snapshot-1",
            ObservedAt = clock.UtcNow,
            MonotonicMs = clock.MonotonicMs,
            Synthetic = false,
        }, CancellationToken.None);
        var diagnosis = new Diagnosis
        {
            DiagnosisId = "diagnosis-001",
            RuleId = "DX-002",
            RuleVersion = "1.1.0",
            Level = EvidenceGrade.Supported,
            Summary = "合成结论：保留段 DNS 地址与可信运行态配置同时存在。",
            EvidenceIds = new[] { "snapshot-1" },
            SuggestedNextStep = "只读核对配置来源。",
            Scope = new DiagnosisScope { TargetId = "target-test" },
        };
        var cycle = new SessionCycle
        {
            Snapshot = snapshot,
            Evidence = new EvidenceSet { Records = Array.Empty<EvidenceRecord>(), Probes = Array.Empty<ProbeResult>(), Snapshot = snapshot },
            Diagnoses = new[] { diagnosis },
            RuleFailures = Array.Empty<RuleEvaluationFailure>(),
            CompletedAt = clock.UtcNow,
            Persisted = true,
            AppendedEvents = 2,
        };
        await using var session = new ObservationSession(collector, new StubProbeRunner(), clock, temp.Paths,
            sessionId: "session-claim", bootId: "boot-test");
        var home = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            home.ApplyCycle(cycle);
            Assert.Equal("发现异常", home.HealthTitle);
            Assert.Contains("证据支持", home.HealthDetail);
            var row = Assert.Single(home.RecentDiagnoses);
            Assert.Equal("DX-002", row.RuleId);
            Assert.Equal("snapshot-1", row.EvidenceText);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Session_appends_each_cycle_and_stops_cleanly()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-append", bootId: "boot-test");
        var first = await session.CollectOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = await session.CollectOnceAsync(CancellationToken.None);
        Assert.True(first.Persisted);
        Assert.True(second.Persisted);

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.Log.IsClean);
        Assert.Equal(2, replay.Log.Events.Count(item => item.EventType == EventType.Snapshot));
        Assert.NotNull(replay.Evidence);
        Assert.Equal(2, session.SnapshotCount);

        await session.StopAsync();
        Assert.Equal(ObservationState.Stopped, session.State);
    }

    [Fact]
    public async Task Diagnosis_run_persists_all_three_paths_without_any_socket()
    {
        using var temp = new TempAppPaths();
        var runner = new StubProbeRunner();
        await using var session = new ObservationSession(new StubSnapshotCollector(), runner, new TestClock(),
            temp.Paths, sessionId: "session-probe", bootId: "boot-test");
        var cycle = await session.RunDiagnosisAsync(new ProbeTarget
        {
            TargetId = "target-test",
            Endpoint = "https://example.invalid/health",
            ExpectedStatusCodes = new[] { 204 },
        }, "http://127.0.0.1:7890", null, CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(3, cycle.Evidence.Probes.Count);
        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.NotNull(replay.Evidence);
        Assert.Equal(3, replay.Evidence!.Probes.Count);
        Assert.Contains(replay.Evidence.Probes, probe => probe.Path == ProbePath.ExplicitHttpProxy);
        await session.StopAsync();
    }

    [Fact]
    public async Task Persistence_failure_is_reported_and_retried_honestly()
    {
        using var temp = new TempAppPaths();
        var sessionId = "session-blocked";
        Directory.CreateDirectory(Path.Combine(temp.Paths.SessionsDirectory, sessionId + ".ndjson"));
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: sessionId, bootId: "boot-test");
        var home = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var cycle = await session.CollectOnceAsync(CancellationToken.None);
            Assert.False(cycle.Persisted);
            Assert.NotNull(cycle.PersistenceError);
            Assert.Equal(cycle.PersistenceError, session.PersistenceError);
            home.ApplyCycle(cycle);
            Assert.Contains(home.Domains, row => row.Name == "日志" && row.StatusText == "写入失败");
            Assert.StartsWith("日志写入失败", home.PersistenceText);

            var retry = await session.CollectOnceAsync(CancellationToken.None);
            Assert.False(retry.Persisted);
            Assert.Equal(0, retry.AppendedEvents);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Observed_state_transitions_are_visible()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-state", bootId: "boot-test");
        Assert.Equal(ObservationState.Idle, session.State);
        session.Start(TimeSpan.FromSeconds(30));
        Assert.Equal(ObservationState.Observing, session.State);
        session.Pause();
        Assert.Equal(ObservationState.Paused, session.State);
        await session.StopAsync();
        Assert.Equal(ObservationState.Stopped, session.State);
    }

    [Fact]
    public async Task One_failed_cycle_is_reported_and_observation_keeps_running()
    {
        using var temp = new TempAppPaths();
        var collector = new FlakySnapshotCollector();
        await using var session = new ObservationSession(collector, new StubProbeRunner(), new TestClock(),
            temp.Paths, sessionId: "session-flaky", bootId: "boot-test");
        var failures = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CollectionFailed += (_, _) => Interlocked.Increment(ref failures);
        session.CycleCompleted += (_, _) =>
        {
            if (session.SnapshotCount >= 2) completed.TrySetResult();
        };
        session.Start(TimeSpan.FromSeconds(1));
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await session.StopAsync();
        }

        Assert.True(Volatile.Read(ref failures) >= 1);
        Assert.True(session.SnapshotCount >= 2);
        Assert.Equal(ObservationState.Stopped, session.State);
    }

    [Fact]
    public async Task Health_is_never_normal_before_the_first_completed_sample()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-empty", bootId: "boot-test");
        var home = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            home.ApplySessionState();
            Assert.NotEqual("当前样本正常", home.HealthTitle);
            Assert.Contains("等待", home.HealthTitle);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Restarting_observation_requires_a_fresh_sample_again()
    {
        using var temp = new TempAppPaths();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-restart", bootId: "boot-test");
        var home = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            home.ApplyCycle(await session.CollectOnceAsync(CancellationToken.None));
            Assert.Equal("当前样本正常", home.HealthTitle);

            session.Pause();
            home.ApplySessionState();
            Assert.Equal("采集已暂停", home.HealthTitle);

            home.StartCommand.Execute(null);
            Assert.NotEqual("当前样本正常", home.HealthTitle);
            Assert.Contains("等待", home.HealthTitle);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Contradicted_conclusion_is_not_displayed_as_an_anomaly()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var collector = new StubSnapshotCollector();
        var snapshot = await collector.CollectAsync(new CollectionContext
        {
            SessionId = "session-contradicted", BootId = "boot-test", SnapshotId = "snapshot-c",
            ObservedAt = clock.UtcNow, MonotonicMs = clock.MonotonicMs, Synthetic = false,
        }, CancellationToken.None);
        var diagnosis = new Diagnosis
        {
            DiagnosisId = "diagnosis-c",
            RuleId = "DX-006",
            RuleVersion = "1.1.0",
            Level = EvidenceGrade.Contradicted,
            Summary = "运行态与磁盘一致，反驳配置陈旧推断。",
            EvidenceIds = new[] { "snapshot-c" },
            SuggestedNextStep = "继续观察。",
            Scope = new DiagnosisScope(),
        };
        await using var session = new ObservationSession(collector, new StubProbeRunner(), clock, temp.Paths,
            sessionId: "session-contradicted", bootId: "boot-test");
        var homeVm = new HomeViewModel(session, Settings, _ => Task.FromResult<SessionCycle?>(null));
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            homeVm.ApplyCycle(new SessionCycle
            {
                Snapshot = snapshot,
                Evidence = new EvidenceSet { Records = Array.Empty<EvidenceRecord>(), Probes = Array.Empty<ProbeResult>(), Snapshot = snapshot },
                Diagnoses = new[] { diagnosis },
                RuleFailures = Array.Empty<RuleEvaluationFailure>(),
                CompletedAt = clock.UtcNow,
                Persisted = true,
                AppendedEvents = 1,
            });
            Assert.NotEqual("发现异常", homeVm.HealthTitle);
            Assert.Contains("反证", homeVm.HealthTitle);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Loading_demo_while_observing_pauses_and_labels_synthetic_diagnoses()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-demo-live", bootId: "boot-test");
        session.Start(TimeSpan.FromSeconds(30));
        using var services = new AppServices(temp.Paths, new SettingsStore(temp.Paths), new AppSettings(),
            clock, session, new NetScope.Probes.HttpProbeTransport());
        services.Exporter = new IncidentExporter();
        var viewModel = new MainViewModel(services, new InlineUiDispatcher());
        try
        {
            await viewModel.LoadDemoAsync();
            Assert.True(viewModel.IsDemo);
            Assert.Equal(ObservationState.Paused, session.State);
            Assert.True(viewModel.Diagnoses.IsSynthetic);
            Assert.Contains("合成", viewModel.Diagnoses.DataLabel);
            Assert.Contains("暂停", viewModel.StatusMessage);

            session.Start(TimeSpan.FromSeconds(30));
            Assert.False(viewModel.IsDemo);
            Assert.False(viewModel.Diagnoses.IsSynthetic);
            Assert.Empty(viewModel.Diagnoses.Rows);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Multi_event_batches_keep_contiguous_sequences_across_cycles()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-batches", bootId: "boot-test");
        var target = new ProbeTarget
        {
            TargetId = "target-test",
            Endpoint = "https://example.invalid/health",
            ExpectedStatusCodes = new[] { 204 },
        };
        await session.RunDiagnosisAsync(target, null, null, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        await session.RunDiagnosisAsync(target, null, null, CancellationToken.None);

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.Log.IsClean);
        Assert.Empty(replay.Log.SequenceGaps);
        Assert.Equal(0, replay.Log.DuplicateCount);
        Assert.Equal(8, replay.Log.Events.Count);
        Assert.Equal(Enumerable.Range(1, 8), replay.Log.Events.Select(item => item.Sequence));
        await session.StopAsync();
    }

    [Fact]
    public void Settings_round_trip_and_corrupt_file_falls_back_to_defaults()
    {
        using var temp = new TempAppPaths();
        var store = new SettingsStore(temp.Paths);
        Assert.Equal(string.Empty, store.Load().ProbeTargetEndpoint);

        store.Save(new AppSettings
        {
            ObservationInterval = TimeSpan.FromSeconds(15),
            ProbeTargetEndpoint = "https://example.invalid/health",
            ExpectedStatus = 200,
            ExplicitProxyEndpoint = "http://127.0.0.1:7890",
        });
        var loaded = store.Load();
        Assert.Equal(15, (int)loaded.ObservationInterval.TotalSeconds);
        Assert.Equal("https://example.invalid/health", loaded.ProbeTargetEndpoint);
        Assert.Equal(200, loaded.ExpectedStatus);

        File.WriteAllText(temp.Paths.SettingsFile, "{ definitely not json");
        var fallback = store.Load();
        Assert.Equal(TimeSpan.FromSeconds(10), fallback.ObservationInterval);
        Assert.Equal(string.Empty, fallback.ProbeTargetEndpoint);
    }

    [Fact]
    public async Task Timeline_and_diagnoses_can_read_a_synthetic_demo_session()
    {
        using var temp = new TempAppPaths();
        var path = await SyntheticShowcase.CreateAsync(temp.Paths, CancellationToken.None);
        var timeline = new TimelineViewModel();
        Assert.True(await timeline.LoadAsync(path));
        Assert.Equal(11, timeline.Rows.Count);
        Assert.True(timeline.IsSynthetic);
        Assert.Empty(timeline.Problems);
        Assert.Empty(timeline.Gaps);

        var replay = new ReplayService().Replay(path);
        var diagnoses = new DiagnosesViewModel();
        diagnoses.LoadReplay(replay);
        Assert.NotEmpty(diagnoses.Rows);
        Assert.All(diagnoses.Rows, row => Assert.NotEmpty(row.Evidence));
    }

    [Fact]
    public async Task Main_view_model_loads_demo_as_explicit_synthetic_data()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var viewModel = new MainViewModel(services, new InlineUiDispatcher());
        await viewModel.LoadDemoAsync();
        Assert.True(viewModel.IsDemo);
        Assert.Contains("合成演示", viewModel.DemoBannerText);
        Assert.Equal(11, viewModel.Timeline.Rows.Count);
        Assert.NotEmpty(viewModel.Diagnoses.Rows);
        Assert.True(viewModel.Diagnoses.IsSynthetic);
        Assert.Contains("合成演示", viewModel.Home.HealthTitle);
        Assert.NotEmpty(viewModel.Home.Domains);
        Assert.Contains(viewModel.Home.RecentDiagnoses, row => row.LevelText is "证据支持" or "直接观察");
    }

    [Fact]
    public async Task Export_preview_from_view_model_uses_the_real_exporter()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var viewModel = new MainViewModel(services, new InlineUiDispatcher());
        await viewModel.LoadDemoAsync();
        await viewModel.Export.PreviewAsync();
        Assert.True(viewModel.Export.HasExporter);
        Assert.True(viewModel.Export.CanExport);
        Assert.Equal(7, viewModel.Export.Files.Count);
        Assert.NotEmpty(viewModel.Export.Redactions);
        Assert.Contains(viewModel.Export.Files, row => row.Name == "checksums.sha256");
    }

    [Fact]
    public async Task Missing_target_disables_diagnosis_without_starting_a_probe()
    {
        using var temp = new TempAppPaths();
        using var services = Composition.CreateDefault(temp.Paths, synthetic: true);
        var runner = new StubProbeRunner();
        var viewModel = new MainViewModel(services, new InlineUiDispatcher());
        var result = await viewModel.DiagnoseAsync(CancellationToken.None);
        Assert.Null(result);
        Assert.True(viewModel.HasError);
        Assert.Equal(0, runner.Calls);
        Assert.False(viewModel.Home.CanDiagnose);
    }

    [Fact]
    public void Settings_view_model_allows_changing_and_resetting_close_behavior()
    {
        using var temp = new TempAppPaths();
        var store = new SettingsStore(temp.Paths);
        AppSettings? applied = null;
        var vm = new SettingsViewModel(store, new AppSettings { HideOnClose = true }, s => applied = s);

        Assert.Equal(1, vm.CloseBehaviorIndex);
        Assert.Equal("关闭窗口时隐藏到托盘", vm.HideOnCloseText);

        vm.CloseBehaviorIndex = 2;
        Assert.Equal(false, store.Load().HideOnClose);
        Assert.Equal(false, applied?.HideOnClose);
        Assert.Equal("关闭窗口时退出程序", vm.HideOnCloseText);

        vm.CloseBehaviorIndex = 0;
        Assert.Null(store.Load().HideOnClose);
        Assert.Null(applied?.HideOnClose);
        Assert.Equal("关闭窗口时询问", vm.HideOnCloseText);
    }
}
