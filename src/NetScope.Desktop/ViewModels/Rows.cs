using System.Windows.Media;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Desktop.ViewModels;

public sealed record DomainRow(string Name, string StatusText, string Detail, Brush Tone)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
    public Brush BadgeBorder => UiText.SoftBorder(Tone);
}

public sealed record EvidenceRow(string EvidenceId, string Kind, string Summary, string Facts);

public sealed record DiagnosisRow(
    string RuleId,
    string RuleVersion,
    string LevelText,
    string Summary,
    string NextStep,
    string EvidenceText,
    string CounterText,
    string MissingText,
    string ScopeText,
    Brush Tone,
    IReadOnlyList<EvidenceRow> Evidence)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
    public Brush BadgeBorder => UiText.SoftBorder(Tone);
}

public sealed record TimelineRow(
    int Sequence,
    string Time,
    string Kind,
    string StatusText,
    string Summary,
    string EventId,
    string PayloadJson,
    Brush Tone)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
    public Brush BadgeBorder => UiText.SoftBorder(Tone);
}

public sealed record ClientEvidenceRow(string ClientId, string ProvenanceText, string FactsText, string ValidityText, string? SourcePath);

public sealed record ClientRow(
    string ClientId,
    string DisplayName,
    string RunningText,
    string VersionText,
    string EndpointsText,
    string TunText,
    string ProvenanceText,
    string GapsText,
    string NotesText,
    IReadOnlyList<ClientEvidenceRow> Evidence)
{
    public Brush RunningTone => RunningText == "运行中" ? UiText.OkBrush : UiText.GapBrush;
    public Brush RunningBg => RunningText == "运行中" ? UiText.OkBgBrush : UiText.GapBgBrush;
    public Brush TunTone => TunText.StartsWith("已检测到", StringComparison.Ordinal) ? UiText.AccentBrush : UiText.GapBrush;
    public Brush TunBg => TunText.StartsWith("已检测到", StringComparison.Ordinal) ? UiText.AccentBgBrush : UiText.GapBgBrush;
}

public sealed record DependencyLayerRow(string Name, string State, string Detail);

public sealed record DependencyRow(
    string AppId,
    string DisplayName,
    string Summary,
    string Boundary,
    IReadOnlyList<DependencyLayerRow> Layers,
    string GapsText);

/// <summary>Display-only mapping. It never upgrades an observation into a stronger claim.</summary>
public static class UiText
{
    public static readonly Brush OkBrush = Frozen(0x0B, 0x8A, 0x56);
    public static readonly Brush WarnBrush = Frozen(0xB4, 0x53, 0x09);
    public static readonly Brush BadBrush = Frozen(0xDC, 0x26, 0x26);
    public static readonly Brush GapBrush = Frozen(0x4B, 0x55, 0x63);
    public static readonly Brush AccentBrush = Frozen(0x4F, 0x46, 0xE5);

    public static readonly Brush OkBgBrush = Frozen(0xE6, 0xF5, 0xEE);
    public static readonly Brush OkBorderBrush = Frozen(0xA7, 0xF3, 0xD0);

    public static readonly Brush WarnBgBrush = Frozen(0xFB, 0xF0, 0xDF);
    public static readonly Brush WarnBorderBrush = Frozen(0xFD, 0xE6, 0x8A);

    public static readonly Brush BadBgBrush = Frozen(0xFD, 0xEC, 0xEC);
    public static readonly Brush BadBorderBrush = Frozen(0xFE, 0xCA, 0xCA);

    public static readonly Brush GapBgBrush = Frozen(0xEF, 0xEF, 0xF5);
    public static readonly Brush GapBorderBrush = Frozen(0xCB, 0xD5, 0xE1);

    public static readonly Brush AccentBgBrush = Frozen(0xEE, 0xF0, 0xFF);
    public static readonly Brush AccentBorderBrush = Frozen(0xC7, 0xD2, 0xFE);

    public static Brush SoftBackground(Brush? tone)
    {
        if (ReferenceEquals(tone, OkBrush)) return OkBgBrush;
        if (ReferenceEquals(tone, WarnBrush)) return WarnBgBrush;
        if (ReferenceEquals(tone, BadBrush)) return BadBgBrush;
        if (ReferenceEquals(tone, AccentBrush)) return AccentBgBrush;
        return GapBgBrush;
    }

    public static Brush SoftBorder(Brush? tone)
    {
        if (ReferenceEquals(tone, OkBrush)) return OkBorderBrush;
        if (ReferenceEquals(tone, WarnBrush)) return WarnBorderBrush;
        if (ReferenceEquals(tone, BadBrush)) return BadBorderBrush;
        if (ReferenceEquals(tone, AccentBrush)) return AccentBorderBrush;
        return GapBorderBrush;
    }

    public static string StatusText(EventStatus status) => status switch
    {
        EventStatus.Ok => "正常",
        EventStatus.Partial => "部分",
        EventStatus.Failed => "失败",
        EventStatus.Timeout => "超时",
        EventStatus.Cancelled => "已取消",
        EventStatus.Unsupported => "不支持",
        EventStatus.AccessDenied => "权限不足",
        _ => "未知",
    };

    public static Brush StatusTone(EventStatus status) => status switch
    {
        EventStatus.Ok => OkBrush,
        EventStatus.Partial => WarnBrush,
        EventStatus.Failed or EventStatus.Timeout or EventStatus.AccessDenied => BadBrush,
        _ => GapBrush,
    };

