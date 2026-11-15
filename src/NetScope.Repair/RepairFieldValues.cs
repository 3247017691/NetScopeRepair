using System.Globalization;
using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// A typed field value. The concrete type is the only thing that can become a desired value:
/// strings from a UI, a diagnosis or an imported package never reach a setter directly.
/// </summary>
public abstract record RepairFieldValue
{
    [JsonIgnore]
    public abstract RepairField Field { get; }

    /// <summary>Stable text used by the plan digest and by before/desired display.</summary>
    public abstract string CanonicalText { get; }
}

/// <summary>
/// Raw native metric. Windows exposes it as an unsigned 32-bit value, so the raw range is kept
/// instead of being truncated through <see cref="int"/>; the kind records whether this is the
/// manually configured value or a value Windows derived from AutomaticMetric.
/// </summary>
public sealed record InterfaceMetricValue : RepairFieldValue
{
    public required uint RawValue { get; init; }

    public required MetricValueKind Kind { get; init; }

    public override RepairField Field => RepairField.InterfaceMetric;

    public override string CanonicalText =>
        "interface_metric:uint:" + RawValue.ToString(CultureInfo.InvariantCulture) + ":" + WireNames.Of(Kind);
}

public sealed record AutomaticMetricValue : RepairFieldValue
{
    /// <summary>True when Windows computes the metric automatically; false when the manual metric applies.</summary>
    public required bool Enabled { get; init; }

    public override RepairField Field => RepairField.AutomaticMetric;

    public override string CanonicalText => "automatic_metric:bool:" + (Enabled ? "enabled" : "disabled");
}

public sealed record IgnoreDefaultRoutesValue : RepairFieldValue
{
    /// <summary>True when the interface refuses to install default routes.</summary>
    public required bool Ignore { get; init; }

    public override RepairField Field => RepairField.IgnoreDefaultRoutes;

    public override string CanonicalText => "ignore_default_routes:bool:" + (Ignore ? "true" : "false");
}

/// <summary>
/// The exact comparison scope for a no-op decision: the same interface GUID, family, store and
/// field. A value that merely looks equal in another store, family or interface is not a no-op.
/// </summary>
public readonly record struct RepairFieldKey(
    Guid InterfaceGuid,
    RepairAddressFamily Family,
    RepairStore Store,
    RepairField Field)
{
    public string CanonicalText =>
        InterfaceGuid.ToString("N") + "|" + WireNames.Of(Family) + "|" + WireNames.Of(Store) + "|" + WireNames.Of(Field);
}

public static class RepairFieldKeys
{
    public static RepairFieldKey For(InterfaceIdentity identity, RepairStore store, RepairField field) =>
        new(identity.InterfaceGuid, identity.Family, store, field);
}

/// <summary>
/// Strict, closed-set parser for the untrusted text that arrives from an IPC frame, a plan
/// request or a UI field. Unknown actions, generic property writes, sign characters and
/// overflow are rejected instead of guessed.
/// </summary>
public static class RepairActionParser
{
    private static readonly Dictionary<string, RepairActionKind> KindByName = new(StringComparer.Ordinal)
    {
        ["set_tun_ipv4_interface_metric"] = RepairActionKind.SetTunIpv4InterfaceMetric,
        ["set_tun_ipv4_automatic_metric"] = RepairActionKind.SetTunIpv4AutomaticMetric,
        ["set_tun_ipv4_ignore_default_routes"] = RepairActionKind.SetTunIpv4IgnoreDefaultRoutes,
        ["recover_transaction"] = RepairActionKind.RecoverTransaction,
    };

    private static readonly Dictionary<string, RepairStore> StoreByName = new(StringComparer.Ordinal)
    {
        ["active_store"] = RepairStore.ActiveStore,
        ["persistent_store"] = RepairStore.PersistentStore,
    };

    public static bool TryParseKind(string? text, out RepairActionKind kind) =>
        KindByName.TryGetValue(text ?? string.Empty, out kind);

    public static bool TryParseStore(string? text, out RepairStore store) =>
        StoreByName.TryGetValue(text ?? string.Empty, out store);

