using System.Collections.ObjectModel;
using System.Windows.Input;
using NetScope.Application;

namespace NetScope.Desktop.ViewModels;

public sealed record ExportFileRow(string Name, string BytesText, string Purpose);

public sealed record ExportRedactionRow(string Category, string CountText, string Strategy);

/// <summary>
/// Export preview and creation. The preview lists categories and counts only; it never shows the
/// values that were removed.
/// </summary>
public sealed class ExportViewModel : ViewModelBase
{
    private readonly IIncidentExporter? _exporter;
    private readonly Func<string?> _sessionPath;
    private readonly Func<string?> _pickFolder;
    private bool _hasExporter;
    private string _notice = "";
    private string _countsText = "尚未生成预览";
    private string _lastPackageText = "";
    private bool _canExport;

    public ExportViewModel(IIncidentExporter? exporter, Func<string?> sessionPath, Func<string?> pickFolder)
    {
        _exporter = exporter;
        _sessionPath = sessionPath ?? throw new ArgumentNullException(nameof(sessionPath));
        _pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));
        HasExporter = exporter is not null;
        Notice = exporter is null
            ? "导出模块尚未接入（T08）。M1 不会上传任何日志；导出只生成本地 ZIP。"
            : "导出前请先预览：包内只保留类别化别名，不包含用户名、设备名、完整路径或凭据。";
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => HasExporter);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => HasExporter && _canExport);
    }

    public ObservableCollection<ExportFileRow> Files { get; } = new();

    public ObservableCollection<ExportRedactionRow> Redactions { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    public ObservableCollection<string> Blocked { get; } = new();

    public ICommand PreviewCommand { get; }

    public ICommand ExportCommand { get; }

    public bool HasExporter
    {
        get => _hasExporter;
        private set => Set(ref _hasExporter, value);
    }

    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public string CountsText
    {
        get => _countsText;
        private set => Set(ref _countsText, value);
    }

    public string LastPackageText
    {
        get => _lastPackageText;
        private set => Set(ref _lastPackageText, value);
    }

    public bool CanExport
    {
        get => _canExport;
        private set
        {
            if (Set(ref _canExport, value)) (ExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public async Task PreviewAsync()
    {
        if (_exporter is null) return;
        var path = _sessionPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "当前会话还没有日志；先开始观察或载入合成演示。";
            return;
        }

        var preview = await Task.Run(() => _exporter.Preview(path)).ConfigureAwait(true);
        Files.Clear();
        foreach (var file in preview.Files)
            Files.Add(new ExportFileRow(file.Name, Size(file.Bytes), file.Purpose));
        Redactions.Clear();
        foreach (var redaction in preview.Redactions)
            Redactions.Add(new ExportRedactionRow(redaction.Category, redaction.ReplacedFields + " 处", redaction.Strategy));
        Warnings.Clear();
        foreach (var warning in preview.Warnings) Warnings.Add(warning);
        Blocked.Clear();
        foreach (var reason in preview.BlockedReasons) Blocked.Add(reason);
        CountsText = $"事件 {preview.EventCount} · 观察缺口 {preview.GapCount} · 诊断 {preview.DiagnosisCount} · 生成时间 {preview.GeneratedAt.ToLocalTime():MM-dd HH:mm:ss}";
        CanExport = preview.CanExport;
        StatusMessage = preview.CanExport ? "预览完成：确认后选择一个空目录导出。" : "当前无法导出，原因见下方阻断项。";
    }

    public async Task ExportAsync()
    {
        if (_exporter is null || !_canExport) return;
        var path = _sessionPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        var folder = _pickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            var result = await Task.Run(() => _exporter.ExportAsync(path, folder, CancellationToken.None)).ConfigureAwait(true);
            LastPackageText = "已生成：" + result.PackagePath + "（" + Size(result.Bytes) + "）";
            StatusMessage = "导出完成。包内保留类别化别名，salt 不随包输出。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            StatusMessage = "导出失败：" + exception.GetType().Name + "。没有生成可用包。";
        }
    }

    public void Reset()
    {
        Files.Clear();
        Redactions.Clear();
        Warnings.Clear();
        Blocked.Clear();
        CanExport = false;
        CountsText = "尚未生成预览";
        LastPackageText = "";
        StatusMessage = null;
    }

    private static string Size(long bytes) => bytes < 1024
        ? bytes + " B"
        : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
}
