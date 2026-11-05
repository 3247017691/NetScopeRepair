using System.Globalization;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>DX-002: a reserved answer needs trustworthy running-mode context before it is explained as fake-IP.</summary>
public sealed class Dx002FakeIpContext : DiagnosticRuleBase
{
    public override string RuleId => "DX-002";

    public override string Version => "1.1.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        var answers = evidence.Probes.Where(probe => probe.ResolvedAddresses.Any(IpRangeClassifier.IsFakeIpAddress)).ToArray();
        var routes = evidence.Routes.Where(IsReservedRoute).ToArray();
        var reservedServer = evidence.Snapshot?.Dns.Value?.ServersByInterface.Values.SelectMany(servers => servers)
            .Any(IpRangeClassifier.IsFakeIpAddress) == true;
        if (answers.Length == 0 && routes.Length == 0 && !reservedServer)
        {
            return null;
        }

        foreach (var probe in answers)
        {
            var runtime = evidence.ByKind("client_runtime_config").Where(record =>
                DiagnosticComparability.TrustedRuntime(record) && Fact(record, "running") == "true"
                && DiagnosticComparability.Present(Fact(record, "client"))
                && DiagnosticComparability.Same(record.TargetId, probe.TargetId)
                && DiagnosticComparability.SameRecordProbeWindow(record, probe)).ToArray();
            var contrary = runtime.Where(record => DiagnosticComparability.Present(Fact(record, "dns_mode"))
                && Fact(record, "dns_mode") is not ("fake-ip" or "unknown")).ToArray();
            var supporting = runtime.Where(record => Fact(record, "dns_mode") == "fake-ip").ToArray();
            var adapters = evidence.ByKind("fake_ip_adapter").Where(record => IsTrustworthyAdapter(record, probe, evidence)).ToArray();
            if (contrary.Length > 0)
            {
                return Create(
                    EvidenceGrade.Candidate,
                    "观测到保留段 DNS 返回地址，但同一作用域的有效运行态明确使用非 fake-IP 模式，不能把该地址解释为已确认的正常 fake-IP 映射，也不能直接称为污染。",
                    new[] { probe.ProbeId },
                    counterEvidenceIds: contrary.Select(record => record.EvidenceId).ToArray(),
                    missingEvidence: new[] { "consistent_running_fake_ip_mode", "reserved_answer_origin" },
                    suggestedNextStep: "只读核对返回地址与运行态模式的来源及其关联。",
                    scope: ScopeOf(probe));
            }

            if (supporting.Length + adapters.Length == 0 || probe.AddressFamily != "ipv4" || !DiagnosticComparability.Complete(probe)
                || !probe.TimingComparable || probe.TopologyChanged
                || probe.Status is not (EventStatus.Ok or EventStatus.Failed or EventStatus.Timeout))
            {
                continue;
            }

            return Create(
                EvidenceGrade.Supported,
                "在同一目标、地址族和观察时段内取得 198.18.0.0/15 保留段 DNS 返回地址，并有可信的运行态 fake-IP 配置或适配器证据。"
                + "这支持 fake-IP 映射上下文，本身不是污染证据；结论仅适用于该样本。",
                new[] { probe.ProbeId }.Concat(supporting.Select(record => record.EvidenceId))
                    .Concat(adapters.Select(record => record.EvidenceId))
                    .Concat(adapters.Length > 0 ? new[] { evidence.Snapshot!.SnapshotId } : Array.Empty<string>()).ToArray(),
                missingEvidence: new[] { "compare_physical_dns_answer" },
                suggestedNextStep: "如需解释实际连接失败，继续核对该目标的请求阶段与运行态路径证据。",
                scope: ScopeOf(probe));
        }

        var ids = answers.Select(probe => probe.ProbeId).ToList();
        if (evidence.Snapshot is not null && (routes.Length > 0 || reservedServer))
        {
            ids.Add(evidence.Snapshot.SnapshotId);
        }

        return Create(
            EvidenceGrade.Candidate,
            "观测到保留段解析、路由或 DNS 服务器线索，但尚未同时证明保留段 DNS 返回地址与可信运行态 fake-IP 模式。"
            + "只能记为未知保留段用途，不能直接称为污染。",
            ids,
            missingEvidence: new[] { "reserved_dns_answer", "effective_fake_ip_configuration", "comparable_runtime_context" },
            suggestedNextStep: "补充实际 DNS 返回地址、有效运行态模式和同一观察窗的来源元数据。",
            scope: answers.Length > 0 ? ScopeOf(answers[0]) : new DiagnosisScope
            {
                AddressFamily = "ipv4",
                StartedAt = evidence.Snapshot?.ObservedAt,
                EndedAt = evidence.Snapshot?.ObservedAt,
                ValidUntil = evidence.Snapshot?.ObservedAt,
            });
    }

    private static bool IsReservedRoute(RouteEntry route) => route.Family == "ipv4"
        && IpRange.TryParse(route.DestinationPrefix, out var range) && range is not null
        && range.PrefixLength >= IpRangeClassifier.FakeIpRange.PrefixLength
        && IpRangeClassifier.IsFakeIpAddress(new System.Net.IPAddress(range.Network).ToString());

    private static bool IsTrustworthyAdapter(EvidenceRecord record, ProbeResult probe, EvidenceSet evidence)
    {
        var snapshot = evidence.Snapshot;
        if (snapshot is null || snapshot.Interfaces.Status != EventStatus.Ok
            || !DiagnosticComparability.Trusted(record) || record.Provenance != "adapter_runtime"
            || Fact(record, "running") != "true" || Fact(record, "fake_ip") != "true"
            || !DiagnosticComparability.Same(record.TargetId, probe.TargetId)
            || !DiagnosticComparability.SameRecordProbeWindow(record, probe)
            || Fact(record, "snapshot_id") != snapshot.SnapshotId
            || !DiagnosticComparability.Close(snapshot.ObservedAt, snapshot.MonotonicMs,
                probe.StartedAt, probe.StartedMonotonicMs, DiagnosticComparability.ContemporaneousMs)
            || !int.TryParse(Fact(record, "if_index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return false;
        }

        return evidence.Interfaces.Any(item => item.IfIndex == index && item.Kind == "tunnel"
            && item.OperationalStatus == "up" && DiagnosticComparability.Present(item.InterfaceGuid));
    }
}
