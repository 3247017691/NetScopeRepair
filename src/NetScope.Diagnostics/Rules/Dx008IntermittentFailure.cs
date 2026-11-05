using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>DX-008: count network outcomes only within one coherent, bounded observation scope.</summary>
public sealed class Dx008IntermittentFailure : DiagnosticRuleBase
{
    public override string RuleId => "DX-008";

    public override string Version => "1.1.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        // Missing fields stay missing; different known endpoints, families, methods, boots or windows never merge.
        var partitions = evidence.Probes.GroupBy(probe => new
        {
            probe.TargetId,
            probe.Path,
            probe.ProxyEndpointId,
            probe.AddressFamily,
            probe.Method,
            probe.RequestOptionsKey,
            probe.ObservationWindowId,
            probe.BootId,
        });

        foreach (var partition in partitions)
        {
            var ordered = partition.OrderBy(probe => probe.StartedAt).ToArray();
            // Even a reused window ID cannot join observations separated by days.
            foreach (var items in BoundedWindows(ordered))
            {
                var successes = items.Where(IsNetworkSuccess).ToArray();
                var failures = items.Where(IsNetworkFailure).ToArray();
                if (successes.Length == 0 || failures.Length == 0)
                {
                    continue;
                }

                var gaps = items.Except(successes).Except(failures).ToArray();
                var measured = successes.Concat(failures).ToArray();
                // A gap's TimingComparable=false describes the missing observation, not a different
                // request scope. Keep it in the evidence/counts; only actual network outcomes can
                // establish the comparison. Any topology change in the window still weakens it.
                var comparable = !items.Any(probe => probe.TopologyChanged)
                    && measured.All(probe => DiagnosticComparability.SameProbeScope(measured[0], probe));
                var missing = new List<string>();
                if (!comparable)
                {
                    missing.Add("comparable_samples");
                    if (items.Any(probe => probe.TopologyChanged))
                    {
                        missing.Add("topology_unchanged_window");
                    }
                }
                else
                {
                    missing.Add("failure_trigger_correlation");
                }

                var gapReasons = gaps.Length == 0 ? "" : "；观察缺口 " + gaps.Length + "/" + items.Length
                    + "（" + string.Join("、", gaps.Select(probe => probe.TransportError ?? WireNames.Of(probe.Status)).Distinct()) + "）";
                return Create(
                    comparable ? EvidenceGrade.Observed : EvidenceGrade.Candidate,
                    (comparable ? "同一可比观察窗内" : "样本可比性尚未证实，不能确认间歇网络故障；候选样本中")
                    + $"{items[0].TargetId}/{WireNames.Of(items[0].Path)} 有 {failures.Length}/{successes.Length + failures.Length} 次有效网络失败"
                    + gapReasons + "。之后的成功不能当作修复证据，也不能据此推断其他地址族或全网状态。",
                    items.Select(probe => probe.ProbeId).ToArray(),
                    counterEvidenceIds: items.Where(probe => probe.TopologyChanged).Select(probe => probe.ProbeId).ToArray(),
                    missingEvidence: missing,
                    suggestedNextStep: "继续采集相同端点、方法、地址族与请求选项的可比样本，并分别记录网络结果和观察缺口。",
                    scope: DiagnosticComparability.Scope(items));
            }
        }

        return null;
    }

    private static IEnumerable<ProbeResult[]> BoundedWindows(ProbeResult[] ordered)
    {
        var remaining = ordered.AsEnumerable();
        while (remaining.Any())
        {
            var first = remaining.First();
            var items = remaining.TakeWhile(probe => (probe.StartedAt - first.StartedAt).TotalMilliseconds
                <= DiagnosticComparability.MaximumWindowMs).ToArray();
            yield return items;
            remaining = remaining.Skip(items.Length);
        }
    }

    private static bool IsNetworkSuccess(ProbeResult probe) => probe.Status == EventStatus.Ok
        && probe.Classification is ProbeClassification.ReachableExpected or ProbeClassification.ReachableUnexpected;

    private static bool IsNetworkFailure(ProbeResult probe) =>
        probe.Status is EventStatus.Failed or EventStatus.Timeout
        && probe.Classification == ProbeClassification.TransportFailure
        && probe.FailedStage != "not_started"
        && probe.OsErrorCode is not (5 or 13 or 10013)
        && !IsGapReason(probe.TransportError);

    private static bool IsGapReason(string? reason)
    {
        var normalized = reason?.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return normalized is not null
            && (normalized.Contains("budget", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("accessdenied", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("notstarted", StringComparison.OrdinalIgnoreCase));
    }
}
