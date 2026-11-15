using System.Windows.Media;
using NetScope.Desktop.Services;
using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Desktop.ViewModels;

public sealed record NavSection(string Title, string Caption, ViewModelBase ViewModel, string IconGeometry = "");

/// <summary>
/// Composition root for the shell. It owns the read-only observation session, routes session
/// events to the pages, and wires the home/diagnosis entry points into the user-confirmed repair page.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string?> _pickFolder;
    private AppSettings _settings;
    private NavSection _selectedSection;
    private string _stateText = "空闲";
    private Brush _stateTone = UiText.GapBrush;
    private string _errorText = "";
    private bool _isBusy;
    private bool _isDemo;
    private string _demoPath = "";
    private bool _switchingMode;
    private CancellationTokenSource? _manualSource;

    public MainViewModel(AppServices services, IUiDispatcher dispatcher, Func<string?>? pickFolder = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pickFolder = pickFolder ?? (() => null);
        _settings = services.Settings;

        QuickRepair = new QuickRepairViewModel(
            new QuickRepairService(sink: new SessionRepairEventSink(services.Session, services.Clock)),
            sessionPath: () => services.Session.SessionPath,
            copyToClipboard: System.Windows.Clipboard.SetText);
        Home = new HomeViewModel(services.Session, () => _settings, DiagnoseAsync, HandleErrorAsync, CollectAsync,
            openRepair: () => OpenRepairAsync(null));
        Timeline = new TimelineViewModel();
        Diagnoses = new DiagnosesViewModel(ruleId => OpenRepairAsync(ruleId));
        CaseReplay = new CaseReplayViewModel();
        Clients = new ClientsViewModel(services.Clients, services.Explanations);
        Export = new ExportViewModel(services.Exporter, () => DisplaySessionPath, _pickFolder);
        Settings = new SettingsViewModel(services.SettingsStore, _settings, ApplySettings);
        Help = new HelpViewModel(services.Session.SessionPath, services.Paths.Root);

        Sections = new[]
        {
            new NavSection("首页", "当前状态与观察会话", Home,
                "M3 9.5L12 3l9 6.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1V9.5z"),
            new NavSection("时间线", "事件、缺口与损坏行", Timeline,
                "M3 12h4l3 8 4-16 3 8h4"),
            new NavSection("诊断与证据", "结论、证据与反证", Diagnoses,
                "M12 2l8 4v6c0 5.25-3.5 10-8 11-4.5-1-8-5.75-8-11V6l8-4zm-2 10l2 2 4-4"),
            new NavSection("案例回放", "固定规则重放本地故障包", CaseReplay,
                "M4 4h16v12H4zM8 20h8M12 16v4M9 9l3-3 3 3M12 6v5"),
            new NavSection("修复", "本机真实体检 · TUN/残留/代理 · 可回滚", QuickRepair,
                "M12 2v4M12 18v4M4.9 4.9l2.8 2.8M16.3 16.3l2.8 2.8M2 12h4M18 12h4M4.9 19.1l2.8-2.8M16.3 7.7l2.8-2.8M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z"),
            new NavSection("多客户端", "只读状态与依赖解释", Clients,
                "M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5"),
            new NavSection("导出", "脱敏预览与本地 ZIP", Export,
                "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"),
            new NavSection("设置", "观察与探测目标", Settings,
                "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6"),
            new NavSection("帮助", "离线说明与边界", Help,
                "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zm-1-7h2v2h-2zm1-9a3.5 3.5 0 0 0-3.5 3.5h2a1.5 1.5 0 1 1 3 0c0 1-1.5 1.5-2.2 2.2-.5.5-.8 1.1-.8 1.8h2c0-.5.2-.8.5-1.1.7-.7 2-1.4 2-2.9A3.5 3.5 0 0 0 12 6z"),
        };
        _selectedSection = Sections[0];
        LoadDemoCommand = new AsyncRelayCommand(() => LoadDemoAsync(), null, ex => _ = HandleErrorAsync(ex));
        RefreshTimelineCommand = new AsyncRelayCommand(
            async () => await Timeline.LoadAsync(DisplaySessionPath), null, ex => _ = HandleErrorAsync(ex));

        services.Session.StateChanged += (_, _) => _dispatcher.Post(UpdateSessionState);
        services.Session.CycleCompleted += (_, cycle) => _dispatcher.Post(() => OnCycle(cycle));
        services.Session.CollectionFailed += (_, type) => _dispatcher.Post(() => Home.ReportFailure(type));
        UpdateSessionState();
        if (Clients.HasProvider) _ = Clients.RefreshAsync(CancellationToken.None);

        QuickRepair.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QuickRepairViewModel.HasPlan) or nameof(QuickRepairViewModel.SummaryText))
                _dispatcher.Post(() => Home.RepairHintText = QuickRepair.HasPlan
                    ? "发现 " + QuickRepair.Actions.Count + " 项可执行修复动作 → 去修复"
                    : "修复：体检本机，发现可修复问题");
        };
    }

    public HomeViewModel Home { get; }

    public TimelineViewModel Timeline { get; }

    public DiagnosesViewModel Diagnoses { get; }

    /// <summary>
    /// Real, user-confirmed repair. This one reads and writes the local machine.
    /// </summary>
    public QuickRepairViewModel QuickRepair { get; }

    /// <summary>T12 read-only case replay workbench; local packages only, never a live probe.</summary>
    public CaseReplayViewModel CaseReplay { get; }

    public ClientsViewModel Clients { get; }

    public ExportViewModel Export { get; }

    public SettingsViewModel Settings { get; }

    public HelpViewModel Help { get; }

    public IReadOnlyList<NavSection> Sections { get; }

    public System.Windows.Input.ICommand LoadDemoCommand { get; }

    public System.Windows.Input.ICommand RefreshTimelineCommand { get; }

    public NavSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !Set(ref _selectedSection, value)) return;
            Raise(nameof(PageTitle));
            Raise(nameof(PageCaption));
        }
    }

    public string PageTitle => SelectedSection.Title;

    public string PageCaption => SelectedSection.Caption;

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public Brush StateTone
    {
        get => _stateTone;
        private set
        {
            if (Set(ref _stateTone, value)) Raise(nameof(StateBg));
        }
    }

    public Brush StateBg => UiText.SoftBackground(StateTone);

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool IsDemo
    {
        get => _isDemo;
        private set
        {
            if (Set(ref _isDemo, value)) Raise(nameof(DemoBannerText));
        }
    }

    public string DemoPath
    {
        get => _demoPath;
        private set
        {
            if (Set(ref _demoPath, value)) Raise(nameof(DemoBannerText));
        }
    }

    public string DemoBannerText => IsDemo
        ? "正在查看合成演示数据（mock），不是本机真实观测：" + DemoPath
        : "本机只读会话：" + _services.Session.SessionPath;

    public string DisplaySessionPath => IsDemo ? DemoPath : _services.Session.SessionPath;

    public string AppVersionText => "修复 · 本机受控写入 · 可回滚";

    public AppSettings SettingsSnapshot => _settings;

    public async Task<SessionCycle?> DiagnoseAsync(CancellationToken cancellationToken)
    {
        if (IsDemo || _switchingMode || IsBusy)
        {
            StatusMessage = "当前正在查看演示或有操作进行中；回到真实观察后再启动诊断。";
            return null;
        }
        if (!TryBuildTarget(out var target, out var error))
        {
            ErrorText = error ?? "请先在设置中配置探测目标。";
            return null;
        }
        return await RunManualAsync(token => _services.Session.RunDiagnosisAsync(
            target, _settings.ExplicitProxyEndpoint, _settings.AddressFamily, token), cancellationToken);
    }

    private async Task<SessionCycle?> CollectAsync(CancellationToken cancellationToken)
    {
        if (IsDemo || _switchingMode || IsBusy) return null;
        return await RunManualAsync(_services.Session.CollectOnceAsync, cancellationToken);
    }

    private async Task OpenRepairAsync(string? ruleId)
    {
        try
        {
            SelectedSection = Sections.First(section => ReferenceEquals(section.ViewModel, QuickRepair));
            await QuickRepair.PreselectForDiagnosisAsync(ruleId);
        }
        catch (Exception exception)
        {
            await HandleErrorAsync(exception);
        }
    }

    private async Task<SessionCycle> RunManualAsync(Func<CancellationToken, Task<SessionCycle>> work,
        CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _manualSource = source;
        IsBusy = true;
        ErrorText = "";
        try { return await work(source.Token); }
        finally
        {
            if (ReferenceEquals(_manualSource, source)) _manualSource = null;
            IsBusy = _switchingMode;
        }
    }

    public async Task LoadDemoAsync(CancellationToken cancellationToken = default)
    {
        if (_switchingMode) return;
        _switchingMode = true;
        try
        {
            IsBusy = true;
            var pausedForDemo = _services.Session.State == ObservationState.Observing;
            _manualSource?.Cancel();
            await _services.Session.PauseAsync().ConfigureAwait(true);
            await Clients.SetDemoModeAsync(true, cancellationToken).ConfigureAwait(true);

            var path = await SyntheticShowcase.CreateAsync(_services.Paths, cancellationToken).ConfigureAwait(true);
            var replay = await Task.Run(() => new ReplayService().Replay(path), cancellationToken)
                .ConfigureAwait(true);
            await Timeline.LoadAsync(path, cancellationToken).ConfigureAwait(true);
            Diagnoses.LoadReplay(replay);
            if (replay.Evidence is { } demoEvidence) Home.ShowDemo(demoEvidence, replay.Diagnoses, path);
            DemoPath = path;
            IsDemo = true;
            UpdateSessionState();
            Export.Reset();
            ErrorText = "";
            StatusMessage = (pausedForDemo ? "已暂停真实观察，" : "")
                + "已载入合成演示：界面数据全部来自 mock，不会发起网络请求，也不会修改系统。点击“开始观察”会回到真实会话。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            ErrorText = "载入合成演示失败：" + exception.GetType().Name;
        }
        finally
        {
            _switchingMode = false;
            IsBusy = false;
        }
    }

    public async Task ExitAsync()
    {
        try
        {
            await _services.Session.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("stop_failed:" + exception.GetType().Name);
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _services.Settings = settings;
        Home.RefreshTargetText();
        StatusMessage = "设置已保存；观察间隔 " + (int)settings.ObservationInterval.TotalSeconds + " 秒将在下次开始观察时生效。";
    }

    private void OnCycle(SessionCycle cycle)
    {
        // Pausing and joining cannot remove callbacks already queued on the UI dispatcher.
        if (_switchingMode || IsDemo || _services.Session.State == ObservationState.Stopped) return;
        Home.ApplyCycle(cycle);
        if (cycle.DiagnosisRequested || cycle.Diagnoses.Count > 0 || cycle.RuleFailures.Count > 0)
            Diagnoses.Apply(cycle.Evidence, cycle.Diagnoses, cycle.RuleFailures, cycle.CompletedAt);
        if (!IsDemo)
        {
            Timeline.MarkDirty();
            Export.Reset();
        }

        UpdateSessionState();
    }

    private void UpdateSessionState()
    {
        var state = _services.Session.State;
        if (_isDemo && state == ObservationState.Observing)
        {
            IsDemo = false;
            Home.ClearDemo();
            Diagnoses.Clear();
            Timeline.Clear();
            Export.Reset();
            _ = Clients.SetDemoModeAsync(false, CancellationToken.None);
        }

        StateText = IsDemo ? "演示" : state switch
        {
            ObservationState.Idle => "空闲",
            ObservationState.Observing => "观察中",
            ObservationState.Paused => "已暂停",
            _ => "已停止",
        };
        StateTone = IsDemo ? UiText.AccentBrush : state switch
        {
            ObservationState.Observing => UiText.OkBrush,
            ObservationState.Paused => UiText.WarnBrush,
            _ => UiText.GapBrush,
        };
        Home.ApplySessionState();
    }

    private bool TryBuildTarget(out NetScope.Application.ProbeTarget target, out string? error)
    {
        target = new NetScope.Application.ProbeTarget
        {
            TargetId = string.IsNullOrWhiteSpace(_settings.ProbeTargetId) ? "target-user-configured" : _settings.ProbeTargetId,
            Endpoint = _settings.ProbeTargetEndpoint,
            ExpectedStatusCodes = new[] { _settings.ExpectedStatus },
        };
        error = null;
        if (!Uri.TryCreate(_settings.ProbeTargetEndpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "请先在“设置”中填写一个 http(s) 探测目标；M1 不会自动探测外部网络。";
            return false;
        }

        return true;
    }

    private Task HandleErrorAsync(Exception exception)
    {
        ErrorText = "操作失败：" + exception.GetType().Name + "。没有修改系统或网络设置。";
        AppLog.Write("ui_error:" + exception.GetType().Name);
        return Task.CompletedTask;
    }
}

