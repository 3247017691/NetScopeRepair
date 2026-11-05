using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>DX-001: only a proven failed connection to the selected local proxy supports this claim.</summary>
public sealed class Dx001DeadProxyDependency : DiagnosticRuleBase
{
    public override string RuleId => "DX-001";

    public override string Version => "1.1.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        var explicitProbes = evidence.Probes.Where(probe => probe.Path == ProbePath.ExplicitHttpProxy).ToArray();
        var first = explicitProbes.FirstOrDefault(IsLocalProxyConnectionFailure);
        if (first is not null)
        {
            var failures = explicitProbes.Where(probe => IsLocalProxyConnectionFailure(probe)
                && (ReferenceEquals(probe, first) || DiagnosticComparability.SameProbeScope(first, probe))).ToArray();
            var successes = explicitProbes.Where(probe => probe.Status == EventStatus.Ok
                && DiagnosticComparability.SameProbeScope(first, probe)).ToArray();
            var level = successes.Length > 0 ? EvidenceGrade.Candidate : EvidenceGrade.Supported;
            return Create(
                level,
                $"请求实际选用的本地代理端点 {first.ProxyEndpointId} 在代理连接阶段被拒绝或超时。"
                + (level == EvidenceGrade.Supported
                    ? "这支持该次请求被不可用的本地代理阻断；结论仅适用于此端点和该次请求。"
                    : "可比观察窗内该端点也有成功样本，因此持续失效只能列为候选。")
                + "不能由此判定目标接口不可达或推广到其他应用。",
                failures.Select(probe => probe.ProbeId).ToArray(),
                counterEvidenceIds: successes.Select(probe => probe.ProbeId).ToArray(),
                missingEvidence: new[] { "direct_connection_to_remote_endpoint", "proxy_process_identity", "application_effective_proxy" },
                suggestedNextStep: "只读核对该代理端点的进程与监听状态，并保留此请求的失败阶段。",
                scope: DiagnosticComparability.Scope(failures.Concat(successes)));
        }

        // TLS, permission, authentication and upstream CONNECT failures say nothing about local availability.
        var unproven = explicitProbes.FirstOrDefault(probe => IsRefusedOrTimedOutConnect(probe)
            && probe.ProxyIsLocal != false && probe.FailedEndpoint != "target");
        if (unproven is not null)
        {
            return Create(
                EvidenceGrade.Candidate,
                "显式代理路径出现连接拒绝或超时，但缺少选中本地代理端点及失败归属的证明，不能确认本地代理失效。",
                new[] { unproven.ProbeId },
                missingEvidence: new[] { "selected_proxy_endpoint_identity", "proven_local_proxy", "proven_proxy_connection_failure" },
                suggestedNextStep: "补充实际选中的代理端点及代理连接阶段证据。",
                scope: ScopeOf(unproven));
        }

        if (explicitProbes.Length > 0)
        {
            return null;
        }

        var configured = ProxyLayers(evidence).Where(entry => entry.Layer.Enabled == true
            || entry.Layer.ProxyServer is not null || entry.Layer.Variables.Count > 0).ToArray();
        if (configured.Length == 0 || evidence.Snapshot is null)
        {
            return null;
        }

        return Create(
            EvidenceGrade.Candidate,
            "代理配置存在于配置层（" + string.Join("、", configured.Select(layer => layer.Name))
            + "），但缺少请求实际采用端点的证据，只能列为候选线索。",
            new[] { evidence.Snapshot.SnapshotId },
            missingEvidence: new[] { "probe_on_explicit_proxy_path", "application_effective_proxy" },
            suggestedNextStep: "在用户主动启动的探测中确认实际使用的端点与连接结果。",
            scope: new DiagnosisScope
            {
                StartedAt = evidence.Snapshot.ObservedAt,
                EndedAt = evidence.Snapshot.ObservedAt,
                ValidUntil = evidence.Snapshot.ObservedAt,
            });
    }

    private static bool IsLocalProxyConnectionFailure(ProbeResult probe) =>
        DiagnosticComparability.Present(probe.ProxyEndpointId) && probe.ProxyIsLocal == true
        && probe.FailedEndpoint == "proxy" && IsRefusedOrTimedOutConnect(probe);

    private static bool IsRefusedOrTimedOutConnect(ProbeResult probe)
    {
        if (probe.Status is not (EventStatus.Failed or EventStatus.Timeout) || probe.FailedStage != "connect"
            || probe.Classification != ProbeClassification.TransportFailure)
        {
            return false;
        }

        var error = probe.TransportError?.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return error is "connectionrefused" or "connectiontimedout" or "timedout" or "timeout" or "connecttimeout"
            or "connectiontimeout" || (string.IsNullOrEmpty(error) && probe.OsErrorCode is 10060 or 10061);
    }
}
