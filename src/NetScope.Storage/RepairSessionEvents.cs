using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Maintenance;

namespace NetScope.Storage;

/// <summary>Builds repair_event envelopes; the writer rebases sequence numbers.</summary>
public static class RepairSessionEvents
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IReadOnlyList<EventEnvelope> Create(IReadOnlyList<RepairEvent> events,
        string sessionId, string bootId, bool synthetic, double monotonicMs)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = new List<EventEnvelope>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var repair = events[index];
            var status = repair.Kind switch
            {
                "repair_applied" => EventStatus.Ok,
                "action_failed" when repair.ElevationSkipped => EventStatus.AccessDenied,
                _ => EventStatus.Failed,
            };
            var envelope = new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion,
                EventId = Guid.NewGuid().ToString("D"),
                SessionId = sessionId,
                BootId = bootId,
                Sequence = index + 1,
                ObservedAt = repair.At,
                MonotonicMs = monotonicMs,
                EventType = EventType.RepairEvent,
                Status = status,
                Source = "maintenance.quick-repair",
                SourceVersion = "1.1",
                Synthetic = synthetic,
                Refs = new EventRefs { RepairPlanId = repair.BatchId },
                Payload = JsonSerializer.SerializeToNode(repair, PayloadOptions)!.AsObject(),
            };
            _ = EnvelopeJson.Serialize(envelope);
            result.Add(envelope);
        }
        return result;
    }
}