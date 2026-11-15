using System.Globalization;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>
/// M2 incident recorder. It is a pure state machine over observation cycles: it never reads the
/// system, starts a probe or writes a file. The session feeds it and persists the returned
/// transitions, so the event log stays the single source of truth.
/// </summary>
public sealed class IncidentRecorder : IIncidentRecorder
{
    private readonly IncidentRecorderOptions _options;
    private readonly List<IncidentRecord> _incidents = new();
    private readonly Dictionary<string, FailureStreak> _failureStreaks = new(StringComparer.Ordinal);
    private readonly List<CycleReference> _buffer = new();
    private readonly Dictionary<IncidentBusinessCategory, BusinessCheckEntry> _business = new();
    private IncidentState _state = IncidentState.Idle;
    private IncidentState? _pausedFrom;
    private IncidentState? _degradedFrom;
    private string? _degradedReason;
    private OpenIncident? _open;
    private bool _shutdown;
    private DateTimeOffset? _lastObservedAt;
    private double? _lastMonotonicMs;
    private string? _lastBootId;
    private DateTimeOffset? _coverageStartedAt;
    private int _observationCount;

    public IncidentRecorder(IncidentRecorderOptions? options = null)
    {
        _options = options ?? new IncidentRecorderOptions();
        foreach (var category in Enum.GetValues<IncidentBusinessCategory>())
        {
            _business[category] = new BusinessCheckEntry
            {
                Category = category,
                Result = IncidentBusinessResult.NotTested,
                Source = "default",
            };
        }
    }

    public IncidentState State => _state;

    public IReadOnlyList<IncidentRecord> Incidents => _incidents;

    public IncidentRecorderStatus Status
    {
        get
        {
            var current = _open?.ToRecord(_state, _options);
            return new IncidentRecorderStatus
            {
                State = _state,
                CurrentIncident = current,
                LastIncident = _incidents.Count == 0 ? null : _incidents[0],
                HasRecentIncident = current is not null || _incidents.Count > 0,
                CanMarkIncident = !_shutdown,
                BusinessChecks = _business.Values.OrderBy(entry => (int)entry.Category).ToArray(),
                DegradedReason = _degradedReason,
                LastObservationAt = _lastObservedAt,
                ObservationCount = _observationCount,
                Policy = _options.Policy,
            };
        }
    }

    public event EventHandler<IncidentRecorderStatus>? StatusChanged;

    public IReadOnlyList<IncidentTransition> Observe(IncidentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var transitions = new List<IncidentTransition>();
        if (_shutdown)
        {
            return transitions;
        }

        var boundary = DetectBoundary(observation);
        if (boundary is not null)
        {
            HandleBoundary(boundary, observation, transitions);
        }

        var cycle = new CycleReference(
            observation.ObservedAt, observation.MonotonicMs, observation.SessionId, observation.BootId,
            observation.Synthetic, observation.ObservationWindowId, observation.SnapshotId,
            observation.EventIds, observation.Probes.Select(probe => probe.ProbeId).ToArray(),
            observation.Diagnoses, observation.Probes, observation.ExternalObservationEnabled);
        _buffer.Add(cycle);
        TrimBuffer(observation.ObservedAt);
        _observationCount++;
        _lastObservedAt = observation.ObservedAt;
        _lastMonotonicMs = observation.MonotonicMs;
        _lastBootId = observation.BootId;
        _coverageStartedAt ??= observation.ObservedAt;

        if (_state == IncidentState.Degraded)
        {
            var restore = _degradedFrom ?? IncidentState.Observing;
            var previous = _state;
            _state = restore;
            _degradedFrom = null;
            _degradedReason = null;
            transitions.Add(Transition(previous, _state, "observation_recovered", observation));
        }

        if (_state is IncidentState.Idle or IncidentState.Closed)
        {
            var previous = _state;
            _state = IncidentState.Observing;
            transitions.Add(Transition(previous, _state,
                previous == IncidentState.Closed ? "observation_continues" : "observation_started", observation));
        }

        if (_state == IncidentState.Paused)
        {
            PublishStatus();
            return transitions;
        }

        // External observation is only accepted from a user-started probe group. Everything else
        // (including adapter/page activity) is local evidence and can never count as a network
        // failure. Missing, cancelled and permission-denied probes are gaps, not failures.
        var probes = observation.ExternalObservationEnabled
            ? observation.Probes
            : Array.Empty<ProbeResult>();
        var failures = new List<ProbeResult>();
        var successes = new List<ProbeResult>();
        var gaps = new List<ProbeResult>();
        foreach (var probe in probes)
        {
            if (IsEffectiveFailure(probe)) failures.Add(probe);
            else if (IsEffectiveSuccess(probe)) successes.Add(probe);
            else gaps.Add(probe);
        }

        if (_open is null)
        {
            foreach (var failure in failures)
            {
                BumpFailureStreak(failure, observation.ObservedAt);
            }

            foreach (var success in successes)
            {
                ResetFailureStreak(KeyOf(success));
            }

            if (failures.Count > 0)
            {
                var trigger = failures.FirstOrDefault(failure =>
                    StreakOf(failure).Count >= _options.FailureThreshold);
                if (trigger is not null)
                {
                    OpenFromThreshold(trigger, observation, transitions);
                }
            }
        }
        else
        {
            UpdateOpenIncident(observation, cycle, failures, successes, gaps, transitions);
        }

        PublishStatus();
        return transitions;
    }

