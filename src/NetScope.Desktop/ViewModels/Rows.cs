using System.Globalization;
using System.Windows.Media;
using NetScope.Application;
using NetScope.Desktop.Services;
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
    IReadOnlyList<EvidenceRow> Evidence,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> CounterEvidenceIds)
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

public sealed record ClientEvidenceRow(
    string ClientId,
    string ProvenanceText,
    string FactsText,
    string ValidityText,
    string NoticesText,
    string? SourcePath);

public sealed record ClientRow(
    string ClientId,
    string DisplayName,
    string RunningText,
    string VersionText,
    string ProcessesText,
    string EndpointsText,
    string TunText,
    string ProvenanceText,
    string GapsText,
    string NotesText,
    IReadOnlyList<ClientEvidenceRow> Evidence)
{
    public Brush RunningTone => RunningText == "运行中" ? UiText.OkBrush : UiText.GapBrush;
    public Brush RunningBg => RunningText == "运行中" ? UiText.OkBgBrush : UiText.GapBgBrush;
    public Brush TunTone => TunText.StartsWith("已归属", StringComparison.Ordinal) ? UiText.OkBrush
        : TunText.StartsWith("归属未确认", StringComparison.Ordinal) ? UiText.WarnBrush : UiText.GapBrush;
    public Brush TunBg => TunText.StartsWith("已归属", StringComparison.Ordinal) ? UiText.OkBgBrush
        : TunText.StartsWith("归属未确认", StringComparison.Ordinal) ? UiText.WarnBgBrush : UiText.GapBgBrush;
}

/// <summary>Machine-level or unattributed observation shown below the client cards.</summary>
public sealed record ObservationRow(
    string ClientId,
    string DisplayName,
    string KindText,
    string SummaryText,
    string DetailText,
    string GapsText);

public sealed record DependencyLayerRow(string Name, string State, string Detail);

/// <summary>One path of a probe group, including a path that produced no result at all.</summary>
public sealed record ProbePathRow(
    string PathText,
    string StatusText,
    Brush Tone,
    string ClassificationText,
    string StageText,
    string EndpointText,
    string FamilyText,
    string FailureText,
    string ConnectionText,
    string ResolutionText,
    string TimingText,
    string NoticeText)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
}

/// <summary>What one path proved about IPv4/IPv6, or why it proved nothing.</summary>
public sealed record FamilyCapabilityRow(string PathText, string RequestedText, string CapabilityText, Brush Tone)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
}

/// <summary>An aggregated partial/unsupported/failed reason of the cited evidence.</summary>
public sealed record ReasonRow(
    string SourceText,
    string StatusText,
    Brush Tone,
    string Summary,
    string Detail,
    int Count)
{
    public string CountText => Count > 1 ? "×" + Count.ToString(CultureInfo.InvariantCulture) : "";
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
}

/// <summary>One field difference between two adjacent observations.</summary>
public sealed record ObservationChangeRow(
    string KindText,
    Brush Tone,
    string IdentityText,
    string FieldText,
    string BeforeText,
    string AfterText,
    string Note)
{
    public Brush BadgeBackground => UiText.SoftBackground(Tone);
}

