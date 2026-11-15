using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T07-R2 home page: collection health, network verification and the last rule run are three
/// separate observations. Zero probe results are never green, and an empty diagnosis is not health.
/// </summary>
public sealed class T07R2HomeVerificationTests
{
    private static readonly AppSettings NoTarget = new();

    [Fact]
    public async Task Complete_collection_with_zero_probes_is_shown_as_not_yet_externally_verified()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new BlockingSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-home", bootId: "boot-t07r2");
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var home = new HomeViewModel(session, () => NoTarget, _ => Task.FromResult<SessionCycle?>(null));
            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-1", clock.UtcNow, 1000), Array.Empty<ProbeResult>(),
                Array.Empty<Diagnosis>(), clock.UtcNow));

            Assert.Equal("采集完整", home.CollectionHealthTitle);
            Assert.Equal(UiText.OkBrush, home.CollectionHealthTone);
            Assert.Equal("尚未进行外部验证", home.VerificationTitle);
            Assert.Equal("尚未进行外部验证", home.HealthTitle);
            Assert.Same(UiText.GapBrush, home.HealthTone);
            Assert.NotEqual("当前样本正常", home.HealthTitle);
            Assert.False(home.HasExternalVerification);
            Assert.Contains("空诊断不代表全网正常", home.VerificationDetail);
            Assert.Contains("未测", home.VerificationPathsText);
            Assert.Contains("最近诊断", home.DiagnosisTimeText);
            Assert.Contains("0 条结论", home.DiagnosisTimeText);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task A_comparable_passing_probe_turns_only_the_verification_block_green()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new BlockingSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-pass", bootId: "boot-t07r2");
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var home = new HomeViewModel(session, () => NoTarget, _ => Task.FromResult<SessionCycle?>(null));
            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-1", clock.UtcNow, 1000), AllPassing(), Array.Empty<Diagnosis>(),
                clock.UtcNow, diagnosisRequested: true));

            Assert.Equal("外部验证通过（受控目标）", home.VerificationTitle);
            Assert.Equal(UiText.OkBrush, home.VerificationTone);
            Assert.Equal("外部验证通过（受控目标）", home.HealthTitle);
            Assert.Contains("不代表全网正常", home.HealthDetail);
            Assert.Contains("DSH", home.HealthDetail);
            Assert.Contains("正常", home.VerificationPathsText);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task A_later_local_sample_marks_the_previous_pass_as_needing_a_retest()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new BlockingSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-stale", bootId: "boot-t07r2");
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var home = new HomeViewModel(session, () => NoTarget, _ => Task.FromResult<SessionCycle?>(null));
            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-1", clock.UtcNow, 1000), AllPassing(), Array.Empty<Diagnosis>(),
                clock.UtcNow, diagnosisRequested: true));
            Assert.Equal("外部验证通过（受控目标）", home.VerificationTitle);

            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-2", clock.UtcNow.AddSeconds(10), 11000), Array.Empty<ProbeResult>(),
                Array.Empty<Diagnosis>(), clock.UtcNow.AddSeconds(10)));

            Assert.Equal("外部验证需复测", home.VerificationTitle);
            Assert.Equal(UiText.WarnBrush, home.VerificationTone);
            Assert.Contains("当前采样没有复测目标连通性", home.VerificationDetail);
            Assert.NotEqual(UiText.OkBrush, home.HealthTone);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task A_failed_path_is_shown_as_not_passed_and_never_as_network_health()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new BlockingSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-failed", bootId: "boot-t07r2");
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var home = new HomeViewModel(session, () => NoTarget, _ => Task.FromResult<SessionCycle?>(null));
            var probes = new[]
            {
                T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, startedMonotonicMs: 1000),
                T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Failed, startedMonotonicMs: 1020,
                    failedStage: "connect", transportError: "connection_refused"),
                T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, startedMonotonicMs: 1040),
            };
            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-1", clock.UtcNow, 1000), probes, Array.Empty<Diagnosis>(),
                clock.UtcNow, diagnosisRequested: true));

            Assert.Equal("外部验证未通过", home.VerificationTitle);
            Assert.Equal(UiText.BadBrush, home.VerificationTone);
            Assert.Equal("外部验证未通过", home.HealthTitle);
            Assert.Contains("失败", home.VerificationPathsText);
            Assert.Contains("无显式代理", home.VerificationPathsText);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task Successful_paths_without_a_proven_family_are_not_comparable()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new BlockingSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-family", bootId: "boot-t07r2");
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var home = new HomeViewModel(session, () => NoTarget, _ => Task.FromResult<SessionCycle?>(null));
            var probes = new[]
            {
                T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, family: null, timingComparable: false,
                    startedMonotonicMs: 1000),
                T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, family: null, timingComparable: false,
                    startedMonotonicMs: 1020),
                T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, family: null, timingComparable: false,
                    startedMonotonicMs: 1040),
            };
            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-1", clock.UtcNow, 1000), probes, Array.Empty<Diagnosis>(),
                clock.UtcNow, diagnosisRequested: true));

            Assert.Equal("外部验证不可比", home.VerificationTitle);
            Assert.Equal(UiText.WarnBrush, home.VerificationTone);
            Assert.Contains("地址族未指定", home.VerificationDetail);
            Assert.NotEqual(UiText.OkBrush, home.HealthTone);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    public async Task The_last_rule_run_is_reported_separately_from_collection_and_verification()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new BlockingSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-diagtime", bootId: "boot-t07r2");
        session.Start(TimeSpan.FromSeconds(30));
        try
        {
            var home = new HomeViewModel(session, () => NoTarget, _ => Task.FromResult<SessionCycle?>(null));
            var diagnosis = T07R2Data.Diagnosis("diagnosis-local-1", new[] { "snapshot-1" },
                EvidenceGrade.Insufficient);
            home.ApplyCycle(T07R2Data.Cycle(
                T07R2Data.Snapshot("snapshot-1", clock.UtcNow, 1000), Array.Empty<ProbeResult>(),
                new[] { diagnosis }, clock.UtcNow));

            Assert.Equal("尚未进行外部验证", home.VerificationTitle);
            Assert.Contains("1 条结论", home.DiagnosisTimeText);
            Assert.Contains("只基于本机证据", home.DiagnosisDetailText);
            Assert.Contains("证据不足", home.HealthTitle);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    private static ProbeResult[] AllPassing() => new[]
    {
        T07R2Data.Probe(ProbePath.SystemDefault, EventStatus.Ok, startedMonotonicMs: 1000),
        T07R2Data.Probe(ProbePath.NoExplicitProxy, EventStatus.Ok, startedMonotonicMs: 1020),
        T07R2Data.Probe(ProbePath.ExplicitHttpProxy, EventStatus.Ok, startedMonotonicMs: 1040),
    };
}