    public IReadOnlyList<IncidentTransition> MarkIncident(string userDescription, IncidentMarkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transitions = new List<IncidentTransition>();
        if (_shutdown)
        {
            return transitions;
        }

        var description = string.IsNullOrWhiteSpace(userDescription) ? "用户标记：刚才断网了" : userDescription.Trim();
        if (_open is not null)
        {
            _open.UserDescriptions.Add(description);
            transitions.Add(Transition(_state, _state, "user_mark_recorded", context, _open.ToRecord(_state, _options)));
            PublishStatus();
            return transitions;
        }

        var incident = BuildIncident(IncidentTrigger.ManualUserMark, context.ObservedAt, context.ObservedAt,
            context.SessionId, context.BootId, context.Synthetic, Array.Empty<string>());
        incident.UserDescriptions.Add(description);
        if (_state == IncidentState.Idle)
        {
            var previous = _state;
            _state = IncidentState.Observing;
            transitions.Add(Transition(previous, _state, "observation_started", context));
        }

        var beforeOpen = _state;
        _open = incident;
        _state = IncidentState.IncidentOpen;
        transitions.Add(Transition(beforeOpen, _state, "user_mark", context, incident.ToRecord(_state, _options)));
        PublishStatus();
        return transitions;
    }

    public IReadOnlyList<IncidentTransition> RecordBusinessResult(
        IncidentBusinessCategory category, IncidentBusinessResult result, string source, string? userNote,
        IncidentMarkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transitions = new List<IncidentTransition>();
        if (_shutdown)
        {
            return transitions;
        }

        var entry = new BusinessCheckEntry
        {
            Category = category,
            Result = result,
            Source = string.IsNullOrWhiteSpace(source) ? "user" : source.Trim(),
            ObservedAt = context.ObservedAt,
            UserNote = string.IsNullOrWhiteSpace(userNote) ? null : userNote.Trim(),
        };
        _business[category] = entry;
        transitions.Add(Transition(_state, _state, "business_check", context, incident: null, business: entry));
        PublishStatus();
        return transitions;
    }

    public IReadOnlyList<IncidentTransition> Pause(IncidentMarkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transitions = new List<IncidentTransition>();
        if (_shutdown || _state == IncidentState.Paused)
        {
            return transitions;
        }

        if (_open is not null)
        {
            _open.AddMissing("observation_paused");
            if (_state == IncidentState.Recovering)
            {
                _open.PostWindowBroken = true;
                _open.RecoveringSince = null;
            }
        }

        _pausedFrom = _state;
        var previous = _state;
        _state = IncidentState.Paused;
        _coverageStartedAt = null;
        transitions.Add(Transition(previous, _state, "observation_paused", context));
        PublishStatus();
        return transitions;
    }