/// <summary>One adjacent pair of observations and every difference kept for display.</summary>
public sealed record ObservationPairRow(
    string HeaderText,
    string IntervalText,
    string CountText,
    IReadOnlyList<ObservationChangeRow> Changes,
    string NotesText);

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
        ProbePath.NoExplicitProxy => "无显式代理（可能经 TUN）",
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

    /// <summary>The failed stage that the probe actually recorded; null is not a failure stage.</summary>
    public static string StageText(string? stage) => stage switch
    {
        null or "" => "未记录（不能归因）",
        "resolve" => "resolve · 本地解析/地址族检查",
        "connect" => "connect · TCP 连接",
        "proxy_connect" => "proxy_connect · 代理隧道",
        "tls" => "tls · TLS 握手",
        "http" => "http · HTTP 响应",
        "unknown" => "unknown · 归因不明",
        _ => stage,
    };

    public static string EndpointText(string? endpoint) => endpoint switch
    {
        "target" => "target（目标端点）",
        "proxy" => "proxy（代理端点）",
        "unknown" or null or "" => "unknown（无法确定）",
        _ => endpoint,
    };

    /// <summary>Why a probe group may or may not be compared across its three paths.</summary>
    public static string ComparabilityText(string code) => code switch
    {
        "request_options_missing_or_different" => "目标/方法/期望状态/超时选项不一致或缺失",
        "observation_window_missing_or_different" => "观察窗口不一致或缺失",
        "boot_missing_or_different" => "启动会话不一致或缺失",
        "address_family_missing_or_different" => "三条路径的地址族不一致",
        "address_family_not_specified" => "地址族未指定（生产方无法证明族）",
        "paths_missing" => "三条路径没有齐全",
        "start_times_outside_window" => "组内启动时间差超过 2 秒",
        "incomparable_status" => "存在不可比状态（partial/unsupported/cancelled 等）",
        "topology_changed" => "组内拓扑在探测期间发生变化",
        "producer_not_confirmed" => "生产者未确认时间可比",
        _ => code,
    };

    public static string FamilyCapabilityText(FamilyCapability capability) => capability.Kind switch
    {
        FamilyCapabilityKind.Enforced =>
            "已强制 " + (capability.RequestedFamily ?? "未指定") + "（本路径真实拨号目标）",
        FamilyCapabilityKind.UnsupportedGap =>
            "Unsupported 缺口 · " + ProbeComparisonExplainer.FamilyGapThroughProxy
            + "：代理端解析目标，本机不能强制或验证目标族",
        FamilyCapabilityKind.UnknownGap =>
            "未知 · " + ProbeComparisonExplainer.FamilyGapSystemDefault
            + "：系统默认栈可能经代理/PAC/TUN，目标族不可观测",
        FamilyCapabilityKind.InvalidRequest =>
            "Unsupported · " + ProbeComparisonExplainer.FamilyGapUnsupportedValue + "：地址族取值无法识别",
        FamilyCapabilityKind.ResolveFailed =>
            "失败 · " + ProbeComparisonExplainer.FamilyGapNoAddress + "：解析没有返回请求族的地址",
        FamilyCapabilityKind.NotRequested =>
            "未指定地址族：跟随系统/代理，不证明任一族的可达性",
        _ => "该路径未产生地址族能力结论",
    };

    public static Brush FamilyCapabilityTone(FamilyCapability capability) => capability.Kind switch
    {
        FamilyCapabilityKind.Enforced => OkBrush,
        FamilyCapabilityKind.UnsupportedGap => GapBrush,
        FamilyCapabilityKind.UnknownGap => GapBrush,
        FamilyCapabilityKind.InvalidRequest => WarnBrush,
        FamilyCapabilityKind.ResolveFailed => BadBrush,
        _ => GapBrush,
    };

    public static string ChangeKindText(ObservationChangeKind kind) => kind switch
    {
        ObservationChangeKind.Changed => "变化",
        ObservationChangeKind.Appeared => "出现",
        ObservationChangeKind.NotObserved => "未观测到",
        _ => "不可比",
    };

    public static Brush ChangeKindTone(ObservationChangeKind kind) => kind switch
    {
        ObservationChangeKind.Changed => WarnBrush,
        ObservationChangeKind.Appeared => AccentBrush,
        ObservationChangeKind.NotObserved => GapBrush,
        _ => GapBrush,
    };

    public static string ObservationTime(DateTimeOffset time) =>
        time.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ObservationTimeWithMillis(DateTimeOffset time) =>
        time.ToLocalTime().ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

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
            EvidenceRows(diagnosis.EvidenceIds, evidence),
            diagnosis.EvidenceIds,
            diagnosis.CounterEvidenceIds);
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

    /// <summary>
    /// One path of a probe group. A path with no result is shown as "未测", never as a success and
    /// never as a failure of the target.
    /// </summary>
    public static ProbePathRow ProbePath(ProbeResult? probe, ProbePath path)
    {
        if (probe is null)
        {
            return new ProbePathRow(UiText.PathText(path), "未测", UiText.GapBrush, "本轮没有该路径结果",
                "未测", "未测", "未测", "未测（缺口，不是成功）", "未观测到连接事实", "未测", "未测",
                "该路径没有产生结果；缺失不等于成功，也不等于目标不可达。");
        }

        var connection = probe.Connection is { } fact
            ? "连接角色 " + UiText.EndpointText(fact.EndpointRole)
              + " · 远端 " + (fact.RemoteAddress ?? "未观测")
              + (fact.RemotePort is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : "")
              + " · 复用 " + (fact.Reused switch { true => "是（本轮未重新解析）", false => "否", _ => "未知" })
            : "未观测到连接事实（该结果不证明连接复用或新建）";

        var addresses = probe.ResolvedAddresses.Count == 0
            ? "未记录"
            : string.Join(", ", probe.ResolvedAddresses.Take(2))
              + (probe.ResolvedAddresses.Count > 2 ? "…（共 " + probe.ResolvedAddresses.Count + " 个）" : "");

        var resolution = "解析位置 " + (probe.ResolutionLocation ?? "unknown")
            + " · 来源 " + (probe.ResolutionSource ?? "未知")
            + " · 时间 " + (probe.ResolvedAt is { } at ? UiText.ObservationTimeWithMillis(at) : "未知")
            + " · 地址 " + addresses;

        var failure = "HTTP " + (probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " · 耗时 " + probe.DurationMs.ToString("0", CultureInfo.InvariantCulture) + " ms"
            + (probe.OsErrorCode is { } os ? " · OS " + os.ToString(CultureInfo.InvariantCulture) : "")
            + (string.IsNullOrWhiteSpace(probe.TransportError) ? "" : " · 错误 " + probe.TransportError);

        var family = "请求族 " + (probe.AddressFamily ?? "未指定")
            + (probe.Status == EventStatus.Ok
                ? " · 失败归属：无（该路径成功）"
                : " · 失败归属 " + UiText.EndpointText(probe.FailedEndpoint));

        var timing = "时间可比：" + (probe.TimingComparable ? "生产方已确认" : "生产方未确认")
            + " · 拓扑变化：" + (probe.TopologyChanged ? "是" : "否")
            + " · 启动 " + UiText.ObservationTimeWithMillis(probe.StartedAt);

        return new ProbePathRow(UiText.PathText(path), UiText.StatusText(probe.Status),
            UiText.StatusTone(probe.Status), UiText.ClassificationText(probe.Classification),
            UiText.StageText(probe.FailedStage), UiText.EndpointText(probe.FailedEndpoint), family, failure,
            connection, resolution, timing, UiText.Shorten(probe.Notice, 320));
    }

    public static FamilyCapabilityRow FamilyCapability(FamilyCapability capability) => new(
        UiText.PathText(capability.Path),
        capability.RequestedFamily ?? "未指定",
        UiText.FamilyCapabilityText(capability),
        UiText.FamilyCapabilityTone(capability));

    public static ReasonRow Reason(EvidenceGapReason reason) => new(
        reason.Source, UiText.StatusText(reason.Status), UiText.StatusTone(reason.Status),
        reason.Summary, reason.Detail, reason.Count);

    public static ObservationChangeRow Change(ObservedChange change) => new(
        UiText.ChangeKindText(change.Kind), UiText.ChangeKindTone(change.Kind), change.IdentityText,
        change.FieldText, change.Before ?? "未观测到", change.After ?? "未观测到", change.Note);

    public static ObservationPairRow ObservationPair(AdjacentObservationChange pair)
    {
        var header = (pair.PreviousIndex + 1).ToString("00", CultureInfo.InvariantCulture)
            + " → " + (pair.CurrentIndex + 1).ToString("00", CultureInfo.InvariantCulture)
            + " 次观察 · " + UiText.ObservationTime(pair.PreviousObservedAt)
            + " → " + UiText.ObservationTime(pair.CurrentObservedAt);
        var interval = "间隔 " + pair.Interval.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
        return new ObservationPairRow(header, interval,
            pair.Changes.Count.ToString(CultureInfo.InvariantCulture) + " 项差异",
            pair.Changes.Select(Change).ToArray(),
            pair.Notes.Count == 0 ? "" : string.Join("；", pair.Notes));
    }
}
