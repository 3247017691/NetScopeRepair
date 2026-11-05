using NetScope.Application;

namespace NetScope.Diagnostics;

public interface IDiagnosisIdFactory
{
    string Next(string ruleId);
}

public sealed class SequentialDiagnosisIdFactory : IDiagnosisIdFactory
{
    private int _counter;

    public string Next(string ruleId) => $"diagnosis-{++_counter:000}";
}

/// <summary>Legacy callers receive both successful diagnoses and failures when any rule failed.</summary>
public sealed class DiagnosticEvaluationException : Exception
{
    public DiagnosticEvaluationException(DiagnosticEvaluationResult outcome)
        : base($"{outcome.Failures.Count} diagnostic rule(s) failed; inspect Outcome for diagnoses and failures.")
    {
        Outcome = outcome;
    }

    public DiagnosticEvaluationResult Outcome { get; }
}

/// <summary>Pure evaluation over evidence, with rule failures represented separately from diagnoses.</summary>
public sealed class DiagnosticEngine
{
    private readonly IReadOnlyList<IDiagnosticRule> _rules;
    private readonly IDiagnosisIdFactory _ids;

    public DiagnosticEngine(IEnumerable<IDiagnosticRule> rules, IDiagnosisIdFactory? idFactory = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal).ToArray();
        _ids = idFactory ?? new SequentialDiagnosisIdFactory();
    }

    public static DiagnosticEngine CreateDefault(IDiagnosisIdFactory? idFactory = null) => new(RuleCatalog.All, idFactory);

    public IReadOnlyList<Diagnosis> Evaluate(EvidenceSet evidence)
    {
        var outcome = EvaluateDetailed(evidence);
        if (outcome.Failures.Count > 0)
        {
            throw new DiagnosticEvaluationException(outcome);
        }

        return outcome.Diagnoses;
    }

    public DiagnosticEvaluationResult EvaluateDetailed(EvidenceSet evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var diagnoses = new List<Diagnosis>();
        var failures = new List<RuleEvaluationFailure>();
        foreach (var rule in _rules)
        {
            try
            {
                var diagnosis = rule.Evaluate(evidence);
                if (diagnosis is not null)
                {
                    diagnoses.Add(diagnosis with { DiagnosisId = _ids.Next(rule.RuleId) });
                }
            }
            catch (Exception exception)
            {
                // Exception messages can contain arbitrary input; retain only the type and rule identity.
                failures.Add(new RuleEvaluationFailure
                {
                    RuleId = rule.RuleId,
                    RuleVersion = rule.Version,
                    ErrorType = exception.GetType().Name,
                    Summary = $"规则 {rule.RuleId} 执行失败（{exception.GetType().Name}），本轮没有得到该规则的结论。",
                });
            }
        }

        return new DiagnosticEvaluationResult { Diagnoses = diagnoses, Failures = failures };
    }
}