    public IReadOnlyList<IncidentTransition> Resume(IncidentMarkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transitions = new List<IncidentTransition>();
        if (_shutdown)
        {
            return transitions;
        }

        IncidentState restore;
        if (_state == IncidentState.Paused)
        {
            restore = _pausedFrom is IncidentState.Paused or IncidentState.Degraded or IncidentState.Idle or null
                ? IncidentState.Observing
                : _pausedFrom.Value;
        }
        else if (_state == IncidentState.Degraded)
        {
            restore = _degradedFrom ?? IncidentState.Observing;
        }
        else if (_state is IncidentState.Idle or IncidentState.Closed)
        {
            restore = IncidentState.Observing;
        }
        else
        {
            return transitions;
        }

        _pausedFrom = null;
        _degradedFrom = null;
        _degradedReason = null;
        _coverageStartedAt = context.ObservedAt;
        if (_open is not null && restore == IncidentState.Recovering)
        {
            _open.RecoveringSince = context.ObservedAt;
            _open.PostWindowBroken = false;
            _open.AddMissing("post_window_restarted_after_pause");
        }

        var previous = _state;
        _state = restore;
        transitions.Add(Transition(previous, _state, "observation_resumed", context,
            _open?.ToRecord(_state, _options)));
        PublishStatus();
        return transitions;
    }

    public IReadOnlyList<IncidentTransition> Degrade(string reason, IncidentMarkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transitions = new List<IncidentTransition>();
        if (_shutdown)
        {
            return transitions;
        }

        var text = string.IsNullOrWhiteSpace(reason) ? "collector_error" : reason.Trim();
        if (_state == IncidentState.Degraded)
        {
            _degradedReason = text;
            PublishStatus();
            return transitions;
        }

        _degradedFrom = _state;
        var previous = _state;
        _state = IncidentState.Degraded;
        _degradedReason = text;
        _coverageStartedAt = null;
        if (_open is not null)
        {
            _open.AddMissing("observation_degraded:" + text);
            if (_open.Recovering())
            {
                _open.PostWindowBroken = true;
                _open.RecoveringSince = null;
            }
        }

        transitions.Add(Transition(previous, _state, "observation_degraded:" + text, context));
        PublishStatus();
        return transitions;
    }

    public IReadOnlyList<IncidentTransition> Shutdown(IncidentMarkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transitions = new List<IncidentTransition>();
        if (_shutdown)
        {
            return transitions;
        }

        if (_open is not null)
        {
            CloseIncomplete("session_end_before_post_window", context, transitions);
        }

        var previous = _state;
        _shutdown = true;
        if (_state != IncidentState.Idle)
        {
            _state = IncidentState.Idle;
            transitions.Add(Transition(previous, _state, "session_ended", context));
        }

        PublishStatus();
        return transitions;
    }

    // ---------- incident lifecycle ----------

    private void OpenFromThreshold(ProbeResult trigger, IncidentObservation observation,
        List<IncidentTransition> transitions)
    {
        var key = KeyOf(trigger);
        var streak = StreakOf(trigger);
        var incident = BuildIncident(IncidentTrigger.ConsecutiveFailures, streak.FirstAt, observation.ObservedAt,
            observation.SessionId, observation.BootId, observation.Synthetic, new[] { key });

        var previous = _state;
        _state = IncidentState.Suspected;
        transitions.Add(Transition(previous, _state, "failure_threshold_reached", observation,
            incident.ToRecord(_state, _options)));
        _open = incident;
        _state = IncidentState.IncidentOpen;
        transitions.Add(Transition(IncidentState.Suspected, _state, "incident_opened", observation,
            incident.ToRecord(_state, _options)));
    }

    private OpenIncident BuildIncident(IncidentTrigger trigger, DateTimeOffset startedAt, DateTimeOffset now,
        string sessionId, string bootId, bool synthetic, IReadOnlyList<string> triggerKeys)
    {
        var requiredFrom = startedAt - _options.PreWindow;
        var covered = _coverageStartedAt is { } coverage
            && coverage <= requiredFrom + _options.MaxSampleGap;
        var missing = new List<string>();
        if (!covered)
        {
            missing.Add("pre_window_incomplete");
        }

        var incident = new OpenIncident
        {
            IncidentId = "incident-" + Guid.NewGuid().ToString("N"),
            Trigger = trigger,
            SessionId = sessionId,
            BootId = bootId,
            Synthetic = synthetic,
            StartedAt = startedAt,
            PreWindowAvailable = covered,
            MissingRanges = missing,
            RecoveryKeys = triggerKeys.Count > 0 ? new List<string>(triggerKeys) : new List<string>(),
        };

        foreach (var cycle in _buffer)
        {
            if (cycle.At < requiredFrom || cycle.At > now + TimeSpan.FromSeconds(1))
            {
                continue;
            }

            incident.AddCycle(cycle);
            if (cycle.At < startedAt)
            {
                continue;
            }

            if (!cycle.ExternalObservationEnabled)
            {
                continue;
            }

            foreach (var probe in cycle.Probes)
            {
                if (IsEffectiveFailure(probe))
                {
                    incident.AddFailure(probe, cycle.At, _options.MergeWindow);
                    incident.EffectiveFailureCount++;
                }
                else if (IsEffectiveSuccess(probe))
                {
                    incident.AddSuccess(probe);
                    incident.EffectiveSuccessCount++;
                }
            }
        }

        return incident;
    }

