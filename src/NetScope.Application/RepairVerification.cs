using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Application;

/// <summary>One field delta the authorized plan allows. Any change outside this set is a failure.</summary>
public sealed record RepairVerificationDelta
{
    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required string BeforeText { get; init; }

    public required string DesiredText { get; init; }

    public string Key => WireNames.Of(Field) + "|" + WireNames.Of(Store);
}

/// <summary>
/// Before/after facts used for technical verification. Method/options/family/target/route are part
/// of comparability: a changed target or route invalidates the planned delta even if the field
/// values happen to match.
/// </summary>
public sealed record RepairVerificationSnapshot
{
    public required string TargetFingerprint { get; init; }

    public required string Method { get; init; }

    public required string AddressFamily { get; init; }

    public required string OptionsKey { get; init; }

    /// <summary>Interface fingerprint of the route actually selected for the target.</summary>
    public string? RouteInterfaceFingerprint { get; init; }

    public required bool IsProtectedUplink { get; init; }

    public required IReadOnlyDictionary<string, string> FieldValues { get; init; }
}

public sealed record RepairVerificationCheck
{
    public required string Name { get; init; }

    public required bool Passed { get; init; }

    public required string Detail { get; init; }
}

public enum RepairVerificationStatus
{
    Passed,
    Failed,
    Insufficient,
}

public sealed record RepairVerificationResult
{
    public required RepairVerificationStatus Status { get; init; }

    public required IReadOnlyList<RepairVerificationCheck> Checks { get; init; }

    public required IReadOnlyList<string> Refusals { get; init; }

    public required string SummaryText { get; init; }
}

/// <summary>
/// Verifies only the deltas the plan allows, plus the unchanged dimensions: target identity,
/// method, address family, request options, selected route/non-target fields. It never broadens
/// the write set and never treats a missing observation as success.
/// </summary>
public static class RepairVerifier
{
    public static RepairVerificationResult Verify(
        IReadOnlyList<RepairVerificationDelta> allowedDeltas,
        RepairVerificationSnapshot? before,
        RepairVerificationSnapshot? after)
    {
        ArgumentNullException.ThrowIfNull(allowedDeltas);
        var checks = new List<RepairVerificationCheck>();
        var refusals = new List<string>();

        void Check(string name, bool passed, string detail)
        {
            checks.Add(new RepairVerificationCheck { Name = name, Passed = passed, Detail = detail });
            if (!passed) refusals.Add(name + ": " + detail);
        }

        if (before is null || after is null)
        {
            Check("observation_complete", false, "缺少修复前或修复后的观测事实。");
            return Result(RepairVerificationStatus.Insufficient, checks, refusals);
        }

        Check("target_unchanged",
            string.Equals(before.TargetFingerprint, after.TargetFingerprint, StringComparison.Ordinal),
            "目标指纹 before=" + before.TargetFingerprint + " after=" + after.TargetFingerprint);
        Check("method_unchanged",
            string.Equals(before.Method, after.Method, StringComparison.OrdinalIgnoreCase),
            "方法 before=" + before.Method + " after=" + after.Method);
        Check("family_unchanged",
            string.Equals(before.AddressFamily, after.AddressFamily, StringComparison.OrdinalIgnoreCase),
            "地址族 before=" + before.AddressFamily + " after=" + after.AddressFamily);
        Check("options_unchanged",
            string.Equals(before.OptionsKey, after.OptionsKey, StringComparison.Ordinal),
            "请求 options before=" + before.OptionsKey + " after=" + after.OptionsKey);
        Check("route_unchanged",
            string.Equals(before.RouteInterfaceFingerprint, after.RouteInterfaceFingerprint, StringComparison.Ordinal),
            "实际选路接口 before=" + (before.RouteInterfaceFingerprint ?? "未记录")
            + " after=" + (after.RouteInterfaceFingerprint ?? "未记录"));
        Check("protected_uplink_not_written",
            !before.IsProtectedUplink && !after.IsProtectedUplink,
            "protected uplink before=" + before.IsProtectedUplink + " after=" + after.IsProtectedUplink);

        var allowed = new HashSet<string>(allowedDeltas.Select(delta => delta.Key), StringComparer.Ordinal);
        foreach (var delta in allowedDeltas)
        {
            var beforeOk = before.FieldValues.TryGetValue(delta.Key, out var beforeValue)
                && string.Equals(beforeValue, delta.BeforeText, StringComparison.Ordinal);
            var afterOk = after.FieldValues.TryGetValue(delta.Key, out var afterValue)
                && string.Equals(afterValue, delta.DesiredText, StringComparison.Ordinal);
            Check("delta:" + delta.Key,
                beforeOk && afterOk,
                "允许的 delta " + delta.Key + " before=" + (before.FieldValues.TryGetValue(delta.Key, out var b) ? b : "缺失")
                + " after=" + (after.FieldValues.TryGetValue(delta.Key, out var a) ? a : "缺失")
                + "，期望 " + delta.BeforeText + "→" + delta.DesiredText);
        }

        var allKeys = before.FieldValues.Keys.Concat(after.FieldValues.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var nonTargetChanges = 0;
        foreach (var key in allKeys)
        {
            var beforeHas = before.FieldValues.TryGetValue(key, out var beforeValue);
            var afterHas = after.FieldValues.TryGetValue(key, out var afterValue);
            var changed = beforeHas != afterHas
                || !string.Equals(beforeValue, afterValue, StringComparison.Ordinal);
            if (!changed) continue;
            if (allowed.Contains(key)) continue;
            nonTargetChanges++;
            Check("non_target_unchanged:" + key, false,
                "非计划字段发生变化：" + key + " " + (beforeHas ? beforeValue : "缺失")
                + "→" + (afterHas ? afterValue : "缺失"));
        }

        if (nonTargetChanges == 0)
        {
            Check("non_target_unchanged", true, "非计划字段没有变化。");
        }

        var status = refusals.Count == 0
            ? RepairVerificationStatus.Passed
            : checks.Any(check => check.Name == "observation_complete" && !check.Passed)
                ? RepairVerificationStatus.Insufficient
                : RepairVerificationStatus.Failed;
        return Result(status, checks, refusals);
    }

    private static RepairVerificationResult Result(
        RepairVerificationStatus status,
        List<RepairVerificationCheck> checks,
        List<string> refusals) => new()
    {
        Status = status,
        Checks = checks.ToArray(),
        Refusals = refusals.Distinct(StringComparer.Ordinal).ToArray(),
        SummaryText = status switch
        {
            RepairVerificationStatus.Passed => "计划允许的 delta 全部成立，目标/方法/族/options/路由与非目标字段未变。",
            RepairVerificationStatus.Insufficient => "观测事实不足，不能判定技术验证通过。",
            _ => "技术验证失败：" + string.Join("；", refusals.Take(4)),
        },
    };
}

public enum DiagnosticComparabilityStatus
{
    Comparable,
    Incomparable,
    Insufficient,
}

public sealed record DiagnosticComparabilityResult
{
    public required DiagnosticComparabilityStatus Status { get; init; }

