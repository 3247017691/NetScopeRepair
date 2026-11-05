using System.Windows.Media;
using NetScope.Desktop.Services;
using NetScope.Storage;

namespace NetScope.Desktop.ViewModels;

public sealed record NavSection(string Title, string Caption, ViewModelBase ViewModel);

/// <summary>
/// Composition root for the shell. It owns the read-only observation session and routes session
/// events to the pages; it exposes no repair action.
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

        Home = new HomeViewModel(services.Session, () => _settings, DiagnoseAsync, HandleErrorAsync, CollectAsync);
        Timeline = new TimelineViewModel();
        Diagnoses = new DiagnosesViewModel();
        Clients = new ClientsViewModel(services.Clients, services.Explanations);
        Export = new ExportViewModel(services.Exporter, () => DisplaySessionPath, _pickFolder);
        Settings = new SettingsViewModel(services.SettingsStore, _settings, ApplySettings);
        Help = new HelpViewModel(services.Session.SessionPath, services.Paths.Root);

        Sections = new[]
        {
            new NavSection("首页", "当前状态与观察会话", Home),
            new NavSection("时间线", "事件、缺口与损坏行", Timeline),
            new NavSection("诊断与证据", "结论、证据与反证", Diagnoses),
            new NavSection("多客户端", "只读状态与依赖解释", Clients),
            new NavSection("导出", "脱敏预览与本地 ZIP", Export),
            new NavSection("设置", "观察与探测目标", Settings),
            new NavSection("帮助", "离线说明与边界", Help),
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
    }

    public HomeViewModel Home { get; }

    public TimelineViewModel Timeline { get; }

    public DiagnosesViewModel Diagnoses { get; }

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
        private set => Set(ref _stateTone, value);
    }

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

    public string AppVersionText => "M1 · 只读诊断 · 无修复入口";

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
