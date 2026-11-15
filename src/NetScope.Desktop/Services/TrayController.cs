using System.Drawing;
using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Services;

/// <summary>
/// Tray lifecycle for the read-only session. Closing the window hides it; explicit "退出" stops
/// collection, flushes what it can, closes an unfinished incident as incomplete and closes the
/// process. No autostart, service or task is created. Incident capture is enabled by this entry
/// point: the M2 tray is where "刚才断网了" / "最近故障" become reachable.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _start;
    private readonly System.Windows.Forms.ToolStripMenuItem _pause;
    private readonly System.Windows.Forms.ToolStripMenuItem _markIncident;
    private readonly System.Windows.Forms.ToolStripMenuItem _recentIncident;
    private readonly MainViewModel _viewModel;
    private readonly ObservationSession _session;
    private readonly IIncidentRecorder _recorder;
    private readonly EventHandler<IncidentRecorderStatus> _recorderChanged;
    private bool _disposed;

    public TrayController(MainViewModel viewModel, ObservationSession session, Action showWindow, Func<Task> exit)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(session);
        _viewModel = viewModel;
        _session = session;
        _recorder = session.EnsureIncidentRecorder();
        _recorderChanged = (_, _) => UpdateState(session);
        _recorder.StatusChanged += _recorderChanged;
        _start = new System.Windows.Forms.ToolStripMenuItem("开始观察", null, (_, _) => viewModel.Home.StartCommand.Execute(null));
        _pause = new System.Windows.Forms.ToolStripMenuItem("暂停观察", null, (_, _) => viewModel.Home.PauseCommand.Execute(null));
        _markIncident = new System.Windows.Forms.ToolStripMenuItem("刚才断网了", null, async (_, _) => await MarkIncidentAsync());
        _recentIncident = new System.Windows.Forms.ToolStripMenuItem("最近故障", null, (_, _) => ShowRecentIncident());
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => showWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_start);
        menu.Items.Add(_pause);
        menu.Items.Add("立即诊断", null, (_, _) => Diagnose());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_markIncident);
        menu.Items.Add(_recentIncident);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await exit().ConfigureAwait(true));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = menu,
            Text = "NetScope Repair",
        };
        _icon.DoubleClick += (_, _) => showWindow();
        session.StateChanged += (_, _) => UpdateState(session);
        viewModel.Home.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HomeViewModel.HealthTitle)) UpdateState(session);
        };
        UpdateState(session);
    }

    /// <summary>True when the session still observes and the recorder accepts a user mark.</summary>
    public static bool CanMarkIncident(IncidentRecorderStatus? status, ObservationState state) =>
        status?.CanMarkIncident == true && state is ObservationState.Observing or ObservationState.Paused;

    /// <summary>True when "最近故障" has something to show.</summary>
    public static bool HasRecentIncident(IncidentRecorderStatus? status) =>
        status?.LastIncident is not null || status?.CurrentIncident is not null;

    /// <summary>Value-free Chinese summary used by the tray balloon; testable without a NotifyIcon.</summary>
    public static string DescribeIncident(IncidentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var trigger = record.Trigger == IncidentTrigger.ManualUserMark ? "用户标记" : "连续有效失败";
        var state = record.State switch
        {
            IncidentState.IncidentOpen => "记录中",
            IncidentState.Recovering => "恢复观察中",
            IncidentState.Closed => record.PostWindowComplete ? "已结束（后窗完整）" : "已结束（后窗不完整）",
            _ => "已记录",
        };
        var window = record.Window.PreWindowAvailable ? "有前窗" : "缺前窗";
        var root = record.Window.PostWindowComplete ? "" : "；根因未证实";
        return $"{trigger} · {state} · {window} · 有效失败 {record.EffectiveFailureCount} 次 · "
            + record.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss") + root;
    }

    public static string DescribeStatus(IncidentRecorderStatus? status)
    {
        if (status is null) return "故障记录未启用。";
        var current = status.CurrentIncident ?? status.LastIncident;
        if (current is null) return "还没有记录到故障。";
        return (status.CurrentIncident is null ? "最近故障：" : "当前故障：") + DescribeIncident(current);
    }

    private async Task MarkIncidentAsync()
    {
        try
        {
            if (!CanMarkIncident(_recorder.Status, _session.State))
            {
                _icon.ShowBalloonTip(4000, "NetScope Repair",
                    "当前没有进行中的观察，不能记录故障。请先开始观察。",
                    System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            var record = await _session.MarkIncidentAsync("用户标记：刚才断网了", CancellationToken.None)
                .ConfigureAwait(true);
            if (record is null)
            {
                _icon.ShowBalloonTip(4000, "NetScope Repair",
                    "故障记录器没有接受本次标记；没有修改任何系统设置。",
                    System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            var persisted = _session.PersistenceError is null;
            _icon.ShowBalloonTip(6000, "NetScope Repair",
                (persisted ? "已记录故障现场。" : "故障已记录，但日志写入失败；该现场可能未持久化。")
                + DescribeIncident(record),
                persisted ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception exception)
        {
            AppLog.Write("tray_mark_incident_failed:" + exception.GetType().Name);
            _icon.ShowBalloonTip(4000, "NetScope Repair",
                "记录故障失败（" + exception.GetType().Name + "）；没有修改系统。",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private void ShowRecentIncident()
    {
        var status = _recorder.Status;
        var record = status.CurrentIncident ?? status.LastIncident;
        if (record is null)
        {
            _icon.ShowBalloonTip(4000, "NetScope Repair",
                "还没有记录到故障。故障捕获只在观察期间有效；退出期间不补造现场。",
                System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        _icon.ShowBalloonTip(6000, "NetScope Repair", DescribeIncident(record),
            record.PostWindowComplete ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void Diagnose()
    {
        if (_viewModel.Home.CanDiagnose)
        {
            _viewModel.Home.DiagnoseCommand.Execute(null);
            return;
        }

        _icon.ShowBalloonTip(4000, "NetScope Repair",
            "还没有可用的探测目标。请打开主窗口，在“设置”中填写 http(s) 目标后再试。",
            System.Windows.Forms.ToolTipIcon.Warning);
    }

    public void ShowHiddenNotice()
    {
        if (_disposed) return;
        _icon.ShowBalloonTip(4000, "NetScope Repair",
            "已隐藏到托盘；当前观察继续。选择“退出”才会停止采集并关闭。",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void UpdateState(ObservationSession session)
    {
        if (_disposed) return;
        var state = session.State switch
        {
            ObservationState.Observing => "观察中",
            ObservationState.Paused => "已暂停",
            ObservationState.Stopped => "已停止",
            _ => "空闲",
        };
        var health = _viewModel.Home.HealthTitle;
        var suffix = health is "发现异常" or "采集不足" or "证据不足或存在反证"
            || health.StartsWith("最近诊断有异常", StringComparison.Ordinal) ? " · " + health : "";
        if (session.PersistenceError is not null) suffix += " · 日志写入失败";
        var status = _recorder.Status;
        suffix += status.State switch
        {
            IncidentState.Suspected => " · 疑似故障",
            IncidentState.IncidentOpen => " · 故障记录中",
            IncidentState.Recovering => " · 恢复观察中",
            IncidentState.Paused => " · 观察暂停",
            IncidentState.Degraded => " · 采集降级",
            IncidentState.Closed => " · 故障已记录",
            _ => "",
        };
        var text = "NetScope Repair · " + state + suffix;
        _icon.Text = text.Length > 63 ? text[..63] : text;
        _start.Enabled = session.State is ObservationState.Idle or ObservationState.Paused;
        _pause.Enabled = session.State == ObservationState.Observing;
        _markIncident.Enabled = CanMarkIncident(status, session.State);
        _recentIncident.Enabled = HasRecentIncident(status);
        _recentIncident.Text = status.CurrentIncident is null ? "最近故障" : "最近故障（记录中）";
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/netscope.ico", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream, new Size(32, 32));
                }
            }
        }
        catch
        {
            // Fall back cleanly in headless unit tests where Application resources are not loaded.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.StatusChanged -= _recorderChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
