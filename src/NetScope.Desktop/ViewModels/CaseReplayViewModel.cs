using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using NetScope.Application;
using NetScope.Diagnostics;
using NetScope.Storage;

namespace NetScope.Desktop.ViewModels;

public sealed record CaseReplayDiagnosisRow(string Scope, string Rule, string Level, string Summary, string Detail);

public sealed record CaseReplayDifferenceRow(string Scope, string Rule, string Disposition, string Recorded, string Recomputed, string Notes);

public sealed record CaseReplayScopeRow(string Sequence, string Snapshot, string Counts, string Diagnostics);

public sealed record CaseReplayIncidentRow(string Incident, string Trigger, string Window, string Scopes, string Missing);

public sealed record CaseReplayIssueRow(string Severity, string Code, string Message);

/// <summary>
/// Read-only M2 case replay page. It accepts one local untrusted incident package, shows admission
/// facts and the recorded diagnoses next to a recomputation with this build's fixed rule set, and
/// never writes, probes or changes the machine. Navigation is intentionally not wired here; the
/// Lead adds the minimal nav hook described in docs/reports/T12-report.md.
/// </summary>
public sealed class CaseReplayViewModel : ViewModelBase
{
    private readonly ICaseReplayService _service;
    private readonly Func<string?> _pickPackage;
    private string _packagePath = "";
    private bool _hasResult;
    private string _statusText = "尚未选择故障包。";
    private string _admissionText = "没有包被读取。";
    private string _packageText = "";
    private string _ruleSetText = "";
    private string _fingerprintText = "";
    private string _notice = "选择一个本机的 NetScope 故障包（ZIP）。包按不可信输入处理："
        + "先做准入与校验和验证，再逐 snapshot scope 离线重放；不联网、不修改系统。";

    public CaseReplayViewModel(ICaseReplayService? service = null, Func<string?>? pickPackage = null)
    {
        _service = service ?? new CaseReplayService(new DesktopDiagnosticRuleSetEvaluator());
        _pickPackage = pickPackage ?? DefaultPickPackage;
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        ReplayCommand = new AsyncRelayCommand(ReplayAsync, () => !string.IsNullOrWhiteSpace(PackagePath));
    }

    public ObservableCollection<CaseReplayDiagnosisRow> RecordedDiagnoses { get; } = new();

    public ObservableCollection<CaseReplayDiagnosisRow> RecomputedDiagnoses { get; } = new();

    public ObservableCollection<CaseReplayDifferenceRow> Differences { get; } = new();

    public ObservableCollection<CaseReplayScopeRow> SnapshotScopes { get; } = new();

    public ObservableCollection<CaseReplayIncidentRow> Incidents { get; } = new();

    public ObservableCollection<CaseReplayIssueRow> Issues { get; } = new();

    public ObservableCollection<string> Notices { get; } = new();

    public ICommand BrowseCommand { get; }

    public ICommand ReplayCommand { get; }

