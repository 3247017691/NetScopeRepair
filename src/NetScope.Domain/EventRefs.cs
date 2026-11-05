using System.Text.Json.Serialization;

namespace NetScope.Domain;

/// <summary>
/// Optional cross-references carried by every envelope. All values are opaque identifiers;
/// unknown reference names are rejected so a typo cannot silently drop a link.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventRefs
{
    [JsonPropertyName("snapshot_id")]
    public string? SnapshotId { get; init; }

    [JsonPropertyName("incident_id")]
    public string? IncidentId { get; init; }

    [JsonPropertyName("probe_id")]
    public string? ProbeId { get; init; }

    [JsonPropertyName("evidence_id")]
    public string? EvidenceId { get; init; }

    [JsonPropertyName("diagnosis_id")]
    public string? DiagnosisId { get; init; }

    [JsonPropertyName("repair_plan_id")]
    public string? RepairPlanId { get; init; }

    [JsonPropertyName("transaction_id")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("probe_group_id")]
    public string? ProbeGroupId { get; init; }
}
