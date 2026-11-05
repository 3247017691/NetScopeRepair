using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters;

/// <summary>
/// The generic, read-only multi-client view. It combines process identity for the supported
/// clients, listening endpoints with owner attribution where readable, TUN adapter presence and
/// per-client configuration evidence. Unknown never becomes a guess: unreadable fields stay null
/// with a gap, and listeners that cannot be attributed are reported as an unidentified group
/// instead of being assigned to a client.
/// </summary>
public sealed class GenericAdapter : IClientStatusProvider
{
    private readonly IProcessSource _processes;
    private readonly IListenerSource _listeners;
    private readonly INetworkAdapterSource _networkAdapters;
    private readonly IProxyLayerSource _proxyLayers;
    private readonly IReadOnlyList<IClientConfigAdapter> _configAdapters;
    private readonly TimeProvider _time;

    /// <summary>Composes the system readers with the Nice and Apex configuration adapters.</summary>
    public GenericAdapter()
        : this(
            new SystemProcessSource(),
            new SystemListenerSource(),
            new SystemNetworkAdapterSource(),
            new SystemProxyLayerSource(),
            new IClientConfigAdapter[]
            {
                new NiceAdapter(new SystemConfigFileSource()),
                new ApexAdapter(new SystemConfigFileSource()),
            })
    {
    }

    public GenericAdapter(
        IProcessSource processes,
        IListenerSource listeners,
        INetworkAdapterSource networkAdapters,
        IProxyLayerSource proxyLayers,
        IReadOnlyList<IClientConfigAdapter> configAdapters,
        TimeProvider? timeProvider = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _listeners = listeners ?? throw new ArgumentNullException(nameof(listeners));
        _networkAdapters = networkAdapters ?? throw new ArgumentNullException(nameof(networkAdapters));
        _proxyLayers = proxyLayers ?? throw new ArgumentNullException(nameof(proxyLayers));
        _configAdapters = configAdapters ?? throw new ArgumentNullException(nameof(configAdapters));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ClientStatus>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readAt = _time.GetUtcNow();

        var processResult = Safe(_processes.ReadProcesses, "进程列表");
        var listenerResult = Safe(_listeners.ReadListeners, "监听端点");
        var adapterResult = Safe(_networkAdapters.ReadAdapters, "网络适配器列表");
        var tun = ObserveTun(adapterResult);
        var proxyResult = Safe(_proxyLayers.ReadLayers, "代理层");

        var matched = new Dictionary<string, List<ProcessObservation>>(StringComparer.Ordinal);
        foreach (var client in ClientCatalog.All)
        {
            matched[client.ClientId] = [];
        }

        if (processResult.Value is not null)
        {
            foreach (var process in processResult.Value)
            {
                foreach (var client in ClientCatalog.All)
                {
                    if (ClientCatalog.Matches(client, process))
                    {
                        matched[client.ClientId].Add(process);
                    }
                }
            }
        }

        var processByPid = new Dictionary<int, ProcessObservation>();
        if (processResult.Value is not null)
        {
            foreach (var process in processResult.Value)
            {
                processByPid.TryAdd(process.ProcessId, process);
            }
        }

        var pidToClient = new Dictionary<int, string>();
        foreach (var (clientId, processes) in matched)
        {
            foreach (var process in processes)
            {
                pidToClient.TryAdd(process.ProcessId, clientId);
            }
        }

        var endpointsByClient = new Dictionary<string, List<ClientEndpoint>>(StringComparer.Ordinal);
        foreach (var client in ClientCatalog.All)
        {
            endpointsByClient[client.ClientId] = [];
        }

        var unattributed = new List<ClientEndpoint>();
        var unattributedOwnerNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownerUnreadable = 0;
        var unsupportedOwner = 0;
        var listenerGaps = new List<string>();

        if (listenerResult.Value is not null)
        {
            foreach (var listener in listenerResult.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (listener.Port is < 1 or > 65535)
                {
                    listenerGaps.Add("忽略了一个端口不在 1..65535 范围内的监听观测。");
                    continue;
                }

                var endpoint = CreateEndpoint(listener);
                if (listener.OwnerProcessId is int ownerPid && pidToClient.TryGetValue(ownerPid, out var clientId))
                {
                    endpointsByClient[clientId].Add(endpoint);
                    continue;
                }

                unattributed.Add(endpoint);
                if (listener.OwnerProcessId is null)
                {
                    ownerUnreadable++;
                }
                else
                {
                    unsupportedOwner++;
                }

                var ownerName = listener.OwnerProcessName;
                if (string.IsNullOrWhiteSpace(ownerName)
                    && listener.OwnerProcessId is int knownPid
                    && processByPid.TryGetValue(knownPid, out var owner))
                {
                    ownerName = owner.Name;
                }

                if (!string.IsNullOrWhiteSpace(ownerName))
                {
                    unattributedOwnerNames.Add(ownerName);
                }
            }
        }

        var statuses = new List<ClientStatus>(ClientCatalog.All.Count);
        foreach (var client in ClientCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(await BuildStatusAsync(
                client,
                matched[client.ClientId],
                endpointsByClient[client.ClientId],
                processResult,
                listenerResult,
                listenerGaps,
                tun,
                proxyResult,
                readAt,
                cancellationToken));
        }

        var finalStatuses = ApplyCoexistenceNotes(statuses);

        if (unattributed.Count > 0)
        {
            finalStatuses = finalStatuses
                .Append(BuildUnidentifiedStatus(
                    unattributed,
                    unattributedOwnerNames,
                    ownerUnreadable,
                    unsupportedOwner,
                    processResult,
                    listenerResult,
                    tun,
                    proxyResult))
                .ToArray();
        }

        return finalStatuses;
    }

