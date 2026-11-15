namespace NetScope.Maintenance;

/// <summary>The kind of a live progress tick. A pure UI side-channel; never persisted to the log.</summary>
public enum RepairStepKind
{
    Phase,
    ActionStart,
    ActionResult,
    PostCheck,
    Info,
    Warning,
    Error,
    Done,
}

/// <summary>
/// One fine-grained, live progress tick emitted while a repair runs. This is a UI side-channel
/// only: it is never written to the NDJSON/SQLite event log and never changes the repair outcome.
/// Step/TotalSteps describe action completion (0/0 means "no determinate scale yet").
/// </summary>
public sealed record RepairProgress(
    RepairStepKind Kind,
    string Message,
    string? ActionId = null,
    string? ActionTitle = null,
    int Step = 0,
    int TotalSteps = 0,
    bool? Applied = null,
    string? Detail = null)
{
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
}
