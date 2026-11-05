using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// Version 1.0 event envelope. The envelope is intentionally strict: every documented
/// field is required, unknown fields are rejected, and the event-specific body stays in
/// <see cref="Payload"/> until a dedicated payload contract is added.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventEnvelope
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("sequence")]
    public required int Sequence { get; init; }

    [JsonPropertyName("observed_at")]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyName("monotonic_ms")]
    public required double MonotonicMs { get; init; }

    [JsonPropertyName("event_type")]
    public required EventType EventType { get; init; }

    [JsonPropertyName("status")]
    public required EventStatus Status { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("source_version")]
    public required string SourceVersion { get; init; }

    [JsonPropertyName("synthetic")]
    public required bool Synthetic { get; init; }

    [JsonPropertyName("refs")]
    public required EventRefs Refs { get; init; }

    [JsonPropertyName("payload")]
    public required JsonObject Payload { get; init; }

    /// <summary>Contract version this build understands.</summary>
    public const string SupportedSchemaVersion = "1.0";
}
