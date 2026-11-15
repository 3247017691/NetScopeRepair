using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Domain;

namespace NetScope.Desktop.ViewModels;

/// <summary>
/// Home page: three separate questions. "Is local collection complete?" (采集健康),
/// "did an explicit external probe prove anything?" (网络验证) and "when did rules last run?"
/// (最近诊断时间). An empty diagnosis is never treated as health, and zero probe results are
/// always shown as "尚未进行外部验证" instead of a green sample.
/// </summary>
public sealed class HomeViewModel : ViewModelBase
{
    private readonly ObservationSession _session;
    private readonly Func<AppSettings> _settings;
    private readonly Func<CancellationToken, Task<SessionCycle?>> _diagnose;
    private string _healthTitle = "尚未开始观察";
    private string _healthDetail = "点击“开始观察”后，只在本机读取网卡、路由、DNS 与代理配置；不会自动发起外部探测。";
    private Brush _healthTone = UiText.GapBrush;
    private string _stateText = "空闲";
    private string _snapshotText = "尚未采集";
    private string _eventText = "0 条事件";
    private string _persistenceText = "日志尚未创建";
    private string _lastCycleText = "暂无采集";
    private string _targetText = "未配置探测目标";
    private string _collectionHealthTitle = "尚未采集";
    private string _collectionHealthDetail = "还没有完成一次本机采集。";
    private Brush _collectionHealthTone = UiText.GapBrush;
    private string _verificationTitle = "尚未进行外部验证";
    private string _verificationDetail = "还没有任何外部探测结果。";
    private string _verificationPathsText = "系统默认路径：未测 · 无显式代理（可能经 TUN）：未测 · 显式 HTTP 代理：未测";
    private Brush _verificationTone = UiText.GapBrush;
    private string _diagnosisTimeText = "尚未运行诊断";
    private string _diagnosisDetailText = "观察开始后，每轮本机采样都会运行本地规则。";
    private bool _isDemo;
    private bool _hasSample;
    private bool _diagnosisThisCycle;
    private DateTimeOffset? _diagnosedAt;
    private string _sessionPathText = "";
    private IReadOnlyList<ProbeResult> _lastProbes = Array.Empty<ProbeResult>();
    private DateTimeOffset? _lastProbeAt;
    private DateTimeOffset? _lastRuleRunAt;
    private int _lastDiagnosisCount;
    private int _probeCitationCount;
    private int _lastRuleFailureCount;
    private bool _lastCycleHadProbes;
    private List<DomainRow> _collectionProblems = new();

