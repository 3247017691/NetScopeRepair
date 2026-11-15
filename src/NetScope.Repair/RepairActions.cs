using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// The closed set of repair actions from design 05 section 3. There is deliberately no
/// <c>SetProperty</c>, no free-form property name and no way to widen this enum at runtime:
/// a broker IPC frame can only carry one of these wire values.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<RepairActionKind>))]
public enum RepairActionKind
{
    [JsonStringEnumMemberName("set_tun_ipv4_interface_metric")]
    SetTunIpv4InterfaceMetric,

    [JsonStringEnumMemberName("set_tun_ipv4_automatic_metric")]
    SetTunIpv4AutomaticMetric,

    [JsonStringEnumMemberName("set_tun_ipv4_ignore_default_routes")]
    SetTunIpv4IgnoreDefaultRoutes,

    /// <summary>
    /// Only valid when driven by the broker's authoritative journal, never by a diagnostic
    /// proposal or by a caller-supplied transaction id. The journal itself is T15 work.
    /// </summary>
    [JsonStringEnumMemberName("recover_transaction")]
    RecoverTransaction,
}

/// <summary>ActiveStore and PersistentStore are different write targets and are never merged.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<RepairStore>))]
public enum RepairStore
{
    [JsonStringEnumMemberName("active_store")]
    ActiveStore,

    [JsonStringEnumMemberName("persistent_store")]
    PersistentStore,
}

[JsonConverter(typeof(WireEnumJsonConverter<RepairAddressFamily>))]
public enum RepairAddressFamily
{
    [JsonStringEnumMemberName("ipv4")]
    Ipv4,

    [JsonStringEnumMemberName("ipv6")]
    Ipv6,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>The three fields V1 may ever write, and only for TUN IPv4 ActiveStore.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<RepairField>))]
public enum RepairField
{
    [JsonStringEnumMemberName("interface_metric")]
    InterfaceMetric,

    [JsonStringEnumMemberName("automatic_metric")]
    AutomaticMetric,

    [JsonStringEnumMemberName("ignore_default_routes")]
    IgnoreDefaultRoutes,
}

/// <summary>
/// Whether an observed metric is the manually configured value or the effective value Windows
/// derived. A derived value can never be used as a restore source.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<MetricValueKind>))]
public enum MetricValueKind
{
    [JsonStringEnumMemberName("manual_configured")]
    ManualConfigured,

    [JsonStringEnumMemberName("effective_derived")]
    EffectiveDerived,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(WireEnumJsonConverter<RepairActionEligibility>))]
public enum RepairActionEligibility
{
    [JsonStringEnumMemberName("allowed")]
    Allowed,

    [JsonStringEnumMemberName("blocked")]
    Blocked,
}

[JsonConverter(typeof(WireEnumJsonConverter<RepairPlanDecision>))]
public enum RepairPlanDecision
{
    [JsonStringEnumMemberName("planned")]
    Planned,

    [JsonStringEnumMemberName("no_op")]
    NoOp,

    [JsonStringEnumMemberName("refused")]
    Refused,
}

/// <summary>Facts about how the three actions map to fields and required verification checks.</summary>
public static class RepairActionRules
{
    /// <summary>The only forward actions a T13 plan may contain.</summary>
    public static IReadOnlyList<RepairActionKind> ForwardActions { get; } = new[]
    {
        RepairActionKind.SetTunIpv4InterfaceMetric,
        RepairActionKind.SetTunIpv4AutomaticMetric,
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes,
    };

    public static RepairField FieldOf(RepairActionKind action) => action switch
    {
        RepairActionKind.SetTunIpv4InterfaceMetric => RepairField.InterfaceMetric,
        RepairActionKind.SetTunIpv4AutomaticMetric => RepairField.AutomaticMetric,
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes => RepairField.IgnoreDefaultRoutes,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No field is defined for this action."),
    };

    public static bool IsForward(RepairActionKind action) => action != RepairActionKind.RecoverTransaction;

    /// <summary>Stable action id, so dependencies and the plan digest do not depend on list order.</summary>
    public static string ActionIdOf(RepairActionKind action) =>
        "act:" + WireNames.Of(action);

    /// <summary>Stable display/order rank: interface metric first, automatic metric second, ignore last.</summary>
    public static int OrderOf(RepairActionKind action) => action switch
    {
        RepairActionKind.SetTunIpv4InterfaceMetric => 0,
        RepairActionKind.SetTunIpv4AutomaticMetric => 1,
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes => 2,
        _ => 3,
    };
}

/// <summary>Bounded values shared by plan, preflight and future broker frames.</summary>
public static class RepairLimits
{
    /// <summary>Design 05: at most 16 actions in one transaction.</summary>
    public const int MaxActionsPerPlan = 16;

    /// <summary>Plans are short-lived; authorization must be re-previewed after expiry.</summary>
    public static TimeSpan MaxPlanTtl { get; } = TimeSpan.FromMinutes(10);

    public static TimeSpan DefaultPlanTtl { get; } = TimeSpan.FromMinutes(10);

    /// <summary>How old a preflight fact may be before it must be re-collected.</summary>
    public static TimeSpan DefaultFactTtl { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Tolerance for a fact whose UTC timestamp is slightly ahead of our wall clock.</summary>
    public static TimeSpan ClockSkewTolerance { get; } = TimeSpan.FromSeconds(5);
}
