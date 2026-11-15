using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Storage;

namespace NetScope.Desktop.ViewModels;

public sealed record RuleFailureRow(string RuleId, string RuleVersion, string ErrorType, string Summary);

/// <summary>
/// Diagnoses with their evidence, counter-evidence and gaps, plus rule failures kept separate.
/// The probe comparison only states what the three paths recorded: an unproven comparability is
/// shown as "不可比" and is never merged into one network conclusion.
/// </summary>
public sealed class DiagnosesViewModel : ViewModelBase
{
    private DiagnosisRow? _selected;
    private readonly Func<string, Task>? _openRepair;
    private string _emptyText = "还没有诊断结论。开始观察后运行一次探测，或载入合成演示。";
    private bool _isSynthetic;
    private DateTimeOffset? _diagnosedAt;
    private EvidenceSet? _evidence;
    private bool _reasonsExpanded = true;
    private string _comparisonVerdictText = "没有可对比的探测结果";
    private string _comparisonDetailText = "本页会在结论引用外部探测时展开三路径对比。";
    private Brush _comparisonTone = UiText.GapBrush;
    private string _probeEmptyText = "选择一条结论查看它的探测对比。";
    private string _groupNoteText = "";

    public DiagnosesViewModel(Func<string, Task>? openRepair = null)
    {
        _openRepair = openRepair;
        ToggleReasonsCommand = new AsyncRelayCommand(() =>
        {
            AreReasonsExpanded = !AreReasonsExpanded;
            return Task.CompletedTask;
        });
        GoRepairCommand = new AsyncRelayCommand(GoRepairAsync, () => Selected is not null, null);
    }

    public ICommand ToggleReasonsCommand { get; }

    public ICommand GoRepairCommand { get; }

    private async Task GoRepairAsync()
    {
        if (Selected is not null && _openRepair is not null)
            await _openRepair(Selected.RuleId).ConfigureAwait(true);
    }

    public string ObservationText => IsSynthetic ? "以下为合成演示结论，时间和证据均来自演示会话。" : _diagnosedAt is { } at
        ? "最近一次手动诊断：" + at.ToLocalTime().ToString("MM-dd HH:mm:ss") + "；后续快照不表示已复测。"
        : "尚未运行手动诊断。";

    public ObservableCollection<DiagnosisRow> Rows { get; } = new();

    public ObservableCollection<RuleFailureRow> Failures { get; } = new();

    public ObservableCollection<EvidenceRow> SelectedEvidence { get; } = new();

    /// <summary>One row per path of the compared group; a missing path is shown as "未测".</summary>
    public ObservableCollection<ProbePathRow> PathRows { get; } = new();

    /// <summary>What each path proved about IPv4/IPv6, including the T04-R2 gap codes.</summary>
    public ObservableCollection<FamilyCapabilityRow> FamilyRows { get; } = new();

    /// <summary>Aggregated partial/unsupported/failed reasons of the evidence the selection cites.</summary>
    public ObservableCollection<ReasonRow> Reasons { get; } = new();

    public DiagnosisRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ((AsyncRelayCommand)GoRepairCommand).RaiseCanExecuteChanged();
            SelectedEvidence.Clear();
            if (value is not null)
            {
                foreach (var evidence in value.Evidence) SelectedEvidence.Add(evidence);
            }