    public HomeViewModel(
        ObservationSession session,
        Func<AppSettings> settings,
        Func<CancellationToken, Task<SessionCycle?>> diagnose,
        Func<Exception, Task>? onError = null,
        Func<CancellationToken, Task<SessionCycle?>>? collect = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _diagnose = diagnose ?? throw new ArgumentNullException(nameof(diagnose));

        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart, ex => onError?.Invoke(ex));
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanPause, ex => onError?.Invoke(ex));
        DiagnoseCommand = new AsyncRelayCommand(
            async () => await _diagnose(CancellationToken.None),
            () => CanDiagnose, ex => onError?.Invoke(ex));
        CollectCommand = new AsyncRelayCommand(async () =>
        {
            if (collect is not null) await collect(CancellationToken.None);
            else ApplyCycle(await _session.CollectOnceAsync(CancellationToken.None));
        }, () => CanCollect, ex => onError?.Invoke(ex));
        ApplySessionState();
        RefreshTargetText();
        _sessionPathText = _session.SessionPath;
    }

    public ObservableCollection<DomainRow> Domains { get; } = new();

    public ObservableCollection<DiagnosisRow> RecentDiagnoses { get; } = new();

    public ICommand StartCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand DiagnoseCommand { get; }

    public ICommand CollectCommand { get; }

    /// <summary>Overall honest summary. Green is only allowed after a comparable passing probe.</summary>
    public string HealthTitle
    {
        get => _healthTitle;
        private set => Set(ref _healthTitle, value);
    }

    public string HealthDetail
    {
        get => _healthDetail;
        private set => Set(ref _healthDetail, value);
    }

    public Brush HealthTone
    {
        get => _healthTone;
        private set => Set(ref _healthTone, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public string SnapshotText
    {
        get => _snapshotText;
        private set => Set(ref _snapshotText, value);
    }

    public string EventText
    {
        get => _eventText;
        private set => Set(ref _eventText, value);
    }

    public string PersistenceText
    {
        get => _persistenceText;
        private set => Set(ref _persistenceText, value);
    }

    public string LastCycleText
    {
        get => _lastCycleText;
        private set => Set(ref _lastCycleText, value);
    }

    public string TargetText
    {
        get => _targetText;
        private set => Set(ref _targetText, value);
    }

    public string SessionPathText
    {
        get => _sessionPathText;
        private set => Set(ref _sessionPathText, value);
    }

    /// <summary>Collection health only answers whether the local data domains were readable.</summary>
    public string CollectionHealthTitle
    {
        get => _collectionHealthTitle;
        private set => Set(ref _collectionHealthTitle, value);
    }

    public string CollectionHealthDetail
    {
        get => _collectionHealthDetail;
        private set => Set(ref _collectionHealthDetail, value);
    }

    public Brush CollectionHealthTone
    {
        get => _collectionHealthTone;
        private set => Set(ref _collectionHealthTone, value);
    }

    /// <summary>Network verification only answers what the last explicit probe actually showed.</summary>
    public string VerificationTitle
    {
        get => _verificationTitle;
        private set => Set(ref _verificationTitle, value);
    }

    public string VerificationDetail
    {
        get => _verificationDetail;
        private set => Set(ref _verificationDetail, value);
    }

    public string VerificationPathsText
    {
        get => _verificationPathsText;
        private set => Set(ref _verificationPathsText, value);
    }

    public Brush VerificationTone
    {
        get => _verificationTone;
        private set => Set(ref _verificationTone, value);
    }

    /// <summary>Last rule run, kept separate from collection time and probe time.</summary>
    public string DiagnosisTimeText
    {
        get => _diagnosisTimeText;
        private set => Set(ref _diagnosisTimeText, value);
    }

    public string DiagnosisDetailText
    {
        get => _diagnosisDetailText;
        private set => Set(ref _diagnosisDetailText, value);
    }

    public bool HasExternalVerification => _lastProbes.Count > 0;

    public string VerificationBoundaryText =>
        "探测只覆盖已启用目标；.NET 探测不是 DSH / Antigravity / Chrome 的业务结果。";

    public bool CanStart => _session.State is ObservationState.Idle or ObservationState.Paused;

    public bool CanPause => _session.State == ObservationState.Observing;

    public bool CanCollect => !_isDemo && _session.State != ObservationState.Stopped;

    public bool CanDiagnose => CanCollect && HasConfiguredTarget;

    public bool HasConfiguredTarget
    {
        get
        {
            var settings = _settings();
            return Uri.TryCreate(settings.ProbeTargetEndpoint, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && string.IsNullOrEmpty(uri.UserInfo);
        }
    }

    public void RefreshTargetText()
    {
        var settings = _settings();
        TargetText = HasConfiguredTarget
            ? "目标档案 " + (settings.ProbeTargetId ?? "target-user-configured")
              + " · " + settings.ProbeTargetEndpoint + " · 期望 HTTP " + settings.ExpectedStatus + " · 方法 GET"
            : "未配置探测目标；在“设置”中填写后才能发起外部验证。未配置时仍会运行本地规则诊断。";
        Raise(nameof(CanDiagnose));
        (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        if (!_isDemo) RecomputeVerification();
    }

    public void ApplySessionState()
    {
        if (_isDemo)
        {
            StateText = "演示";
            return;
        }

        StateText = _session.State switch
        {
            ObservationState.Idle => "空闲",
            ObservationState.Observing => "观察中",
            ObservationState.Paused => "已暂停",
            _ => "已停止",
        };
        Raise(nameof(CanStart));
        Raise(nameof(CanPause));
        Raise(nameof(CanCollect));
        Raise(nameof(CanDiagnose));
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CollectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        if (_session.State == ObservationState.Paused)
        {
            HealthTitle = "采集已暂停";
            HealthDetail = "暂停期间不产生新样本；已有记录仍然可读，但不能当作当前状态。";
            HealthTone = UiText.GapBrush;
            CollectionHealthTitle = "采集已暂停";
            CollectionHealthDetail = "暂停期间不产生新的本机样本；已有数据域结果保持上一次的值。";
            CollectionHealthTone = UiText.GapBrush;
        }
        else if (_session.State == ObservationState.Observing)
        {
            Recompute();
        }
    }

    public void ApplyCycle(SessionCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        _isDemo = false;
        _hasSample = true;
        Domains.Clear();
        var snapshot = cycle.Snapshot;
        Domains.Add(UiRows.Domain("interfaces", snapshot.Interfaces));
        Domains.Add(UiRows.Domain("routes", snapshot.Routes));
        Domains.Add(UiRows.Domain("dns", snapshot.Dns));
        var proxyDomain = snapshot.Proxies;
        if (proxyDomain.Status == NetScope.Domain.EventStatus.Ok && snapshot.ProxyCompleteness() != NetScope.Domain.EventStatus.Ok
            && proxyDomain.Value is { } proxy)
        {
            var layers = new[] { ("进程环境", proxy.ToolProcessEnvironment), ("用户环境", proxy.UserEnvironment),
                ("机器环境", proxy.MachineEnvironment), ("WinINET", proxy.WinInet), ("WinHTTP", proxy.WinHttp) };
            proxyDomain = Collected<ProxyState>.Partial(proxy, string.Join("；", layers
                .Where(item => item.Item2.Status != NetScope.Domain.EventStatus.Ok)
                .Select(item => item.Item1 + " " + UiText.StatusText(item.Item2.Status))));
        }
        Domains.Add(UiRows.Domain("proxy", proxyDomain));
        if (cycle.RuleFailures.Count > 0)
            Domains.Add(new DomainRow("诊断规则", "执行失败", cycle.RuleFailures.Count + " 条规则没有产生结论", UiText.WarnBrush));
        Domains.Add(new DomainRow("日志", cycle.Persisted ? "已写入" : "写入失败",
            cycle.PersistenceError ?? "本批事件已确认落盘", cycle.Persisted ? UiText.OkBrush : UiText.WarnBrush));

        _collectionProblems = Domains
            .Where(row => row.Name is not "诊断规则")
            .Where(row => row.StatusText is not ("正常" or "已写入"))
            .ToList();

        _lastRuleFailureCount = cycle.RuleFailures.Count;
        _lastCycleHadProbes = cycle.Evidence.Probes.Count > 0;
        if (_lastCycleHadProbes)
        {
            _lastProbes = cycle.Evidence.Probes;
            _lastProbeAt = cycle.CompletedAt;
        }

        _lastRuleRunAt = cycle.CompletedAt;
        _lastDiagnosisCount = cycle.Diagnoses.Count;
        var probeIds = cycle.Evidence.Probes.Select(probe => probe.ProbeId).ToHashSet(StringComparer.Ordinal);
        _probeCitationCount = probeIds.Count == 0
            ? 0
            : cycle.Diagnoses.Count(diagnosis => diagnosis.EvidenceIds.Any(probeIds.Contains));

        _diagnosisThisCycle = cycle.DiagnosisRequested || cycle.Diagnoses.Count > 0 || cycle.RuleFailures.Count > 0;
        if (_diagnosisThisCycle || cycle.Diagnoses.Count > 0)
        {
            _diagnosedAt = cycle.CompletedAt;
            RecentDiagnoses.Clear();
            foreach (var diagnosis in cycle.Diagnoses)
                RecentDiagnoses.Add(UiRows.Diagnosis(diagnosis, cycle.Evidence));
        }
        SessionPathText = _session.SessionPath;
        RefreshTargetText();

        SnapshotText = "最近快照 " + snapshot.ObservedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        EventText = $"{_session.EventCount} 条事件 · {_session.SnapshotCount} 个快照";
        PersistenceText = cycle.Persisted
            ? "日志已追加 " + cycle.AppendedEvents + " 条事件"
            : "日志写入失败：" + (cycle.PersistenceError ?? "未知原因");
        LastCycleText = _diagnosisThisCycle
            ? "手动诊断 " + cycle.CompletedAt.ToLocalTime().ToString("HH:mm:ss") + " · " + cycle.Diagnoses.Count + " 条结论"
            : "本轮仅采集本机快照，未复测连通性" + (_diagnosedAt is { } at ? "；最近诊断 " + at.ToLocalTime().ToString("HH:mm:ss") : "");
        StatusMessage = cycle.RuleFailures.Count == 0
            ? null
            : cycle.RuleFailures.Count + " 条规则执行失败，已在“诊断与证据”中单列。";
        Recompute();
    }

    public void ShowDemo(EvidenceSet evidence, IReadOnlyList<Diagnosis> diagnoses, string demoPath)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _isDemo = true;
        Raise(nameof(CanCollect));
        Raise(nameof(CanDiagnose));
        (CollectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        _hasSample = true;
        Domains.Clear();
        var snapshot = evidence.Snapshot;
        if (snapshot is not null)
        {
            Domains.Add(UiRows.Domain("interfaces", snapshot.Interfaces));
            Domains.Add(UiRows.Domain("routes", snapshot.Routes));
            Domains.Add(UiRows.Domain("dns", snapshot.Dns));
            Domains.Add(UiRows.Domain("proxy", snapshot.Proxies));
            Domains.Add(new DomainRow("数据来源", "合成", "演示会话，不是本机观测", UiText.AccentBrush));
        }
        else
        {
            Domains.Add(new DomainRow("数据来源", "不可用", "演示证据缺少快照。", UiText.WarnBrush));
        }

        RecentDiagnoses.Clear();
        foreach (var diagnosis in diagnoses)
            RecentDiagnoses.Add(UiRows.Diagnosis(diagnosis, evidence));

        _lastProbes = evidence.Probes;
        _lastProbeAt = snapshot?.ObservedAt;
        _lastRuleRunAt = snapshot?.ObservedAt;
        _lastDiagnosisCount = diagnoses.Count;
        _probeCitationCount = diagnoses.Count(diagnosis =>
            diagnosis.EvidenceIds.Any(id => evidence.Probes.Any(probe => probe.ProbeId == id)));
        _lastRuleFailureCount = 0;
        _lastCycleHadProbes = evidence.Probes.Count > 0;
        _collectionProblems = new List<DomainRow>();

        HealthTitle = "合成演示（非真实观测）";
        HealthDetail = "以下数据全部来自 mock 场景，用于演示页面与证据链；不代表本机状态，也不会发起网络请求或修改系统。";
        HealthTone = UiText.AccentBrush;
        CollectionHealthTitle = "合成演示（非真实观测）";
        CollectionHealthDetail = "演示数据由 mock 生成；采集健康只用于展示布局。";
        CollectionHealthTone = UiText.AccentBrush;
        VerificationTitle = "合成演示：三路径为 mock";
        VerificationDetail = "演示中的路径结果由 mock 场景生成，不会联网；它不是本机验证，也不代表任何业务已恢复。";
        VerificationPathsText = PathSummary(evidence.Probes);
        VerificationTone = UiText.AccentBrush;
        DiagnosisTimeText = "合成演示诊断 · " + diagnoses.Count + " 条结论";
        DiagnosisDetailText = "演示结论只用于展示证据链与缺口表达，不能当作真实诊断。";
        StateText = "演示";
        SnapshotText = snapshot is null
            ? "无快照"
            : "合成快照 " + snapshot.ObservedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        EventText = "11 条合成事件";
        PersistenceText = "演示文件已保存到本机会话目录（synthetic=true）";
        LastCycleText = diagnoses.Count == 0 ? "演示没有产生结论" : "演示产生 " + diagnoses.Count + " 条结论";
        TargetText = "演示目标 target-demo（合成，不会联网）";
        SessionPathText = demoPath;
        StatusMessage = null;
    }

    public void ClearDemo()
    {
        if (!_isDemo) return;
        _isDemo = false;
        _hasSample = false;
        Domains.Clear();
        RecentDiagnoses.Clear();
        _lastProbes = Array.Empty<ProbeResult>();
        _lastProbeAt = null;
        _lastRuleRunAt = null;
        _lastDiagnosisCount = 0;
        _probeCitationCount = 0;
        _lastRuleFailureCount = 0;
        _lastCycleHadProbes = false;
        _diagnosisThisCycle = false;
        _diagnosedAt = null;
        _collectionProblems = new List<DomainRow>();
        SnapshotText = "尚未采集";
        EventText = "0 条事件";
        PersistenceText = "日志尚未创建";
        LastCycleText = "暂无采集";
        SessionPathText = _session.SessionPath;
        CollectionHealthTitle = "尚未采集";
        CollectionHealthDetail = "还没有完成一次本机采集。";
        CollectionHealthTone = UiText.GapBrush;
        VerificationTitle = "尚未进行外部验证";
        VerificationDetail = "还没有任何外部探测结果。";
        VerificationPathsText = "系统默认路径：未测 · 无显式代理（可能经 TUN）：未测 · 显式 HTTP 代理：未测";
        VerificationTone = UiText.GapBrush;
        DiagnosisTimeText = "尚未运行诊断";
        DiagnosisDetailText = "观察开始后，每轮本机采样都会运行本地规则。";
        RefreshTargetText();
        ApplySessionState();
    }

    public void ReportFailure(string type)
    {
        HealthTitle = "采集不足";
        HealthDetail = "本次采集没有完成（" + type + "）；界面不会把未知当作正常。";
        HealthTone = UiText.WarnBrush;
        CollectionHealthTitle = "采集不足";
        CollectionHealthDetail = "本次采集没有完成（" + type + "）；未知不会被当作正常。";
        CollectionHealthTone = UiText.WarnBrush;
        StatusMessage = "采集失败：" + type;
    }

    private void Recompute()
    {
        if (_isDemo) return;
        if (!_hasSample)
        {
            HealthTitle = _session.State == ObservationState.Observing ? "等待首个样本" : "尚未采集";
            HealthDetail = _session.State == ObservationState.Observing
                ? "观察已开始，但还没有完成一次采集；在首个样本完成前不会显示“正常”。"
                : "还没有完成一次采集；未知不会被当作正常。";
            HealthTone = UiText.GapBrush;
            CollectionHealthTitle = _session.State == ObservationState.Observing ? "等待首个样本" : "尚未采集";
            CollectionHealthDetail = "还没有可用的本机数据域结果。";
            CollectionHealthTone = UiText.GapBrush;
            RecomputeDiagnosisTime();
            return;
        }

        if (_session.State is ObservationState.Idle or ObservationState.Stopped)
        {
            HealthTitle = "尚未开始观察";
            HealthDetail = "点击“开始观察”后建立本地会话；退出应用会停止记录。";
            HealthTone = UiText.GapBrush;
            CollectionHealthTitle = "尚未开始观察";
            CollectionHealthDetail = "还没有开始本机观察。";
            CollectionHealthTone = UiText.GapBrush;
            RecomputeDiagnosisTime();
            return;
        }

        if (_session.State == ObservationState.Paused)
        {
            HealthTitle = "采集已暂停";
            HealthDetail = "暂停期间不产生新样本；已有记录仍然可读，但不能当作当前状态。";
            HealthTone = UiText.GapBrush;
            CollectionHealthTitle = "采集已暂停";
            CollectionHealthDetail = "暂停期间不产生新的本机样本。";
            CollectionHealthTone = UiText.GapBrush;
            RecomputeDiagnosisTime();
            return;
        }

        if (_collectionProblems.Count > 0)
        {
            CollectionHealthTitle = "采集不足";
            CollectionHealthDetail = string.Join("；", _collectionProblems.Select(row => row.Name + " " + row.StatusText));
            CollectionHealthTone = UiText.WarnBrush;
        }
        else
        {
            CollectionHealthTitle = "采集完整";
            CollectionHealthDetail = "本机数据域完整；这只描述采集，不代表网络连通性，也不代表外部目标可达。";
            CollectionHealthTone = UiText.OkBrush;
        }

        RecomputeVerification();
        RecomputeDiagnosisTime();

        if (_collectionProblems.Count > 0)
        {
            HealthTitle = "采集不足";
            HealthDetail = CollectionHealthDetail;
            HealthTone = UiText.WarnBrush;
            return;
        }

        var strongest = RecentDiagnoses
            .FirstOrDefault(row => row.LevelText is "证据支持" or "已验证" or "直接观察" or "候选线索");
        if (strongest is not null)
        {
            HealthTitle = _diagnosisThisCycle ? "发现异常" : "最近诊断有异常（未复测）";
            HealthDetail = (_diagnosisThisCycle || _diagnosedAt is not { } at ? "" : at.ToLocalTime().ToString("HH:mm:ss") + " · ")
                + strongest.LevelText + " · " + strongest.Summary;
            HealthTone = strongest.LevelText is "证据支持" or "已验证" ? UiText.BadBrush : UiText.WarnBrush;
            return;
        }

        // Contradicted / insufficient conclusions are not anomalies; they must not read as one.
        var residual = RecentDiagnoses.FirstOrDefault();
        if (residual is not null)
        {
            HealthTitle = "证据不足或存在反证";
            HealthDetail = residual.LevelText + " · " + residual.Summary;
            HealthTone = UiText.WarnBrush;
            return;
        }

        HealthTitle = VerificationTitle;
        HealthDetail = VerificationDetail;
        HealthTone = VerificationTone;
    }

    /// <summary>
    /// 0 probe results never become green: an empty diagnosis set only means "no conclusion yet",
    /// not that the network works. A stale passing probe is reported as "needs re-test".
    /// </summary>
    private void RecomputeVerification()
    {
        VerificationPathsText = PathSummary(_lastProbes);
        if (_lastProbes.Count == 0)
        {
            var configured = HasConfiguredTarget;
            VerificationTitle = "尚未进行外部验证";
            VerificationTone = UiText.GapBrush;
            VerificationDetail = configured
                ? "目标已配置，但还没有任何外部探测结果；点击“立即诊断”才会请求该目标。没有诊断结论不等于网络正常，也不代表全网正常。"
                : "还没有任何外部探测结果；空诊断不代表全网正常。未配置探测目标时仍会运行本地规则诊断；外部验证需要先在“设置”中填写目标，再手动点击“立即诊断”。";
            return;
        }

        var groups = ProbeComparisonExplainer.Compare(_lastProbes);
        var group = groups.Count == 0 ? null : groups[^1];
        var at = _lastProbeAt is { } time ? time.ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间未知";
        if (group is null)
        {
            VerificationTitle = "外部验证结果无法解释";
            VerificationTone = UiText.WarnBrush;
            VerificationDetail = "有探测结果，但无法按探测组读取它们；详情见“诊断与证据”。";
            return;
        }

        var failed = group.Results.Where(probe => probe.Status is EventStatus.Failed or EventStatus.Timeout).ToArray();
        var gaps = group.Results.Where(probe =>
            probe.Status is EventStatus.Partial or EventStatus.Unsupported or EventStatus.Cancelled
                or EventStatus.AccessDenied or EventStatus.Unknown).ToArray();
        var reasons = string.Join("；", group.Results
            .Where(probe => probe.Status != EventStatus.Ok)
            .Select(probe => UiText.PathText(probe.Path) + " " + UiText.StatusText(probe.Status)
                + "（阶段 " + UiText.StageText(probe.FailedStage)
                + (string.IsNullOrWhiteSpace(probe.TransportError) ? "" : " · " + probe.TransportError) + "）"));

        if (failed.Length > 0 || gaps.Length > 0)
        {
            VerificationTitle = failed.Length > 0 ? "外部验证未通过" : "外部验证存在缺口";
            VerificationTone = failed.Length > 0 ? UiText.BadBrush : UiText.WarnBrush;
            VerificationDetail = "目标 " + group.TargetId + " · " + at + " · " + reasons
                + "。只有 " + group.Results.Count(probe => probe.Status == EventStatus.Ok) + "/"
                + group.Results.Count + " 条路径成功；不把缺失或未支持当作通过。";
            return;
        }

        if (!group.Comparability.Comparable)
        {
            VerificationTitle = "外部验证不可比";
            VerificationTone = UiText.WarnBrush;
            VerificationDetail = "目标 " + group.TargetId + " · " + at + "："
                + string.Join("；", group.Comparability.Unmet.Select(UiText.ComparabilityText))
                + "。三条路径只有在同目标、同方法、同地址族、同窗口时才合并比较；当前只作单路径观察。";
            return;
        }

        if (!_lastCycleHadProbes)
        {
            VerificationTitle = "外部验证需复测";
            VerificationTone = UiText.WarnBrush;
            VerificationDetail = "最近一次外部验证 " + at + " 通过（目标 " + group.TargetId
                + "）；当前采样没有复测目标连通性。结论只在那个时间窗内成立。";
            return;
        }

        VerificationTitle = "外部验证通过（受控目标）";
        VerificationTone = UiText.OkBrush;
        VerificationDetail = "目标 " + group.TargetId + " · " + at
            + "：三条路径在同一目标、方法、地址族与观察窗口内成功。只覆盖该探测目标，不代表全网正常，"
            + "也不代表 DSH / Antigravity / Chrome 的业务已恢复。";
    }

    private void RecomputeDiagnosisTime()
    {
        if (_lastRuleRunAt is not { } at)
        {
            DiagnosisTimeText = "尚未运行诊断";
            DiagnosisDetailText = "观察开始后，每轮本机采样都会运行本地规则。";
            return;
        }

        DiagnosisTimeText = "最近诊断 " + at.ToLocalTime().ToString("HH:mm:ss")
            + " · " + _lastDiagnosisCount + " 条结论";
        if (_lastRuleFailureCount > 0)
        {
            DiagnosisDetailText = _lastRuleFailureCount + " 条规则执行失败，没有生成结论；失败不会被写成正常。";
            return;
        }

        if (_lastDiagnosisCount == 0)
        {
            DiagnosisDetailText = "本次没有产生结论。空结论不等于正常；请查看采集缺口与时间线。";
            return;
        }

        DiagnosisDetailText = _probeCitationCount > 0
            ? _probeCitationCount + " 条结论引用了外部探测结果；其余结论只基于本机证据。"
            : "结论只基于本机证据，没有验证外部目标连通性。";
    }

    private static string PathSummary(IReadOnlyList<ProbeResult> probes)
    {
        var parts = new List<string>();
        foreach (var path in ProbeComparisonExplainer.Paths)
        {
            var probe = probes.LastOrDefault(item => item.Path == path);
            parts.Add(UiText.PathText(path) + "：" + (probe is null ? "未测" : UiText.StatusText(probe.Status)));
        }

        return string.Join(" · ", parts);
    }

    private Task StartAsync()
    {
        ClearDemo();
        _hasSample = false;
        _diagnosedAt = null;
        _diagnosisThisCycle = false;
        _lastProbes = Array.Empty<ProbeResult>();
        _lastProbeAt = null;
        _lastRuleRunAt = null;
        _lastDiagnosisCount = 0;
        _probeCitationCount = 0;
        _lastRuleFailureCount = 0;
        _lastCycleHadProbes = false;
        _collectionProblems = new List<DomainRow>();
        Domains.Clear();
        RecentDiagnoses.Clear();
        SnapshotText = "等待首个样本";
        LastCycleText = "等待首个样本";
        _session.Start(_settings().ObservationInterval);
        ApplySessionState();
        StatusMessage = "观察已开始；关闭窗口只会隐藏到托盘，显式退出才停止记录。";
        return Task.CompletedTask;
    }

    private Task PauseAsync()
    {
        _session.Pause();
        ApplySessionState();
        StatusMessage = "观察已暂停；不会继续产生快照。";
        return Task.CompletedTask;
    }
}
