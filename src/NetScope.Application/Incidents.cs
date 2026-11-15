using NetScope.Domain;

namespace NetScope.Application;

/// <summary>
/// Incident capture states from design 03 §4. A state is a statement about the observation
/// session, never about the network: "closed" only means the observation window ended.
/// </summary>
public enum IncidentState
{
    Idle,
    Observing,
    Suspected,
    IncidentOpen,
    Recovering,
    Closed,
    Paused,
    Degraded,
}

/// <summary>What opened an incident. A user statement is an explicit observation, not a probe.</summary>
public enum IncidentTrigger
{
    ConsecutiveFailures,
    ManualUserMark,
}

/// <summary>The four user-facing business checks from design 11 §4 / QA-038.</summary>
public enum IncidentBusinessCategory
{
    Dsh,
    Antigravity,
    Chrome,
    TunApplication,
}

/// <summary>Not tested is its own result and can never be counted as a pass.</summary>
public enum IncidentBusinessResult
{
    Pass,
    Fail,
    NotTested,
}

public enum IncidentTransitionKind
{
    StateChange,
    BusinessCheck,
}

/// <summary>
/// The fixed external-observation policy. M2 never starts an external probe on its own: a probe
/// observation is only accepted when the user explicitly started the comparison group, and it
/// stops when the session exits. Frequency and budget are the documented defaults and are shown
/// by the UI before enabling external observation.
/// </summary>
public sealed record IncidentObservationPolicy
{
    public static IncidentObservationPolicy Default { get; } = new();

    public TimeSpan ExternalProbeInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ExternalProbeBudget { get; init; } = TimeSpan.FromSeconds(30);

    public bool RequiresExplicitUserStart { get; init; } = true;

    /// <summary>M2 has no background probe scheduler; only user-started groups contribute.</summary>
    public bool AutomaticExternalProbes { get; init; }
}

/// <summary>Pre/post observation window of one incident, with honest completeness markers.</summary>
public sealed record IncidentWindow
{
    public static readonly TimeSpan DefaultPreWindow = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan DefaultPostWindow = TimeSpan.FromMinutes(2);

    public TimeSpan PreWindow { get; init; } = DefaultPreWindow;

    public TimeSpan PostWindow { get; init; } = DefaultPostWindow;

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>False when the recorder did not observe the full five minutes before the start.</summary>
    public bool PreWindowAvailable { get; init; }

    /// <summary>True only when the full post window was observed without a new failure.</summary>
    public bool PostWindowComplete { get; init; }

    public IReadOnlyList<string> MissingRanges { get; init; } = Array.Empty<string>();

    public DateTimeOffset PreWindowStart => StartedAt - PreWindow;

    public DateTimeOffset? PostWindowEnd => EndedAt + PostWindow;
}

/// <summary>
/// One completed or explicitly requested observation cycle, as supplied by the session. The
/// recorder never reads the system, starts probes or persists events itself.
/// </summary>
public sealed record IncidentObservation
{
    public required string SessionId { get; init; }

    public required string BootId { get; init; }

    public required string ObservationWindowId { get; init; }

    public required string SnapshotId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required double MonotonicMs { get; init; }

    public required bool Synthetic { get; init; }

    public IReadOnlyList<ProbeResult> Probes { get; init; } = Array.Empty<ProbeResult>();

    public IReadOnlyList<Diagnosis> Diagnoses { get; init; } = Array.Empty<Diagnosis>();

    /// <summary>Persisted event ids of this cycle; empty when persistence failed.</summary>
    public IReadOnlyList<string> EventIds { get; init; } = Array.Empty<string>();

    public bool Persisted { get; init; } = true;

    public string? PersistenceError { get; init; }

    /// <summary>Local data domains that could not be read in this cycle (never network failures).</summary>
    public IReadOnlyList<string> CollectionGaps { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True only for a probe group the user explicitly started with a target. Local samples never
    /// enable this, so adapter or page activity can never create a network-failure conclusion.
    /// </summary>
    public bool ExternalObservationEnabled { get; init; }

    public string? ExternalObservationTargetId { get; init; }

    public TimeSpan? ExternalObservationBudget { get; init; }
}

/// <summary>Scope for user-initiated recorder changes that happen outside an observation cycle.</summary>
public sealed record IncidentMarkContext
{
    public required string SessionId { get; init; }

    public required string BootId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required double MonotonicMs { get; init; }

    public required bool Synthetic { get; init; }
}

/// <summary>One failure class merged inside the five-minute merge window.</summary>
public sealed record IncidentFailureGroup
{
    public required string ClassKey { get; init; }

    public required string TargetId { get; init; }

    public required string Path { get; init; }

    public string? ProxyEndpointId { get; init; }

    public string? AddressFamily { get; init; }

    public string? Method { get; init; }

    public required DateTimeOffset FirstAt { get; init; }

    public required DateTimeOffset LastAt { get; init; }

    public required int Count { get; init; }
}

/// <summary>An incident with its explicit scope, references, merged failures and closure reason.</summary>
public sealed record IncidentRecord
{
    public required string IncidentId { get; init; }

    public required IncidentTrigger Trigger { get; init; }

    public required IncidentState State { get; init; }

    public required string SessionId { get; init; }

    public required string BootId { get; init; }

