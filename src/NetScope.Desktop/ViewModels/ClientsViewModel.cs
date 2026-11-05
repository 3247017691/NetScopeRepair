using System.Collections.ObjectModel;
using System.Windows.Input;
using NetScope.Application;
using NetScope.Desktop.Services;

namespace NetScope.Desktop.ViewModels;

/// <summary>Read-only multi-client view. It never offers stop, switch or takeover actions.</summary>
public sealed class ClientsViewModel : ViewModelBase
{
    private readonly IClientStatusProvider? _provider;
    private readonly IDependencyExplainer? _explainer;
    private bool _hasProvider;
    private string _notice = "适配器尚未接入。";
    private DependencyRow? _selectedDependency;
    private bool _demoMode;
    private int _refreshVersion;
    private readonly DemoClientProvider _demoProvider = new();

    public ClientsViewModel(IClientStatusProvider? provider, IDependencyExplainer? explainer)
    {
        _provider = provider;
        _explainer = explainer;
        HasProvider = provider is not null;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None), () => HasProvider);
        Notice = provider is null
            ? "客户端适配器尚未接入（T09）。M1 不会停止、切换或重启任何客户端。"
            : "只读显示：不会停止、切换或重启客户端。";
    }

    public ObservableCollection<ClientRow> Rows { get; } = new();

    public ObservableCollection<DependencyRow> Dependencies { get; } = new();

    public ICommand RefreshCommand { get; }

    public bool HasProvider
    {
        get => _hasProvider;
        private set => Set(ref _hasProvider, value);
    }

    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public DependencyRow? SelectedDependency
    {
        get => _selectedDependency;
        set => Set(ref _selectedDependency, value);
    }

    public async Task SetDemoModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        _demoMode = enabled;
        Interlocked.Increment(ref _refreshVersion);
        Rows.Clear();
        Dependencies.Clear();
        SelectedDependency = null;
        HasProvider = enabled || _provider is not null;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var provider = _demoMode ? _demoProvider : _provider;
        var explainer = _demoMode ? _demoProvider : _explainer;
        if (provider is null) return;
        var demo = _demoMode;
        var version = Interlocked.Increment(ref _refreshVersion);
        try
        {
            // The default adapters perform synchronous local reads; keep those off the UI thread.
            var clients = await Task.Run(() => provider.ReadAsync(cancellationToken), cancellationToken).ConfigureAwait(true);
            var explanations = explainer is null ? Array.Empty<DependencyExplanation>()
                : await Task.Run(() => explainer.ExplainAsync(clients, cancellationToken), cancellationToken).ConfigureAwait(true);
            if (version != Volatile.Read(ref _refreshVersion)) return;
            Rows.Clear();
            foreach (var client in clients) Rows.Add(ToRow(client));
            Dependencies.Clear();
            foreach (var explanation in explanations)
            {
                Dependencies.Add(new DependencyRow(
                    explanation.AppId, explanation.DisplayName, explanation.Summary, explanation.Boundary,
                    explanation.Layers.Select(layer => new DependencyLayerRow(layer.Name, layer.State, layer.Detail)).ToArray(),
                    explanation.Gaps.Count == 0 ? "无" : string.Join("；", explanation.Gaps)));
            }
            SelectedDependency = Dependencies.FirstOrDefault();
            Notice = demo ? "合成演示客户端（mock），不是本机状态。"
                : clients.Count == 0 ? "没有识别到受支持的客户端进程或配置；这不等于没有客户端在运行。"
                : "只读显示：不会停止、切换或重启客户端。";
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref _refreshVersion))
                StatusMessage = "读取客户端状态失败：" + exception.GetType().Name + "；已保留上一份显示。";
        }
    }

    private static ClientRow ToRow(ClientStatus client)
    {
        var running = client.Running switch
        {
            true => "运行中",
            false => "未运行",
            _ => "未知",
        };
        var endpoints = client.Endpoints.Count == 0
            ? "未知"
            : string.Join("；", client.Endpoints.Select(item => item.Role + " " + item.Display));
        var tun = client.TunEnabled switch
        {
            true => "已检测到 TUN" + (string.IsNullOrWhiteSpace(client.TunAddress) ? "" : " · " + client.TunAddress),
            false => "未检测到 TUN",
            _ => "未知",
        };
        var provenance = string.Join("；", client.Evidence
            .Select(item => ProvenanceText(item.Provenance))
            .Distinct(StringComparer.Ordinal));
        var evidence = client.Evidence.Select(item => new ClientEvidenceRow(
            item.ClientId,
            ProvenanceText(item.Provenance),
            (item.Facts.Count == 0 ? "无可显示字段" : string.Join(" · ", item.Facts.Select(pair => pair.Key + "=" + pair.Value)))
                + "\n解析器 " + item.ParserVersion + " · 读取 " + (item.ReadAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "未知")
                + " · 文件修改 " + (item.FileModifiedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "未知")
                + "\nSHA-256 " + (item.ContentHash ?? "未知")
                + (item.Gaps.Count == 0 ? "" : "\n证据缺口：" + string.Join("；", item.Gaps)),
            item.Valid switch
            {
                true when item.Gaps.Count > 0 => "已解析（存在缺口）",
                true => "已解析",
                false => "无效：" + (item.ValidityReason ?? "原因未知"),
                _ => "未验证",
            },
            item.SourcePath)).ToArray();
        return new ClientRow(
            client.ClientId,
            client.DisplayName,
            running,
            client.Version ?? "未知",
            endpoints,
            tun,
            string.IsNullOrEmpty(provenance) ? "无配置证据" : provenance,
            client.Gaps.Count == 0 ? "无" : string.Join("；", client.Gaps),
            client.Notes.Count == 0 ? "无" : string.Join("；", client.Notes),
            evidence);
    }

    private static string ProvenanceText(ConfigProvenance provenance) => provenance switch
    {
        ConfigProvenance.RuntimeApi => "运行 API",
        ConfigProvenance.ProcessStartReference => "启动引用",
        ConfigProvenance.DiskOnly => "磁盘文件",
        _ => "用户陈述",
    };
}