    private void UpdateOpenIncident(IncidentObservation observation, CycleReference cycle,
        List<ProbeResult> failures, List<ProbeResult> successes, List<ProbeResult> gaps,
        List<IncidentTransition> transitions)
    {
        var incident = _open!;
        incident.AddCycle(cycle);
        incident.LastObservedAt = observation.ObservedAt;
        if (incident.RecoveryKeys.Count == 0 && observation.ExternalObservationEnabled
            && observation.Probes.Count > 0)
        {
            foreach (var probe in observation.Probes)
            {
                incident.RecoveryKeys.Add(KeyOf(probe));
            }
        }

        foreach (var failure in failures)
        {
            incident.AddFailure(failure, observation.ObservedAt, _options.MergeWindow);
            incident.EffectiveFailureCount++;
        }

        foreach (var success in successes)
        {
            incident.AddSuccess(success);
            incident.EffectiveSuccessCount++;
        }

        if (observation.CollectionGaps.Count > 0)
        {
            incident.AddMissing("collection_gap:" + string.Join("、", observation.CollectionGaps));
        }

        if (!observation.Persisted)
        {
            incident.AddMissing("events_not_persisted:" + (observation.PersistenceError ?? "unknown"));
        }

        if (gaps.Count > 0)
        {
            incident.AddMissing("probe_gap:" + string.Join("、",
                gaps.Select(probe => probe.Status.ToString()).Distinct(StringComparer.Ordinal)));
            if (_state == IncidentState.Recovering)
            {
                // A missing sample is not a failure, but it cannot certify "no new failure" for
                // the rest of the post window; restart the window from this observed sample.
                incident.PostWindowBroken = true;
                incident.RecoveringSince = observation.ObservedAt;
                transitions.Add(Transition(_state, _state, "post_window_restarted_probe_gap", observation,
                    incident.ToRecord(_state, _options)));
            }

            return;
        }

        if (_state == IncidentState.IncidentOpen)
        {
            if (incident.RecoveryKeys.Count > 0 && incident.RecoveryKeys.All(key =>
                    incident.SuccessStreak(key) >= _options.RecoverySuccessThreshold))
            {
                var previous = _state;
                _state = IncidentState.Recovering;
                incident.RecoveringSince = observation.ObservedAt;
                incident.PostWindowBroken = false;
                transitions.Add(Transition(previous, _state, "recovery_successes_reached", observation,
                    incident.ToRecord(_state, _options)));
            }

            return;
        }

        if (_state != IncidentState.Recovering)
        {
            return;
        }

        if (failures.Count > 0)
        {
            var previous = _state;
            _state = IncidentState.IncidentOpen;
            incident.RecoveringSince = null;
            incident.PostWindowBroken = false;
            incident.SuccessStreaks.Clear();
            incident.AddMissing("new_failure_during_recovery");
            transitions.Add(Transition(previous, _state, "failure_after_recovery", observation,
                incident.ToRecord(_state, _options)));
            return;
        }

        if (incident.PostWindowBroken || incident.RecoveringSince is not { } recoveringSince)
        {
            return;
        }

        if (observation.ObservedAt < recoveringSince + _options.PostWindow)
        {
            return;
        }

        if (!observation.ExternalObservationEnabled)
        {
            incident.AddMissing("post_window_probe_coverage_missing");
            return;
        }

        var closedAt = observation.ObservedAt;
        var previousState = _state;
        incident.EndedAt = recoveringSince + _options.PostWindow;
        incident.ClosedAt = closedAt;
        incident.ClosureReason = "recovery_verified_no_new_failure";
        incident.PostWindowComplete = true;
        var record = incident.ToRecord(IncidentState.Closed, _options);
        _open = null;
        _state = IncidentState.Closed;
        _incidents.Insert(0, record);
        TrimIncidents();
        transitions.Add(Transition(previousState, _state, "incident_closed", observation, record));
    }

