using System.Windows.Media;

namespace NetScope.Desktop.ViewModels;

public enum StepStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
}

public enum RepairRunState
{
    Idle,
    Running,
    Success,
    Failed,
}

public enum RepairStage { Inspect, Review, Execute, Verify }

/// <summary>One action in the live execution step list, with its real-time status and timing.</summary>
public sealed class StepRow : ViewModelBase
{
    private StepStatus _status = StepStatus.Pending;
    private string _resultText = "";
    private string _elapsedText = "";

    public StepRow(string id, string title, string detail, string risk)
    {
        Id = id;
        Title = title;
        Detail = detail;
        Risk = risk;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Risk { get; }
    public DateTimeOffset StartedAt { get; set; }

    public StepStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(Tone));
            Raise(nameof(ToneBg));
            Raise(nameof(Glyph));
            Raise(nameof(IsRunning));
            Raise(nameof(IsPending));
            Raise(nameof(IsDone));
        }
    }

    public bool IsRunning => Status == StepStatus.Running;
    public bool IsPending => Status == StepStatus.Pending;
    public bool IsDone => Status is StepStatus.Success or StepStatus.Failed or StepStatus.Skipped;

    public string StatusText => Status switch
    {
        StepStatus.Pending => "待执行",
        StepStatus.Running => "执行中",
        StepStatus.Success => "成功",
        StepStatus.Failed => "失败",
        StepStatus.Skipped => "跳过",
        _ => "",
    };

    public Brush Tone => Status switch
    {
        StepStatus.Success => UiText.OkBrush,
        StepStatus.Failed => UiText.BadBrush,
        StepStatus.Running => UiText.AccentBrush,
        _ => UiText.GapBrush,
    };

    public Brush ToneBg => UiText.SoftBackground(Tone);

    public string Glyph => Status switch
    {
        StepStatus.Success => "✓",
        StepStatus.Failed => "✕",
        StepStatus.Skipped => "–",
        _ => "",
    };

    public string ResultText
    {
        get => _resultText;
        set => Set(ref _resultText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        set => Set(ref _elapsedText, value);
    }
}

/// <summary>One line of the real-time execution log (monospace, timestamped, tone-colored).</summary>
public sealed record LogLine(string Time, string Text, Brush Tone);
