using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NetScope.Application;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Cli;

/// <summary>
/// Read-only case replay command. It admits one untrusted local incident package, verifies its
/// checksums/manifest, replays each snapshot scope and shows the recorded diagnoses next to a
/// recomputation with this build's fixed rule set. No network, collector, probe or system write is
/// reachable from this path.
/// </summary>
internal static class CaseReplayCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("CASE_REPLAY_USAGE: netscope case-replay <incident-package.zip>");
            return 4;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("MISSING_INPUT: the package file was not found.");
            return 2;
        }

        CaseReplayResult result;
        try
        {
            result = new CaseReplayService(new DiagnosticRuleSetEvaluator()).Replay(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine("CASE_REPLAY_FAILED: local package could not be read (" + exception.GetType().Name
                + "); no analysis is claimed.");
            return 1;
        }

        Console.WriteLine("NetScope Repair CLI - offline case replay");
        Console.WriteLine("DATA SOURCE: one local untrusted incident package only; no network, no probe, no system write.");
        Console.WriteLine();

        Console.WriteLine("admission=" + (result.Admission.Accepted ? "accepted" : "rejected")
            + " entries=" + result.Admission.EntryCount
            + " total_uncompressed_bytes=" + result.Admission.TotalUncompressedBytes
            + " max_path_depth=" + result.Admission.MaxObservedPathDepth);
        if (result.Package is { } package)
        {
            Console.WriteLine("package_format=" + package.PackageFormat + " version=" + package.PackageFormatVersion
                + " synthetic=" + Format(package.Synthetic) + " incident=" + package.IncidentId);
            Console.WriteLine("package=events:" + package.EventCount + " snapshots:" + package.SnapshotCount
                + " recorded_diagnoses:" + package.RecordedDiagnosisCount
                + " typed_evidence:" + Format(package.HasTypedEvidence)
                + " probes:" + Format(package.HasProbeResults)
                + " client_evidence:" + Format(package.HasClientEvidence));
            Console.WriteLine("package_rule_versions=" + JoinOrDash(package.RecordedRuleVersions));
            Console.WriteLine("package_redaction_policy=" + (package.RedactionPolicyVersion ?? "-"));
        }

        Console.WriteLine("rule_set=" + result.RuleSet.RuleSetVersion + " app=" + result.RuleSet.AppVersion);
        Console.WriteLine();

        Console.WriteLine("snapshot scopes");
        foreach (var scope in result.SnapshotScopes)
        {
            Console.WriteLine($"  seq={scope.Sequence} snapshot={scope.SnapshotId} event={scope.SnapshotEventId}"
                + $" records={scope.RecordCount} probes={scope.ProbeCount}"
                + $" recorded={scope.RecordedDiagnosisCount} recomputed={scope.RecomputedDiagnosisCount}");
        }

        Console.WriteLine("recorded diagnoses (as saved in the package)");
        if (result.RecordedDiagnoses.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var item in result.RecordedDiagnoses)
        {
            var diagnosis = item.Diagnosis;
            Console.WriteLine($"  [{item.ScopeSnapshotEventId ?? "unscoped"}] {diagnosis.RuleId}@{diagnosis.RuleVersion}"
                + $" {diagnosis.Level} supported={Format(item.RuleSupported)}"
                + $" version_match={Format(item.VersionMatchesCurrent)}");
            Console.WriteLine("    " + diagnosis.Summary);
        }

        Console.WriteLine("recomputed diagnoses (fixed rule set " + result.RuleSet.RuleSetVersion + ")");
        if (!result.Recomputed)
        {
            Console.WriteLine("  (not computed: " + result.Status + ")");
        }
        else if (result.RecomputedDiagnoses.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var item in result.RecomputedDiagnoses)
        {
            var diagnosis = item.Diagnosis;
            Console.WriteLine($"  [{item.ScopeSnapshotEventId ?? "unscoped"}] {diagnosis.RuleId}@{diagnosis.RuleVersion}"
                + $" {diagnosis.Level}");
            Console.WriteLine("    " + diagnosis.Summary);
        }

        Console.WriteLine("differences (recorded vs recomputed; separate facts)");
        if (result.Differences.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var difference in result.Differences)
        {
            Console.WriteLine($"  [{difference.ScopeSnapshotEventId ?? "unscoped"}] {difference.RuleId}"
                + $" {difference.Disposition} recorded={Format(difference.RecordedLevel)}"
                + $" recomputed={Format(difference.RecomputedLevel)}");
            foreach (var note in difference.Notes) Console.WriteLine("    " + note);
        }

        Console.WriteLine("incident windows");
        if (result.Incidents.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var incident in result.Incidents)
        {
            Console.WriteLine($"  {incident.IncidentId} trigger={incident.Trigger ?? "-"}"
                + $" states={JoinOrDash(incident.States)}"
                + $" pre_window={Format(incident.PreWindowAvailable)}"
                + $" post_window={Format(incident.PostWindowComplete)}"
                + $" starts={FormatTime(incident.StartedAt)} ends={FormatTime(incident.ClosedAt)}");
            Console.WriteLine($"    scope_events={JoinOrDash(incident.SnapshotScopeEventIds)}"
                + $" spans_multiple_cycles={Format(incident.SpansMultipleSnapshotCycles)}"
                + $" missing={JoinOrDash(incident.MissingRanges)}");
        }

        Console.WriteLine("issues");
        if (result.Issues.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"  {issue.Severity} {issue.Code}: {issue.Message}"
                + (issue.EntryName is null ? "" : " [" + issue.EntryName + "]"));
        }

        Console.WriteLine("notices");
        foreach (var notice in result.Notices) Console.WriteLine("  " + notice);
        if (result.Notices.Count == 0) Console.WriteLine("  (none)");

        Console.WriteLine();
        Console.WriteLine("fingerprint=" + result.DeterministicFingerprint);
        Console.WriteLine("RESULT=" + result.Status);
        return result.Status switch
        {
            CaseReplayStatus.Ok => 0,
            CaseReplayStatus.Unsupported => 3,
            _ => 1,
        };
    }

    private static string Format(bool? value) => value is null ? "-" : value.Value ? "yes" : "no";

    private static string Format(EvidenceGrade? value) => value?.ToString() ?? "-";

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";

    private static string JoinOrDash(IReadOnlyList<string> values) => values.Count == 0 ? "-" : string.Join(",", values);
}

/// <summary>This build's rule catalog, pinned to one immutable rule-set version string.</summary>
internal sealed class DiagnosticRuleSetEvaluator : ICaseRuleEvaluator
{
    private static readonly IReadOnlyList<IDiagnosticRule> Rules = RuleCatalog.All;

    public DiagnosticRuleSetEvaluator()
    {
        var versions = Rules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
            .Select(rule => new CaseRuleVersion(rule.RuleId, rule.Version))
            .ToArray();
        var canonical = string.Join(";", versions.Select(version => version.RuleId + "@" + version.Version));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var appVersion = typeof(DiagnosticEngine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DiagnosticEngine).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        RuleSet = new CaseRuleSetInfo
        {
            RuleSetVersion = "diagnostics/" + hash[..16],
            AppVersion = appVersion,
            Rules = versions,
            Disclaimer = "重算使用当前构建固定规则集；与包内记录诊断分开显示，不代表原版规则行为。",
        };
    }

    public CaseRuleSetInfo RuleSet { get; }

    public DiagnosticEvaluationResult Evaluate(EvidenceSet evidence) =>
        new DiagnosticEngine(Rules).EvaluateDetailed(evidence);
}