    private void CloseIncomplete(string reason, IncidentMarkContext context,
        List<IncidentTransition> transitions)
    {
        var incident = _open;
        if (incident is null)
        {
            return;
        }

        incident.EndedAt = context.ObservedAt;
        incident.ClosedAt = context.ObservedAt;
        incident.ClosureReason = reason;
        incident.PostWindowComplete = false;
        incident.AddMissing("post_window_incomplete");
        var record = incident.ToRecord(IncidentState.Closed, _options);
        var previous = _state;
        _open = null;
        _state = IncidentState.Closed;
        _incidents.Insert(0, record);
        TrimIncidents();
        transitions.Add(Transition(previous, _state, "incident_closed:" + reason, context, record));
    }

    private void CloseIncomplete(string reason, IncidentObservation observation,
        List<IncidentTransition> transitions)
    {
        if (_open is null)
        {
            return;
        }

        var context = new IncidentMarkContext
        {
            SessionId = observation.SessionId,
            BootId = observation.BootId,
            ObservedAt = observation.ObservedAt,
            MonotonicMs = observation.MonotonicMs,
            Synthetic = observation.Synthetic,
        };
        CloseIncomplete(reason, context, transitions);
    }

    private void HandleBoundary(string reason, IncidentObservation observation,
        List<IncidentTransition> transitions)
    {
        if (_open is not null)
        {
            CloseIncomplete(reason, observation, transitions);
        }

        _failureStreaks.Clear();
        _buffer.Clear();
        _coverageStartedAt = null;
        if (_state is not (IncidentState.Paused or IncidentState.Degraded))
        {
            var previous = _state;
            _state = IncidentState.Observing;
            transitions.Add(Transition(previous, _state, "observation_boundary:" + reason, observation));
        }
    }

    // ---------- helpers ----------

    private string? DetectBoundary(IncidentObservation observation)
    {
        if (_lastBootId is { } boot && !string.Equals(boot, observation.BootId, StringComparison.Ordinal))
        {
            return "boot_changed";
        }

        if (_lastMonotonicMs is { } monotonic
            && observation.MonotonicMs + 0.5 < monotonic)
        {
            return "monotonic_backwards";
        }

        if (_lastObservedAt is { } observedAt && _lastMonotonicMs is { } lastMonotonic)
        {
            var wallDelta = (observation.ObservedAt - observedAt).TotalMilliseconds;
            var monotonicDelta = observation.MonotonicMs - lastMonotonic;
            if (Math.Abs(wallDelta - monotonicDelta) > _options.ClockDiscontinuityTolerance.TotalMilliseconds)
            {
                return "clock_discontinuity";
            }
        }

        return null;
    }

    private void TrimBuffer(DateTimeOffset newest)
    {
        var threshold = newest - _options.PreWindow - _options.MaxSampleGap;
        while (_buffer.Count > 0 && (_buffer[0].At < threshold
            || _buffer.Count > _options.MaxBufferedObservations))
        {
            _buffer.RemoveAt(0);
        }
    }

    private void TrimIncidents()
    {
        while (_incidents.Count > _options.MaxRecentIncidents)
        {
            _incidents.RemoveAt(_incidents.Count - 1);
        }
    }

    private FailureStreak StreakOf(ProbeResult probe)
    {
        var key = KeyOf(probe);
        return _failureStreaks.TryGetValue(key, out var streak) ? streak : new FailureStreak(0, default, default);
    }

    private void BumpFailureStreak(ProbeResult probe, DateTimeOffset at)
    {
        var key = KeyOf(probe);
        _failureStreaks[key] = _failureStreaks.TryGetValue(key, out var current)
            ? current with { Count = current.Count + 1, LastAt = at }
            : new FailureStreak(1, at, at);
    }

