using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>DX-011: only a contemporaneous connection to the sampled endpoint can contradict its list.</summary>
public sealed class Dx011CollectionContradiction : DiagnosticRuleBase
{
    public override string RuleId => "DX-011";

    public override string Version => "1.1.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        foreach (var listening in evidence.ByKind("listening_ports").Where(IsEmptyOrIncomplete))
        {
            var matches = evidence.Probes.Where(probe => probe.Path == ProbePath.ExplicitHttpProxy
                && probe.Status == EventStatus.Ok && probe.ProxyIsLocal == true
                && DiagnosticComparability.Same(listening.ProxyEndpointId, probe.ProxyEndpointId)
                && DiagnosticComparability.SameRecordProbeWindow(listening, probe)
                && !probe.TopologyChanged).ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            return Create(
                EvidenceGrade.Insufficient,
                "监听列表为空或读取受限，但同一地址族、同一代理端点的同时段实际连接成功。列表样本不足或存在采样竞态，不能据此报告端口未监听或停止客户端。",
                new[] { listening.EvidenceId }.Concat(matches.Select(probe => probe.ProbeId)).ToArray(),
                counterEvidenceIds: matches.Select(probe => probe.ProbeId).ToArray(),
                missingEvidence: new[] { "repeated_listening_sample", "process_ownership" },
                suggestedNextStep: "补采该端点的监听列表，保留采样时间和缺口原因。",
                scope: DiagnosticComparability.Scope(matches));
        }

        return null;
    }

    private static bool IsEmptyOrIncomplete(EvidenceRecord record) =>
        !HasFact(record, "count") || Fact(record, "count") == "0"
        || Fact(record, "sample_state") is "empty" or "access_denied" or "partial" or "timeout"
        || record.Status is EventStatus.Partial or EventStatus.AccessDenied or EventStatus.Timeout;
}
