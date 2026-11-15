using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// The exact transaction states from design 05 §6. No extra state may be invented: a transaction
/// is either in one of these states or it does not exist.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<RepairTransactionState>))]
public enum RepairTransactionState
{
    [JsonStringEnumMemberName("planned")]
    Planned,

    [JsonStringEnumMemberName("authorized")]
    Authorized,

    [JsonStringEnumMemberName("prepared")]
    Prepared,

    [JsonStringEnumMemberName("applying")]
    Applying,

    [JsonStringEnumMemberName("observing")]
    Observing,

    [JsonStringEnumMemberName("committed")]
    Committed,

    [JsonStringEnumMemberName("rolling_back")]
    RollingBack,

    [JsonStringEnumMemberName("rolled_back")]
    RolledBack,

    [JsonStringEnumMemberName("rollback_conflict")]
    RollbackConflict,

    [JsonStringEnumMemberName("rollback_failed")]
    RollbackFailed,

    [JsonStringEnumMemberName("recovery_required")]
    RecoveryRequired,

    [JsonStringEnumMemberName("aborted")]
    Aborted,

    [JsonStringEnumMemberName("no_op")]
    NoOp,
}

/// <summary>Allowed transitions only. An illegal transition is refused, never coerced.</summary>
public static class RepairTransactionStateRules
{
    private static readonly Dictionary<RepairTransactionState, RepairTransactionState[]> Transitions = new()
    {
        [RepairTransactionState.Planned] = new[]
        {
            RepairTransactionState.Authorized,
            RepairTransactionState.NoOp,
            RepairTransactionState.Aborted,
        },
        [RepairTransactionState.Authorized] = new[]
        {
            RepairTransactionState.Prepared,
            RepairTransactionState.Aborted,
        },
        [RepairTransactionState.Prepared] = new[]
        {
            RepairTransactionState.Applying,
            RepairTransactionState.Aborted,
            RepairTransactionState.RecoveryRequired,
        },
        [RepairTransactionState.Applying] = new[]
        {
            RepairTransactionState.Observing,
            RepairTransactionState.RollingBack,
            RepairTransactionState.RecoveryRequired,
        },
        [RepairTransactionState.Observing] = new[]
        {
            RepairTransactionState.Committed,
            RepairTransactionState.RollingBack,
            RepairTransactionState.RecoveryRequired,
        },
        [RepairTransactionState.RollingBack] = new[]
        {
            RepairTransactionState.RolledBack,
            RepairTransactionState.RollbackConflict,
            RepairTransactionState.RollbackFailed,
            RepairTransactionState.RecoveryRequired,
        },
    };

    public static IReadOnlyList<RepairTransactionState> TerminalStates { get; } = new[]
    {
        RepairTransactionState.Committed,
        RepairTransactionState.RolledBack,
        RepairTransactionState.RollbackConflict,
        RepairTransactionState.RollbackFailed,
        RepairTransactionState.RecoveryRequired,
        RepairTransactionState.Aborted,
        RepairTransactionState.NoOp,
    };

    public static bool IsTerminal(RepairTransactionState state) => TerminalStates.Contains(state);

    public static bool CanTransition(RepairTransactionState from, RepairTransactionState to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static bool IsKnown(RepairTransactionState state) => Enum.IsDefined(state);

    public static string WireName(RepairTransactionState state) => WireNames.Of(state);
}

/// <summary>Design 05 §3/§6 budgets, expressed once for the transaction layer.</summary>
public sealed record RepairTransactionLimits
{
    public TimeSpan Handshake { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Total { get; init; } = TimeSpan.FromSeconds(180);

    public TimeSpan MaxTechnicalObservation { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan CompensationReserve { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Latest point at which a new forward change or a new observation may start.</summary>
    public TimeSpan LatestForwardAction => Total - CompensationReserve;

    /// <summary>A single native read/write is bounded, but it is never abandoned mid-call.</summary>
    public TimeSpan NativeCallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public static RepairTransactionLimits Design05 { get; } = new();

    public TimeSpan EffectiveObservationWindow(TimeSpan elapsed)
    {
        var remainingForward = LatestForwardAction - elapsed;
        if (remainingForward <= TimeSpan.Zero) return TimeSpan.Zero;
        return remainingForward < MaxTechnicalObservation ? remainingForward : MaxTechnicalObservation;
    }
}

/// <summary>
/// Stable refusal codes for the transaction/journal layer. They are separate from diagnostics and
/// from T13 plan refusals so a journal failure is never collapsed into "plan refused".
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<RepairTransactionRefusalCode>))]
public enum RepairTransactionRefusalCode
{
    [JsonStringEnumMemberName("journal_capability_closed")]
    JournalCapabilityClosed,

    [JsonStringEnumMemberName("journal_directory_not_fixed")]
    JournalDirectoryNotFixed,

    [JsonStringEnumMemberName("journal_directory_user_writable")]
    JournalDirectoryUserWritable,

    [JsonStringEnumMemberName("journal_directory_reparse_point")]
    JournalDirectoryReparsePoint,

    [JsonStringEnumMemberName("journal_lock_held")]
    JournalLockHeld,

    [JsonStringEnumMemberName("journal_corrupt")]
    JournalCorrupt,

    [JsonStringEnumMemberName("journal_version_unsupported")]
    JournalVersionUnsupported,

    [JsonStringEnumMemberName("journal_sequence_gap")]
    JournalSequenceGap,

    [JsonStringEnumMemberName("journal_flush_failed")]
    JournalFlushFailed,

    [JsonStringEnumMemberName("journal_locked")]
    JournalLocked,

    [JsonStringEnumMemberName("unresolved_transaction_present")]
    UnresolvedTransactionPresent,

    [JsonStringEnumMemberName("plan_not_executable")]
    PlanNotExecutable,

    [JsonStringEnumMemberName("authorization_invalid")]
    AuthorizationInvalid,

    [JsonStringEnumMemberName("capability_unavailable")]
    CapabilityUnavailable,

    [JsonStringEnumMemberName("target_drift")]
    TargetDrift,

    [JsonStringEnumMemberName("external_value_changed")]
    ExternalValueChanged,

    [JsonStringEnumMemberName("before_mismatch")]
    BeforeMismatch,

    [JsonStringEnumMemberName("write_failed")]
    WriteFailed,

    [JsonStringEnumMemberName("readback_mismatch")]
    ReadbackMismatch,

    [JsonStringEnumMemberName("observation_failed")]
    ObservationFailed,

    [JsonStringEnumMemberName("observation_unavailable")]
    ObservationUnavailable,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("deadline_exceeded")]
    DeadlineExceeded,

    [JsonStringEnumMemberName("rollback_conflict")]
    RollbackConflict,

    [JsonStringEnumMemberName("rollback_failed")]
    RollbackFailed,

    [JsonStringEnumMemberName("recovery_uncertain")]
    RecoveryUncertain,

    [JsonStringEnumMemberName("cross_boot")]
    CrossBoot,

    [JsonStringEnumMemberName("target_rebuilt")]
    TargetRebuilt,

    [JsonStringEnumMemberName("synthetic_not_allowed")]
    SyntheticNotAllowed,

    [JsonStringEnumMemberName("no_actions")]
    NoActions,

    [JsonStringEnumMemberName("execution_closed")]
    ExecutionClosed,
}

public sealed record RepairTransactionRefusal
{
    public required RepairTransactionRefusalCode Code { get; init; }

    public required string Detail { get; init; }

    public static RepairTransactionRefusal Of(RepairTransactionRefusalCode code, string detail) => new()
    {
        Code = code,
        Detail = detail,
    };
}