    private void ResetFailureStreak(string key) => _failureStreaks.Remove(key);

    private static string KeyOf(ProbeResult probe) => string.Join('|',
        probe.TargetId, probe.Path, probe.ProxyEndpointId ?? string.Empty, probe.AddressFamily ?? string.Empty,
        probe.Method ?? string.Empty, probe.RequestOptionsKey ?? string.Empty);

    private static string ClassOf(ProbeResult probe) => string.Join('|',
        probe.TargetId, probe.Path, probe.ProxyEndpointId ?? string.Empty, probe.AddressFamily ?? string.Empty,
        probe.Method ?? string.Empty);

    internal static bool IsEffectiveFailure(ProbeResult probe)
    {
        if (probe.Status is not (EventStatus.Failed or EventStatus.Timeout)
            || probe.Classification != ProbeClassification.TransportFailure
            || probe.FailedStage == "not_started"
            || probe.OsErrorCode is 5 or 13 or 10013)
        {
            return false;
        }

        var reason = probe.TransportError?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return reason is not null
            && (reason.Contains("budget", StringComparison.Ordinal)
                || reason.Contains("cancel", StringComparison.Ordinal)
                || reason.Contains("permission", StringComparison.Ordinal)
                || reason.Contains("accessdenied", StringComparison.Ordinal)
                || reason.Contains("unsupported", StringComparison.Ordinal)
                || reason.Contains("notstarted", StringComparison.Ordinal))
            ? false
            : true;
    }

    internal static bool IsEffectiveSuccess(ProbeResult probe) =>
        probe.Status == EventStatus.Ok
        && probe.Classification is ProbeClassification.ReachableExpected or ProbeClassification.ReachableUnexpected;

    private IncidentTransition Transition(IncidentState previous, IncidentState state, string reason,
        IncidentObservation observation, IncidentRecord? incident = null) => new()
        {
            Kind = IncidentTransitionKind.StateChange,
            PreviousState = previous,
            State = state,
            Reason = reason,
            At = observation.ObservedAt,
            MonotonicMs = observation.MonotonicMs,
            SessionId = observation.SessionId,
            BootId = observation.BootId,
            Synthetic = observation.Synthetic,
            Incident = incident,
        };

    private IncidentTransition Transition(IncidentState previous, IncidentState state, string reason,
        IncidentMarkContext context, IncidentRecord? incident = null, BusinessCheckEntry? business = null) => new()
        {
            Kind = business is null ? IncidentTransitionKind.StateChange : IncidentTransitionKind.BusinessCheck,
            PreviousState = previous,
            State = state,
            Reason = reason,
            At = context.ObservedAt,
            MonotonicMs = context.MonotonicMs,
            SessionId = context.SessionId,
            BootId = context.BootId,
            Synthetic = context.Synthetic,
            Incident = incident,
            BusinessCheck = business,
        };

    private void PublishStatus() => StatusChanged?.Invoke(this, Status);

    private sealed record FailureStreak(int Count, DateTimeOffset FirstAt, DateTimeOffset LastAt);

    private sealed record CycleReference(
        DateTimeOffset At,
        double MonotonicMs,
        string SessionId,
        string BootId,
        bool Synthetic,
        string ObservationWindowId,
        string SnapshotId,
        IReadOnlyList<string> EventIds,
        IReadOnlyList<string> ProbeIds,
        IReadOnlyList<Diagnosis> Diagnoses,
        IReadOnlyList<ProbeResult> Probes,
        bool ExternalObservationEnabled);

    private sealed class OpenIncident
    {
        private readonly Dictionary<string, int> _failureGroups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _successStreaks = new(StringComparer.Ordinal);
        private readonly List<IncidentFailureGroup> _failures = new();
        private readonly List<string> _eventIds = new();
        private readonly List<string> _snapshotIds = new();
        private readonly List<string> _probeIds = new();
        private readonly List<string> _diagnosisIds = new();

        public required string IncidentId { get; init; }

        public required IncidentTrigger Trigger { get; init; }

        public required string SessionId { get; init; }

        public required string BootId { get; init; }