    public static string GradeText(EvidenceGrade grade) => grade switch
    {
        EvidenceGrade.Observed => "直接观察",
        EvidenceGrade.Candidate => "候选线索",
        EvidenceGrade.Supported => "证据支持",
        EvidenceGrade.Verified => "已验证",
        EvidenceGrade.Insufficient => "证据不足",
        _ => "存在反证",
    };

    public static Brush GradeTone(EvidenceGrade grade) => grade switch
    {
        EvidenceGrade.Supported or EvidenceGrade.Verified => BadBrush,
        EvidenceGrade.Observed => WarnBrush,
        EvidenceGrade.Candidate => WarnBrush,
        EvidenceGrade.Contradicted => GapBrush,
        _ => GapBrush,
    };

    public static string PathText(ProbePath path) => path switch
    {
        ProbePath.SystemDefault => "系统默认路径",
        ProbePath.NoExplicitProxy => "不使用显式代理",
        _ => "显式 HTTP 代理",
    };

    public static string ClassificationText(ProbeClassification classification) => classification switch
    {
        ProbeClassification.ReachableExpected => "按预期可达",
        ProbeClassification.ReachableUnexpected => "可达但响应不同",
        ProbeClassification.AuthenticationRequired => "需要认证",
        ProbeClassification.TransportFailure => "传输失败",
        _ => "不确定",
    };

    public static string Shorten(string? value, int limit = 120) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= limit ? value : value[..limit] + "…";

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public static class UiRows
{
    public static DiagnosisRow Diagnosis(Diagnosis diagnosis, EvidenceSet? evidence)
    {
        var scope = new List<string>();
        if (diagnosis.Scope.TargetId is { Length: > 0 } target) scope.Add("目标 " + target);
        if (diagnosis.Scope.Path is { } path) scope.Add(UiText.PathText(path));
        if (diagnosis.Scope.AddressFamily is { Length: > 0 } family) scope.Add("地址族 " + family);
        if (diagnosis.Scope.ProxyEndpointId is { Length: > 0 } proxy) scope.Add("代理 " + proxy);
        if (diagnosis.Scope.StartedAt is { } started)
            scope.Add("时间 " + started.ToLocalTime().ToString("MM-dd HH:mm:ss"));
        return new DiagnosisRow(
            diagnosis.RuleId,
            diagnosis.RuleVersion,
            UiText.GradeText(diagnosis.Level),
            diagnosis.Summary,
            diagnosis.SuggestedNextStep,
            diagnosis.EvidenceIds.Count == 0 ? "无" : string.Join("、", diagnosis.EvidenceIds),
            diagnosis.CounterEvidenceIds.Count == 0 ? "无" : string.Join("、", diagnosis.CounterEvidenceIds),
            diagnosis.MissingEvidence.Count == 0 ? "无" : string.Join("、", diagnosis.MissingEvidence),
            scope.Count == 0 ? "未限定" : string.Join(" · ", scope),
            UiText.GradeTone(diagnosis.Level),
            EvidenceRows(diagnosis.EvidenceIds, evidence));
    }

    public static IReadOnlyList<EvidenceRow> EvidenceRows(IEnumerable<string> ids, EvidenceSet? evidence)
    {
        var rows = new List<EvidenceRow>();
        foreach (var id in ids)
        {
            var record = evidence?.Records.FirstOrDefault(item => item.EvidenceId == id);
            if (record is not null)
            {
                rows.Add(new EvidenceRow(id, "证据记录 · " + record.Kind, record.Summary,
                    string.Join(" · ", record.Facts.Select(pair => pair.Key + "=" + pair.Value))
                    + $" · 来源 {record.Source ?? "未知"} · {UiText.StatusText(record.Status)}"));
                continue;
            }

            var probe = evidence?.Probes.FirstOrDefault(item => item.ProbeId == id);
            if (probe is not null)
            {
                rows.Add(new EvidenceRow(id, "探测 · " + UiText.PathText(probe.Path),
                    $"{UiText.StatusText(probe.Status)} · HTTP {probe.HttpStatus?.ToString() ?? "-"} · {UiText.ClassificationText(probe.Classification)}",
                    string.Join(" · ", new[]
                    {
                        "阶段=" + (probe.FailedStage ?? "-"),
                        "失败归属=" + (probe.FailedEndpoint ?? "unknown"),
                        "地址族=" + (probe.AddressFamily ?? "unknown"),
                        "代理=" + (probe.ProxyEndpointId ?? "未使用"),
                        "耗时=" + probe.DurationMs.ToString("0") + "ms",
                        "错误=" + (probe.TransportError ?? "-"),
                    })));
                continue;
            }

            var snapshot = evidence?.Snapshot;
            if (snapshot is not null && snapshot.SnapshotId == id)
            {
                rows.Add(new EvidenceRow(id, "网络快照", "本机只读采集",
                    string.Join(" · ", snapshot.Completeness().Select(pair => pair.Key + "=" + UiText.StatusText(pair.Value)))));
                continue;
            }

            rows.Add(new EvidenceRow(id, "引用缺失", "该证据 ID 不在当前可重建的事实中。", "需要检查日志是否完整。"));
        }

        return rows;
    }

    public static DomainRow Domain<T>(string name, Collected<T> domain) where T : class =>
        new(name, UiText.StatusText(domain.Status),
            domain.Reason ?? (domain.Status == EventStatus.Ok ? "已读取" : "无原因，不能当作正常"),
            UiText.StatusTone(domain.Status));
}