    public string PackagePath
    {
        get => _packagePath;
        set
        {
            if (!Set(ref _packagePath, value ?? "")) return;
            if (ReplayCommand is AsyncRelayCommand command) command.RaiseCanExecuteChanged();
        }
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => Set(ref _hasResult, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string AdmissionText
    {
        get => _admissionText;
        private set => Set(ref _admissionText, value);
    }

    public string PackageText
    {
        get => _packageText;
        private set => Set(ref _packageText, value);
    }

    public string RuleSetText
    {
        get => _ruleSetText;
        private set => Set(ref _ruleSetText, value);
    }

    public string FingerprintText
    {
        get => _fingerprintText;
        private set => Set(ref _fingerprintText, value);
    }

    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public async Task BrowseAsync()
    {
        var picked = _pickPackage();
        if (!string.IsNullOrWhiteSpace(picked)) PackagePath = picked;
        await Task.CompletedTask;
    }

    public async Task ReplayAsync()
    {
        if (string.IsNullOrWhiteSpace(PackagePath))
        {
            StatusMessage = "请先选择一个本地故障包（ZIP）。";
            return;
        }

        try
        {
            var result = await Task.Run(() => _service.Replay(PackagePath)).ConfigureAwait(true);
            Apply(result);
            StatusMessage = result.Status switch
            {
                CaseReplayStatus.Ok => "回放完成：记录诊断与当前固定规则重算分开显示。",
                CaseReplayStatus.Unsupported => "包可读，但格式/规则版本不受当前构建支持；只显示记录事实。",
                _ => "包未通过准入或完整性校验；没有生成分析结论。",
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or System.Text.Json.JsonException
                                              or NotSupportedException or ArgumentException)
        {
            StatusMessage = "回放失败：" + exception.GetType().Name + "。没有修改任何系统或网络设置。";
        }
    }

    /// <summary>Binds one result to the page; used by the command and by view-model tests.</summary>
    public void Apply(CaseReplayResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        HasResult = true;
        StatusText = result.Status switch
        {
            CaseReplayStatus.Ok => "已准入并完成重算（只读离线）。",
            CaseReplayStatus.Unsupported => "已读取，但格式/版本不受支持；未使用当前规则冒充原版。",
            _ => "拒绝：未通过准入或完整性校验；没有分析结论。",
        };
        AdmissionText = "准入：" + (result.Admission.Accepted ? "通过" : "拒绝")
            + "；条目 " + result.Admission.EntryCount
            + "；声明解压总量 " + result.Admission.TotalUncompressedBytes.ToString("N0", CultureInfo.InvariantCulture) + " 字节"
            + "；最大路径深度 " + result.Admission.MaxObservedPathDepth + "。";
        PackageText = result.Package is { } package
            ? "包格式：" + (package.PackageFormat ?? "-") + " / " + (package.PackageFormatVersion ?? "-")
                + "；事件 " + package.EventCount + "；快照 " + package.SnapshotCount
                + "；记录诊断 " + package.RecordedDiagnosisCount
                + "；typed evidence=" + YesNo(package.HasTypedEvidence)
                + "；探测=" + YesNo(package.HasProbeResults)
                + "；客户端证据=" + YesNo(package.HasClientEvidence) + "。"
            : "包信息不可用（准入失败或格式无法解析）。";
        RuleSetText = "固定规则集：" + result.RuleSet.RuleSetVersion
            + "；应用 " + result.RuleSet.AppVersion
            + "；共 " + result.RuleSet.Rules.Count + " 条规则。"
            + (result.Recomputed ? "本次已重算。" : "本次未重算。");
        FingerprintText = "确定性指纹：" + result.DeterministicFingerprint;

        RecordedDiagnoses.Clear();
        foreach (var item in result.RecordedDiagnoses)
        {
            RecordedDiagnoses.Add(new CaseReplayDiagnosisRow(
                item.ScopeSnapshotEventId ?? "unscoped",
                item.Diagnosis.RuleId + "@" + item.Diagnosis.RuleVersion,
                item.Diagnosis.Level.ToString(),
                item.Diagnosis.Summary,
                "规则支持=" + YesNo(item.RuleSupported) + "；版本匹配=" + YesNo(item.VersionMatchesCurrent)
                + "；证据=" + JoinOrDash(item.Diagnosis.EvidenceIds)
                + "；缺失=" + JoinOrDash(item.Diagnosis.MissingEvidence)));
        }

        RecomputedDiagnoses.Clear();
        foreach (var item in result.RecomputedDiagnoses)
        {
            RecomputedDiagnoses.Add(new CaseReplayDiagnosisRow(
                item.ScopeSnapshotEventId ?? "unscoped",
                item.Diagnosis.RuleId + "@" + item.Diagnosis.RuleVersion,
                item.Diagnosis.Level.ToString(),
                item.Diagnosis.Summary,
                "证据=" + JoinOrDash(item.Diagnosis.EvidenceIds)
                + "；缺失=" + JoinOrDash(item.Diagnosis.MissingEvidence)));
        }

        Differences.Clear();
        foreach (var difference in result.Differences)
        {
            Differences.Add(new CaseReplayDifferenceRow(
                difference.ScopeSnapshotEventId ?? "unscoped",
                difference.RuleId,
                difference.Disposition.ToString(),
                difference.RecordedLevel?.ToString() ?? "-",
                difference.RecomputedLevel?.ToString() ?? "-",
                string.Join("；", difference.Notes)));
        }

        SnapshotScopes.Clear();
        foreach (var scope in result.SnapshotScopes)
        {
            SnapshotScopes.Add(new CaseReplayScopeRow(
                "#" + scope.Sequence,
                scope.SnapshotId,
                "记录 " + scope.RecordCount + "；探测 " + scope.ProbeCount,
                "记录诊断 " + scope.RecordedDiagnosisCount + "；重算 " + scope.RecomputedDiagnosisCount));
        }

        Incidents.Clear();
        foreach (var incident in result.Incidents)
        {
            Incidents.Add(new CaseReplayIncidentRow(
                incident.IncidentId,
                incident.Trigger ?? "-",
                "前窗=" + YesNo(incident.PreWindowAvailable) + "；后窗=" + YesNo(incident.PostWindowComplete)
                + "；" + (incident.StartedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-")
                + " → " + (incident.ClosedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-"),
                JoinOrDash(incident.SnapshotScopeEventIds) + "；跨周期=" + YesNo(incident.SpansMultipleSnapshotCycles),
                JoinOrDash(incident.MissingRanges)));
        }

        Issues.Clear();
        foreach (var issue in result.Issues)
        {
            Issues.Add(new CaseReplayIssueRow(issue.Severity.ToString(), issue.Code, issue.Message));
        }

        Notices.Clear();
        foreach (var notice in result.Notices) Notices.Add(notice);
    }

    private static string? DefaultPickPackage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择本机的 NetScope 故障包",
            Filter = "NetScope 故障包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string YesNo(bool? value) => value is null ? "-" : value.Value ? "是" : "否";

    private static string JoinOrDash(IReadOnlyList<string> values) => values.Count == 0 ? "-" : string.Join(",", values);
}

/// <summary>
/// This build's fixed rule catalog for the page. It evaluates only the in-memory evidence set and
/// has no collector, probe, broker or repair reference.
/// </summary>
internal sealed class DesktopDiagnosticRuleSetEvaluator : ICaseRuleEvaluator
{
    private static readonly IReadOnlyList<IDiagnosticRule> Rules = RuleCatalog.All;

    public DesktopDiagnosticRuleSetEvaluator()
    {
        var versions = Rules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
            .Select(rule => new CaseRuleVersion(rule.RuleId, rule.Version))
            .ToArray();
        var canonical = string.Join(";", versions.Select(version => version.RuleId + "@" + version.Version));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
        var appVersion = typeof(DiagnosticEngine).Assembly.GetName().Version?.ToString() ?? "unknown";
        RuleSet = new CaseRuleSetInfo
        {
            RuleSetVersion = "diagnostics/" + hash[..16],
            AppVersion = appVersion,
            Rules = versions,
            Disclaimer = "重算使用当前构建固定规则集；与包内记录诊断分开显示。",
        };
    }

    public CaseRuleSetInfo RuleSet { get; }

    public DiagnosticEvaluationResult Evaluate(EvidenceSet evidence) =>
        new DiagnosticEngine(Rules).EvaluateDetailed(evidence);
}
