using NetScope.Application;
using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Desktop.Services;

/// <summary>Repair events join the observing session's timeline (same NDJSON + SQLite projection).</summary>
public sealed class SessionRepairEventSink(ObservationSession session, IClock clock) : IRepairEventSink
{
    public Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken)
        => session.AppendRepairEventsAsync(
            RepairSessionEvents.Create(events, session.SessionId, session.BootId, session.Synthetic, clock.MonotonicMs),
            cancellationToken);
}