    /// <summary>
    /// Reads the five proxy configuration layers without values. This is kept separate from
    /// <see cref="ReadAsync"/> because a client status never claims which layer a request used.
    /// </summary>
    public Task<Collected<IReadOnlyList<ProxyLayerObservation>>> ReadProxyLayersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Safe(_proxyLayers.ReadLayers, "代理层"));
    }

    private async Task<ClientStatus> BuildStatusAsync(
        KnownClient client,
        IReadOnlyList<ProcessObservation> matches,
        IReadOnlyList<ClientEndpoint> endpoints,
        Collected<IReadOnlyList<ProcessObservation>> processResult,
        Collected<IReadOnlyList<ListenerObservation>> listenerResult,
        IReadOnlyList<string> listenerGaps,
        TunView tun,
        Collected<IReadOnlyList<ProxyLayerObservation>> proxyResult,
        DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        var gaps = new List<string>();
        var notes = new List<string>();
        var first = matches.Count > 0 ? matches[0] : null;

        bool? running;
        if (!processResult.HasValue)
        {
            running = null;
            gaps.Add($"进程列表不可读：{processResult.Reason ?? "unknown"}");
            notes.Add("运行状态未知：进程列表不可读；未发现进程不能当作未运行。");
        }
        else if (processResult.Status == EventStatus.Partial)
        {
            running = first is null ? null : true;
            if (first is null)
            {
                gaps.Add("进程列表只读取了一部分；未发现该客户端进程不能证明其未运行。");
                notes.Add("运行状态未知：进程列表不完整；未发现进程不能当作未运行。");
            }
            else
            {
                notes.Add(RunningNote(first));
            }
        }
        else
        {
            running = first is not null;
            notes.Add(running == true
                ? RunningNote(first!)
                : "未运行：进程源中未发现该客户端的受支持进程。配置证据只表示磁盘上存在配置，不代表已加载。");
        }

        if (first is not null)
        {
            if (string.IsNullOrWhiteSpace(first.ExecutablePath))
            {
                gaps.Add("已匹配客户端进程，但可执行文件路径不可读；路径保持未知。");
            }

            if (!string.IsNullOrWhiteSpace(first.Version))
            {
                notes.Add($"版本：{first.Version}。");
            }
        }

        notes.Add(
            $"身份匹配：按 {ClientCatalog.IdentityVersion} 白名单（{string.Join("、", client.ProcessNames)}）；"
            + "未知名称的进程不推定为客户端。");

        if (!listenerResult.HasValue)
        {
            gaps.Add($"监听端点不可读：{listenerResult.Reason ?? "unknown"}");
            notes.Add("端点未知：监听列表不可读，没有把空列表当作无监听。");
        }
        else if (listenerResult.Status == EventStatus.Partial)
        {
            gaps.Add("监听端点列表不完整，核对时可能缺少端点。");
        }

        gaps.AddRange(listenerGaps);

        if (tun.Gap is not null)
        {
            gaps.Add(tun.Gap);
        }

        if (tun.Note is not null)
        {
            notes.Add(tun.Note);
        }

        var proxyView = DescribeProxyLayers(proxyResult);
        notes.Add(proxyView.Note);
        if (proxyView.Gap is not null)
        {
            gaps.Add(proxyView.Gap);
        }

        ClientConfigEvidence? evidence = null;
        var configAdapter = _configAdapters.FirstOrDefault(adapter => adapter.ClientId == client.ClientId);
        if (configAdapter is null)
        {
            gaps.Add("未提供该客户端的配置适配器；配置状态保持未知。");
            notes.Add("配置未知：没有适配器，不填写默认值。");
        }
        else
        {
            try
            {
                evidence = await configAdapter.ReadAsync(readAt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                gaps.Add($"配置读取失败（{exception.GetType().Name}）。");
                notes.Add("配置未知：配置适配器执行失败。");
            }
        }

        if (evidence is not null)
        {
            // A valid subset is not complete evidence: missing fields and ignored structures
            // remain visible at the client-status boundary as well as on the evidence object.
            gaps.AddRange(evidence.Gaps);
            if (evidence.Valid == true)
            {
                notes.Add($"已配置：{ProvenanceText(evidence.Provenance)} 证据有效"
                    + (evidence.SourcePath is null ? "（无本地路径）" : $"（{evidence.SourcePath}）")
                    + "；配置存在不证明已被客户端加载。");
            }
            else
            {
                var reason = evidence.ValidityReason ?? "无法验证";
                gaps.Add($"配置证据不可用：{reason}");
                notes.Add($"配置未知：{reason}。配置状态保持未知，不推定默认值。");
            }
        }

        return new ClientStatus
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName,
            Running = running,
            Version = first?.Version,
            Endpoints = endpoints,
            TunEnabled = tun.Enabled,
            TunAddress = tun.Address,
            Evidence = evidence is null ? Array.Empty<ClientConfigEvidence>() : new[] { evidence },
            Gaps = gaps,
            Notes = notes,
        };
    }

    private static ClientStatus BuildUnidentifiedStatus(
        IReadOnlyList<ClientEndpoint> endpoints,
        SortedSet<string> ownerNames,
        int ownerUnreadable,
        int unsupportedOwner,
        Collected<IReadOnlyList<ProcessObservation>> processResult,
        Collected<IReadOnlyList<ListenerObservation>> listenerResult,
        TunView tun,
        Collected<IReadOnlyList<ProxyLayerObservation>> proxyResult)
    {
        var gaps = new List<string>
        {
            "以下监听端点不属于任何受支持客户端；仅记录端点，不推断客户端身份。",
        };

        if (ownerUnreadable > 0)
        {
            gaps.Add($"有 {ownerUnreadable} 个监听端点无法读取所有者进程；未归属到任何客户端。");
        }

        if (unsupportedOwner > 0)
        {
            gaps.Add($"有 {unsupportedOwner} 个监听端点属于未支持的进程；未归类为受支持客户端。");
        }

        if (!processResult.HasValue)
        {
            gaps.Add("进程列表不可读，监听者身份保持未知。");
        }

        if (listenerResult.Status == EventStatus.Partial)
        {
            gaps.Add("监听端点列表不完整，未识别端点可能缺少。");
        }

        if (tun.Gap is not null)
        {
            gaps.Add(tun.Gap);
        }

        var notes = new List<string>
        {
            "未识别：该组端点用于展示未知监听，不推断客户端身份，不判定冲突，也不提供停止/切换入口。",
        };

        if (ownerNames.Count > 0)
        {
            notes.Add($"监听者进程：{string.Join("、", ownerNames)}（均非受支持客户端）。");
        }

        if (tun.Note is not null)
        {
            notes.Add(tun.Note);
        }

        var proxyView = DescribeProxyLayers(proxyResult);
        notes.Add(proxyView.Note);
        if (proxyView.Gap is not null)
        {
            gaps.Add(proxyView.Gap);
        }

        return new ClientStatus
        {
            ClientId = KnownClientIds.UnknownListener,
            DisplayName = "未识别的监听进程",
            Running = true,
            Version = null,
            Endpoints = endpoints,
            TunEnabled = tun.Enabled,
            TunAddress = tun.Address,
            Evidence = Array.Empty<ClientConfigEvidence>(),
            Gaps = gaps,
            Notes = notes,
        };
    }

    private static IReadOnlyList<ClientStatus> ApplyCoexistenceNotes(IReadOnlyList<ClientStatus> statuses)
    {
        var byEndpoint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            foreach (var endpoint in status.Endpoints)
            {
                var key = EndpointKey(endpoint);
                if (!byEndpoint.TryGetValue(key, out var owners))
                {
                    owners = [];
                    byEndpoint[key] = owners;
                }

                if (!owners.Contains(status.ClientId, StringComparer.Ordinal))
                {
                    owners.Add(status.ClientId);
                }
            }
        }

        var shared = byEndpoint
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (shared.Count == 0)
        {
            return statuses;
        }

        var updated = new List<ClientStatus>(statuses.Count);
        foreach (var status in statuses)
        {
            var notes = new List<string>(status.Notes);
            foreach (var endpoint in status.Endpoints)
            {
                var key = EndpointKey(endpoint);
                if (!shared.Contains(key))
                {
                    continue;
                }

                var others = statuses
                    .Where(other => other.ClientId != status.ClientId
                        && other.Endpoints.Any(candidate => string.Equals(EndpointKey(candidate), key, StringComparison.Ordinal)))
                    .Select(other => other.DisplayName)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (others.Length == 0)
                {
                    continue;
                }

                notes.Add($"端点并存：{endpoint.Display} 同时出现在 {status.DisplayName} 与 {string.Join("、", others)}。"
                    + "M1 只记录并存，不判定冲突，也不提供停止/切换。");
            }

            updated.Add(notes.Count == status.Notes.Count ? status : status with { Notes = notes });
        }

        return updated;
    }

    private static ClientEndpoint CreateEndpoint(ListenerObservation listener) => new()
    {
        Role = "listen",
        Host = string.IsNullOrWhiteSpace(listener.Host) ? null : listener.Host.Trim(),
        Port = listener.Port,
        Source = listener.OwnerProcessId is null ? "tcp_listener:owner_unknown" : "tcp_listener:owner_pid",
    };

    private static string EndpointKey(ClientEndpoint endpoint) =>
        $"{(string.IsNullOrWhiteSpace(endpoint.Host) ? "*" : endpoint.Host.Trim().ToLowerInvariant())}:{endpoint.Port}";

    private static TunView ObserveTun(Collected<IReadOnlyList<NetworkAdapterObservation>> result)
    {
        if (!result.HasValue)
        {
            return new TunView(null, null, null, $"网络适配器列表不可读：{result.Reason ?? "unknown"}");
        }

        var candidates = result.Value!
            .Select(TunAdapterHeuristics.Describe)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
        var active = candidates.FirstOrDefault(candidate => candidate.Up);

        if (active is not null)
        {
            var address = active.Adapter.Addresses.Count == 0 ? null : active.Adapter.Addresses[0];
            var gap = result.Status == EventStatus.Partial
                ? "网络适配器列表只读取了一部分，TUN 结论可能不完整。"
                : address is null ? "TUN 候选适配器未报告可用地址。" : null;
            return new TunView(
                true,
                address,
                $"TUN 候选：{active.Adapter.Name}（依据：{active.Reason}）。适配器存在不等于由该客户端创建或承载本次流量。",
                gap);
        }

        return new TunView(
            false,
            null,
            "未观测到启用的 TUN 候选适配器；这只表示适配器列表中没有匹配项。",
            result.Status == EventStatus.Partial ? "网络适配器列表只读取了一部分，TUN 结论可能不完整。" : null);
    }

    private static string RunningNote(ProcessObservation process) =>
        $"运行中：已观测到进程 {process.Name ?? "<未知进程名>"}（PID {process.ProcessId}）。"
        + "运行状态不证明该进程正在承载代理流量。";

    /// <summary>
    /// Value-free proxy layer summary. It is shown next to each client but explicitly does not
    /// claim which layer that client's requests actually used.
    /// </summary>
    private static ProxyView DescribeProxyLayers(Collected<IReadOnlyList<ProxyLayerObservation>> result)
    {
        if (!result.HasValue)
        {
            return new ProxyView(
                $"代理层摘要不可用：{result.Reason ?? "unknown"}；不假设任何层。",
                $"代理层不可读：{result.Reason ?? "unknown"}");
        }

        var layers = result.Value!;
        if (layers.Count == 0)
        {
            return new ProxyView(
                "代理层摘要：没有可报告的代理层。",
                result.Status == EventStatus.Partial ? "代理层列表不完整。" : null);
        }

        var parts = layers.Select(layer => $"{layer.Layer}={LayerState(layer.Enabled)}");
        return new ProxyView(
            $"代理层摘要（本机只读，不证明该客户端实际使用）：{string.Join("；", parts)}。值不采集。",
            result.Status == EventStatus.Partial ? "代理层列表不完整。" : null);
    }

    private static string LayerState(bool? enabled) => enabled switch
    {
        true => "已设置",
        false => "未设置",
        _ => "未知",
    };

    private static string ProvenanceText(ConfigProvenance provenance) => provenance switch
    {
        ConfigProvenance.RuntimeApi => "runtime_api",
        ConfigProvenance.ProcessStartReference => "process_start_reference",
        ConfigProvenance.UserReport => "user_report",
        _ => "disk_only",
    };

    private static Collected<IReadOnlyList<T>> Safe<T>(
        Func<Collected<IReadOnlyList<T>>> read,
        string what)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Collected<IReadOnlyList<T>>.Unavailable(
                EventStatus.Failed,
                $"{what}读取失败。",
                new[] { exception.GetType().Name });
        }
    }

    private sealed record TunView(bool? Enabled, string? Address, string? Note, string? Gap);

    private sealed record ProxyView(string Note, string? Gap);
}
