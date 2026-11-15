using System.Collections.ObjectModel;
using System.Windows.Input;
using NetScope.Application;
using NetScope.Desktop.Services;

namespace NetScope.Desktop.ViewModels;

/// <summary>Read-only multi-client view. It never offers stop, switch or takeover actions.</summary>
public sealed class ClientsViewModel : ViewModelBase
{
    /// <summary>Stable ids of the two supported client cards; other rows are machine observations.</summary>
    private static readonly string[] ClientIds = ["nice", "apex"];

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

    /// <summary>Machine-level TUN candidates and processes/listeners without a client attribution.</summary>
    public ObservableCollection<ObservationRow> Observations { get; } = new();

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
        Observations.Clear();
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
            Observations.Clear();
            foreach (var client in clients)
            {
                if (ClientIds.Contains(client.ClientId, StringComparer.Ordinal))
                {
                    Rows.Add(ToRow(client));
                }
                else
                {
                    Observations.Add(ToObservation(client));
                }
            }

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
        var endpoints = ToEndpointsText(client.Endpoints);
        var tun = client.TunOwnership switch
        {
            "unconfirmed" => "归属未确认",
            "unknown" => "归属未知",
            _ => "归属未知",
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
            ValidityText(item),
            item.Notices.Count == 0
                ? ""
                : "配置提示（不计为缺口）：" + string.Join("；", item.Notices),
            item.SourcePath)).ToArray();
        return new ClientRow(
            client.ClientId,
            client.DisplayName,
            running,
            client.Version ?? "未知",
            ToProcessesText(client.Processes),
            endpoints,
            tun,
            string.IsNullOrEmpty(provenance) ? "无配置证据" : provenance,
            client.Gaps.Count == 0 ? "无" : string.Join("；", client.Gaps),
            client.Notes.Count == 0 ? "无" : string.Join("；", client.Notes),
            evidence);
    }

    private static ObservationRow ToObservation(ClientStatus status)
    {
        var kind = status.ClientId switch
        {
            "system_tun" => "系统级",
            "unattributed_core" => "未归属内核",
            "unknown_listener" => "未识别监听",
            _ => "其他观察",
        };

        var summary = status.ClientId switch
        {
            "system_tun" => ToTunCandidatesText(status),
            "unattributed_core" => status.Processes.Count == 0
                ? "没有未归属的内核候选。"
                : string.Join("\n", status.Processes.Select(process => ToProcessText(process, includeRole: false))),
            _ => status.Endpoints.Count == 0
                ? "没有未归属的监听端点。"
                : string.Join("\n", status.Endpoints.Select(ToUnattributedEndpointText)),
        };

        return new ObservationRow(
            status.ClientId,
            status.DisplayName,
            kind,
            summary,
            status.Notes.Count == 0 ? "无" : string.Join("；", status.Notes),
            status.Gaps.Count == 0 ? "无" : string.Join("；", status.Gaps));
    }

    private static string ToTunCandidatesText(ClientStatus status)
    {
        if (status.TunCandidates.Count == 0)
        {
            return status.TunEnabled switch
            {
                false => "未观测到启用的 TUN 候选适配器。",
                _ => "TUN 候选未知（适配器列表不可读或只读取了一部分）。",
            };
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(status.TunAddress))
        {
            lines.Add("地址分族：" + status.TunAddress);
        }

        lines.AddRange(status.TunCandidates.Select(candidate =>
        {
            var identity = candidate.Identity ?? "GUID 未读取";
            var index = candidate.InterfaceIndex is int value ? value.ToString() : "未读取";
            var luid = candidate.Luid is long luidValue ? "0x" + luidValue.ToString("x16") : "未读取";
            return $"{candidate.Name}（{candidate.OperationalStatus}）· 依据 {candidate.Reason}"
                + $"\n  标识 {identity} · ifIndex {index} · LUID {luid}"
                + $"\n  IPv4 {FamilyText(candidate.Ipv4Addresses)} · IPv6 {FamilyText(candidate.Ipv6Addresses)}"
                + (candidate.FieldGaps.Count == 0 ? "" : "\n  字段读取：" + string.Join("、", candidate.FieldGaps));
        }));
        return string.Join("\n", lines);
    }

    private static string ToProcessesText(IReadOnlyList<ClientProcess> processes)
    {
        if (processes.Count == 0)
        {
            return "未观测到该客户端的进程（配置存在不代表已加载）。";
        }

        return string.Join("\n", processes.Select(process => ToProcessText(process, includeRole: true)));
    }

    private static string ToProcessText(ClientProcess process, bool includeRole)
    {
        var role = process.Role == "ui" ? "界面" : "内核";
        var parts = new List<string>();
        if (includeRole)
        {
            parts.Add(role);
        }

        parts.Add("PID " + process.ProcessId);
        if (!string.IsNullOrWhiteSpace(process.Name))
        {
            parts.Add(process.Name!);
        }

        parts.Add("启动 " + (process.StartTime?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "未知"));
        if (process.Role == "core")
        {
            parts.Add(process.Association switch
            {
                "parent_child" => "关联 父子进程",
                "same_install_directory" => "关联 同安装目录",
                _ => "关联 未确认",
            });
        }

        if (!string.IsNullOrWhiteSpace(process.Version))
        {
            parts.Add("版本 " + process.Version);
        }

        var lines = new List<string> { string.Join(" · ", parts) };
        if (process.FieldGaps.Count > 0)
        {
            lines.Add("  字段读取：" + string.Join("、", process.FieldGaps));
        }

        if (!string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            lines.Add("  路径：" + process.ExecutablePath);
        }

        if (!string.IsNullOrWhiteSpace(process.AssociationEvidence))
        {
            lines.Add("  依据：" + process.AssociationEvidence);
        }

        return string.Join("\n", lines);
    }

    private static string ToEndpointsText(IReadOnlyList<ClientEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return "无（磁盘配置中的端口不当作实际监听）";
        }

        return string.Join("\n", endpoints.Select(endpoint =>
        {
            var owner = endpoint.OwnerProcessId is int pid
                ? $"所有者 PID {pid}" + (string.IsNullOrWhiteSpace(endpoint.OwnerProcessName) ? "" : " " + endpoint.OwnerProcessName)
                    + "（读取 " + (endpoint.OwnerReadState ?? "unknown") + "）"
                : "所有者未读取（" + (endpoint.OwnerReadReason ?? endpoint.OwnerReadState ?? "unknown") + "）";
            var attribution = endpoint.Attribution switch
            {
                "ui" => "UI 进程直接监听",
                "core" => "内核进程监听",
                _ => "归属待确认",
            };
            return $"listen {endpoint.Display} [{endpoint.AddressFamily}] · {owner} · {attribution}";
        }));
    }

    private static string ToUnattributedEndpointText(ClientEndpoint endpoint)
    {
        var owner = endpoint.OwnerProcessId is int pid
            ? $"所有者 PID {pid}" + (string.IsNullOrWhiteSpace(endpoint.OwnerProcessName) ? "" : " " + endpoint.OwnerProcessName)
            : "所有者未读取（" + (endpoint.OwnerReadReason ?? endpoint.OwnerReadState ?? "unknown") + "）";
        return $"listen {endpoint.Display} [{endpoint.AddressFamily}] · {owner}"
            + (string.IsNullOrWhiteSpace(endpoint.AttributionEvidence) ? "" : "\n  " + endpoint.AttributionEvidence);
    }

    private static string FamilyText(IReadOnlyList<string> addresses) =>
        addresses.Count == 0 ? "未报告" : string.Join("、", addresses);

    private static string ValidityText(ClientConfigEvidence item)
    {
        var coverage = item.Coverage.Count == 0
            ? ""
            : " · 字段覆盖 " + item.Coverage.Count(pair => pair.Value == ConfigFactCoverage.Present) + "/" + item.Coverage.Count;
        return item.Valid switch
        {
            true when item.Gaps.Count > 0 => "已解析（存在缺口）" + coverage,
            true => "已解析" + coverage,
            false => "无效：" + (item.ValidityReason ?? "原因未知"),
            _ => "未验证",
        };
    }

    private static string ProvenanceText(ConfigProvenance provenance) => provenance switch
    {
        ConfigProvenance.RuntimeApi => "运行 API",
        ConfigProvenance.ProcessStartReference => "启动引用",
        ConfigProvenance.DiskOnly => "磁盘文件",
        _ => "用户陈述",
    };
}
