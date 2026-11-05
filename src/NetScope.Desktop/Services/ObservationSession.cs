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
        bool synthetic = false, string? sessionId = null, string? bootId = null, IEventLogFileAccess? logFiles = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logFiles = logFiles;
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
                catch (Exception exception) { CollectionFailed?.Invoke(this, exception.GetType().Name); }
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
    }

    /// <summary>Also drains a cancelled sample before switching the UI to synthetic evidence.</summary>
    public async Task PauseAsync()
    {
        Pause();
        Task? pending;
        lock (_stateLock) pending = _loopTask;
        if (pending is not null) await pending.ConfigureAwait(false);
        await _cycleGate.WaitAsync().ConfigureAwait(false);
        _cycleGate.Release();
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
        await _cycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
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
            var evidence = new EvidenceSet { Records = Array.Empty<EvidenceRecord>(), Probes = Array.Empty<ProbeResult>(), Snapshot = snapshot };
            token.ThrowIfCancellationRequested();
            return await CompleteAsync(evidence, Array.Empty<Diagnosis>(), Array.Empty<RuleEvaluationFailure>(), false, token).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
    }

    /// <summary>Runs one explicitly requested group and evaluates the local rules.</summary>
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
            var group = await _probes.RunAsync(plan, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var evidence = new EvidenceSet { Records = Array.Empty<EvidenceRecord>(), Probes = group.Results, Snapshot = snapshot };
            var evaluation = _diagnostics.EvaluateDetailed(evidence);
            return await CompleteAsync(evidence, evaluation.Diagnoses, evaluation.Failures, true, token).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
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
        IReadOnlyList<RuleEvaluationFailure> failures, bool diagnosisRequested, CancellationToken cancellationToken)
    {
        var completedAt = _clock.UtcNow;
        var appended = 0;
        string? error = null;
        if (!_persistenceDisabled)
        {
            try { appended = await PersistAsync(evidence, diagnoses, completedAt, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
            {
                error = exception.GetType().Name;
                lock (_stateLock) { _persistenceDisabled = true; _persistenceError = error; }
            }
        }
        else { lock (_stateLock) error = _persistenceError; }
        cancellationToken.ThrowIfCancellationRequested();
        var cycle = new SessionCycle
        {
            Snapshot = evidence.Snapshot!, Evidence = evidence, Diagnoses = diagnoses, RuleFailures = failures,
            CompletedAt = completedAt, Persisted = appended > 0, PersistenceError = error,
            AppendedEvents = appended, DiagnosisRequested = diagnosisRequested,
        };
        CycleCompleted?.Invoke(this, cycle);
        return cycle;
    }

    private async Task<int> PersistAsync(EvidenceSet evidence, IReadOnlyList<Diagnosis> diagnoses,
        DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var events = EvidenceSessionEvents.Create(evidence, diagnoses, SessionId, BootId, Synthetic, completedAt, _clock.MonotonicMs,
                batchId: Guid.NewGuid().ToString("N"));
            _log ??= new NdjsonEventLog(SessionPath, _logFiles);
            var appended = 0;
            foreach (var envelope in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rebased = envelope with { Sequence = _nextSequence };
                // Once writing a record begins, finish its flush before observing cancellation.
                // Otherwise a flushed/partly flushed event could be committed without advancing
                // the sequence. Cancellation is admitted between complete records instead.
                await _log.AppendAsync(rebased, CancellationToken.None).ConfigureAwait(false);
                _nextSequence = rebased.Sequence + 1;
                appended++;
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
            return appended;
        }
        finally { _writeGate.Release(); }
    }

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
