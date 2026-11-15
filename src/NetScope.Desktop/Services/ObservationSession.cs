using System.Text.Json;
using NetScope.Application;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Desktop.Services;

/// <summary>A snapshot plus any diagnoses produced in one cycle, and whether it reached disk.</summary>
public sealed record SessionCycle
{
    public required NetworkSnapshot Snapshot { get; init; }
    public required EvidenceSet Evidence { get; init; }
    public required IReadOnlyList<Diagnosis> Diagnoses { get; init; }
    public required IReadOnlyList<RuleEvaluationFailure> RuleFailures { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required bool Persisted { get; init; }
    public string? PersistenceError { get; init; }
    public required int AppendedEvents { get; init; }
    public bool DiagnosisRequested { get; init; }

    /// <summary>The explicit window every adapter record of this cycle was stamped with.</summary>
    public string? ObservationWindowId { get; init; }

    /// <summary>Adapter domains that could not be read; empty is not the same as complete.</summary>
    public IReadOnlyList<string> EvidenceGaps { get; init; } = Array.Empty<string>();
}

public enum ObservationState { Idle, Observing, Paused, Stopped }

/// <summary>
/// Manually started, read-only observation. Local samples and explicit probes share one operation
/// gate. Stopping cancels all work and joins it before disposing the authoritative log.
/// </summary>
public sealed class ObservationSession : IAsyncDisposable
{
    private readonly ISnapshotCollector _collector;
    private readonly IProbeRunner _probes;
    private readonly IClock _clock;
    private readonly IObservationEvidenceSource? _evidenceSource;
    private IIncidentRecorder? _recorder;
    private readonly object _recorderAttachLock = new();
    private readonly object _pendingTransitionLock = new();
    private readonly List<IncidentTransition> _pendingTransitions = new();
    private int _pendingFlushActive;
    private Task? _pendingFlushTask;
    private string? _recorderError;
    private readonly DiagnosticEngine _diagnostics = DiagnosticEngine.CreateDefault(new SessionDiagnosisIds());
    private readonly AppPaths _paths;
    private readonly IEventLogFileAccess? _logFiles;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _loopSource;
    private Task? _loopTask;
    private Task? _stopTask;
    private NdjsonEventLog? _log;
    private SqliteEventIndex? _index;
    private string? _persistenceError;
    private string? _projectionError;
    private bool _persistenceDisabled;
    private int _nextSequence = 1;
    private int _snapshotCount;
    private int _eventCount;
    private int _disposeRequested;
    private ObservationState _state = ObservationState.Idle;
    private DateTimeOffset? _lastSnapshotAt;

    public ObservationSession(ISnapshotCollector collector, IProbeRunner probes, IClock clock, AppPaths paths,
        bool synthetic = false, string? sessionId = null, string? bootId = null, IEventLogFileAccess? logFiles = null,
        IObservationEvidenceSource? evidenceSource = null, IIncidentRecorder? recorder = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logFiles = logFiles;
        _evidenceSource = evidenceSource;
        _recorder = recorder;
        Synthetic = synthetic;
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") : sessionId;
        BootId = string.IsNullOrWhiteSpace(bootId) ? NewBootId() : bootId;
        _paths.EnsureCreated();
        SessionPath = _paths.NewSessionPath(SessionId);
    }

    public bool Synthetic { get; }
    public string SessionId { get; }
    public string BootId { get; }
    public string SessionPath { get; }
    /// <summary>The adapter evidence bridge this session was composed with; null means an explicitly reduced session.</summary>
    public IObservationEvidenceSource? EvidenceSource => _evidenceSource;
    /// <summary>The M2 incident recorder; null until the tray or a caller enables incident capture.</summary>
    public IIncidentRecorder? Recorder { get { lock (_recorderAttachLock) return _recorder; } }
    /// <summary>Last unexpected incident-recorder error; never silently treated as a diagnosis.</summary>
    public string? RecorderError { get { lock (_stateLock) return _recorderError; } }
    public ObservationState State { get { lock (_stateLock) return _state; } }
    public DateTimeOffset? LastSnapshotAt { get { lock (_stateLock) return _lastSnapshotAt; } }
    public int SnapshotCount => Volatile.Read(ref _snapshotCount);
    public int EventCount => Volatile.Read(ref _eventCount);
    public string? PersistenceError { get { lock (_stateLock) return _persistenceError; } }
    /// <summary>Secondary projection failure; the authoritative NDJSON log is unaffected.</summary>
    public string? ProjectionError { get { lock (_stateLock) return _projectionError; } }
    public event EventHandler? StateChanged;
    public event EventHandler<SessionCycle>? CycleCompleted;
    public event EventHandler<string>? CollectionFailed;

