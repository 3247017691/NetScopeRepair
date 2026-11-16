using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetScope.Desktop.Services;
using NetScope.Maintenance;
using NetScope.Storage.Export;

namespace NetScope.Desktop.ViewModels;

/// <summary>One selectable, typed repair action shown on the real maintenance page.</summary>
public sealed class ActionRow : ViewModelBase
{
    private bool _isSelected;

    public ActionRow(RepairAction action)
    {
        Id = action.Id;
        Title = action.Title;
        Detail = action.Detail;
        Risk = action.Risk.ToString();
        Kind = action.Kind;
        RequiresElevation = action.RequiresElevation;
        Current = action.Current;
        Desired = action.Desired;
        _isSelected = action.SelectedByDefault;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Risk { get; }
    public string Kind { get; }
    public bool RequiresElevation { get; }
    public string? Current { get; }
    public string? Desired { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string Badge => RequiresElevation ? Risk + " · 需管理员" : Risk;
}

public sealed class FindingRow
{
    public FindingRow(Finding finding)
    {
        Code = finding.Code;
        Title = finding.Title;
        Detail = finding.Detail;
        Severity = finding.Severity.ToString();
    }

    public string Code { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Severity { get; }
}

/// <summary>One problem↔fix pair: the finding (when mapped) beside the action that addresses it.</summary>
public sealed class RepairItemRow : ViewModelBase
{
    public RepairItemRow(FindingRow? finding, ActionRow action)
    {
        Finding = finding;
        Action = action;
        Action.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionRow.IsSelected)) Raise(nameof(IsSelected));
        };
    }

    public FindingRow? Finding { get; }
    public ActionRow Action { get; }
    public bool HasFinding => Finding is not null;
    public string ProblemText => Finding?.Title ?? Action.Title;
    public string CodeRef => Finding?.Code ?? "";
    public string FixText => Action.Title;
    public string BeforeAfterText
    {
        get
        {
            // Display-only: the planner emits "" for cache refresh; collapse empty pairs to "" so no
            // bare arrow renders. IsNullOrEmpty (not Whitespace) — a whitespace value is real data.
            // Action.Current/Desired keep their raw values; executor/journal semantics are untouched.
            var current = string.IsNullOrEmpty(Action.Current) ? null : Action.Current;
            var desired = string.IsNullOrEmpty(Action.Desired) ? null : Action.Desired;
            return current is null && desired is null ? ""
                : (current ?? "未设置") + " → " + (desired ?? "清除");
        }
    }
    public string RiskBadge => Action.Badge;
    public bool IsSelected
    {
        get => Action.IsSelected;
        set => Action.IsSelected = value;
    }
}

/// <summary>
/// Repair page: reads the local machine, shows typed findings, and applies only the actions the
/// user explicitly selected and confirmed. Every applied change is journaled and can be rolled
/// back. While a repair runs, a fine-grained progress stream drives a live step list, a real-time
/// log, a determinate progress bar and an elapsed timer — so a click is never silent.
/// </summary>
public sealed class QuickRepairViewModel : ViewModelBase
{
    private readonly QuickRepairService _service;
    private readonly HandoffExporter? _exporter;
    private readonly Func<string>? _sessionPath;
    private readonly Action<string>? _copyToClipboard;
    private readonly IUiDispatcher _dispatcher;
    private readonly IProgress<RepairProgress> _progress;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _handoffTimer;
    private readonly Stopwatch _handoffStopwatch = new();
    private RepairPlan? _plan;
    private bool _isBusy;
    private bool _deep;
    private bool _hasPlan;
    private bool _applyConfirmationPending;
    private bool _restoreConfirmationPending;
    private bool _writeOperation;
    private RepairRunState _runState = RepairRunState.Idle;
    private NetworkPathModel _networkPath = NetworkPathModel.Unmeasured;
    private PostCheckResult? _lastPostCheck;
    private string _currentPhaseText = "待命";
    private string _elapsedText = "";
    private double _progressValue;
    private double _progressMax;
    private bool _hasDeterminateProgress;
    private string _summaryText = "尚未体检。点击「体检本机」读取真实网络状态（只读，不修改任何设置）。";
    private string _elevationText = "";
    private string _journalText = "";

