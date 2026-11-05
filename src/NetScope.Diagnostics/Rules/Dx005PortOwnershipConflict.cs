using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>DX-005: a port conflict needs a contention error and contemporaneous endpoint ownership.</summary>
public sealed class Dx005PortOwnershipConflict : DiagnosticRuleBase
{
    public override string RuleId => "DX-005";

    public override string Version => "1.1.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        var bindFailures = evidence.ByKind("port_bind_failure");
        var ownership = evidence.ByKind("port_ownership");
        foreach (var failure in bindFailures)
        {
            var endpoint = DiagnosticComparability.NormalizeEndpoint(Fact(failure, "endpoint"));
            if (endpoint is null || !IsContention(Fact(failure, "error")) || !TrustedFailure(failure))
            {
                continue;
            }

            var family = DiagnosticComparability.EndpointFamily(endpoint);
            var owner = ownership.FirstOrDefault(record =>
                endpoint == DiagnosticComparability.NormalizeEndpoint(Fact(record, "endpoint"))
                && DiagnosticComparability.Present(Fact(record, "owner_process"))
                && DiagnosticComparability.Trusted(record)
                && failure.AddressFamily == family && record.AddressFamily == family
                && DiagnosticComparability.SameRecordWindow(failure, record));
            if (owner is null)
            {
                continue;
            }

            return Create(
                EvidenceGrade.Supported,
                $"同一观察时段内，端点 {endpoint} 出现实际地址争用 bind 失败，且其所有者为 {Fact(owner, "owner_process")}，支持端口归属冲突。",
                new[] { failure.EvidenceId, owner.EvidenceId },
                missingEvidence: new[] { "repeated_sample", "other_client_runtime_state" },
                suggestedNextStep: "只读复核此端点的所有者和两个客户端运行态，再由用户决定是否调整配置。",
                scope: DiagnosticComparability.Scope(failure));
        }

        if (bindFailures.Length > 0)
        {
            var failure = bindFailures[0];
            return Create(
                EvidenceGrade.Candidate,
                "记录了 bind 失败，但尚未同时证明可规范化端点、有效所有者、地址争用错误及时间关联，不能判定端口冲突。",
                new[] { failure.EvidenceId },
                missingEvidence: new[] { "normalized_endpoint", "contention_bind_error", "port_ownership", "contemporaneous_ownership", "listening_ports_sample" },
                suggestedNextStep: "补采相同地址端口在同一观察时段的实际所有者；权限失败应另行记录。",
                scope: DiagnosticComparability.Scope(failure));
        }

        var shared = evidence.ByKind("client_configuration")
            .Select(record => new
            {
                Record = record,
                Endpoint = DiagnosticComparability.NormalizeEndpoint(EndpointFromConfiguration(record)),
            })
            .Where(item => item.Endpoint is not null && DiagnosticComparability.Present(Fact(item.Record, "client")))
            .GroupBy(item => item.Endpoint, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(item => Fact(item.Record, "client")).Distinct(StringComparer.Ordinal).Count() >= 2);
        if (shared is null)
        {
            return null;
        }

        return Create(
            EvidenceGrade.Candidate,
            $"多个客户端配置声明同一地址端口 {shared.Key}，仅此不足以判定端口冲突。",
            shared.Select(item => item.Record.EvidenceId).ToArray(),
            missingEvidence: new[] { "actual_bind_failure", "port_ownership" },
            suggestedNextStep: "只读核对同一地址端口的实际所有者与连接结果。");
    }

    private static bool TrustedFailure(EvidenceRecord record) => !record.IsUserReport
        && record.Status is EventStatus.Ok or EventStatus.Failed
        && DiagnosticComparability.Present(record.Source) && DiagnosticComparability.Present(record.Provenance);

    private static bool IsContention(string error) => error.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant()
        is "addressinuse" or "addressalreadyinuse" or "wsaeaddrinuse" or "eaddrinuse" or "10048";

    private static string EndpointFromConfiguration(EvidenceRecord record)
    {
        var address = Fact(record, "address");
        return address.Contains(':', StringComparison.Ordinal) && !address.StartsWith('[')
            ? $"[{address}]:{Fact(record, "port")}" : $"{address}:{Fact(record, "port")}";
    }
}
