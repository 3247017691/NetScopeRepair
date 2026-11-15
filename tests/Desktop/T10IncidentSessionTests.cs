using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T10 session integration: cycles and explicit probe groups feed the recorder, user marks are
/// persisted immediately, pause/exit drain markers, and nothing is captured after exit.
/// </summary>
public sealed class T10IncidentSessionTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Manual_mark_is_recorded_immediately_and_markers_survive_replay()
    {
        using var temp = new TempAppPaths();
        var recorder = new IncidentRecorder();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t10-mark", bootId: "boot-test", recorder: recorder);
        session.Start(TimeSpan.FromSeconds(30));
        await session.CollectOnceAsync(CancellationToken.None);
        Assert.Equal(IncidentState.Observing, recorder.State);

        var incident = await session.MarkIncidentAsync("用户标记：刚才断网了", CancellationToken.None);
        Assert.NotNull(incident);
        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        await session.PauseAsync();
        await session.StopAsync();

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var markers = replay.Log.Events.Where(item => item.EventType == EventType.IncidentMarker).ToArray();
        Assert.NotEmpty(markers);
        Assert.All(markers, marker => Assert.False(marker.Payload.ContainsKey("payload_version")));
        var userMark = Assert.Single(markers, marker => marker.Payload["reason"]!.GetValue<string>() == "user_mark");
        Assert.Equal(incident!.IncidentId, userMark.Refs.IncidentId);
        Assert.Equal(incident.IncidentId, userMark.Payload["incident"]!["incident_id"]!.GetValue<string>());
        Assert.Equal("manual_user_mark", userMark.Payload["incident"]!["trigger"]!.GetValue<string>());
        Assert.Equal("incident_open", userMark.Payload["state"]!.GetValue<string>());
        Assert.Contains(markers, marker =>
            marker.Payload["reason"]!.GetValue<string>() == "incident_closed:session_end_before_post_window");
        var closed = Assert.Single(recorder.Incidents);
        Assert.Equal("session_end_before_post_window", closed.ClosureReason);
        Assert.Equal(IncidentState.Idle, recorder.State);
    }

    [Fact]
    public async Task Business_checks_are_persisted_as_markers_and_not_tested_stays_distinct()
    {
        using var temp = new TempAppPaths();
        var recorder = new IncidentRecorder();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t10-business", bootId: "boot-test", recorder: recorder);
        session.Start(TimeSpan.FromSeconds(30));
        await session.CollectOnceAsync(CancellationToken.None);

        var entry = await session.RecordBusinessResultAsync(IncidentBusinessCategory.Dsh,
            IncidentBusinessResult.Pass, "user", "用户在观察窗内完成真实聊天", CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(IncidentBusinessResult.Pass, entry!.Result);
        Assert.Equal(IncidentBusinessResult.NotTested,
            recorder.Status.BusinessChecks.Single(item => item.Category == IncidentBusinessCategory.Chrome).Result);
        Assert.False(recorder.Status.AllBusinessChecksPassed);
        await session.PauseAsync();
        await session.StopAsync();

        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var marker = Assert.Single(replay.Log.Events, item =>
            item.EventType == EventType.IncidentMarker && item.Payload.ContainsKey("business_check"));
        Assert.Equal("business_check", marker.Payload["kind"]!.GetValue<string>());
        Assert.Equal("dsh", marker.Payload["business_check"]!["category"]!.GetValue<string>());
        Assert.Equal("pass", marker.Payload["business_check"]!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task Three_explicit_failed_probe_groups_open_an_incident_through_suspected()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var recorder = new IncidentRecorder();
        var runner = new StubProbeRunner(EventStatus.Failed);
        await using var session = new ObservationSession(new StubSnapshotCollector(), runner, clock, temp.Paths,
            sessionId: "session-t10-threshold", bootId: "boot-test", recorder: recorder);
        session.Start(TimeSpan.FromSeconds(30));
        var target = new ProbeTarget
        {
            TargetId = "target-t10",
            Endpoint = "https://example.invalid/health",
        };
        for (var index = 0; index < 2; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await session.RunDiagnosisAsync(target, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);
            Assert.Equal(IncidentState.Observing, recorder.State);
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        var cycle = await session.RunDiagnosisAsync(target, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);

        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Equal(IncidentTrigger.ConsecutiveFailures, current.Trigger);
        Assert.True(current.EffectiveFailureCount >= 3);
        Assert.True(current.Window.PreWindowAvailable || current.Window.MissingRanges.Contains("pre_window_incomplete"));
        Assert.Contains(cycle.Evidence.Probes, probe => probe.Path == ProbePath.ExplicitHttpProxy);
        var markers = new ReplayService().Replay(session.SessionPath).Log.Events
            .Where(item => item.EventType == EventType.IncidentMarker).ToArray();
        Assert.Contains(markers, marker => marker.Payload["state"]!.GetValue<string>() == "suspected");
        Assert.Contains(markers, marker => marker.Payload["reason"]!.GetValue<string>() == "incident_opened");
        await session.StopAsync();
    }

    [Fact]
    public async Task Local_samples_never_open_an_incident_without_a_user_probe()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var recorder = new IncidentRecorder();
        var runner = new StubProbeRunner(EventStatus.Failed);
        await using var session = new ObservationSession(new StubSnapshotCollector(), runner, clock, temp.Paths,
            sessionId: "session-t10-local", bootId: "boot-test", recorder: recorder);
        session.Start(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 8; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await session.CollectOnceAsync(CancellationToken.None);
        }

        Assert.Equal(IncidentState.Observing, recorder.State);
        Assert.Null(recorder.Status.CurrentIncident);
        Assert.Equal(0, runner.Calls);
        await session.StopAsync();
    }

    [Fact]
    public async Task Pause_flushes_markers_and_explicit_exit_stops_recording()
    {
        using var temp = new TempAppPaths();
        var recorder = new IncidentRecorder();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-t10-exit", bootId: "boot-test", recorder: recorder);
        session.Start(TimeSpan.FromSeconds(30));
        await session.CollectOnceAsync(CancellationToken.None);
        await session.PauseAsync();
        Assert.Equal(IncidentState.Paused, recorder.State);
        var pausedReplay = new ReplayService().Replay(session.SessionPath);
        Assert.True(pausedReplay.IsClean, string.Join("; ", pausedReplay.EvidenceProblems));
        Assert.Contains(pausedReplay.Log.Events, item => item.EventType == EventType.IncidentMarker
            && item.Payload["state"]!.GetValue<string>() == "paused");

        await session.StopAsync();
        Assert.Equal(IncidentState.Idle, recorder.State);
        Assert.False(recorder.Status.CanMarkIncident);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.MarkIncidentAsync("退出后的标记", CancellationToken.None));
        var stoppedReplay = new ReplayService().Replay(session.SessionPath);
        Assert.True(stoppedReplay.IsClean, string.Join("; ", stoppedReplay.EvidenceProblems));
        Assert.Contains(stoppedReplay.Log.Events, item => item.EventType == EventType.IncidentMarker
            && item.Payload["reason"]!.GetValue<string>() == "session_ended");
    }

    [Fact]
    public async Task Only_an_explicit_probe_cycle_is_marked_as_user_enabled_external_observation()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        var recorder = new CapturingRecorder();
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t10-gate", bootId: "boot-test", recorder: recorder);

        await session.CollectOnceAsync(CancellationToken.None);
        var local = Assert.Single(recorder.Observations);
        Assert.False(local.ExternalObservationEnabled);
        Assert.Empty(local.Probes);

        clock.Advance(TimeSpan.FromSeconds(10));
        await session.RunDiagnosisAsync(new ProbeTarget
        {
            TargetId = "target-t10-gate",
            Endpoint = "https://example.invalid/health",
        }, "http://127.0.0.1:7890", "ipv4", CancellationToken.None);

        var external = recorder.Observations[^1];
        Assert.True(external.ExternalObservationEnabled);
        Assert.Equal(3, external.Probes.Count);
        Assert.Equal("target-t10-gate", external.ExternalObservationTargetId);
        Assert.NotNull(external.ExternalObservationBudget);
        await session.StopAsync();
    }

    [Fact]
    public void Tray_helpers_choose_enablement_and_describe_the_latest_incident()
    {
        var recorder = new IncidentRecorder();
        Assert.False(TrayController.HasRecentIncident(recorder.Status));
        Assert.False(TrayController.CanMarkIncident(recorder.Status, ObservationState.Idle));

        recorder.Observe(ObservationAt(Origin));
        Assert.True(TrayController.CanMarkIncident(recorder.Status, ObservationState.Observing));
        Assert.True(TrayController.CanMarkIncident(recorder.Status, ObservationState.Paused));
        Assert.False(TrayController.CanMarkIncident(recorder.Status, ObservationState.Stopped));
        Assert.False(TrayController.HasRecentIncident(recorder.Status));

        var transitions = recorder.MarkIncident("用户标记：刚才断网了", Context(Origin.AddSeconds(5)));
        var incident = transitions[^1].Incident!;
        Assert.True(TrayController.HasRecentIncident(recorder.Status));
        var text = TrayController.DescribeIncident(incident);
        Assert.Contains("用户标记", text, StringComparison.Ordinal);
        Assert.Contains("记录中", text, StringComparison.Ordinal);
        Assert.Contains("缺前窗", text, StringComparison.Ordinal);
        Assert.Contains("当前故障", TrayController.DescribeStatus(recorder.Status), StringComparison.Ordinal);

        recorder.Shutdown(Context(Origin.AddSeconds(30)));
        var closed = recorder.Incidents[0];
        var closedText = TrayController.DescribeIncident(closed);
        Assert.Contains("已结束（后窗不完整）", closedText, StringComparison.Ordinal);
        Assert.Contains("根因未证实", closedText, StringComparison.Ordinal);
    }

    private sealed class CapturingRecorder : IIncidentRecorder
    {
        public List<IncidentObservation> Observations { get; } = new();

        public IncidentState State => IncidentState.Observing;

        public IncidentRecorderStatus Status { get; } = new()
        {
            State = IncidentState.Observing,
            HasRecentIncident = false,
            CanMarkIncident = true,
        };

        public IReadOnlyList<IncidentRecord> Incidents => Array.Empty<IncidentRecord>();

        public event EventHandler<IncidentRecorderStatus>? StatusChanged { add { } remove { } }

        public IReadOnlyList<IncidentTransition> Observe(IncidentObservation observation)
        {
            Observations.Add(observation);
            return Array.Empty<IncidentTransition>();
        }

        public IReadOnlyList<IncidentTransition> MarkIncident(string userDescription, IncidentMarkContext context) =>
            Array.Empty<IncidentTransition>();

        public IReadOnlyList<IncidentTransition> RecordBusinessResult(IncidentBusinessCategory category,
            IncidentBusinessResult result, string source, string? userNote, IncidentMarkContext context) =>
            Array.Empty<IncidentTransition>();

        public IReadOnlyList<IncidentTransition> Pause(IncidentMarkContext context) =>
            Array.Empty<IncidentTransition>();

        public IReadOnlyList<IncidentTransition> Resume(IncidentMarkContext context) =>
            Array.Empty<IncidentTransition>();

        public IReadOnlyList<IncidentTransition> Degrade(string reason, IncidentMarkContext context) =>
            Array.Empty<IncidentTransition>();

        public IReadOnlyList<IncidentTransition> Shutdown(IncidentMarkContext context) =>
            Array.Empty<IncidentTransition>();
    }

    private static IncidentObservation ObservationAt(DateTimeOffset at) => new()
    {
        SessionId = "session-t10",
        BootId = "boot-t10",
        ObservationWindowId = "window-t10",
        SnapshotId = "snapshot-t10",
        ObservedAt = at,
        MonotonicMs = (at - Origin).TotalMilliseconds,
        Synthetic = false,
    };

    private static IncidentMarkContext Context(DateTimeOffset at) => new()
    {
        SessionId = "session-t10",
        BootId = "boot-t10",
        ObservedAt = at,
        MonotonicMs = (at - Origin).TotalMilliseconds,
        Synthetic = false,
    };
}
