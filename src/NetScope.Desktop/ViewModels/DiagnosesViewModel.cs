using System.Collections.ObjectModel;
using NetScope.Application;
using NetScope.Storage;

namespace NetScope.Desktop.ViewModels;

public sealed record RuleFailureRow(string RuleId, string RuleVersion, string ErrorType, string Summary);

/// <summary>Diagnoses with their evidence, counter-evidence and gaps, plus rule failures kept separate.</summary>
public sealed class DiagnosesViewModel : ViewModelBase
{
    private DiagnosisRow? _selected;
    private string _emptyText = "还没有诊断结论。开始观察后运行一次探测，或载入合成演示。";
    private bool _isSynthetic;
    private DateTimeOffset? _diagnosedAt;

    public string ObservationText => IsSynthetic ? "以下为合成演示结论，时间和证据均来自演示会话。" : _diagnosedAt is { } at
        ? "最近一次手动诊断：" + at.ToLocalTime().ToString("MM-dd HH:mm:ss") + "；后续快照不表示已复测。"
        : "尚未运行手动诊断。";

    public ObservableCollection<DiagnosisRow> Rows { get; } = new();

    public ObservableCollection<RuleFailureRow> Failures { get; } = new();

    public ObservableCollection<EvidenceRow> SelectedEvidence { get; } = new();

    public DiagnosisRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            SelectedEvidence.Clear();
            if (value is null) return;
            foreach (var evidence in value.Evidence) SelectedEvidence.Add(evidence);
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

    public void Clear()
    {
        Rows.Clear();
        Failures.Clear();
        SelectedEvidence.Clear();
        Selected = null;
        IsSynthetic = false;
        _diagnosedAt = null;
        Raise(nameof(ObservationText));
        Raise(nameof(HasRows));
        Raise(nameof(HasFailures));
        EmptyText = "还没有诊断结论。开始观察后运行一次探测。";
    }

    public void Apply(EvidenceSet? evidence, IReadOnlyList<Diagnosis> diagnoses, IReadOnlyList<RuleEvaluationFailure> failures,
        DateTimeOffset? diagnosedAt = null)
    {
        _diagnosedAt = diagnosedAt;
        Raise(nameof(ObservationText));
        IsSynthetic = false;
        Rows.Clear();
        foreach (var diagnosis in diagnoses) Rows.Add(UiRows.Diagnosis(diagnosis, evidence));
        Failures.Clear();
        foreach (var failure in failures)
            Failures.Add(new RuleFailureRow(failure.RuleId, failure.RuleVersion, failure.ErrorType, failure.Summary));
        Selected = Rows.FirstOrDefault();
        Raise(nameof(HasRows));
        Raise(nameof(HasFailures));
        EmptyText = "本次采集没有产生诊断结论。没有结论不等于一切正常，请查看时间线与采集缺口。";
        StatusMessage = failures.Count == 0 ? null : failures.Count + " 条规则执行失败，没有生成对应结论。";
    }

    public void LoadReplay(ReplayResult replay)
    {
        Apply(replay.Evidence, replay.Diagnoses, Array.Empty<RuleEvaluationFailure>());
        IsSynthetic = replay.Summary.AllSynthetic;
        if (!replay.IsClean)
            StatusMessage = "回放发现证据一致性问题；结论仅在被保存的事实范围内成立。";
        EmptyText = "这个会话保存的诊断结论为空。";
    }
}