    public required bool Synthetic { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public required IncidentWindow Window { get; init; }

    public IReadOnlyList<string> UserDescriptions { get; init; } = Array.Empty<string>();

    /// <summary>Persisted event ids of the pre-window, incident and post-window cycles.</summary>
    public IReadOnlyList<string> EventIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SnapshotIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ProbeIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DiagnosisIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<IncidentFailureGroup> Failures { get; init; } = Array.Empty<IncidentFailureGroup>();

    public IReadOnlyList<string> MissingRanges { get; init; } = Array.Empty<string>();

    public string? ClosureReason { get; init; }

    public bool PostWindowComplete { get; init; }

    public int EffectiveFailureCount { get; init; }

    public int EffectiveSuccessCount { get; init; }

    public string? LastSnapshotId { get; init; }

    public string? LastObservationWindowId { get; init; }
}

/// <summary>One business verification entry; default is not tested, never a pass.</summary>
public sealed record BusinessCheckEntry
{
    public required IncidentBusinessCategory Category { get; init; }

    public required IncidentBusinessResult Result { get; init; }

    public required string Source { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public string? UserNote { get; init; }

    public bool IsPass => Result == IncidentBusinessResult.Pass;
}

/// <summary>Everything the tray or a view needs to display the current capture state.</summary>
public sealed record IncidentRecorderStatus
{
    public required IncidentState State { get; init; }

    public IncidentRecord? CurrentIncident { get; init; }

    public IncidentRecord? LastIncident { get; init; }

    public required bool HasRecentIncident { get; init; }

    /// <summary>True while the recorder can still accept a user mark (false after shutdown).</summary>
    public required bool CanMarkIncident { get; init; }

    public IReadOnlyList<BusinessCheckEntry> BusinessChecks { get; init; } = Array.Empty<BusinessCheckEntry>();

    public string? DegradedReason { get; init; }

    public DateTimeOffset? LastObservationAt { get; init; }

    public int ObservationCount { get; init; }

    /// <summary>True only when all four business categories recorded a real pass.</summary>
    public bool AllBusinessChecksPassed =>
        BusinessChecks.Count == 4 && BusinessChecks.All(entry => entry.IsPass);

    public IncidentObservationPolicy Policy { get; init; } = IncidentObservationPolicy.Default;
}

/// <summary>A state change or business-check marker that the session persists as an event.</summary>
public sealed record IncidentTransition
{
    public required IncidentTransitionKind Kind { get; init; }

    public required IncidentState PreviousState { get; init; }

    public required IncidentState State { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset At { get; init; }

    public required double MonotonicMs { get; init; }

    public required string SessionId { get; init; }

    public required string BootId { get; init; }

    public required bool Synthetic { get; init; }

    public IncidentRecord? Incident { get; init; }

    public BusinessCheckEntry? BusinessCheck { get; init; }
}

/// <summary>Limits of the M2 incident recorder; defaults are the frozen design values.</summary>
public sealed record IncidentRecorderOptions
{
    public TimeSpan PreWindow { get; init; } = IncidentWindow.DefaultPreWindow;

    public TimeSpan PostWindow { get; init; } = IncidentWindow.DefaultPostWindow;

    public TimeSpan MergeWindow { get; init; } = TimeSpan.FromMinutes(5);

    public int FailureThreshold { get; init; } = 3;

    public int RecoverySuccessThreshold { get; init; } = 3;

    /// <summary>Largest observation gap accepted as continuous post-window coverage.</summary>
    public TimeSpan MaxSampleGap { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Wall-clock vs monotonic divergence that is treated as a sleep/clock discontinuity.</summary>
    public TimeSpan ClockDiscontinuityTolerance { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxRecentIncidents { get; init; } = 20;

    public int MaxBufferedObservations { get; init; } = 2048;

    public IncidentObservationPolicy Policy { get; init; } = IncidentObservationPolicy.Default;
}

/// <summary>
/// Pure incident state machine driven by observation cycles and explicit user actions. It never
/// reads the system, starts a probe or writes a file; the session persists the returned transitions.
/// </summary>
public interface IIncidentRecorder
{
    IncidentState State { get; }

    IncidentRecorderStatus Status { get; }

    /// <summary>Finalized incidents, newest first.</summary>
    IReadOnlyList<IncidentRecord> Incidents { get; }

    IReadOnlyList<IncidentTransition> Observe(IncidentObservation observation);

    IReadOnlyList<IncidentTransition> MarkIncident(string userDescription, IncidentMarkContext context);

    IReadOnlyList<IncidentTransition> RecordBusinessResult(
        IncidentBusinessCategory category, IncidentBusinessResult result, string source, string? userNote,
        IncidentMarkContext context);

    IReadOnlyList<IncidentTransition> Pause(IncidentMarkContext context);

    IReadOnlyList<IncidentTransition> Resume(IncidentMarkContext context);

    IReadOnlyList<IncidentTransition> Degrade(string reason, IncidentMarkContext context);

    /// <summary>Explicit exit: closes an unfinished incident as incomplete, then accepts no data.</summary>
    IReadOnlyList<IncidentTransition> Shutdown(IncidentMarkContext context);

    event EventHandler<IncidentRecorderStatus>? StatusChanged;
}
