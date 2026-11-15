using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

[JsonConverter(typeof(WireEnumJsonConverter<RepairRiskLevel>))]
public enum RepairRiskLevel
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,
}

/// <summary>
/// One proposed field change with its exact target identity, store, typed before/desired value,
/// dependency, evidence and capability. It is data for preview only; nothing in this type can
/// execute a write.
/// </summary>
public sealed record RepairAction
{
    public required string ActionId { get; init; }

    public required RepairActionKind Kind { get; init; }

    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required RepairAddressFamily Family { get; init; }

    public required string TargetFingerprint { get; init; }

    public required InterfaceIdentity Identity { get; init; }

    public required RepairFieldValue Before { get; init; }

    public required RepairFieldValue Desired { get; init; }

    /// <summary>Source of the before value, so a preview can show why it trusts the baseline.</summary>
    public required string BeforeSource { get; init; }

    public required string BeforeSourceVersion { get; init; }

    public required EventStatus BeforeStatus { get; init; }

    public required DateTimeOffset BeforeObservedAtUtc { get; init; }

    public required TimeSpan BeforeTtl { get; init; }

    public IReadOnlyList<string> DependsOnActionIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    public required PlatformCapability Capability { get; init; }

    public required RepairActionEligibility Eligibility { get; init; }

    public IReadOnlyList<RepairRefusal> BlockingReasons { get; init; } = Array.Empty<RepairRefusal>();

    public required RepairRiskLevel Risk { get; init; }

    public required string RollbackCondition { get; init; }

    public required string ObservationPlan { get; init; }

    /// <summary>Maximum technical observation window for this action, inside the transaction budget.</summary>
    public TimeSpan ObservationWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>When this specific action's preview stops being usable.</summary>
    public required DateTimeOffset ValidUntilUtc { get; init; }

    public bool IsNoOp { get; init; }

    public bool IsChanged => !IsNoOp
        && !string.Equals(Before.CanonicalText, Desired.CanonicalText, StringComparison.Ordinal);
}

/// <summary>A requested field that already equals the desired value; it is not counted as repair.</summary>
public sealed record RepairNoOpField
{
    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required string TargetFingerprint { get; init; }

    public required string Value { get; init; }

    public required string Reason { get; init; }
}

/// <summary>A set of actions that must be consented to as one displayed combination.</summary>
public sealed record RepairDependencyGroup
{
    public required string GroupId { get; init; }

    public required IReadOnlyList<string> ActionIds { get; init; }

    public required bool RequiredTogether { get; init; }
}

/// <summary>
/// Everything an authorization must bind. The binding itself cannot be issued here: in this
/// delivery every real action remains unavailable, and T14 owns the broker identity/UAC flow.
/// </summary>
public sealed record RepairAuthorizationTerms
{
    public required string PlanDigest { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public IReadOnlyList<string> ActionIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<RepairDependencyGroup> DependencyGroups { get; init; } = Array.Empty<RepairDependencyGroup>();

    public IReadOnlyList<string> RequiredBindingFields { get; init; } = DefaultBindingFields;

    public bool PerActionConsentRequired { get; init; } = true;

    public bool DenyByDefault { get; init; } = true;

    public bool UacIsNotConsent { get; init; } = true;

    /// <summary>False by design: no authorization exists until the T14 broker verifies identity.</summary>
    public bool BindingIssued { get; init; }

    public static IReadOnlyList<string> DefaultBindingFields { get; } = new[]
    {
        "user_sid",
        "logon_session",
        "plan_digest",
        "target_fingerprint",
        "expires_at_utc",
        "confirmed_action_ids",
        "nonce",
    };
}

/// <summary>
/// An immutable preview plan. A transaction may only reference a plan and its digest; changing a
/// value, a target or a store requires a new plan and a new preview.
/// </summary>
public sealed record RepairPlan
{
    public const string CurrentSchemaVersion = "1.0";

    public required string PlanId { get; init; }

