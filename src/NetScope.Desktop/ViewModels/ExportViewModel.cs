using System.Collections.ObjectModel;
using System.Windows.Input;
using NetScope.Application;
using NetScope.Storage.Export;

namespace NetScope.Desktop.ViewModels;

public sealed record ExportFileRow(string Name, string BytesText, string Purpose);

public sealed record ExportRedactionRow(string Category, string CountText, string Strategy);

/// <summary>One package-shape line mirrored from the manifest sections, shown apart from warnings.</summary>
public sealed record ExportShapeRow(string Label, string Value);

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

    public ObservableCollection<ExportShapeRow> Shape { get; } = new();

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

        ExportPreview preview;
        ExportPackageDetails? details = null;
        if (_exporter is IExportPackageDetailsProvider provider)
        {
            details = await Task.Run(() => provider.PreviewDetails(path)).ConfigureAwait(true);
            preview = details.Preview;
        }
        else
        {
            preview = await Task.Run(() => _exporter.Preview(path)).ConfigureAwait(true);
        }

        Files.Clear();
        foreach (var file in preview.Files)
            Files.Add(new ExportFileRow(file.Name, Size(file.Bytes), file.Purpose));
        Redactions.Clear();
        foreach (var redaction in preview.Redactions)
            Redactions.Add(new ExportRedactionRow(redaction.Category, redaction.ReplacedFields + " 处", redaction.Strategy));
        Shape.Clear();
        if (details is not null) FillShape(details.Statistics);
        Warnings.Clear();
        foreach (var warning in preview.Warnings) Warnings.Add(warning);
        Blocked.Clear();
        foreach (var reason in preview.BlockedReasons) Blocked.Add(reason);
        CountsText = $"事件 {preview.EventCount} · 诊断 {preview.DiagnosisCount} · 包内文件 {preview.Files.Count} · 生成时间 {preview.GeneratedAt.ToLocalTime():MM-dd HH:mm:ss}";
        CanExport = preview.CanExport;
        StatusMessage = preview.CanExport ? "预览完成：确认后选择一个空目录导出。" : "当前无法导出，原因见下方阻断项。";
    }

    /// <summary>
    /// Mirrors the manifest's separated sections in the preview. An old exporter that only
    /// implements <see cref="IIncidentExporter"/> simply leaves this block empty instead of
    /// showing invented counts.
    /// </summary>
    private void FillShape(PackageStatistics statistics)
    {
        var gaps = statistics.Gaps;
        Shape.Add(new ExportShapeRow("缺口",
            $"序列缺口 {gaps.SequenceGapCount} · 独立 gap 事件 {gaps.GapEventCount} · "
            + $"观察缺口事件 {gaps.ObservationGapEventCount} · 失败事件 {gaps.FailedEventCount}"));

        var completeness = statistics.Completeness;
        Shape.Add(new ExportShapeRow("域完整性", completeness.SnapshotCount == 0
            ? "无快照；无法判断采集是否完整。"
            : $"{completeness.SnapshotCount} 个快照，其中 {completeness.IncompleteSnapshotCount} 个存在非 ok 域；"
              + $"采集完整：{(completeness.CollectionComplete ? "是" : "否")}（gaps=0 只表示没有独立 gap 事件）"
              + $"\n域：{Statuses(completeness.Domains)}"
              + $"\n子域：{Statuses(completeness.ProxySubdomains)}"));

        var probes = statistics.ProbeCoverage;
        Shape.Add(new ExportShapeRow("探测覆盖", probes.HasProbeResults
            ? $"探测事件 {probes.ProbeEvents} · 组 {probes.ProbeGroups} · 目标 {probes.TargetCount} · "
              + $"成功 {probes.Successful} / 部分 {probes.Partial} / 失败 {probes.Failed} · "
              + $"地址族 {Join(probes.AddressFamilies)} · 路径 {Join(probes.Paths)}"
            : "无 probe 结果：未做外部可达性验证；本地快照不等于网络健康。"));

        var versions = statistics.Versions;
        Shape.Add(new ExportShapeRow("版本",
            $"app {versions.AppVersion} @ {versions.AppBuildCommit}（{CommitSourceText(versions.AppBuildCommitSource)}）· "
            + $"规则 {Join(versions.RuleVersions)} · 解析器 {Join(versions.ParserVersions)} · "
            + $"证据负载 {Join(versions.EvidencePayloadVersions)} · 来源版本 {Join(versions.SourceVersions)} · "
            + $"脱敏策略 {versions.RedactionPolicyVersion} · 包格式 {versions.PackageFormatVersion}"));

        var changes = statistics.Changes;
        var fields = string.Join("、", changes.FieldCounts.Select(item => item.Field + "×" + item.Count));
        Shape.Add(new ExportShapeRow("变化摘要",
            $"相邻可比快照对 {changes.ComparablePairs} · 接口 {changes.InterfacesCompared} 个 · "
            + $"变化字段 {(string.IsNullOrEmpty(fields) ? "无" : fields)} · "
            + $"清单 {changes.Changes.Count} 条{(changes.Truncated ? "（已截断）" : "")}；只记录参数变化，不推断原因或操作者。"));

        Shape.Add(new ExportShapeRow("诊断来源",
            $"包内记录诊断 {statistics.Diagnoses.RecordedInPackage} 条 · 导出时离线重算 {statistics.Diagnoses.RecomputedByExporter} 条；"
            + "导出器不重跑规则，离线重算需由独立只读工具完成。"));
    }

    private static string Statuses(IReadOnlyList<PackageStatusCount> counts) => counts.Count == 0
        ? "无"
        : string.Join("、", counts.Select(item => item.Path + " " + item.Status + "×" + item.Count));

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "无" : string.Join("、", values);

    private static string CommitSourceText(string source) => source switch
    {
        "assembly_informational_version" => "程序集 build 版本",
        "assembly_metadata" => "程序集元数据",
        _ => "构建未嵌入 commit",
    };

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
        Shape.Clear();
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