    public QuickRepairViewModel(QuickRepairService? service = null, HandoffExporter? exporter = null,
        Func<string>? sessionPath = null, Action<string>? copyToClipboard = null, IUiDispatcher? dispatcher = null)
    {
        _service = service ?? new QuickRepairService();
        _exporter = exporter;
        _sessionPath = sessionPath;
        _copyToClipboard = copyToClipboard;
        _dispatcher = dispatcher ?? new InlineUiDispatcher();
        _progress = new UiProgress(_dispatcher, ApplyProgress);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => ElapsedText = FormatElapsed(_stopwatch.Elapsed);
        _handoffTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _handoffTimer.Tick += (_, _) =>
        {
            if (HandoffProgressText(IsBusy, _handoffStopwatch.Elapsed) is { } msg) StatusMessage = msg;
        };
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, null, OnError);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, null, OnError);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, null, OnError);
        ProxyOnCommand = new AsyncRelayCommand(() => SwitchProxyAsync(true), null, OnError);
        ProxyOffCommand = new AsyncRelayCommand(() => SwitchProxyAsync(false), null, OnError);
        HandoffCommand = new AsyncRelayCommand(HandoffAsync, null, OnError);
        MarkUnresolvedCommand = new AsyncRelayCommand(MarkUnresolvedAsync, null, OnError);
    }

    public ObservableCollection<FindingRow> Findings { get; } = new();
    public ObservableCollection<ActionRow> Actions { get; } = new();
    public ObservableCollection<StepRow> Steps { get; } = new();
    public ObservableCollection<LogLine> LogLines { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();
    public ObservableCollection<RepairItemRow> RepairItems { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ProxyOnCommand { get; }
    public ICommand ProxyOffCommand { get; }
    public ICommand HandoffCommand { get; }
    public ICommand MarkUnresolvedCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanApply));
                Raise(nameof(Stage));
                Raise(nameof(VerdictText));
            }
        }
    }

    public bool Deep
    {
        get => _deep;
        set
        {
            if (Set(ref _deep, value)) StatusMessage = "高风险模式已" + (value ? "开启，下次体检会列出幽灵网卡与静态 DNS 动作（默认不选中）。" : "关闭。");
        }
    }

    public bool HasPlan
    {
        get => _hasPlan;
        private set
        {
            if (Set(ref _hasPlan, value))
            {
                Raise(nameof(Stage));
                Raise(nameof(VerdictText));
            }
        }
    }

    public RepairRunState RunState
    {
        get => _runState;
        private set
        {
            if (!Set(ref _runState, value)) return;
            Raise(nameof(RunStateText));
            Raise(nameof(RunStateTone));
            Raise(nameof(IsRunIdle));
            Raise(nameof(IsUnresolved));
            Raise(nameof(Stage));
            Raise(nameof(VerdictText));
        }
    }

    public NetworkPathModel NetworkPath
    {
        get => _networkPath;
        private set => Set(ref _networkPath, value);
    }

    public RepairStage Stage =>
        !HasPlan ? RepairStage.Inspect
        : IsBusy && _writeOperation ? RepairStage.Execute
        : RunState is RepairRunState.Success or RepairRunState.Failed ? RepairStage.Verify
        : RepairStage.Review;

    public string VerdictText =>
        !HasPlan ? "尚未体检。点击「体检本机」读取真实网络状态（只读，不修改任何设置）。"
        : Findings.Count == 0 ? "本次体检未发现可修复问题。"
        : RunState is RepairRunState.Success ? "修复已执行，修后自检见下方实测结果。"
        : Findings[0].Title + "。" + Findings[0].Detail;

    public bool IsRunIdle => RunState == RepairRunState.Idle;
    public bool IsUnresolved => RunState == RepairRunState.Failed;
    public string RunStateText => RunState switch
    {
        RepairRunState.Running => "执行中",
        RepairRunState.Success => "已完成",
        RepairRunState.Failed => "有失败",
        _ => "待命",
    };
    public Brush RunStateTone => RunState switch
    {
        RepairRunState.Running => UiText.AccentBrush,
        RepairRunState.Success => UiText.OkBrush,
        RepairRunState.Failed => UiText.BadBrush,
        _ => UiText.GapBrush,
    };

    public string CurrentPhaseText
    {
        get => _currentPhaseText;
        private set => Set(ref _currentPhaseText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public double ProgressMax
    {
        get => _progressMax;
        private set => Set(ref _progressMax, value);
    }

    public bool HasDeterminateProgress
    {
        get => _hasDeterminateProgress;
        private set => Set(ref _hasDeterminateProgress, value);
    }

    public bool HasRun => Steps.Count > 0;
    public bool HasLog => LogLines.Count > 0;
    public int LogCount => LogLines.Count;

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string ElevationText
    {
        get => _elevationText;
        private set => Set(ref _elevationText, value);
    }

    public string JournalText
    {
        get => _journalText;
        private set => Set(ref _journalText, value);
    }

    public bool CanApply => HasPlan && !IsBusy && Actions.Count > 0;

    public string ApplyButtonText => _applyConfirmationPending
        ? "再次点击确认执行（" + SelectedCount + " 项）"
        : "执行所选动作";

    public string RestoreButtonText => _restoreConfirmationPending ? "再次点击确认回滚" : "回滚全部改动";

    private int SelectedCount => Actions.Count(row => row.IsSelected);

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        BeginOperation("正在体检本机（只读）…", determinate: false, write: false);
        try
        {
            _lastPostCheck = null;
            await LoadPlanAsync(stream: true).ConfigureAwait(true);
            StatusMessage = "体检完成（只读）。勾选动作后点「执行所选动作」，会再确认一次才真正写入。";
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Fetch a plan and refresh the selection lists. A quiet refresh (after a write) does not
    /// stream progress, so it never overwrites the repair's live status, steps or log.
    /// </summary>
    private async Task LoadPlanAsync(bool stream = false)
    {
        var plan = await _service.PlanAsync(Deep, CancellationToken.None, stream ? _progress : null).ConfigureAwait(true);
        _plan = plan;
        Findings.Clear();
        Actions.Clear();
        foreach (var finding in plan.Findings) Findings.Add(new FindingRow(finding));
        foreach (var action in plan.Actions) Actions.Add(new ActionRow(action));
        var snapshot = plan.Snapshot;
        SummaryText = $"观测 {snapshot.ObservedAt:HH:mm:ss} · 有网出口 if={snapshot.ActiveEgressIndex} {snapshot.ActiveEgressAlias} "
            + $"· 代理端口 {(snapshot.WorkingProxyPort is null ? "无" : snapshot.WorkingProxyPort.ToString())} "
            + $"· 发现 {plan.Findings.Count} 项 · 动作 {plan.Actions.Count} 项";
        ElevationText = snapshot.IsElevated
            ? "当前以管理员运行：接口度量、WinHTTP、机器级变量都可以执行。"
            : "当前非管理员：接口度量 / WinHTTP / 机器级变量会被如实跳过；如需执行请以管理员启动本程序。";
        JournalText = _service.Journal.HasBackup
            ? "已有可回滚备份：" + _service.Journal.Path
            : "尚无回滚备份（执行动作后会自动生成）。";
        NetworkPath = NetworkPathModel.FromSnapshot(snapshot, _lastPostCheck);
        RepairItems.Clear();
        foreach (var action in Actions)
        {
            var finding = Findings.FirstOrDefault(f => KindsForFinding(f.Code).Contains(action.Kind));
            RepairItems.Add(new RepairItemRow(finding, action));
        }
        Raise(nameof(VerdictText));
        HasPlan = true;
        _applyConfirmationPending = false;
        _restoreConfirmationPending = false;
        Raise(nameof(ApplyButtonText));
        Raise(nameof(RestoreButtonText));
        Raise(nameof(CanApply));
    }

    // Planner findings carry maintenance codes (DEAD_PROXY…); the diagnosis map only speaks DX rule ids.
    private static IReadOnlyList<string> KindsForFinding(string code) =>
        DiagnosisRepairMap.ActionKindsFor(code switch
        {
            "DEAD_PROXY" or "SYSTEM_PROXY_DEAD" => "DX-001",
            "STALE_EGRESS" or "STALE_DNS" or "GHOST_DEVICE" => "DX-006",
            _ => code,
        });

    /// <summary>Entry from diagnosis/home: refresh, then pre-select the actions mapped to the rule.</summary>
    public async Task PreselectForDiagnosisAsync(string? ruleId)
    {
        if (IsBusy) return;
        await RefreshAsync().ConfigureAwait(true);
        if (ruleId is null) return;
        var kinds = DiagnosisRepairMap.ActionKindsFor(ruleId);
        if (kinds.Count == 0)
        {
            StatusMessage = "诊断 " + ruleId + " 暂无对应修复动作（只报告）。";
            return;
        }
        foreach (var row in Actions) row.IsSelected = kinds.Contains(row.Kind);
        StatusMessage = "已按诊断 " + ruleId + " 预选修复动作；确认后执行。";
    }

    private async Task ApplyAsync()
    {
        if (IsBusy) return;
        if (_plan is null)
        {
            await RefreshAsync().ConfigureAwait(true);
            return;
        }
        if (!_applyConfirmationPending)
        {
            var count = SelectedCount;
            if (count == 0)
            {
                StatusMessage = "没有勾选任何动作，未修改任何设置。";
                return;
            }
            _applyConfirmationPending = true;
            Raise(nameof(ApplyButtonText));
            StatusMessage = "即将写入 " + count + " 项改动（会记入回滚备份）。再次点击同一按钮确认执行。";
            return;
        }

        _applyConfirmationPending = false;
        Raise(nameof(ApplyButtonText));
        var selected = Actions.Where(row => row.IsSelected).Select(row => row.Id).ToArray();
        BeginOperation("正在执行修复…", determinate: true, write: true);
        Steps.Clear();
        foreach (var row in Actions.Where(r => r.IsSelected))
            Steps.Add(new StepRow(row.Id, row.Title, row.Detail, row.Risk));
        Raise(nameof(HasRun));
        try
        {
            var report = await _service.ApplyAsync(_plan, selected, false, CancellationToken.None, _progress).ConfigureAwait(true);
            _lastPostCheck = report.PostCheck;
            StatusMessage = $"执行完成：应用 {report.Applied} / 跳过 {report.Skipped} / 失败 {report.Failed}。已打开的程序需重启才认新环境变量。";
            await LoadPlanAsync().ConfigureAwait(true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RestoreAsync()
    {
        if (IsBusy) return;
        if (!_restoreConfirmationPending)
        {
            _restoreConfirmationPending = true;
            Raise(nameof(RestoreButtonText));
            StatusMessage = "回滚会把所有已记录的改动还原到第一次执行前的状态。再次点击确认。";
            return;
        }
        _restoreConfirmationPending = false;
        Raise(nameof(RestoreButtonText));
        BeginOperation("正在回滚全部改动…", determinate: true, write: true);
        Steps.Clear();
        Raise(nameof(HasRun));
        try
        {
            var report = await _service.RestoreAsync(false, CancellationToken.None, _progress).ConfigureAwait(true);
            StatusMessage = $"回滚完成：还原 {report.Restored} 项，失败 {report.Failed} 项。";
            _lastPostCheck = null;
            await LoadPlanAsync().ConfigureAwait(true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task SwitchProxyAsync(bool on)
    {
        if (IsBusy) return;
        BeginOperation(on ? "正在开启代理并对齐端口…" : "正在关闭代理并清理残留…", determinate: true, write: true);
        Steps.Clear();
        Raise(nameof(HasRun));
        try
        {
            var report = on
                ? await _service.ProxyOnAsync(7890, false, CancellationToken.None, _progress).ConfigureAwait(true)
                : await _service.ProxyOffAsync(false, CancellationToken.None, _progress).ConfigureAwait(true);
            StatusMessage = on
                ? "已开启代理并对齐到真实端口；已打开的程序需重启。"
                : "已关闭代理并清理环境变量：现在全部直连，国内软件不需要开代理。";
            _lastPostCheck = null;
            await LoadPlanAsync().ConfigureAwait(true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task HandoffAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "正在生成交接包（脱敏 + 打包），请稍候…";
        _handoffStopwatch.Restart();
        _handoffTimer.Start();
        try
        {
            var path = _sessionPath?.Invoke();
            if (path is null || !File.Exists(path))
            {
                StatusMessage = "没有会话日志，无法生成交接包。";
                return;
            }
            var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetScopeRepair", "handoff");
            var result = await (_exporter ?? new HandoffExporter())
                .ExportAsync(path, outDir, null, CancellationToken.None).ConfigureAwait(true);
            (_copyToClipboard ?? System.Windows.Clipboard.SetText)(result.Prompt);
            AddLog(RepairStepKind.Info, "交给 AI：" + result.PackagePath);
            StatusMessage = "交接包已生成，提示词已复制到剪贴板：" + result.PackagePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "交接包生成失败：" + exception.Message;
        }
        finally
        {
            _handoffTimer.Stop();
            IsBusy = false;
        }
    }

    private async Task MarkUnresolvedAsync()
    {
        var ok = await _service.MarkUnresolvedAsync(CancellationToken.None).ConfigureAwait(true);
        StatusMessage = ok
            ? "已记录「还是没好」。点「交给 AI」生成失败案例包。"
            : "没有可标记的修复批次（先执行一次修复）。";
    }

    private void BeginOperation(string phase, bool determinate, bool write)
    {
        _writeOperation = write;
        IsBusy = true;
        RunState = RepairRunState.Running;
        CurrentPhaseText = phase;
        HasDeterminateProgress = determinate;
        ProgressValue = 0;
        ProgressMax = determinate ? 1 : 0;
        LogLines.Clear();
        Raise(nameof(HasLog));
        Raise(nameof(LogCount));
        _stopwatch.Restart();
        ElapsedText = "0.0s";
        _timer.Start();
    }

    private void EndOperation()
    {
        _timer.Stop();
        ElapsedText = FormatElapsed(_stopwatch.Elapsed);
        IsBusy = false;
        Raise(nameof(CanApply));
    }

    private void ApplyProgress(RepairProgress p)
    {
        AddLog(p.Kind, p.Message + (string.IsNullOrEmpty(p.Detail) ? "" : "  " + p.Detail), p.At);
        if (p.Kind is RepairStepKind.Phase or RepairStepKind.ActionStart or RepairStepKind.PostCheck or RepairStepKind.Done)
            CurrentPhaseText = p.Message;
        if (p.TotalSteps > 0)
        {
            ProgressMax = p.TotalSteps;
            ProgressValue = p.Step;
            HasDeterminateProgress = true;
        }

        switch (p.Kind)
        {
            case RepairStepKind.ActionStart when p.ActionId is not null:
            {
                var row = EnsureStep(p.ActionId, p.ActionTitle ?? p.Message, p.Detail ?? "");
                row.StartedAt = p.At;
                row.Status = StepStatus.Running;
                break;
            }
            case RepairStepKind.ActionResult when p.ActionId is not null:
            {
                var row = EnsureStep(p.ActionId, p.ActionTitle ?? p.Message, "");
                row.Status = p.Applied == true ? StepStatus.Success
                    : (p.Detail?.Contains("跳过") == true || p.Detail?.Contains("管理员") == true) ? StepStatus.Skipped
                    : StepStatus.Failed;
                row.ResultText = p.Detail ?? "";
                if (row.StartedAt != default)
                    row.ElapsedText = (p.At - row.StartedAt).TotalSeconds.ToString("0.0") + "s";
                break;
            }
            case RepairStepKind.Done when _writeOperation:
                RunState = p.Applied == false ? RepairRunState.Failed : RepairRunState.Success;
                break;
        }
    }

    private StepRow EnsureStep(string id, string title, string detail)
    {
        var row = Steps.FirstOrDefault(s => s.Id == id);
        if (row is null)
        {
            row = new StepRow(id, title, detail, "");
            Steps.Add(row);
            Raise(nameof(HasRun));
        }
        return row;
    }

    private void AddLog(RepairStepKind kind, string text, DateTimeOffset? at = null)
    {
        var (prefix, tone) = kind switch
        {
            RepairStepKind.Phase => ("›", UiText.AccentBrush),
            RepairStepKind.ActionStart => ("▶", UiText.AccentBrush),
            RepairStepKind.ActionResult => ("·", UiText.GapBrush),
            RepairStepKind.PostCheck => ("✓", UiText.OkBrush),
            RepairStepKind.Info => ("·", UiText.GapBrush),
            RepairStepKind.Warning => ("!", UiText.WarnBrush),
            RepairStepKind.Error => ("✕", UiText.BadBrush),
            RepairStepKind.Done => ("■", UiText.OkBrush),
            _ => ("·", UiText.GapBrush),
        };
        LogLines.Add(new LogLine((at ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm:ss"), prefix + " " + text, tone));
        Raise(nameof(HasLog));
        Raise(nameof(LogCount));
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0") + "s";

    // Reassurance for a large handoff package: only while busy and past 8s. The guard compares double
    // seconds so it fires just after 8.0s; the displayed count stays truncated to whole seconds.
    internal static string? HandoffProgressText(bool isBusy, TimeSpan elapsed)
    {
        if (!isBusy || elapsed.TotalSeconds <= 8) return null;
        return "日志较大，仍在打包…已耗时 " + (int)elapsed.TotalSeconds + "s";
    }

    private void OnError(Exception exception)
    {
        RunState = RepairRunState.Failed;
        StatusMessage = "操作失败：" + exception.GetType().Name + "；没有声称修改成功。";
    }

    private sealed class UiProgress(IUiDispatcher dispatcher, Action<RepairProgress> apply) : IProgress<RepairProgress>
    {
        public void Report(RepairProgress value) => dispatcher.Post(() => apply(value));
    }
}