    /// <summary>Starts local samples only. A resumed loop joins its predecessor before collecting.</summary>
    public void Start(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromMinutes(10)) interval = TimeSpan.FromSeconds(10);
        lock (_stateLock)
        {
            EnsureOpen();
            if (_state == ObservationState.Observing) return;
            var previous = _loopTask;
            var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
            _loopSource = source;
            _state = ObservationState.Observing;
            _loopTask = Task.Run(() => ObserveAsync(previous, source, interval));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        QueueRecorderTransitions(RecorderForChange()?.Resume(RecorderContext()) ?? Array.Empty<IncidentTransition>());
    }

    private async Task ObserveAsync(Task? previous, CancellationTokenSource source, TimeSpan interval)
    {
        var token = source.Token;
        try
        {
            if (previous is not null) await previous.ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try { await CollectOnceAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    CollectionFailed?.Invoke(this, exception.GetType().Name);
                    await DegradeRecorderAsync(exception.GetType().Name).ConfigureAwait(false);
                }
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_loopSource, source)) _loopSource = null;
                source.Dispose();
            }
        }
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (_state != ObservationState.Observing) return;
            _loopSource?.Cancel();
            _state = ObservationState.Paused;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        QueueRecorderTransitions(RecorderForChange()?.Pause(RecorderContext()) ?? Array.Empty<IncidentTransition>());
    }

    /// <summary>Also drains a cancelled sample before switching the UI to synthetic evidence.</summary>
    public async Task PauseAsync()
    {
        Pause();
        Task? pending;
        lock (_stateLock) pending = _loopTask;
        if (pending is not null) await pending.ConfigureAwait(false);
        await _cycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pendingMarkers = Volatile.Read(ref _pendingFlushTask);
            if (pendingMarkers is not null) await pendingMarkers.ConfigureAwait(false);
            await FlushPendingTransitionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
    }

    public Task StopAsync()
    {
        lock (_stateLock)
        {
            if (_stopTask is not null) return _stopTask;
            _state = ObservationState.Stopped;
            _lifetimeSource.Cancel();
            var loopTask = _loopTask;
            _stopTask = Task.Run(() => StopCoreAsync(loopTask));
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(Task? loopTask)
    {
        _lifetimeSource.Cancel();
        if (loopTask is not null) await loopTask.ConfigureAwait(false);
        var pendingFlush = Volatile.Read(ref _pendingFlushTask);
        if (pendingFlush is not null) await pendingFlush.ConfigureAwait(false);
        await _cycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // An explicit exit drains queued markers and closes an unfinished incident as
                // incomplete before the log handle is released. "Hidden to tray" never gets here.
                await FlushPendingTransitionsCoreAsync(CancellationToken.None).ConfigureAwait(false);
                var shutdown = RecorderForChange()?.Shutdown(RecorderContext())
                    ?? Array.Empty<IncidentTransition>();
                await PersistTransitionsCoreAsync(shutdown, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
                _log?.Dispose();
                _log = null;
                _index?.Dispose();
                _index = null;
            }
            finally { _writeGate.Release(); }
        }
        finally { _cycleGate.Release(); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private CancellationTokenSource OperationToken(CancellationToken token)
    {
        lock (_stateLock)
        {
            EnsureOpen();
            return CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeSource.Token);
        }
    }

    private void EnsureOpen()
    {
        if (_state == ObservationState.Stopped) throw new InvalidOperationException("The session has been stopped.");
    }

    public async Task<SessionCycle> CollectOnceAsync(CancellationToken cancellationToken)
    {
        using var source = OperationToken(cancellationToken);
        var token = source.Token;
        await _cycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_stateLock) EnsureOpen();
            var snapshot = await CollectSnapshotAsync(token).ConfigureAwait(false);
            var windowId = NewWindowId();
            var observation = await CollectEvidenceAsync(ContextFor(snapshot, windowId), token).ConfigureAwait(false);
            var evidence = new EvidenceSet
            {
                Records = observation.Records,
                Probes = Array.Empty<ProbeResult>(),
                Snapshot = snapshot,
            };
            // Local rules are evaluated on locally available evidence. Rules that need an
            // external probe keep candidate/insufficient wording instead of receiving a fake one.
            var evaluation = _diagnostics.EvaluateDetailed(evidence);
            token.ThrowIfCancellationRequested();
            return await CompleteAsync(evidence, evaluation.Diagnoses, evaluation.Failures, false,
                observation.Gaps, windowId, plan: null, token).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
    }

    /// <summary>Runs one explicitly requested group and evaluates the rules over both local and probe evidence.</summary>
    public async Task<SessionCycle> RunDiagnosisAsync(ProbeTarget target, string? explicitProxyEndpoint,
        string? addressFamily, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var source = OperationToken(cancellationToken);
        var token = source.Token;
        await _cycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_stateLock) EnsureOpen();
            var startedAt = _clock.UtcNow;
            var snapshot = await CollectSnapshotAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var groupId = Guid.NewGuid().ToString("N");
            var plan = new ProbeGroupPlan
            {
                ProbeGroupId = "group-" + groupId, Target = target,
                ExplicitProxyEndpoint = string.IsNullOrWhiteSpace(explicitProxyEndpoint) ? null : explicitProxyEndpoint,
                ProxyEndpointId = string.IsNullOrWhiteSpace(explicitProxyEndpoint) ? null : "proxy-configured",
                AddressFamily = string.IsNullOrWhiteSpace(addressFamily) ? null : addressFamily,
                ObservationWindowId = "window-" + groupId, BootId = BootId, StartedAt = startedAt,
            };
            var observation = await CollectEvidenceAsync(ContextFor(snapshot, plan.ObservationWindowId!, plan), token)
                .ConfigureAwait(false);
            var group = await _probes.RunAsync(plan, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var evidence = new EvidenceSet
            {
                Records = observation.Records,
                Probes = group.Results,
                Snapshot = snapshot,
            };
            var evaluation = _diagnostics.EvaluateDetailed(evidence);
            return await CompleteAsync(evidence, evaluation.Diagnoses, evaluation.Failures, true,
                observation.Gaps, plan.ObservationWindowId!, plan, token).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
    }

    private static string NewWindowId() => "window-" + Guid.NewGuid().ToString("N");

    private ObservationEvidenceContext ContextFor(NetworkSnapshot snapshot, string windowId, ProbeGroupPlan? plan = null) => new()
    {
        SessionId = SessionId,
        BootId = BootId,
        SnapshotId = snapshot.SnapshotId,
        ObservationWindowId = windowId,
        ObservedAt = snapshot.ObservedAt,
        MonotonicMs = snapshot.MonotonicMs,
        Synthetic = Synthetic,
        ProxyEndpointId = plan?.ProxyEndpointId,
        TargetId = plan?.Target.TargetId,
        AddressFamily = plan?.AddressFamily,
    };

    /// <summary>
    /// Reads the adapter evidence for one cycle. A failing source yields an explicit gap rather
    /// than an empty-but-fine evidence set; cancellation still cancels the cycle.
    /// </summary>
    private async Task<ObservationEvidenceResult> CollectEvidenceAsync(
        ObservationEvidenceContext context, CancellationToken cancellationToken)
    {
        if (_evidenceSource is null)
        {
            return ObservationEvidenceResult.Empty;
        }

        try
        {
            return await _evidenceSource.CollectAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ObservationEvidenceResult
            {
                Records = Array.Empty<EvidenceRecord>(),
                Gaps = new[]
                {
                    new ObservationEvidenceGap
                    {
                        Source = "observation_evidence",
                        Status = EventStatus.Failed,
                        Reason = "证据源读取失败（" + exception.GetType().Name + "）；本轮没有适配器记录。",
                    },
                },
            };
        }
    }

    private async Task<NetworkSnapshot> CollectSnapshotAsync(CancellationToken cancellationToken)
    {
        var observedAt = _clock.UtcNow;
        var snapshot = await _collector.CollectAsync(new CollectionContext
        {
            SessionId = SessionId, BootId = BootId,
            SnapshotId = "snapshot-" + observedAt.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"),
            ObservedAt = observedAt, MonotonicMs = _clock.MonotonicMs, Synthetic = Synthetic,
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock) _lastSnapshotAt = observedAt;
        Interlocked.Increment(ref _snapshotCount);
        return snapshot;
    }

    private async Task<SessionCycle> CompleteAsync(EvidenceSet evidence, IReadOnlyList<Diagnosis> diagnoses,
        IReadOnlyList<RuleEvaluationFailure> failures, bool diagnosisRequested,
        IReadOnlyList<ObservationEvidenceGap> evidenceGaps, string observationWindowId, ProbeGroupPlan? plan,
        CancellationToken cancellationToken)
    {
        var completedAt = _clock.UtcNow;
        var appended = 0;
        string? error = null;
        var eventIds = (IReadOnlyList<string>)Array.Empty<string>();
        var persisted = false;
        if (!_persistenceDisabled)
        {
            try
            {
                var outcome = await PersistAsync(evidence, diagnoses, failures, evidenceGaps, observationWindowId,
                    completedAt, cancellationToken).ConfigureAwait(false);
                appended = outcome.Count;
                eventIds = outcome.EventIds;
                persisted = outcome.Count > 0;
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                error = exception.GetType().Name;
                lock (_stateLock) { _persistenceDisabled = true; _persistenceError = error; }
            }
        }
        else { lock (_stateLock) error = _persistenceError; }
        cancellationToken.ThrowIfCancellationRequested();

        var recorder = RecorderForChange();
        if (recorder is not null)
        {
            var transitions = ObserveRecorder(recorder, evidence, observationWindowId, diagnoses, eventIds, persisted,
                error, evidenceGaps, diagnosisRequested, plan, completedAt);
            if (transitions.Count > 0)
            {
                try
                {
                    appended += await PersistTransitionsWithGateAsync(transitions, completedAt, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsPersistenceFailure(exception))
                {
                    error ??= exception.GetType().Name;
                    lock (_stateLock) { _persistenceDisabled = true; _persistenceError = error; }
                }
            }
        }

        var cycle = new SessionCycle
        {
            Snapshot = evidence.Snapshot!, Evidence = evidence, Diagnoses = diagnoses, RuleFailures = failures,
            CompletedAt = completedAt, Persisted = appended > 0, PersistenceError = error,
            AppendedEvents = appended, DiagnosisRequested = diagnosisRequested,
            ObservationWindowId = observationWindowId,
            EvidenceGaps = evidenceGaps.Select(gap => gap.Source + "：" + gap.Reason).ToArray(),
        };
        CycleCompleted?.Invoke(this, cycle);
        return cycle;
    }

    private IReadOnlyList<IncidentTransition> ObserveRecorder(IIncidentRecorder recorder, EvidenceSet evidence,
        string observationWindowId, IReadOnlyList<Diagnosis> diagnoses, IReadOnlyList<string> eventIds,
        bool persisted, string? persistenceError, IReadOnlyList<ObservationEvidenceGap> evidenceGaps,
        bool diagnosisRequested, ProbeGroupPlan? plan, DateTimeOffset completedAt)
    {
        try
        {
            var observation = new IncidentObservation
            {
                SessionId = SessionId,
                BootId = BootId,
                ObservationWindowId = observationWindowId,
                SnapshotId = evidence.Snapshot!.SnapshotId,
                ObservedAt = completedAt,
                MonotonicMs = _clock.MonotonicMs,
                Synthetic = Synthetic,
                Probes = evidence.Probes,
                Diagnoses = diagnoses,
                EventIds = eventIds,
                Persisted = persisted,
                PersistenceError = persistenceError,
                CollectionGaps = evidenceGaps.Select(gap => gap.Source + "：" + gap.Reason).ToArray(),
                // Only a user-started comparison group may contribute network failures. Local
                // samples never become failures, even when a page or adapter looks unhealthy.
                ExternalObservationEnabled = diagnosisRequested,
                ExternalObservationTargetId = plan?.Target.TargetId,
                ExternalObservationBudget = plan?.GroupBudget,
            };
            return recorder.Observe(observation);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception) || exception is not OperationCanceledException)
        {
            lock (_stateLock) _recorderError = exception.GetType().Name;
            return Array.Empty<IncidentTransition>();
        }
    }

    private async Task<PersistOutcome> PersistAsync(EvidenceSet evidence, IReadOnlyList<Diagnosis> diagnoses,
        IReadOnlyList<RuleEvaluationFailure> failures, IReadOnlyList<ObservationEvidenceGap> evidenceGaps,
        string observationWindowId, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var events = EvidenceSessionEvents.Create(evidence, diagnoses, SessionId, BootId, Synthetic, completedAt,
                _clock.MonotonicMs, batchId: Guid.NewGuid().ToString("N"), failures: failures,
                evidenceGaps: evidenceGaps, observationWindowId: observationWindowId);
            return await AppendEventsCoreAsync(events, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Appends already-built events and advances the session sequence; the write gate must be held.</summary>
    private async Task<PersistOutcome> AppendEventsCoreAsync(IReadOnlyList<EventEnvelope> events,
        CancellationToken cancellationToken)
    {
        _log ??= new NdjsonEventLog(SessionPath, _logFiles);
        var ids = new List<string>(events.Count);
        var appended = 0;
        foreach (var envelope in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rebased = envelope with { Sequence = _nextSequence };
            // Once writing a record begins, finish its flush before observing cancellation.
            // Otherwise a flushed/partly flushed event could be committed without advancing the
            // sequence. Cancellation is admitted between complete records instead.
            await _log.AppendAsync(rebased, CancellationToken.None).ConfigureAwait(false);
            _nextSequence = rebased.Sequence + 1;
            appended++;
            ids.Add(rebased.EventId);
            Interlocked.Increment(ref _eventCount);
            try
            {
                _index ??= SqliteEventIndex.Open(Path.ChangeExtension(SessionPath, ".db")!);
                _index.Project(rebased);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_stateLock) _projectionError = exception.GetType().Name;
            }
        }

        return new PersistOutcome(appended, ids);
    }

    private async Task<int> PersistTransitionsWithGateAsync(IReadOnlyList<IncidentTransition> transitions,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (transitions.Count == 0) return 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_stopTask is not null)
                    throw new InvalidOperationException("The session has been stopped; queued incident markers are not written after exit.");
            }

            return await PersistTransitionsCoreAsync(transitions, at, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Writes incident marker events; the write gate must be held.</summary>
    private async Task<int> PersistTransitionsCoreAsync(IReadOnlyList<IncidentTransition> transitions,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (transitions.Count == 0) return 0;
        var events = IncidentSessionEvents.Create(transitions, SessionId, BootId, Synthetic, at,
            _clock.MonotonicMs, batchId: Guid.NewGuid().ToString("N"));
        var outcome = await AppendEventsCoreAsync(events, cancellationToken).ConfigureAwait(false);
        return outcome.Count;
    }

    private async Task FlushPendingTransitionsCoreAsync(CancellationToken cancellationToken)
    {
        List<IncidentTransition> batch;
        lock (_pendingTransitionLock)
        {
            if (_pendingTransitions.Count == 0) return;
            batch = _pendingTransitions.ToList();
            _pendingTransitions.Clear();
        }

        try
        {
            await PersistTransitionsCoreAsync(batch, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            lock (_pendingTransitionLock) _pendingTransitions.InsertRange(0, batch);
            lock (_stateLock) _persistenceError ??= exception.GetType().Name;
        }
    }

    private async Task FlushPendingTransitionsAsync(CancellationToken cancellationToken)
    {
        if (_persistenceDisabled) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await FlushPendingTransitionsCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Synchronous lifecycle calls (start/pause from the tray) only queue their markers. A single
    /// background flush drains them; an explicit exit drains the queue before releasing the log.
    /// </summary>
    private void QueueRecorderTransitions(IReadOnlyList<IncidentTransition> transitions)
    {
        if (transitions.Count == 0 || _persistenceDisabled) return;
        lock (_stateLock)
        {
            if (_stopTask is not null) return;
        }

        lock (_pendingTransitionLock) _pendingTransitions.AddRange(transitions);
        Volatile.Write(ref _pendingFlushTask, TryFlushPendingTransitionsAsync());
    }

    private async Task TryFlushPendingTransitionsAsync()
    {
        if (Interlocked.Exchange(ref _pendingFlushActive, 1) != 0) return;
        try
        {
            while (true)
            {
                lock (_stateLock)
                {
                    if (_stopTask is not null) return;
                }

                List<IncidentTransition> batch;
                lock (_pendingTransitionLock)
                {
                    if (_pendingTransitions.Count == 0) return;
                    batch = _pendingTransitions.ToList();
                    _pendingTransitions.Clear();
                }

                try
                {
                    await PersistTransitionsWithGateAsync(batch, _clock.UtcNow, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsPersistenceFailure(exception))
                {
                    lock (_pendingTransitionLock) _pendingTransitions.InsertRange(0, batch);
                    lock (_stateLock) _persistenceError ??= exception.GetType().Name;
                    return;
                }
            }
        }
        finally { Interlocked.Exchange(ref _pendingFlushActive, 0); }
    }

    private async Task DegradeRecorderAsync(string reason)
    {
        var recorder = RecorderForChange();
        if (recorder is null) return;
        var transitions = recorder.Degrade(reason, RecorderContext());
        if (transitions.Count == 0) return;
        try
        {
            await PersistTransitionsWithGateAsync(transitions, _clock.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            lock (_stateLock) _persistenceError ??= exception.GetType().Name;
        }
    }

    /// <summary>Creates the M2 incident recorder once; the tray calls this before observation starts.</summary>
    public IIncidentRecorder EnsureIncidentRecorder()
    {
        lock (_recorderAttachLock)
        {
            return _recorder ??= new IncidentRecorder();
        }
    }

    /// <summary>Attaches an externally composed recorder; keeps a single recorder per session.</summary>
    public void AttachIncidentRecorder(IIncidentRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        lock (_recorderAttachLock)
        {
            _recorder = recorder;
        }
    }

    /// <summary>User mark ("刚才断网了"): opens or annotates an incident immediately and persists the marker.</summary>
    public async Task<IncidentRecord?> MarkIncidentAsync(string? userDescription,
        CancellationToken cancellationToken)
    {
        lock (_stateLock) EnsureOpen();
        var recorder = RecorderForChange();
        if (recorder is null) return null;
        var transitions = recorder.MarkIncident(
            string.IsNullOrWhiteSpace(userDescription) ? "用户标记：刚才断网了" : userDescription!,
            RecorderContext());
        if (transitions.Count == 0) return null;
        var record = transitions[^1].Incident;
        try
        {
            await PersistTransitionsWithGateAsync(transitions, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            lock (_stateLock) { _persistenceDisabled = true; _persistenceError = exception.GetType().Name; }
        }

        return record;
    }

    /// <summary>Records one of the four business checks; not_tested stays distinct from pass.</summary>
    public async Task<BusinessCheckEntry?> RecordBusinessResultAsync(IncidentBusinessCategory category,
        IncidentBusinessResult result, string source, string? userNote, CancellationToken cancellationToken)
    {
        lock (_stateLock) EnsureOpen();
        var recorder = RecorderForChange();
        if (recorder is null) return null;
        var transitions = recorder.RecordBusinessResult(category, result, source, userNote, RecorderContext());
        if (transitions.Count == 0) return null;
        try
        {
            await PersistTransitionsWithGateAsync(transitions, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            lock (_stateLock) { _persistenceDisabled = true; _persistenceError = exception.GetType().Name; }
        }

        return transitions[^1].BusinessCheck;
    }

    private IIncidentRecorder? RecorderForChange() => Recorder;

    private IncidentMarkContext RecorderContext() => new()
    {
        SessionId = SessionId,
        BootId = BootId,
        ObservedAt = _clock.UtcNow,
        MonotonicMs = _clock.MonotonicMs,
        Synthetic = Synthetic,
    };

    private static bool IsPersistenceFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException;

    private sealed record PersistOutcome(int Count, IReadOnlyList<string> EventIds);

    private sealed class SessionDiagnosisIds : IDiagnosisIdFactory
    {
        public string Next(string ruleId) => "diagnosis-" + Guid.NewGuid().ToString("N");
    }

    private static string NewBootId()
    {
        var boot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return "boot-" + boot.ToUniversalTime().ToString("yyyyMMddTHHmmss");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        _writeGate.Dispose();
        _cycleGate.Dispose();
        _lifetimeSource.Dispose();
    }
}
