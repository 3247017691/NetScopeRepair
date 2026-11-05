using System.Windows;
using System.Windows.Threading;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop;

public partial class App : System.Windows.Application
{
    private AppServices? _services;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayController? _tray;
    private bool _exiting;
    private bool _disposed;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Shutdown(await RunSmokeTestAsync(e.Args[1]));
            }
            catch (Exception exception)
            {
                AppLog.Write("smoke_failed:" + exception.GetType().FullName);
                Shutdown(2);
            }

            return;
        }

        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--render-screens", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RenderScreensAsync(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                AppLog.Write("render_failed:" + exception.GetType().FullName);
                Console.Error.WriteLine(exception);
                Shutdown(1);
            }

            return;
        }

        try
        {
            ShowShell();
        }
        catch (Exception exception)
        {
            AppLog.Write("startup_failed:" + exception.GetType().FullName);
            MessageBox.Show("NetScope Repair 启动失败：" + exception.GetType().Name
                + "。没有修改任何系统设置。请查看应用数据目录下的 logs。", "NetScope Repair",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ShowShell()
    {
        _services = Composition.CreateDefault();
        _viewModel = new MainViewModel(_services, new WpfUiDispatcher(Dispatcher), PickFolder);
        _window = new MainWindow { DataContext = _viewModel };
        _window.HideRequested += (_, _) => HandleCloseRequest();
        MainWindow = _window;
        _window.Show();
        _tray = new TrayController(_viewModel, _services.Session, ShowWindow, ExitAsync);

        // Local snapshots start with the app; no external probe is started automatically.
        _services.Session.Start(_services.Settings.ObservationInterval);
    }

    private async Task RenderScreensAsync(string directory)
    {
        _services = Composition.CreateDefault(synthetic: true);
        _viewModel = new MainViewModel(_services, new WpfUiDispatcher(Dispatcher));
        await ScreenRenderer.RenderAsync(_viewModel, directory);
    }

    /// <summary>
    /// Read-only startup smoke test: real collector, one snapshot, honest persistence result.
    /// No window is shown, no external probe is started and no system setting is touched.
    /// </summary>
    private static async Task<int> RunSmokeTestAsync(string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var services = Composition.CreateDefault(new AppPaths(Path.Combine(outputDirectory, "smoke-data")));
        try
        {
            var cycle = await services.Session.CollectOnceAsync(CancellationToken.None);
            await services.Session.CollectOnceAsync(CancellationToken.None);
            cycle = await services.Session.CollectOnceAsync(CancellationToken.None);
            var clients = services.Clients is null ? Array.Empty<NetScope.Application.ClientStatus>()
                : await services.Clients.ReadAsync(CancellationToken.None);
            NetScope.Application.ExportPreview? preview = null;
            NetScope.Application.ExportResult? package = null;
            string? exportError = null;
            if (services.Exporter is { } exporter)
            {
                try
                {
                    preview = exporter.Preview(services.Session.SessionPath);
                    if (preview.CanExport)
                    {
                        var exportDirectory = Path.Combine(
                            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
                            "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                        package = await exporter.ExportAsync(services.Session.SessionPath, exportDirectory, CancellationToken.None);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidDataException or InvalidOperationException)
                {
                    exportError = exception.GetType().Name;
                }
            }

            var result = new
            {
                persisted = cycle.Persisted,
                persistence_error = cycle.PersistenceError,
                events = services.Session.EventCount,
                snapshot_count = services.Session.SnapshotCount,
                snapshot_id = cycle.Snapshot.SnapshotId,
                clients = clients.Select(client => new
                {
                    client_id = client.ClientId, running = client.Running,
                    evidence = client.Evidence.Select(item => new
                    {
                        provenance = item.Provenance.ToString(), valid = item.Valid,
                        parser = item.ParserVersion, fields = item.Facts.Keys, gaps = item.Gaps.Count,
                    }),
                }),
                synthetic = cycle.Snapshot.Synthetic,
                session_path = services.Session.SessionPath,
                domains = cycle.Snapshot.Completeness().ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
                export_preview_can_export = preview?.CanExport,
                export_preview_files = preview?.Files.Count,
                export_preview_redactions = preview?.Redactions.Count,
                export_blocked = preview?.BlockedReasons,
                export_package = package is null ? null : new { path = package.PackagePath, bytes = package.Bytes },
                export_error = exportError,
            };
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return cycle.Persisted && package is not null ? 0 : 2;
        }
        finally
        {
            await services.Session.StopAsync();
            services.Dispose();
        }
    }

    private static string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择导出目录；不会覆盖已有文件",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void HideToTray()
    {
        if (_window is null) return;
        _window.Hide();
        _tray?.ShowHiddenNotice();
    }

    private void HandleCloseRequest()
    {
        if (_services is null) return;
        var choice = _services.Settings.HideOnClose;
        if (choice == true)
        {
            HideToTray();
            return;
        }

        if (choice == false)
        {
            _ = ExitAsync();
            return;
        }

        var dialog = new CloseChoiceDialog { Owner = _window };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Remember)
        {
            var updated = _services.Settings with { HideOnClose = !dialog.ExitSelected };
            _services.Settings = updated;
            try
            {
                _services.SettingsStore.Save(updated);
                _viewModel?.Settings.Load(updated);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("settings_save_failed:" + exception.GetType().Name);
            }
        }

        if (dialog.ExitSelected) _ = ExitAsync();
        else HideToTray();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _tray?.Dispose();
            _tray = null;
            if (_window is not null)
            {
                _window.AllowRealClose = true;
                _window.Close();
                _window = null;
            }

            if (_viewModel is not null) await _viewModel.ExitAsync().ConfigureAwait(true);
            DisposeServices();
        }
        finally
        {
            Shutdown();
        }
    }

    private void DisposeServices()
    {
        if (_disposed) return;
        _disposed = true;
        _services?.Dispose();
        _services = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        DisposeServices();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("unhandled:" + e.Exception.GetType().FullName);
        MessageBox.Show("界面操作失败：" + e.Exception.GetType().Name
            + "。没有修改任何网络设置或客户端配置。", "NetScope Repair",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
