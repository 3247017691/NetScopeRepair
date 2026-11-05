using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>
/// DX-010 路由修复 no-op.
/// <para>
/// Compares a proposed change with the current state. When the target values are already in
/// place the rule contradicts the story "changing these values fixed the outage", because
/// applying them would change nothing.
/// </para>
/// </summary>
public sealed class Dx010RoutingNoOp : DiagnosticRuleBase
{
    public override string RuleId => "DX-010";

    public override string Version => "1.0.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        if (evidence.ProposedChanges.Count == 0 || evidence.Interfaces.Count == 0)
        {
            return null;
        }

        var alreadyAtTarget = new List<string>();
        var evidenceIds = new List<string>();

        foreach (var change in evidence.ProposedChanges)
        {
            var target = evidence.Interfaces.FirstOrDefault(item =>
                string.Equals(item.InterfaceGuid, change.TargetInterfaceGuid, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                continue;
            }

            var current = CurrentValue(target, change.Field);
            if (current is null)
            {
                continue;
            }

            if (!string.Equals(current, change.DesiredValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            alreadyAtTarget.Add($"{target.Name} 的 {change.Field} 当前已是 {current}");
            evidenceIds.Add(evidence.Snapshot!.SnapshotId);
        }

        if (alreadyAtTarget.Count == 0)
        {
            return null;
        }

        var allNoOp = alreadyAtTarget.Count == evidence.ProposedChanges.Count;

        return Create(
            allNoOp ? EvidenceGrade.Contradicted : EvidenceGrade.Observed,
            "拟议变更与当前值相同：" + string.Join("；", alreadyAtTarget) + "。"
            + "执行这些字段只会是 no-op，不能用“改了这些值之后恢复”来解释本次故障。",
            evidenceIds: evidenceIds,
            counterEvidenceIds: Array.Empty<string>(),
            missingEvidence: new[] { "before_change_sample", "change_time_correlation" },
            suggestedNextStep: "记录变更前后同一路径的可比探测结果，再讨论归因；不要为了“做点什么”而重复写入相同值。",
            scope: new DiagnosisScope { ValidUntil = evidence.Snapshot?.ObservedAt });
    }

    private static string? CurrentValue(InterfaceState target, string field) => field switch
    {
        "interface_metric" => target.Metric?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "automatic_metric" => target.AutomaticMetric?.ToString(),
        "disable_default_routes" => target.DisableDefaultRoutes?.ToString(),
        "if_index" => target.IfIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };
}
