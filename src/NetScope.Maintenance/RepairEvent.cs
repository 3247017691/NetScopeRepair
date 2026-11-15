namespace NetScope.Maintenance;

/// <summary>
/// One repair outcome worth persisting. Kinds: repair_applied / action_failed /
/// postcheck_failed / user_unresolved. Carries before/after snapshots when available.
/// </summary>
public sealed record RepairEvent(
    string BatchId,
    string Kind,
    DateTimeOffset At,
    string? ActionId,
    string? ActionKind,
    string? Title,
    string? Error,
    bool ElevationSkipped,
    PostCheckResult? PostCheck,
    MaintenanceSnapshot? Before,
    MaintenanceSnapshot? After);

/// <summary>Persistence port for repair outcomes; null sink keeps the service purely local.</summary>
public interface IRepairEventSink
{
    Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken);
}