    public required IReadOnlyList<string> Unmet { get; init; }

    public required string Text { get; init; }

    public bool Comparable => Status == DiagnosticComparabilityStatus.Comparable;
}

/// <summary>
/// Repair before/after comparability gate. It rejects TopologyChanged instead of clearing the
/// flag: a topology change makes the two probes non-comparable, so no repair effect can be
/// claimed from them. The input ProbeResult instances are immutable and are never modified.
/// </summary>
public static class DiagnosticComparabilityGate
{
    public static DiagnosticComparabilityResult Evaluate(ProbeResult? before, ProbeResult? after)
    {
        var unmet = new List<string>();
        if (before is null || after is null)
        {
            return new DiagnosticComparabilityResult
            {
                Status = DiagnosticComparabilityStatus.Insufficient,
                Unmet = new[] { "missing_probe" },
                Text = "缺少修复前或修复后的探测结果，不能比较。",
            };
        }

        if (before.TopologyChanged || after.TopologyChanged)
            unmet.Add("topology_changed");
        if (!string.Equals(before.TargetId, after.TargetId, StringComparison.Ordinal))
            unmet.Add("target_changed");
        if (!string.Equals(before.Method, after.Method, StringComparison.OrdinalIgnoreCase))
            unmet.Add("method_changed");
        if (!string.Equals(before.AddressFamily, after.AddressFamily, StringComparison.OrdinalIgnoreCase))
            unmet.Add("family_changed");
        if (!string.Equals(before.RequestOptionsKey, after.RequestOptionsKey, StringComparison.Ordinal))
            unmet.Add("options_changed");
        if (!before.TimingComparable || !after.TimingComparable)
            unmet.Add("timing_not_comparable");
        if (!string.Equals(before.ProbeGroupId, after.ProbeGroupId, StringComparison.Ordinal)
            && string.IsNullOrEmpty(before.ObservationWindowId))
            unmet.Add("window_not_recorded");

        if (unmet.Count == 0)
        {
            return new DiagnosticComparabilityResult
            {
                Status = DiagnosticComparabilityStatus.Comparable,
                Unmet = Array.Empty<string>(),
                Text = "目标/方法/族/options/窗口一致，且生产方确认时间可比；不修改任何探测标志。",
            };
        }

        return new DiagnosticComparabilityResult
        {
            Status = DiagnosticComparabilityStatus.Incomparable,
            Unmet = unmet.Distinct(StringComparer.Ordinal).ToArray(),
            Text = "不可比：" + string.Join("；", unmet.Distinct(StringComparer.Ordinal))
                + "。保留 TopologyChanged 原值，不清零标志来制造可比。",
        };
    }
}