    /// <summary>Parses the desired value for one action into its typed value.</summary>
    public static bool TryParseDesired(
        RepairActionKind action,
        string? text,
        out RepairFieldValue? value,
        out string? error)
    {
        value = null;
        error = null;

        switch (action)
        {
            case RepairActionKind.SetTunIpv4InterfaceMetric:
                if (text is null
                    || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var raw))
                {
                    error = "InterfaceMetric 必须是 0..4294967295 的十进制原始 uint 值，不接受符号、空白、十六进制或截断的 int。";
                    return false;
                }

                value = new InterfaceMetricValue { RawValue = raw, Kind = MetricValueKind.ManualConfigured };
                return true;

            case RepairActionKind.SetTunIpv4AutomaticMetric:
                if (string.Equals(text, "enabled", StringComparison.Ordinal))
                {
                    value = new AutomaticMetricValue { Enabled = true };
                    return true;
                }

                if (string.Equals(text, "disabled", StringComparison.Ordinal))
                {
                    value = new AutomaticMetricValue { Enabled = false };
                    return true;
                }

                error = "AutomaticMetric 只接受 enabled 或 disabled。";
                return false;

            case RepairActionKind.SetTunIpv4IgnoreDefaultRoutes:
                if (string.Equals(text, "true", StringComparison.Ordinal))
                {
                    value = new IgnoreDefaultRoutesValue { Ignore = true };
                    return true;
                }

                if (string.Equals(text, "false", StringComparison.Ordinal))
                {
                    value = new IgnoreDefaultRoutesValue { Ignore = false };
                    return true;
                }

                error = "IgnoreDefaultRoutes 只接受 true 或 false。";
                return false;

            case RepairActionKind.RecoverTransaction:
                error = "RecoverTransaction 不接受调用方指定的期望值，只能由 broker 按权威 journal 恢复流程使用。";
                return false;

            default:
                error = "未定义的动作。";
                return false;
        }
    }
}

/// <summary>Why a typed comparison did or did not count as a no-op.</summary>
public sealed record RepairNoOpAssessment
{
    public required bool IsNoOp { get; init; }

    public required string ReasonCode { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Field-by-field no-op comparison under the exact identity + family + store + field key.
/// Derived metric values never establish a no-op because they cannot be restored or compared
/// as if they were the manually configured value.
/// </summary>
public static class RepairNoOpEvaluator
{
    public static RepairNoOpAssessment Assess(
        RepairFieldKey key,
        RepairFieldKey observedKey,
        RepairFieldValue before,
        RepairFieldValue desired)
    {
        if (key.InterfaceGuid != observedKey.InterfaceGuid)
        {
            return Not("identity_mismatch", "接口 GUID 不同，不能按同一目标做 no-op 比较。");
        }

        if (key.Family != observedKey.Family)
        {
            return Not("family_mismatch", "地址族不同，IPv4 值不能与 IPv6 值互相抵消。");
        }

        if (key.Store != observedKey.Store)
        {
            return Not("store_mismatch", "ActiveStore 与 PersistentStore 是不同目标，不能跨 store 做 no-op 比较。");
        }

        if (key.Field != observedKey.Field)
        {
            return Not("field_mismatch", "字段不同，不能互相做 no-op 比较。");
        }

        if (before is InterfaceMetricValue observedMetric)
        {
            if (observedMetric.Kind != MetricValueKind.ManualConfigured)
            {
                return Not("value_not_manual", "当前 metric 是有效派生值，不能作为 no-op 依据或恢复基线。");
            }

            if (desired is not InterfaceMetricValue desiredMetric)
            {
                return Not("value_type_mismatch", "期望值类型与字段不匹配。");
            }

            return observedMetric.RawValue == desiredMetric.RawValue
                ? Yes("字段值已等于期望值（手动配置的原始 uint 相同）。")
                : Not("value_differs", "字段当前值与期望值不同。");
        }

        if (before is AutomaticMetricValue observedAutomatic)
        {
            if (desired is not AutomaticMetricValue desiredAutomatic)
            {
                return Not("value_type_mismatch", "期望值类型与字段不匹配。");
            }

            return observedAutomatic.Enabled == desiredAutomatic.Enabled
                ? Yes("AutomaticMetric 当前已等于期望值。")
                : Not("value_differs", "AutomaticMetric 当前值与期望值不同。");
        }

        if (before is IgnoreDefaultRoutesValue observedIgnore)
        {
            if (desired is not IgnoreDefaultRoutesValue desiredIgnore)
            {
                return Not("value_type_mismatch", "期望值类型与字段不匹配。");
            }

            return observedIgnore.Ignore == desiredIgnore.Ignore
                ? Yes("IgnoreDefaultRoutes 当前已等于期望值。")
                : Not("value_differs", "IgnoreDefaultRoutes 当前值与期望值不同。");
        }

        return Not("value_unknown", "未知的字段值类型。");
    }

    private static RepairNoOpAssessment Yes(string reason) =>
        new() { IsNoOp = true, ReasonCode = "no_op", Reason = reason };

    private static RepairNoOpAssessment Not(string code, string reason) =>
        new() { IsNoOp = false, ReasonCode = code, Reason = reason };
}
