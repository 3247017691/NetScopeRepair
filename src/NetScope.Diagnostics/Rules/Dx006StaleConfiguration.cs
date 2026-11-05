using System.Globalization;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics.Rules;

/// <summary>DX-006: file age is a clue; only a trustworthy runtime comparison proves a mismatch.</summary>
public sealed class Dx006StaleConfiguration : DiagnosticRuleBase
{
    public override string RuleId => "DX-006";

    public override string Version => "1.1.0";

    public override Diagnosis? Evaluate(EvidenceSet evidence)
    {
        var files = evidence.ByKind("client_config_file");
        var runtimes = evidence.ByKind("client_runtime_config");
        Diagnosis? candidate = null;
        foreach (var file in files)
        {
            var client = Fact(file, "client");
            if (!DiagnosticComparability.Present(client))
            {
                continue;
            }

            var sameRuntime = runtimes.Where(runtime => SameConfiguration(file, runtime)
                && Fact(runtime, "running") == "true" && DiagnosticComparability.Present(Fact(runtime, "config_hash")))
                .ToArray();
            var trusted = sameRuntime.Where(runtime => DiagnosticComparability.Trusted(file)
                && DiagnosticComparability.TrustedRuntime(runtime) && DiagnosticComparability.SameRecordWindow(file, runtime)).ToArray();
            var matching = trusted.Where(runtime => SameHash(file, runtime)).ToArray();
            var differing = trusted.Where(runtime => !SameHash(file, runtime)).ToArray();
            if (matching.Length > 0)
            {
                return Create(
                    differing.Length > 0 ? EvidenceGrade.Candidate : EvidenceGrade.Contradicted,
                    differing.Length > 0
                        ? $"{client} 的运行态配置样本同时存在相同和不同哈希，当前证据冲突，不能确认磁盘配置失配。"
                        : $"{client} 的运行态与磁盘配置路径和哈希一致，反驳“文件时间较早即配置陈旧”的推断。",
                    new[] { file.EvidenceId }.Concat(differing.Select(record => record.EvidenceId)).ToArray(),
                    counterEvidenceIds: matching.Select(record => record.EvidenceId).ToArray(),
                    missingEvidence: differing.Length > 0 ? new[] { "consistent_runtime_configuration" } : Array.Empty<string>(),
                    suggestedNextStep: "保留运行态来源与观察时间；如需解释网络故障，应继续核对请求和路径证据。",
                    scope: DiagnosticComparability.Scope(file));
            }

            if (differing.Length > 0)
            {
                return Create(
                    EvidenceGrade.Supported,
                    $"{client} 同一配置路径的磁盘哈希与同时采集的有效运行态哈希不同，支持运行态与磁盘配置不一致；这仍不证明它导致网络故障。",
                    new[] { file.EvidenceId }.Concat(differing.Select(record => record.EvidenceId)).ToArray(),
                    missingEvidence: new[] { "configuration_difference_explanation", "request_failure_correlation" },
                    suggestedNextStep: "只读核对加载来源及哈希差异，不要根据磁盘内容推断当前运行行为。",
                    scope: DiagnosticComparability.Scope(file));
            }

            var process = evidence.ByKind("client_process").FirstOrDefault(record =>
                string.Equals(Fact(record, "client"), client, StringComparison.Ordinal)
                && Parse(Fact(record, "started_at"), out _));
            var ageClue = process is not null && Parse(Fact(file, "mtime"), out var modifiedAt)
                && Parse(Fact(process, "started_at"), out var startedAt) && modifiedAt < startedAt;
            if (sameRuntime.Length == 0 && !ageClue)
            {
                continue;
            }

            candidate ??= Create(
                EvidenceGrade.Candidate,
                $"{client} 的磁盘配置尚未经可信运行态核验。文件早于进程启动属于正常加载顺序，单凭 mtime 不能证明配置陈旧或失配。",
                new[] { file.EvidenceId }.Concat(ageClue ? new[] { process!.EvidenceId } : Array.Empty<string>()).ToArray(),
                counterEvidenceIds: sameRuntime.Where(runtime => SameHash(file, runtime)).Select(record => record.EvidenceId).ToArray(),
                missingEvidence: new[] { "runtime_api_configuration", "effective_config_validation", "contemporaneous_configuration_hashes" },
                suggestedNextStep: "只读采集有效运行态配置及来源、路径、时间和哈希，与磁盘样本比较。",
                scope: DiagnosticComparability.Scope(file));
        }

        return candidate;
    }

    private static bool SameConfiguration(EvidenceRecord file, EvidenceRecord runtime) =>
        DiagnosticComparability.Same(Fact(file, "client"), Fact(runtime, "client"))
        && DiagnosticComparability.Present(Fact(file, "config_hash"))
        && DiagnosticComparability.Present(Fact(file, "config_path"))
        && string.Equals(Fact(file, "config_path").Replace('\\', '/'), Fact(runtime, "config_path").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool SameHash(EvidenceRecord file, EvidenceRecord runtime) =>
        string.Equals(Fact(file, "config_hash"), Fact(runtime, "config_hash"), StringComparison.OrdinalIgnoreCase);

    private static bool Parse(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
}
