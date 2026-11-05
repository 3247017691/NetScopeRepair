using System.Drawing;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Services;

/// <summary>
/// Tray lifecycle for the read-only session. Closing the window hides it; explicit "退出" stops
/// collection, flushes what it can and closes the process. No autostart, service or task is created.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _start;
    private readonly System.Windows.Forms.ToolStripMenuItem _pause;
    private readonly MainViewModel _viewModel;
    private bool _disposed;

    public TrayController(MainViewModel viewModel, ObservationSession session, Action showWindow, Func<Task> exit)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(session);
        _viewModel = viewModel;
        _start = new System.Windows.Forms.ToolStripMenuItem("开始观察", null, (_, _) => viewModel.Home.StartCommand.Execute(null));
        _pause = new System.Windows.Forms.ToolStripMenuItem("暂停观察", null, (_, _) => viewModel.Home.PauseCommand.Execute(null));
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => showWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_start);
        menu.Items.Add(_pause);
        menu.Items.Add("立即诊断", null, (_, _) => Diagnose());
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("标记故障（M2）") { Enabled = false });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await exit().ConfigureAwait(true));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
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
        var text = "NetScope Repair · " + state + suffix;
        _icon.Text = text.Length > 63 ? text[..63] : text;
        _start.Enabled = session.State is ObservationState.Idle or ObservationState.Paused;
        _pause.Enabled = session.State == ObservationState.Observing;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
