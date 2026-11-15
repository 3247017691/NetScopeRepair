using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
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
        _isSelected = action.SelectedByDefault;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Risk { get; }
    public string Kind { get; }
    public bool RequiresElevation { get; }

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

/// <summary>
/// Real maintenance page: reads the local machine, shows typed findings, and applies only the
/// actions the user explicitly selected and confirmed. Every applied change is journaled and the
/// same page can roll everything back. This is deliberately independent of the TUN three-field
/// repair gateway, whose delivery gate remains disabled pending the G3 review.
/// </summary>
public sealed class QuickRepairViewModel : ViewModelBase
{
    private readonly QuickRepairService _service;
    private readonly HandoffExporter? _exporter;
    private readonly Func<string>? _sessionPath;
    private readonly Action<string>? _copyToClipboard;
    private RepairPlan? _plan;
    private bool _isBusy;
    private bool _deep;
    private bool _hasPlan;
    private bool _applyConfirmationPending;
    private bool _restoreConfirmationPending;
    private string _summaryText = "尚未体检。点击「体检本机」读取真实网络状态（只读，不修改任何设置）。";
    private string _elevationText = "";
    private string _journalText = "";

    public QuickRepairViewModel(QuickRepairService? service = null, HandoffExporter? exporter = null,
        Func<string>? sessionPath = null, Action<string>? copyToClipboard = null)
    {
        _service = service ?? new QuickRepairService();
        _exporter = exporter;
        _sessionPath = sessionPath;
        _copyToClipboard = copyToClipboard;
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
    public ObservableCollection<string> Messages { get; } = new();

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
            if (Set(ref _isBusy, value)) Raise(nameof(CanApply));
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
        private set => Set(ref _hasPlan, value);
    }

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
        IsBusy = true;
        try
        {
            var plan = await _service.PlanAsync(Deep, CancellationToken.None).ConfigureAwait(true);
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
            HasPlan = true;
            _applyConfirmationPending = false;
            _restoreConfirmationPending = false;
            Raise(nameof(ApplyButtonText));
            Raise(nameof(RestoreButtonText));
            Raise(nameof(CanApply));
            StatusMessage = "体检完成（只读）。勾选动作后点「执行所选动作」，会再确认一次才真正写入。";
        }
        finally
        {
            IsBusy = false;
        }
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
        var selected = Actions.Where(row => row.IsSelected).Select(row => row.Id).ToArray();
        IsBusy = true;
        try
        {
            var report = await _service.ApplyAsync(_plan, selected, false, CancellationToken.None).ConfigureAwait(true);
            Messages.Clear();
            foreach (var result in report.Results)
                Messages.Add(result.Applied
                    ? $"✔ {result.Title}  {result.Before} → {result.After}"
                    : $"· {result.Title}：{result.Error ?? "未执行"}");
            foreach (var message in report.Messages) Messages.Add(message);
            StatusMessage = $"执行完成：应用 {report.Applied} / 跳过 {report.Skipped} / 失败 {report.Failed}。已打开的程序需重启才认新环境变量。";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
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
        IsBusy = true;
        try
        {
            var report = await _service.RestoreAsync(false, CancellationToken.None).ConfigureAwait(true);
            Messages.Clear();
            foreach (var message in report.Messages) Messages.Add(message);
            StatusMessage = $"回滚完成：还原 {report.Restored} 项，失败 {report.Failed} 项。";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SwitchProxyAsync(bool on)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var report = on
                ? await _service.ProxyOnAsync(7890, false, CancellationToken.None).ConfigureAwait(true)
                : await _service.ProxyOffAsync(false, CancellationToken.None).ConfigureAwait(true);
            Messages.Clear();
            foreach (var result in report.Results)
                Messages.Add(result.Applied ? $"✔ {result.Title}" : $"· {result.Title}：{result.Error ?? "跳过"}");
            foreach (var message in report.Messages) Messages.Add(message);
            StatusMessage = on
                ? "已开启代理并对齐到真实端口；已打开的程序需重启。"
                : "已关闭代理并清理环境变量：现在全部直连，国内软件不需要开代理。";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandoffAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
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
            Messages.Add("交给 AI：" + result.PackagePath);
            StatusMessage = "交接包已生成，提示词已复制到剪贴板：" + result.PackagePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "交接包生成失败：" + exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task MarkUnresolvedAsync()
    {
        var ok = await _service.MarkUnresolvedAsync(CancellationToken.None).ConfigureAwait(true);
        StatusMessage = ok
            ? "已记录「还是没好」。点「交给 AI」生成失败案例包。"
            : "没有可标记的修复批次（先执行一次修复）。";
    }

    private void OnError(Exception exception)
        => StatusMessage = "操作失败：" + exception.GetType().Name + "；没有声称修改成功。";
}
