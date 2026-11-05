using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// The <c>event_type</c> values defined by <c>contracts/event-envelope.schema.json</c> v1.0.
/// Wire names are snake_case and are part of the persisted contract.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<EventType>))]
public enum EventType
{
    [JsonStringEnumMemberName("snapshot")]
    Snapshot,

    [JsonStringEnumMemberName("probe_result")]
    ProbeResult,

    [JsonStringEnumMemberName("diagnosis")]
    Diagnosis,

    [JsonStringEnumMemberName("incident_marker")]
    IncidentMarker,

    [JsonStringEnumMemberName("collector_error")]
    CollectorError,

    [JsonStringEnumMemberName("gap")]
    Gap,

    [JsonStringEnumMemberName("repair_event")]
    RepairEvent,
}
