using System.Text.Json;

namespace NetScope.Domain;

/// <summary>
/// Enforces the parts of the published contract that <c>required</c> alone cannot express:
/// a member must be present <em>and</em> must carry a usable value. A JSON <c>null</c> for a
/// required identifier creates an object that silently breaks dedupe and cross references, so
/// the reader rejects it instead of handing it to callers.
/// </summary>
public static class EnvelopeIntegrity
{
    /// <summary>Throws <see cref="JsonException"/> describing every missing required value.</summary>
    public static void EnsureComplete(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var problems = new List<string>();
        if (!double.IsFinite(envelope.MonotonicMs)) problems.Add("monotonic_ms must be finite");

        RequireText(problems, "schema_version", envelope.SchemaVersion);
        RequireText(problems, "event_id", envelope.EventId);
        RequireText(problems, "session_id", envelope.SessionId);
        RequireText(problems, "boot_id", envelope.BootId);
        RequireText(problems, "source", envelope.Source);
        RequireText(problems, "source_version", envelope.SourceVersion);

        if (envelope.Refs is null)
        {
            problems.Add("refs must be an object; null is not accepted");
        }

        if (envelope.Refs is { } refs)
        {
            foreach (var (name, value) in new (string, string?)[]
            {
                ("snapshot_id", refs.SnapshotId), ("incident_id", refs.IncidentId),
                ("probe_id", refs.ProbeId), ("evidence_id", refs.EvidenceId),
                ("diagnosis_id", refs.DiagnosisId), ("repair_plan_id", refs.RepairPlanId),
                ("transaction_id", refs.TransactionId), ("probe_group_id", refs.ProbeGroupId),
            })
            {
                if (value is not null) RequireText(problems, "refs." + name, value);
            }
        }

        if (envelope.Payload is null)
        {
            problems.Add("payload must be an object; null is not accepted");
        }

        if (problems.Count > 0)
        {
            throw new JsonException("The envelope violates the required-member contract: " + string.Join("; ", problems));
        }
    }

    private static void RequireText(List<string> problems, string name, string? value)
    {
        if (value is null)
        {
            problems.Add($"{name} must be a string; null is not accepted");
        }
        else if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{name} must not be empty or whitespace");
        }
    }
}
