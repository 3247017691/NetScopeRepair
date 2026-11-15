using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Unit.Tests;

/// <summary>
/// T10 incident state machine under a deterministic fake time source. Observations are supplied
/// directly, so no collector, probe transport or network is involved.
/// </summary>
public sealed class T10IncidentRecorderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));
    private static int _probeCounter;

    [Fact]
    public void Three_consecutive_effective_failures_go_through_suspected_to_incident_open()
    {
        var recorder = new IncidentRecorder();
        var transitions = new List<IncidentTransition>();
        for (var index = 0; index < 3; index++)
        {
            transitions.AddRange(recorder.Observe(Observation(T0.AddSeconds(index * 10), Failure(T0.AddSeconds(index * 10)))));
        }

        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        Assert.Contains(transitions, item => item.State == IncidentState.Suspected && item.Reason == "failure_threshold_reached");
        Assert.Contains(transitions, item => item.State == IncidentState.IncidentOpen && item.Reason == "incident_opened");
        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Equal(IncidentTrigger.ConsecutiveFailures, current.Trigger);
        Assert.Equal(T0, current.StartedAt);
        Assert.Equal(3, current.EffectiveFailureCount);
        var group = Assert.Single(current.Failures);
        Assert.Equal(3, group.Count);
        Assert.Equal(T0, group.FirstAt);
        Assert.Equal(T0.AddSeconds(20), group.LastAt);
        Assert.False(current.Window.PreWindowAvailable);
        Assert.Contains("pre_window_incomplete", current.Window.MissingRanges);
    }

    [Fact]
    public void Five_minutes_of_pre_window_is_captured_and_kept_as_incident_evidence()
    {
        var recorder = new IncidentRecorder();
        for (var index = 0; index <= 10; index++)
        {
            var at = T0.AddSeconds(index * 30);
            recorder.Observe(Observation(at, external: false, eventIds: new[] { "local-" + index }));
        }

        var failureStart = T0.AddSeconds(300);
        recorder.Observe(Observation(failureStart, Failure(failureStart), eventIds: new[] { "fail-0" }));
        recorder.Observe(Observation(failureStart.AddSeconds(10), Failure(failureStart.AddSeconds(10)), eventIds: new[] { "fail-1" }));
        recorder.Observe(Observation(failureStart.AddSeconds(20), Failure(failureStart.AddSeconds(20)), eventIds: new[] { "fail-2" }));

        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.True(current.Window.PreWindowAvailable);
        Assert.DoesNotContain("pre_window_incomplete", current.Window.MissingRanges);
        Assert.Contains("local-0", current.EventIds);
        Assert.Contains("local-10", current.EventIds);
        Assert.Contains("fail-0", current.EventIds);
        Assert.Equal(failureStart, current.StartedAt);
        Assert.Equal(failureStart.AddSeconds(-300), current.Window.PreWindowStart);
    }

    [Fact]
    public void Manual_user_mark_opens_an_incident_immediately_without_thresholds()
    {
        var recorder = new IncidentRecorder();
        recorder.Observe(Observation(T0));

        var transitions = recorder.MarkIncident("用户标记：刚才断网了", Context(T0.AddSeconds(5)));

        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        Assert.Contains(transitions, item => item.Reason == "user_mark" && item.State == IncidentState.IncidentOpen);
        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Equal(IncidentTrigger.ManualUserMark, current.Trigger);
        Assert.Equal(T0.AddSeconds(5), current.StartedAt);
        Assert.Contains("用户标记：刚才断网了", current.UserDescriptions);
        Assert.False(current.Window.PreWindowAvailable);
        Assert.False(current.PostWindowComplete);
    }

    [Fact]
    public void Missing_cancelled_and_permission_samples_are_not_network_failures()
    {
        var recorder = new IncidentRecorder();
        var at = T0;
        for (var index = 0; index < 10; index++)
        {
            var time = at.AddSeconds(index * 10);
            recorder.Observe(Observation(time,
                NotStarted(time),
                PermissionDenied(time),
                Cancelled(time),
                Unsupported(time)));
        }

        Assert.Equal(IncidentState.Observing, recorder.State);
        Assert.Null(recorder.Status.CurrentIncident);
        Assert.Empty(recorder.Incidents);
    }

    [Fact]
    public void Same_class_failures_merge_for_five_minutes_but_keep_count_first_and_last()
    {
        var recorder = new IncidentRecorder();
        DateTimeOffset time = T0;
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(time, Failure(time)));
            time = time.AddSeconds(10);
        }

        recorder.Observe(Observation(time, Failure(time)));
        recorder.Observe(Observation(time.AddSeconds(20), Failure(time.AddSeconds(20))));
        var merged = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        var group = Assert.Single(merged.Failures);
        Assert.Equal(5, group.Count);
        Assert.Equal(T0, group.FirstAt);
        Assert.Equal(time.AddSeconds(20), group.LastAt);

        recorder.Observe(Observation(time.AddSeconds(20).AddMinutes(6), Failure(time.AddSeconds(20).AddMinutes(6))));
        var split = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Equal(2, split.Failures.Count);
        Assert.Contains(split.Failures, item => item.Count == 5 && item.FirstAt == T0);
        Assert.Contains(split.Failures, item => item.Count == 1);
    }

    [Fact]
    public void Gap_samples_inside_an_incident_are_missing_ranges_not_failures()
    {
        var recorder = new IncidentRecorder();
        OpenByFailures(recorder);
        var at = T0.AddSeconds(30);
        recorder.Observe(Observation(at, Cancelled(at), PermissionDenied(at)));

        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        Assert.Equal(3, current.EffectiveFailureCount);
        Assert.Contains(current.MissingRanges, reason => reason.StartsWith("probe_gap:", StringComparison.Ordinal));
    }

    [Fact]
    public void Successes_on_a_different_target_do_not_count_as_recovery()
    {
        var recorder = new IncidentRecorder();
        OpenByFailures(recorder);
        var at = T0.AddSeconds(30);
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(at.AddSeconds(index * 10), SuccessOn(at.AddSeconds(index * 10), "target-B")));
        }

        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Equal(3, current.EffectiveSuccessCount);
    }

    [Fact]
    public void Three_comparable_successes_enter_recovering_and_a_complete_post_window_closes()
    {
        var recorder = new IncidentRecorder();
        OpenByFailures(recorder);
        var recoveringAt = T0.AddSeconds(30);
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(recoveringAt.AddSeconds(index * 10), Success(recoveringAt.AddSeconds(index * 10))));
        }

        Assert.Equal(IncidentState.Recovering, recorder.State);
        var postEnd = recoveringAt.AddSeconds(20).AddMinutes(2);
        recorder.Observe(Observation(postEnd, Success(postEnd)));

        Assert.Equal(IncidentState.Closed, recorder.State);
        var closed = Assert.Single(recorder.Incidents);
        Assert.Equal("recovery_verified_no_new_failure", closed.ClosureReason);
        Assert.True(closed.PostWindowComplete);
        Assert.True(closed.Window.PostWindowComplete);
        Assert.Equal(recoveringAt.AddSeconds(20).AddMinutes(2), closed.Window.EndedAt);
        Assert.Null(recorder.Status.CurrentIncident);
        Assert.Equal(closed.IncidentId, Assert.IsType<IncidentRecord>(recorder.Status.LastIncident).IncidentId);
    }

    [Fact]
    public void A_new_failure_in_the_post_window_reopens_and_restarts_recovery()
    {
        var recorder = new IncidentRecorder();
        OpenByFailures(recorder);
        var success = T0.AddSeconds(30);
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(success.AddSeconds(index * 10), Success(success.AddSeconds(index * 10))));
        }

        Assert.Equal(IncidentState.Recovering, recorder.State);
        var failureInWindow = T0.AddSeconds(90);
        recorder.Observe(Observation(failureInWindow, Failure(failureInWindow)));

        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Contains("new_failure_during_recovery", current.MissingRanges);
        Assert.Single(current.Failures);

        var secondSuccess = failureInWindow.AddSeconds(10);
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(secondSuccess.AddSeconds(index * 10), Success(secondSuccess.AddSeconds(index * 10))));
        }

        Assert.Equal(IncidentState.Recovering, recorder.State);
        var postEnd = secondSuccess.AddSeconds(20).AddMinutes(2);
        recorder.Observe(Observation(postEnd, Success(postEnd)));
        var closed = Assert.Single(recorder.Incidents);
        Assert.Equal(4, closed.EffectiveFailureCount);
        Assert.True(closed.PostWindowComplete);
    }

    [Fact]
    public void Pause_and_shutdown_close_an_unfinished_window_as_incomplete()
    {
        var recorder = new IncidentRecorder();
        OpenByFailures(recorder);
        var success = T0.AddSeconds(30);
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(success.AddSeconds(index * 10), Success(success.AddSeconds(index * 10))));
        }

        recorder.Pause(Context(T0.AddSeconds(100)));
        Assert.Equal(IncidentState.Paused, recorder.State);
        var shutdown = recorder.Shutdown(Context(T0.AddSeconds(110)));

        Assert.Equal(IncidentState.Idle, recorder.State);
        Assert.Contains(shutdown, item => item.State == IncidentState.Closed);
        var closed = Assert.Single(recorder.Incidents);
        Assert.Equal("session_end_before_post_window", closed.ClosureReason);
        Assert.False(closed.PostWindowComplete);
        Assert.Contains("post_window_incomplete", closed.MissingRanges);
        Assert.False(recorder.Status.CanMarkIncident);
        Assert.Empty(recorder.Observe(Observation(T0.AddSeconds(120), Success(T0.AddSeconds(120)))));
    }

    [Fact]
    public void Sleep_and_boot_change_close_an_open_incident_as_incomplete_and_reset_pre_window()
    {
        var slept = new IncidentRecorder();
        OpenByFailures(slept);
        var sleepContext = new IncidentObservation
        {
            SessionId = "session-t10",
            BootId = "boot-t10",
            ObservationWindowId = "window-sleep",
            SnapshotId = "snapshot-sleep",
            ObservedAt = T0.AddHours(2),
            MonotonicMs = 30_000,
            Synthetic = false,
            Probes = new[] { Success(T0.AddHours(2)) },
            ExternalObservationEnabled = true,
        };
        slept.Observe(sleepContext);
        var sleptIncident = Assert.Single(slept.Incidents);
        Assert.Equal("clock_discontinuity", sleptIncident.ClosureReason);
        Assert.False(sleptIncident.PostWindowComplete);

        var rebooted = new IncidentRecorder();
        OpenByFailures(rebooted);
        rebooted.Observe(Observation(T0.AddSeconds(30), Success(T0.AddSeconds(30))) with { BootId = "boot-other" });
        var rebootIncident = Assert.Single(rebooted.Incidents);
        Assert.Equal("boot_changed", rebootIncident.ClosureReason);
        Assert.Equal(IncidentState.Observing, rebooted.State);
    }

    [Fact]
    public void Degraded_is_a_separate_state_and_a_later_observation_recovers_it()
    {
        var recorder = new IncidentRecorder();
        recorder.Observe(Observation(T0));
        var degraded = recorder.Degrade("snapshot_collector_failed", Context(T0.AddSeconds(10)));
        Assert.Equal(IncidentState.Degraded, recorder.State);
        Assert.Contains(degraded, item => item.State == IncidentState.Degraded);
        Assert.Equal("snapshot_collector_failed", recorder.Status.DegradedReason);

        recorder.Observe(Observation(T0.AddSeconds(20)));
        Assert.Equal(IncidentState.Observing, recorder.State);
        Assert.Null(recorder.Status.DegradedReason);
    }

    [Fact]
    public void Local_samples_never_supply_network_failure_evidence()
    {
        var recorder = new IncidentRecorder();
        for (var index = 0; index < 10; index++)
        {
            var at = T0.AddSeconds(index * 10);
            recorder.Observe(Observation(at, external: false, probes: new[] { Failure(at) }));
        }

        Assert.Equal(IncidentState.Observing, recorder.State);
        Assert.Null(recorder.Status.CurrentIncident);

        OpenByFailures(recorder, T0.AddSeconds(100));
        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
    }

    [Fact]
    public void A_recovering_incident_does_not_close_without_external_probe_coverage()
    {
        var recorder = new IncidentRecorder();
        OpenByFailures(recorder);
        var success = T0.AddSeconds(30);
        for (var index = 0; index < 3; index++)
        {
            recorder.Observe(Observation(success.AddSeconds(index * 10), Success(success.AddSeconds(index * 10))));
        }

        var postEnd = success.AddSeconds(20).AddMinutes(2);
        recorder.Observe(Observation(postEnd, external: false));
        Assert.Equal(IncidentState.Recovering, recorder.State);
        var current = Assert.IsType<IncidentRecord>(recorder.Status.CurrentIncident);
        Assert.Contains("post_window_probe_coverage_missing", current.MissingRanges);

        recorder.Observe(Observation(postEnd.AddSeconds(10), Success(postEnd.AddSeconds(10))));
        Assert.Equal(IncidentState.Closed, recorder.State);
    }

    [Fact]
    public void Four_business_checks_start_not_tested_and_not_tested_never_counts_as_pass()
    {
        var recorder = new IncidentRecorder();
        var status = recorder.Status;
        Assert.Equal(4, status.BusinessChecks.Count);
        Assert.All(status.BusinessChecks, entry => Assert.Equal(IncidentBusinessResult.NotTested, entry.Result));
        Assert.False(status.AllBusinessChecksPassed);

        recorder.RecordBusinessResult(IncidentBusinessCategory.Dsh, IncidentBusinessResult.Pass, "user",
            "用户在观察窗内完成真实聊天", Context(T0.AddMinutes(1)));
        recorder.RecordBusinessResult(IncidentBusinessCategory.Antigravity, IncidentBusinessResult.Fail, "user",
            "原失败操作仍失败", Context(T0.AddMinutes(2)));
        recorder.RecordBusinessResult(IncidentBusinessCategory.Chrome, IncidentBusinessResult.Pass, "user",
            "国内外页面加载", Context(T0.AddMinutes(3)));
        recorder.RecordBusinessResult(IncidentBusinessCategory.TunApplication, IncidentBusinessResult.NotTested,
            "user", "未取得依赖 TUN 的应用", Context(T0.AddMinutes(4)));

        var updated = recorder.Status;
        Assert.Equal(IncidentBusinessResult.Pass, updated.BusinessChecks.Single(entry => entry.Category == IncidentBusinessCategory.Dsh).Result);
        Assert.Equal(IncidentBusinessResult.Fail, updated.BusinessChecks.Single(entry => entry.Category == IncidentBusinessCategory.Antigravity).Result);
        Assert.Equal(IncidentBusinessResult.NotTested,
            updated.BusinessChecks.Single(entry => entry.Category == IncidentBusinessCategory.TunApplication).Result);
        Assert.False(updated.AllBusinessChecksPassed);
        Assert.NotNull(updated.BusinessChecks.Single(entry => entry.Category == IncidentBusinessCategory.Dsh).ObservedAt);
    }

    [Fact]
    public void Status_changes_are_published_for_the_tray()
    {
        var recorder = new IncidentRecorder();
        var states = new List<IncidentState>();
        recorder.StatusChanged += (_, status) => states.Add(status.State);
        recorder.Observe(Observation(T0));
        recorder.Observe(Observation(T0.AddSeconds(10), Failure(T0.AddSeconds(10))));
        recorder.MarkIncident("标记", Context(T0.AddSeconds(20)));
        Assert.Contains(IncidentState.Observing, states);
        Assert.Contains(IncidentState.IncidentOpen, states);
    }

    private static void OpenByFailures(IncidentRecorder recorder, DateTimeOffset? start = null)
    {
        var baseAt = start ?? T0;
        for (var index = 0; index < 3; index++)
        {
            var at = baseAt.AddSeconds(index * 10);
            recorder.Observe(Observation(at, Failure(at)));
        }

        Assert.Equal(IncidentState.IncidentOpen, recorder.State);
    }

    private static IncidentObservation Observation(DateTimeOffset at, ProbeResult probe,
        IReadOnlyList<string>? eventIds = null) =>
        Observation(at, external: true, eventIds: eventIds, probes: new[] { probe });

    private static IncidentObservation Observation(DateTimeOffset at, params ProbeResult[] probes) =>
        Observation(at, external: true, probes: probes);

    private static IncidentObservation Observation(DateTimeOffset at, bool external, IReadOnlyList<string>? eventIds = null,
        ProbeResult[]? probes = null) => new()
        {
            SessionId = "session-t10",
            BootId = "boot-t10",
            ObservationWindowId = "window-" + at.ToUnixTimeMilliseconds(),
            SnapshotId = "snapshot-" + at.ToUnixTimeMilliseconds(),
            ObservedAt = at,
            MonotonicMs = (at - T0).TotalMilliseconds,
            Synthetic = false,
            Probes = probes ?? Array.Empty<ProbeResult>(),
            EventIds = eventIds ?? new[] { "event-" + at.ToUnixTimeMilliseconds() },
            ExternalObservationEnabled = external,
            ExternalObservationTargetId = "target-A",
        };

    private static IncidentMarkContext Context(DateTimeOffset at) => new()
    {
        SessionId = "session-t10",
        BootId = "boot-t10",
        ObservedAt = at,
        MonotonicMs = (at - T0).TotalMilliseconds,
        Synthetic = false,
    };

    private static ProbeResult Failure(DateTimeOffset at) =>
        Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Timeout, "connect", null, "ipv4");

    private static ProbeResult Success(DateTimeOffset at) =>
        Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Ok, null, 204, "ipv4");

    private static ProbeResult SuccessOn(DateTimeOffset at, string target) =>
        Success(at) with { TargetId = target };

    private static ProbeResult NotStarted(DateTimeOffset at) =>
        Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Failed, "not_started", null, "ipv4");

    private static ProbeResult PermissionDenied(DateTimeOffset at)
    {
        var probe = Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Failed, "connect", null, "ipv4");
        return probe with { OsErrorCode = 10013, TransportError = "access_denied" };
    }

    private static ProbeResult Cancelled(DateTimeOffset at)
    {
        var probe = Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Cancelled, "connect", null, "ipv4");
        return probe with { TransportError = "cancelled_by_budget" };
    }

    private static ProbeResult Unsupported(DateTimeOffset at) =>
        Probe(at, ProbePath.NoExplicitProxy, EventStatus.Unsupported, "resolve", null, "ipv6");

    private static ProbeResult Probe(DateTimeOffset at, ProbePath path, EventStatus status, string? failedStage,
        int? httpStatus, string family)
    {
        var id = Interlocked.Increment(ref _probeCounter);
        return new ProbeResult
        {
            ProbeId = "probe-" + id,
            ProbeGroupId = "group-" + id,
            TargetId = "target-A",
            Path = path,
            Status = status,
            StartedAt = at,
            StartedMonotonicMs = (at - T0).TotalMilliseconds,
            DurationMs = 10,
            HttpStatus = httpStatus,
            Classification = status == EventStatus.Ok
                ? ProbeClassification.ReachableExpected
                : status is EventStatus.Failed or EventStatus.Timeout or EventStatus.Cancelled
                    ? ProbeClassification.TransportFailure
                    : ProbeClassification.Inconclusive,
            AddressFamily = family,
            Method = "GET",
            RequestOptionsKey = "options-A",
            ObservationWindowId = "window-" + at.ToUnixTimeMilliseconds(),
            BootId = "boot-t10",
            ProxyEndpointId = "proxy-A",
            ProxyIsLocal = true,
            FailedStage = failedStage,
            FailedEndpoint = status == EventStatus.Ok ? null : "target",
            TimingComparable = true,
        };
    }
}