            RecomputeProbeDetail();
        }
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    public bool HasRows => Rows.Count > 0;

    public bool HasFailures => Failures.Count > 0;

    public bool IsSynthetic
    {
        get => _isSynthetic;
        private set
        {
            if (Set(ref _isSynthetic, value))
            {
                Raise(nameof(DataLabel));
                Raise(nameof(ObservationText));
            }
        }
    }

    public string DataLabel => IsSynthetic
        ? "合成演示结论（mock，不是本机观测）"
        : "本机规则结论（基于已保存证据）";

    public bool HasProbeComparison => PathRows.Count > 0;

    public bool HasNoProbeComparison => !HasProbeComparison;

    public string ProbeComparisonTitle => "三路径对比（同目标 / 同方法 / 同地址族 / 同窗口）";

    public string ComparisonVerdictText
    {
        get => _comparisonVerdictText;
        private set => Set(ref _comparisonVerdictText, value);
    }

    public string ComparisonDetailText
    {
        get => _comparisonDetailText;
        private set => Set(ref _comparisonDetailText, value);
    }

    public Brush ComparisonTone
    {
        get => _comparisonTone;
        private set => Set(ref _comparisonTone, value);
    }

    public string ProbeEmptyText
    {
        get => _probeEmptyText;
        private set => Set(ref _probeEmptyText, value);
    }

    public string GroupNoteText
    {
        get => _groupNoteText;
        private set => Set(ref _groupNoteText, value);
    }

    public string ProbeBoundaryText => ProbeComparisonExplainer.ProbeBoundaryText;

    public string FamilyCapabilityTitle => "地址族能力（IPv4 / IPv6）";

    public string FamilyCapabilityCaption =>
        "只有无显式代理路径在本进程真正拨号目标，才能强制并证明地址族；代理与系统默认路径的族限制作为 Unsupported/未知缺口显示。";

    public bool HasReasons => Reasons.Count > 0;

    public bool ShowReasonsEmpty => !HasReasons;

    public bool AreReasonsExpanded
    {
        get => _reasonsExpanded;
        private set
        {
            if (!Set(ref _reasonsExpanded, value)) return;
            Raise(nameof(ReasonsVisible));
            Raise(nameof(ReasonsToggleText));
        }
    }

    public bool ReasonsVisible => AreReasonsExpanded && HasReasons;

    public string ReasonsToggleText => AreReasonsExpanded ? "收起原因明细" : "展开原因明细";

    public string ReasonHeaderText
    {
        get
        {
            var total = Reasons.Sum(reason => reason.Count);
            return HasReasons
                ? "部分 / 未支持原因（" + total.ToString(CultureInfo.InvariantCulture) + " 条）"
                : "部分 / 未支持原因";
        }
    }

    public string ReasonsEmptyText => "所选结论引用的证据没有部分/未支持条目；这不代表采集完整，请同时查看采集健康与时间线。";

    public string ReasonsCaption =>
        "按所选结论引用的 evidence_id 聚合；重复原因只合并计数，不会删除。Partial/Unsupported 不会改写成 Ok。";

    public void Clear()
    {
        Rows.Clear();
        Failures.Clear();
        SelectedEvidence.Clear();
        PathRows.Clear();
        FamilyRows.Clear();
        Reasons.Clear();
        _evidence = null;
        Selected = null;
        IsSynthetic = false;
        _diagnosedAt = null;
        Raise(nameof(ObservationText));
        Raise(nameof(HasRows));
        Raise(nameof(HasFailures));
        Raise(nameof(HasReasons));
        Raise(nameof(ShowReasonsEmpty));
        Raise(nameof(ReasonHeaderText));
        Raise(nameof(HasProbeComparison));
        Raise(nameof(HasNoProbeComparison));
        EmptyText = "还没有诊断结论。开始观察后运行一次探测。";
        ResetProbeDetail("选择一条结论查看它的探测对比。");
    }

    public void Apply(EvidenceSet? evidence, IReadOnlyList<Diagnosis> diagnoses, IReadOnlyList<RuleEvaluationFailure> failures,
        DateTimeOffset? diagnosedAt = null)
    {
        _diagnosedAt = diagnosedAt;
        Raise(nameof(ObservationText));
        IsSynthetic = false;
        _evidence = evidence;
        Rows.Clear();
        foreach (var diagnosis in diagnoses) Rows.Add(UiRows.Diagnosis(diagnosis, evidence));
        Failures.Clear();
        foreach (var failure in failures)
            Failures.Add(new RuleFailureRow(failure.RuleId, failure.RuleVersion, failure.ErrorType, failure.Summary));
        Selected = Rows.FirstOrDefault();
        RecomputeProbeDetail();
        Raise(nameof(HasRows));
        Raise(nameof(HasFailures));
        EmptyText = "本次采集没有产生诊断结论。没有结论不等于一切正常，请查看时间线与采集缺口。";
        StatusMessage = failures.Count == 0 ? null : failures.Count + " 条规则执行失败，没有生成对应结论。";
    }

    public void LoadReplay(ReplayResult replay)
    {
        // Replay exposes every saved diagnosis but only the latest snapshot scope as the
        // single-snapshot-compatible view. The detail lookup unions the saved scopes so an older
        // diagnosis can still resolve the probes it actually cited; reasons and comparisons are
        // then scoped by that diagnosis's own evidence ids/group ids.
        var probes = replay.SnapshotScopes
            .SelectMany(scope => scope.Evidence.Probes)
            .Concat(replay.Evidence?.Probes ?? Array.Empty<ProbeResult>())
            .GroupBy(probe => probe.ProbeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var records = replay.SnapshotScopes
            .SelectMany(scope => scope.Evidence.Records)
            .Concat(replay.Evidence?.Records ?? Array.Empty<EvidenceRecord>())
            .GroupBy(record => record.EvidenceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var evidence = new EvidenceSet
        {
            Snapshot = replay.Evidence?.Snapshot,
            Probes = probes,
            Records = records,
        };

        Apply(evidence, replay.Diagnoses, Array.Empty<RuleEvaluationFailure>());
        IsSynthetic = replay.Summary.AllSynthetic;
        if (!replay.IsClean)
            StatusMessage = "回放发现证据一致性问题；结论仅在被保存的事实范围内成立。";
        EmptyText = "这个会话保存的诊断结论为空。";
    }

    private void RecomputeProbeDetail()
    {
        PathRows.Clear();
        FamilyRows.Clear();
        Reasons.Clear();
        GroupNoteText = "";

        var selected = Selected;
        if (selected is null)
        {
            ResetProbeDetail("选择一条结论查看它的探测对比。");
            RecomputeReasons(new HashSet<string>(StringComparer.Ordinal));
            return;
        }

        var referenced = new HashSet<string>(selected.EvidenceIds, StringComparer.Ordinal);
        var citedProbes = _evidence?.Probes
            .Where(probe => referenced.Contains(probe.ProbeId))
            .ToArray() ?? Array.Empty<ProbeResult>();

        // The comparison unit is the probe group, not the single path a rule happened to cite.
        // Only groups the selection actually cites are expanded; unrelated cycles stay out.
        var citedGroups = citedProbes.Select(probe => probe.ProbeGroupId).ToHashSet(StringComparer.Ordinal);
        var probes = _evidence?.Probes
            .Where(probe => citedGroups.Contains(probe.ProbeGroupId))
            .ToArray() ?? Array.Empty<ProbeResult>();

        if (probes.Length == 0)
        {
            ResetProbeDetail(referenced.Count == 0
                ? "这条结论没有引用任何证据。"
                : "这条结论没有引用外部探测结果；只基于本机证据，未验证目标连通性。");
        }
        else
        {
            var groups = ProbeComparisonExplainer.Compare(probes);
            var group = groups.Count == 0 ? null : groups[^1];
            if (group is null)
            {
                ResetProbeDetail("探测结果无法按探测组读取。");
            }
            else
            {
                foreach (var path in ProbeComparisonExplainer.Paths)
                    PathRows.Add(UiRows.ProbePath(group.For(path), path));

                foreach (var path in ProbeComparisonExplainer.Paths)
                {
                    var capability = group.FamilyCapabilities.FirstOrDefault(item => item.Path == path)
                        ?? new FamilyCapability(path, null, FamilyCapabilityKind.NotObserved, null);
                    FamilyRows.Add(UiRows.FamilyCapability(capability));
                }

                ApplyComparability(group);
                if (groups.Count > 1)
                {
                    GroupNoteText = "所选结论引用了 " + groups.Count + " 组探测结果；这里展开最近一组（"
                        + UiText.ObservationTime(group.StartedAt) + "），其余组见时间线。";
                }
            }
        }

        RecomputeReasons(referenced);
        Raise(nameof(HasProbeComparison));
        Raise(nameof(HasNoProbeComparison));
    }

    private void ApplyComparability(ProbeGroupComparison group)
    {
        var comparability = group.Comparability;
        ComparisonVerdictText = comparability.Comparable ? "三路径可比较" : "三路径不可比";
        ComparisonTone = comparability.Comparable ? UiText.OkBrush : UiText.WarnBrush;

        var spread = StartSpreadText(group.Results);
        var range = group.EndedAt is { } endedAt
            ? UiText.ObservationTime(group.StartedAt) + " → " + UiText.ObservationTime(endedAt)
            : UiText.ObservationTime(group.StartedAt) + " → 结束时间未知";
        var basis = "目标 " + group.TargetId
            + " · 方法 " + (group.Method ?? "未记录")
            + " · 地址族 " + (group.AddressFamily ?? "未指定")
            + " · 窗口 " + (group.ObservationWindowId ?? "未记录")
            + " · 时间 " + range
            + " · 启动跨度 " + spread + "（阈值 2000 ms）";
        ComparisonDetailText = comparability.Comparable
            ? basis + "；RequestOptionsKey 一致，三条路径结果可比，生产者已确认时间可比。"
            : basis + "；不具备可比性：" + string.Join("；", comparability.Unmet.Select(UiText.ComparabilityText))
              + "。只作单路径观察，不合并成网络结论。";
    }

    private void RecomputeReasons(HashSet<string> referenced)
    {
        var reasons = ProbeComparisonExplainer.AggregateReasons(_evidence, referenced);
        foreach (var reason in reasons) Reasons.Add(UiRows.Reason(reason));
        Raise(nameof(HasReasons));
        Raise(nameof(ShowReasonsEmpty));
        Raise(nameof(ReasonHeaderText));
        Raise(nameof(ReasonsVisible));
    }

    private void ResetProbeDetail(string emptyText)
    {
        PathRows.Clear();
        FamilyRows.Clear();
        ProbeEmptyText = emptyText;
        ComparisonVerdictText = "没有可对比的探测结果";
        ComparisonDetailText = "三路径对比需要一条引用了外部探测结果的结论。";
        ComparisonTone = UiText.GapBrush;
        GroupNoteText = "";
    }

    private static string StartSpreadText(IReadOnlyList<ProbeResult> results)
    {
        var times = results
            .Select(result => result.StartedMonotonicMs)
            .Where(value => value is { } time && double.IsFinite(time) && time >= 0)
            .Select(value => value!.Value)
            .ToArray();
        if (times.Length == 0) return "未记录";
        return (times.Max() - times.Min()).ToString("0", CultureInfo.InvariantCulture) + " ms";
    }
}