    public required string SchemaVersion { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required string SnapshotId { get; init; }

    /// <summary>The exact preflight whose facts this plan relies on.</summary>
    public required string PreflightId { get; init; }

    public string? DiagnosisId { get; init; }

    public string? IncidentId { get; init; }

    public required string BootId { get; init; }

    public required RepairClockDomain PlannerClock { get; init; }

    public required string ClockNotice { get; init; }

    public required RepairPlanDecision Decision { get; init; }

    public required string Digest { get; init; }

    public required string TargetFingerprint { get; init; }

    public required IReadOnlyList<RepairAction> Actions { get; init; }

    public required IReadOnlyList<RepairNoOpField> NoOpFields { get; init; }

    public required IReadOnlyList<RepairRefusal> Refusals { get; init; }

    public required IReadOnlyList<string> EvidenceIds { get; init; }

    public IReadOnlyList<string> MissingEvidence { get; init; } = Array.Empty<string>();

    public required RepairAuthorizationTerms Authorization { get; init; }

    /// <summary>True only when every changed action is eligible. False for this delivery.</summary>
    public required bool ExecutionAllowed { get; init; }

    public bool Synthetic { get; init; }

    public IReadOnlyList<RepairAction> ChangedActions => Actions.Where(action => action.IsChanged).ToArray();

    /// <summary>A no-op or refused plan is never counted as a repair.</summary>
    public bool CountsAsRepair => Decision == RepairPlanDecision.Planned && ChangedActions.Count > 0;

    public bool CanAuthorize => ExecutionAllowed && Decision == RepairPlanDecision.Planned;
}

/// <summary>
/// Stable SHA-256 over the fields an authorization must bind. It excludes itself and the
/// authorization object, so a digest can be shown in a preview and compared later.
/// </summary>
public static class RepairPlanDigest
{
    public static string Compute(RepairPlan plan)
    {
        var builder = new StringBuilder();
        Append(builder, "schema", plan.SchemaVersion);
        Append(builder, "plan_id", plan.PlanId);
        Append(builder, "created_utc", plan.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(builder, "expires_utc", plan.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(builder, "snapshot", plan.SnapshotId);
        Append(builder, "preflight", plan.PreflightId);
        Append(builder, "diagnosis", plan.DiagnosisId);
        Append(builder, "incident", plan.IncidentId);
        Append(builder, "boot", plan.BootId);
        Append(builder, "clock", plan.PlannerClock.DomainId + "|" + plan.PlannerClock.BootId);
        Append(builder, "target", plan.TargetFingerprint);
        Append(builder, "decision", WireNames.Of(plan.Decision));
        Append(builder, "execution_allowed", plan.ExecutionAllowed ? "true" : "false");
        Append(builder, "synthetic", plan.Synthetic ? "true" : "false");

        foreach (var action in plan.Actions.OrderBy(action => action.ActionId, StringComparer.Ordinal))
        {
            Append(
                builder,
                "action",
                action.ActionId + "|" + WireNames.Of(action.Kind) + "|" + WireNames.Of(action.Field)
                + "|" + WireNames.Of(action.Store) + "|" + WireNames.Of(action.Family)
                + "|" + action.TargetFingerprint
                + "|" + action.Before.CanonicalText + "|" + action.Desired.CanonicalText
                + "|" + string.Join(",", action.DependsOnActionIds.Order(StringComparer.Ordinal))
                + "|" + action.Capability.CapabilityId
                + "|" + WireNames.Of(action.Capability.Status)
                + "|" + action.Capability.ReasonCode
                + "|" + WireNames.Of(action.Eligibility)
                + "|" + string.Join(',', action.BlockingReasons
                    .Select(blocking => WireNames.Of(blocking.Code))
                    .Order(StringComparer.Ordinal)));
        }

        foreach (var noOp in plan.NoOpFields.OrderBy(item => item.Field).ThenBy(item => item.Store))
        {
            Append(builder, "no_op", WireNames.Of(noOp.Field) + "|" + WireNames.Of(noOp.Store) + "|" + noOp.TargetFingerprint + "|" + noOp.Value);
        }

        foreach (var refusal in plan.Refusals.OrderBy(item => WireNames.Of(item.Code), StringComparer.Ordinal))
        {
            Append(builder, "refusal", WireNames.Of(refusal.Code) + "|" + refusal.Field + "|" + refusal.Action);
        }

        foreach (var evidence in plan.EvidenceIds.Order(StringComparer.Ordinal))
        {
            Append(builder, "evidence", evidence);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string key, string? value) =>
        builder.Append(key).Append('=').Append(value ?? string.Empty).Append('\n');
}
