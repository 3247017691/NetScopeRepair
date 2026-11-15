using System.Text.Json.Serialization;

namespace NetScope.Repair;

/// <summary>
/// Stable refusal codes. The UI translates them; tests assert the code rather than matching
/// Chinese prose, and no code is ever collapsed into a generic failure.
/// </summary>
[JsonConverter(typeof(NetScope.Domain.WireEnumJsonConverter<RepairRefusalCode>))]
public enum RepairRefusalCode
{
    [JsonStringEnumMemberName("identity_unknown")]
    IdentityUnknown,

    [JsonStringEnumMemberName("identity_incomplete")]
    IdentityIncomplete,

    [JsonStringEnumMemberName("critical_unknown")]
    CriticalUnknown,

    [JsonStringEnumMemberName("attribution_unknown")]
    AttributionUnknown,

    [JsonStringEnumMemberName("attribution_untrusted")]
    AttributionUntrusted,

    [JsonStringEnumMemberName("interface_ambiguous")]
    InterfaceAmbiguous,

    [JsonStringEnumMemberName("not_tun")]
    NotTun,

    [JsonStringEnumMemberName("interface_rebuilt")]
    InterfaceRebuilt,

    [JsonStringEnumMemberName("target_drift")]
    TargetDrift,

    [JsonStringEnumMemberName("cross_boot")]
    CrossBoot,

    [JsonStringEnumMemberName("fact_stale")]
    FactStale,

    [JsonStringEnumMemberName("clock_skew")]
    ClockSkew,

    [JsonStringEnumMemberName("protected_uplink")]
    ProtectedUplink,

    [JsonStringEnumMemberName("uplink_unknown")]
    UplinkUnknown,

    [JsonStringEnumMemberName("uplink_changed")]
    UplinkChanged,

    [JsonStringEnumMemberName("family_not_supported")]
    FamilyNotSupported,

    [JsonStringEnumMemberName("store_not_supported")]
    StoreNotSupported,

    [JsonStringEnumMemberName("value_unknown")]
    ValueUnknown,

    [JsonStringEnumMemberName("value_not_manual")]
    ValueNotManual,

    [JsonStringEnumMemberName("automatic_metric_unknown")]
    AutomaticMetricUnknown,

    [JsonStringEnumMemberName("coupling_unverified")]
    CouplingUnverified,

    [JsonStringEnumMemberName("dependency_missing")]
    DependencyMissing,

    [JsonStringEnumMemberName("action_not_allowed")]
    ActionNotAllowed,

    [JsonStringEnumMemberName("action_value_invalid")]
    ActionValueInvalid,

    [JsonStringEnumMemberName("action_limit_exceeded")]
    ActionLimitExceeded,

    [JsonStringEnumMemberName("duplicate_action")]
    DuplicateAction,

    [JsonStringEnumMemberName("no_actions")]
    NoActions,

    [JsonStringEnumMemberName("recover_requires_journal")]
    RecoverRequiresJournal,

    [JsonStringEnumMemberName("proposal_untyped")]
    ProposalUntyped,

    [JsonStringEnumMemberName("capability_unavailable")]
    CapabilityUnavailable,

    [JsonStringEnumMemberName("capability_disallowed")]
    CapabilityDisallowed,

    [JsonStringEnumMemberName("plan_expired")]
    PlanExpired,

    [JsonStringEnumMemberName("plan_digest_mismatch")]
    PlanDigestMismatch,

    [JsonStringEnumMemberName("plan_not_executable")]
    PlanNotExecutable,

    [JsonStringEnumMemberName("authorization_mismatch")]
    AuthorizationMismatch,

    [JsonStringEnumMemberName("consent_missing")]
    ConsentMissing,
}

public sealed record RepairRefusal
{
    public required RepairRefusalCode Code { get; init; }

    public required string Detail { get; init; }

    /// <summary>Wire field name when the refusal is about one field.</summary>
    public string? Field { get; init; }

    /// <summary>Wire action name when the refusal is about one action.</summary>
    public string? Action { get; init; }

    public IReadOnlyList<string> MissingEvidence { get; init; } = Array.Empty<string>();

    public static RepairRefusal Of(
        RepairRefusalCode code,
        string detail,
        string? field = null,
        string? action = null,
        IEnumerable<string>? missingEvidence = null) => new()
        {
            Code = code,
            Detail = detail,
            Field = field,
            Action = action,
            MissingEvidence = missingEvidence?.ToArray() ?? Array.Empty<string>(),
        };
}
