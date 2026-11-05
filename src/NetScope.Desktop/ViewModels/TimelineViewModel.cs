using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Desktop.ViewModels;

/// <summary>The captured session timeline, including unreadable lines and sequence gaps.</summary>
public sealed class TimelineViewModel : ViewModelBase
{
    private string _sessionPath = "";
    private string _summaryText = "还没有可显示的时间线。开始观察或载入合成演示后即可查看。";
    private string _countsText = "";
    private bool _isSynthetic;
    private bool _isDirty;
    private string _detailJson = "选择一行可查看该事件的完整 payload。";
    private TimelineRow? _selectedRow;

    public ObservableCollection<TimelineRow> Rows { get; } = new();

    public ObservableCollection<string> Problems { get; } = new();

    public ObservableCollection<string> Gaps { get; } = new();

    public string SessionPath
    {
        get => _sessionPath;
        private set => Set(ref _sessionPath, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string CountsText
    {
        get => _countsText;
        private set => Set(ref _countsText, value);
    }

    public bool IsSynthetic
    {
        get => _isSynthetic;
        private set
        {
            if (Set(ref _isSynthetic, value)) Raise(nameof(DataLabel));
        }
    }

    public string DataLabel => IsSynthetic ? "合成数据（非真实观测）" : "真实本机会话";

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    public string DetailJson
    {
        get => _detailJson;
        private set => Set(ref _detailJson, value);
    }

    public TimelineRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            DetailJson = value?.PayloadJson ?? "选择一行可查看该事件的完整 payload。";
        }
    }

    public void MarkDirty() => IsDirty = true;

    public void Clear()
    {
        Rows.Clear();
        Problems.Clear();
        Gaps.Clear();
        SessionPath = "";
        SummaryText = "还没有可显示的时间线。开始观察或载入合成演示后即可查看。";
        CountsText = "";
        IsSynthetic = false;
        IsDirty = false;
        SelectedRow = null;
        StatusMessage = null;
    }

    public async Task<bool> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusMessage = "会话文件尚不存在：" + path;
            return false;
        }

        try
        {
            var replay = await Task.Run(() => new ReplayService().Replay(path), cancellationToken).ConfigureAwait(true);
            Rows.Clear();
            foreach (var envelope in replay.Log.Events)
                Rows.Add(ToRow(envelope));

            Problems.Clear();
            foreach (var problem in replay.Log.Problems)
                Problems.Add($"第 {problem.LineNumber} 行：{problem.Message}" + (problem.IsTruncatedTail ? "（截断尾行）" : ""));
            foreach (var problem in replay.EvidenceProblems)
                Problems.Add("证据一致性：" + problem);
            Gaps.Clear();
            foreach (var gap in replay.Log.SequenceGaps)
                Gaps.Add($"序号缺口：期望 {gap.Expected}，实际 {gap.Found}（缺 {gap.Missing}）");
            if (replay.Log.DuplicateCount > 0)
                Gaps.Add($"重复事件：{replay.Log.DuplicateCount} 条（保留首次出现）");

            SessionPath = path;
            IsSynthetic = replay.Summary.AllSynthetic;
            CountsText = $"事件 {replay.Summary.EventCount} · 成功 {replay.Summary.SuccessCount} · 部分 {replay.Summary.PartialCount} · 观察缺口 {replay.Summary.ObservationGapCount}";
            SummaryText = replay.Log.IsClean && replay.EvidenceProblems.Count == 0
                ? "记录完整可读；时间线按事件序号展示。"
                : "记录存在可读性或证据一致性问题，已在下方列出，相关事件不会被当作成功。";
            IsDirty = false;
            SelectedRow = null;
            StatusMessage = replay.Summary.EventCount == 0 ? "这个会话没有任何事件。" : null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            StatusMessage = "读取会话失败：" + exception.GetType().Name + "。文件可能正在写入或已损坏。";
            return false;
        }
    }

    private static TimelineRow ToRow(EventEnvelope envelope) => new(
        envelope.Sequence,
        envelope.ObservedAt.ToLocalTime().ToString("MM-dd HH:mm:ss.fff"),
        KindText(envelope.EventType),
        UiText.StatusText(envelope.Status),
        Summary(envelope),
        envelope.EventId,
        JsonSerializer.Serialize(envelope.Payload, new JsonSerializerOptions { WriteIndented = true }),
        UiText.StatusTone(envelope.Status));

    private static string KindText(EventType type) => type switch
    {
        EventType.Snapshot => "网络快照",
        EventType.ProbeResult => "探测结果",
        EventType.Diagnosis => "诊断",
        EventType.IncidentMarker => "故障标记",
        EventType.CollectorError => "采集错误",
        EventType.Gap => "观察缺口",
        _ => "修复事件",
    };

    private static string Summary(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload.TryGetPropertyValue("diagnosis", out var diagnosis) && diagnosis is JsonObject diagnosisObject)
        {
            var rule = Text(diagnosisObject, "rule_id") ?? "?";
            var level = Text(diagnosisObject, "level") ?? "?";
            var summary = Text(diagnosisObject, "summary") ?? "";
            return $"{rule} [{level}] {UiText.Shorten(summary, 90)}";
        }

        if (payload.TryGetPropertyValue("probe_result", out var probe) && probe is JsonObject probeObject)
        {
            var path = Text(probeObject, "path") ?? "?";
            var classification = Text(probeObject, "classification") ?? "?";
            var http = Text(probeObject, "http_status");
            var error = Text(probeObject, "transport_error");
            return $"{path} · {classification}" + (http is not null ? " · HTTP " + http : "")
                + (error is not null ? " · " + error : "");
        }

        if (payload.TryGetPropertyValue("snapshot", out var snapshot) && snapshot is JsonObject snapshotObject)
        {
            var id = Text(snapshotObject, "snapshot_id") ?? "?";
            return "本机只读快照 " + id;
        }

        return envelope.Status == EventStatus.Ok ? "事件已记录" : "无可读摘要";
    }

    private static string? Text(JsonObject value, string key) =>
        value.TryGetPropertyValue(key, out var node) && node is JsonValue json && json.TryGetValue<string>(out var text)
            ? text
            : null;
}
