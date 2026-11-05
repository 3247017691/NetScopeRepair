using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Diagnostics;

/// <summary>
/// Shared plumbing for the M0 rules. A rule is a pure function over evidence: it does not open a
/// socket, start a process or change a setting, and it can never propose an executable action.
/// </summary>
public abstract class DiagnosticRuleBase : IDiagnosticRule
{
    /// <summary>Set by <see cref="DiagnosticEngine"/>; a rule never invents its own id.</summary>
    public const string PendingId = "pending";

    public abstract string RuleId { get; }

    public abstract string Version { get; }

    public abstract Diagnosis? Evaluate(EvidenceSet evidence);

    /// <summary>
    /// Builds a diagnosis that cites at least one piece of evidence. A conclusion without an
    /// evidence reference is exactly what this product exists to avoid.
    /// </summary>
    protected Diagnosis Create(
        EvidenceGrade level,
        string summary,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<string>? counterEvidenceIds = null,
        IReadOnlyList<string>? missingEvidence = null,
        string suggestedNextStep = "",
        DiagnosisScope? scope = null)
    {
        if (evidenceIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"{RuleId} tried to publish a conclusion without citing evidence.");
        }

        return new Diagnosis
        {
            DiagnosisId = PendingId,
            RuleId = RuleId,
            RuleVersion = Version,
            Level = level,
            Summary = summary,
            EvidenceIds = evidenceIds.Distinct(StringComparer.Ordinal).ToArray(),
            CounterEvidenceIds = counterEvidenceIds?.Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
            MissingEvidence = missingEvidence ?? Array.Empty<string>(),
            AllowedRepairActions = Array.Empty<string>(),
            SuggestedNextStep = suggestedNextStep,
            Scope = scope ?? new DiagnosisScope(),
        };
    }

    protected static string Fact(EvidenceRecord record, string name) =>
        record.Facts.TryGetValue(name, out var value) ? value : string.Empty;

    protected static bool HasFact(EvidenceRecord record, string name) =>
        record.Facts.ContainsKey(name);

    protected static DiagnosisScope ScopeOf(ProbeResult probe) => DiagnosticComparability.Scope(new[] { probe });

    /// <summary>Every proxy layer that was actually readable, with its name.</summary>
    protected static IReadOnlyList<(string Name, ProxyLayer Layer)> ProxyLayers(EvidenceSet evidence)
    {
        var proxies = evidence.Snapshot?.Proxies.Value;
        if (proxies is null)
        {
            return Array.Empty<(string, ProxyLayer)>();
        }

        var candidates = new (string Name, Collected<ProxyLayer> Layer)[]
        {
            ("tool_process_environment", proxies.ToolProcessEnvironment),
            ("user_environment", proxies.UserEnvironment),
            ("machine_environment", proxies.MachineEnvironment),
            ("wininet", proxies.WinInet),
            ("winhttp", proxies.WinHttp),
        };

        return candidates
            .Where(candidate => candidate.Layer.HasValue)
            .Select(candidate => (candidate.Name, candidate.Layer.Value!))
            .ToArray();
    }
}
