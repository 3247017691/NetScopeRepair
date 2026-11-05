using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using NetScope.Application;
using NetScope.Desktop.Services;

namespace NetScope.Desktop.ViewModels;

/// <summary>
/// Home page: the honest state of the current sample, the observation session and the latest
/// diagnoses. "正常" here only ever means "this sample was complete"; it is never "网络正常".
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
    private bool _isDemo;
    private bool _hasSample;
    private bool _diagnosisThisCycle;
    private DateTimeOffset? _diagnosedAt;
    private string _sessionPathText = "";

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
            ? $"探测目标 {settings.ProbeTargetEndpoint} · 期望 HTTP {settings.ExpectedStatus}"
            : "未配置探测目标；在“设置”中填写后才能发起探测。";
        Raise(nameof(CanDiagnose));
        (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        _diagnosisThisCycle = cycle.DiagnosisRequested || cycle.Diagnoses.Count > 0 || cycle.RuleFailures.Count > 0;
        if (_diagnosisThisCycle)
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

        HealthTitle = "合成演示（非真实观测）";
        HealthDetail = "以下数据全部来自 mock 场景，用于演示页面与证据链；不代表本机状态，也不会发起网络请求或修改系统。";
        HealthTone = UiText.AccentBrush;
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
        SnapshotText = "尚未采集";
        EventText = "0 条事件";
        PersistenceText = "日志尚未创建";
        LastCycleText = "暂无采集";
        SessionPathText = _session.SessionPath;
        RefreshTargetText();
        ApplySessionState();
    }

    public void ReportFailure(string type)
    {
        HealthTitle = "采集不足";
        HealthDetail = "本次采集没有完成（" + type + "）；界面不会把未知当作正常。";
        HealthTone = UiText.WarnBrush;
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
            return;
        }

        if (_session.State is ObservationState.Idle or ObservationState.Stopped)
        {
            HealthTitle = "尚未开始观察";
            HealthDetail = "点击“开始观察”后建立本地会话；退出应用会停止记录。";
            HealthTone = UiText.GapBrush;
            return;
        }

        if (_session.State == ObservationState.Paused)
        {
            HealthTitle = "采集已暂停";
            HealthDetail = "暂停期间不产生新样本；已有记录仍然可读，但不能当作当前状态。";
            HealthTone = UiText.GapBrush;
            return;
        }

        var incomplete = Domains.Where(row => row.StatusText is not ("正常" or "已写入")).ToArray();
        if (incomplete.Length > 0)
        {
            HealthTitle = "采集不足";
            HealthDetail = string.Join("；", incomplete.Select(row => row.Name + " " + row.StatusText));
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

        HealthTitle = "当前样本正常";
        HealthDetail = "仅表示本次采集的数据域完整、没有产生异常结论；不代表全网正常，也不代表其他应用已恢复。";
        HealthTone = UiText.OkBrush;
    }

    private Task StartAsync()
    {
        ClearDemo();
        _hasSample = false;
        _diagnosedAt = null;
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