        public required bool Synthetic { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public required bool PreWindowAvailable { get; init; }

        public required List<string> MissingRanges { get; init; }

        public required List<string> RecoveryKeys { get; init; }

        public List<string> UserDescriptions { get; } = new();

        public Dictionary<string, int> SuccessStreaks => _successStreaks;

        public int EffectiveFailureCount { get; set; }

        public int EffectiveSuccessCount { get; set; }

        public DateTimeOffset? RecoveringSince { get; set; }

        public DateTimeOffset? LastObservedAt { get; set; }

        public DateTimeOffset? EndedAt { get; set; }

        public DateTimeOffset? ClosedAt { get; set; }

        public string? ClosureReason { get; set; }

        public bool PostWindowComplete { get; set; }

        public bool PostWindowBroken { get; set; }

        public string? LastSnapshotId { get; private set; }

        public string? LastObservationWindowId { get; private set; }

        public bool Recovering() => RecoveringSince is not null;

        public void AddMissing(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason) && !MissingRanges.Contains(reason, StringComparer.Ordinal))
            {
                MissingRanges.Add(reason);
            }
        }

        public void AddCycle(CycleReference cycle)
        {
            _eventIds.AddRange(cycle.EventIds);
            _snapshotIds.Add(cycle.SnapshotId);
            _probeIds.AddRange(cycle.ProbeIds);
            _diagnosisIds.AddRange(cycle.Diagnoses.Select(diagnosis => diagnosis.DiagnosisId));
            LastSnapshotId = cycle.SnapshotId;
            LastObservationWindowId = cycle.ObservationWindowId;
        }

        public void AddFailure(ProbeResult probe, DateTimeOffset at, TimeSpan mergeWindow)
        {
            var classKey = ClassOf(probe);
            if (_failureGroups.TryGetValue(classKey, out var index))
            {
                var existing = _failures[index];
                if (at - existing.LastAt <= mergeWindow)
                {
                    _failures[index] = existing with { Count = existing.Count + 1, LastAt = at };
                    _successStreaks[KeyOf(probe)] = 0;
                    return;
                }
            }

            _failures.Add(new IncidentFailureGroup
            {
                ClassKey = classKey,
                TargetId = probe.TargetId,
                Path = probe.Path.ToString(),
                ProxyEndpointId = probe.ProxyEndpointId,
                AddressFamily = probe.AddressFamily,
                Method = probe.Method,
                FirstAt = at,
                LastAt = at,
                Count = 1,
            });
            _failureGroups[classKey] = _failures.Count - 1;
            _successStreaks[KeyOf(probe)] = 0;
        }

        public void AddSuccess(ProbeResult probe)
        {
            var key = KeyOf(probe);
            _successStreaks[key] = _successStreaks.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        public int SuccessStreak(string key) => _successStreaks.GetValueOrDefault(key);

        public IncidentRecord ToRecord(IncidentState state, IncidentRecorderOptions options) => new()
        {
            IncidentId = IncidentId,
            Trigger = Trigger,
            State = state,
            SessionId = SessionId,
            BootId = BootId,
            Synthetic = Synthetic,
            StartedAt = StartedAt,
            ClosedAt = ClosedAt,
            Window = new IncidentWindow
            {
                PreWindow = options.PreWindow,
                PostWindow = options.PostWindow,
                StartedAt = StartedAt,
                EndedAt = EndedAt,
                PreWindowAvailable = PreWindowAvailable,
                PostWindowComplete = PostWindowComplete,
                MissingRanges = MissingRanges.ToArray(),
            },
            UserDescriptions = UserDescriptions.ToArray(),
            EventIds = _eventIds.Distinct(StringComparer.Ordinal).ToArray(),
            SnapshotIds = _snapshotIds.Distinct(StringComparer.Ordinal).ToArray(),
            ProbeIds = _probeIds.Distinct(StringComparer.Ordinal).ToArray(),
            DiagnosisIds = _diagnosisIds.Distinct(StringComparer.Ordinal).ToArray(),
            Failures = _failures.ToArray(),
            MissingRanges = MissingRanges.ToArray(),
            ClosureReason = ClosureReason,
            PostWindowComplete = PostWindowComplete,
            EffectiveFailureCount = EffectiveFailureCount,
            EffectiveSuccessCount = EffectiveSuccessCount,
            LastSnapshotId = LastSnapshotId,
            LastObservationWindowId = LastObservationWindowId,
        };
    }
}

