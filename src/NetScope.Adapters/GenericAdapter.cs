using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters;

/// <summary>
/// The generic, read-only multi-client view. It combines process identity for the supported
/// clients, listening endpoints with owner attribution where readable, machine-level TUN candidates
/// and per-client configuration evidence. Unknown never becomes a guess: unreadable fields keep
/// their own reason code, a core name alone does not prove ownership, listeners that cannot be
/// attributed stay in an unidentified group, and a TUN adapter is never copied onto every client.
/// </summary>
public sealed class GenericAdapter : IClientStatusProvider
{
    private static readonly TimeSpan InstanceTimeTolerance = TimeSpan.FromSeconds(2);

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

    public async Task<IReadOnlyList<ClientStatus>> ReadAsync(CancellationToken cancellationToken) =>
        (await ReadObservationAsync(cancellationToken).ConfigureAwait(false)).Clients;

    /// <summary>
    /// One read of every source this adapter needs, returned together with the derived client
    /// rows. Evidence projection uses this instead of re-reading a second time, so a record can
    /// never mix facts from two different reads of the same cycle.
    /// </summary>
    public async Task<AdapterObservationRead> ReadObservationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readAt = _time.GetUtcNow();

        var processResult = Safe(_processes.ReadProcesses, "进程列表");
        var listenerResult = Safe(_listeners.ReadListeners, "监听端点");
        var adapterResult = Safe(_networkAdapters.ReadAdapters, "网络适配器列表");
        var tun = ObserveTun(adapterResult);
        var proxyResult = Safe(_proxyLayers.ReadLayers, "代理层");

        var processes = processResult.Value ?? Array.Empty<ProcessObservation>();
        var identity = BuildIdentity(processes, listenerResult);
        var endpointSets = AttributeListeners(listenerResult, identity, processes);

        var statuses = new List<ClientStatus>(ClientCatalog.All.Count + 3);
        foreach (var client in ClientCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(await BuildStatusAsync(
                client,
                identity,
                endpointSets,
                processResult,
                listenerResult,
                tun,
                adapterResult,
                proxyResult,
                readAt,
                cancellationToken));
        }

        var finalStatuses = ApplyCoexistenceNotes(statuses);
        var rows = new List<ClientStatus>(finalStatuses) { BuildSystemTunStatus(tun, proxyResult) };

        if (identity.UnattributedCores.Count > 0)
        {
            rows.Add(BuildUnattributedCoreStatus(identity.UnattributedCores, tun, proxyResult));
        }

        if (endpointSets.Unattributed.Count > 0)
        {
            rows.Add(BuildUnidentifiedStatus(endpointSets, tun, proxyResult));
        }

        return new AdapterObservationRead(
            readAt,
            processResult,
            listenerResult,
            adapterResult,
            proxyResult,
            rows);
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

    private static IdentityView BuildIdentity(
        IReadOnlyList<ProcessObservation> processes,
        Collected<IReadOnlyList<ListenerObservation>> listenerResult)
    {
        var ui = new Dictionary<string, List<ProcessObservation>>(StringComparer.Ordinal);
        foreach (var client in ClientCatalog.All)
        {
            ui[client.ClientId] = [];
        }

        var uiByPid = new Dictionary<int, string>();
        foreach (var process in processes)
        {
            foreach (var client in ClientCatalog.All)
            {
                if (!ClientCatalog.Matches(client, process))
                {
                    continue;
                }

                ui[client.ClientId].Add(process);
                uiByPid.TryAdd(process.ProcessId, client.ClientId);
                break;
            }
        }

        var uiProcesses = ui.SelectMany(pair => pair.Value).ToArray();
        var uiPids = uiProcesses.Select(process => process.ProcessId).ToHashSet();
        var listenerOwners = (listenerResult.Value ?? Array.Empty<ListenerObservation>())
            .Where(listener => listener.OwnerProcessId is not null)
            .Select(listener => listener.OwnerProcessId!.Value)
            .ToHashSet();

        var cores = new List<CoreCandidate>();
        foreach (var process in processes)
        {
            if (uiPids.Contains(process.ProcessId))
            {
                continue;
            }

            var named = ClientCatalog.IsAnyCoreNameCandidate(process);
            var parentPidMatches = uiProcesses
                .Where(candidate => candidate.ProcessId == process.ParentProcessId)
                .ToArray();
            var childCandidate = parentPidMatches.Length > 0;
            var sameDirectoryClients = uiProcesses
                .Where(candidate => SameDirectory(process.ExecutablePath, candidate.ExecutablePath))
                .Select(candidate => candidate.ProcessId)
                .ToHashSet();
            var ownsListener = listenerOwners.Contains(process.ProcessId);

            // A path-only process is only considered a core when it also owns a listener; this
            // avoids turning every helper executable in an install directory into a proxy core.
            if (!named && !childCandidate && !(ownsListener && sameDirectoryClients.Count > 0))
            {
                continue;
            }

            cores.Add(AttributeCore(process, parentPidMatches, ui, sameDirectoryClients, named, ownsListener));
        }

        return new IdentityView(ui, uiByPid, cores);
    }

    private static CoreCandidate AttributeCore(
        ProcessObservation process,
        IReadOnlyList<ProcessObservation> parentPidMatches,
        IReadOnlyDictionary<string, List<ProcessObservation>> ui,
        IReadOnlySet<int> sameDirectoryPids,
        bool named,
        bool ownsListener)
    {
        var gaps = new List<string>();
        var fieldGaps = FieldGaps(process);

        // A parent PID is only verifiable when both instances report a start time and the child did
        // not start before the parent. Otherwise the link could belong to a reused PID.
        var verifiedParents = new List<(string ClientId, ProcessObservation Ui)>();
        foreach (var parent in parentPidMatches)
        {
            var clientId = ClientIdOfPid(ui, parent.ProcessId);
            if (clientId is null)
            {
                continue;
            }

            if (parent.StartTime is not DateTimeOffset parentStarted
                || process.StartTime is not DateTimeOffset coreStarted
                || coreStarted < parentStarted - InstanceTimeTolerance)
            {
                continue;
            }

            verifiedParents.Add((clientId, parent));
        }

        var sameDirectoryClients = new List<string>();
        foreach (var (clientId, clientProcesses) in ui)
        {
            if (clientProcesses.Any(candidate => sameDirectoryPids.Contains(candidate.ProcessId)))
            {
                sameDirectoryClients.Add(clientId);
            }
        }

        if (parentPidMatches.Count > 0 && verifiedParents.Count == 0)
        {
            gaps.Add("父子 PID 相同，但双方启动时间不可读或顺序矛盾，无法验证是同一实例；未归属。");
        }

        var parentClients = verifiedParents.Select(pair => pair.ClientId).Distinct(StringComparer.Ordinal).ToArray();
        if (parentClients.Length > 1)
        {
            gaps.Add("该内核候选同时与多个客户端 UI 进程形成父子关系，归属存在歧义；未归属。");
        }

        (string ClientId, ProcessObservation Ui)? parentMatch = parentClients.Length == 1
            ? verifiedParents.First(pair => pair.ClientId == parentClients[0])
            : null;

        if (parentMatch is { } match)
        {
            if (sameDirectoryClients.Count == 1 && !string.Equals(sameDirectoryClients[0], match.ClientId, StringComparison.Ordinal))
            {
                gaps.Add("父子关系指向一个客户端，而安装目录指向另一个客户端；归属存在歧义，未归属。");
                return CoreCandidate.Unattributed(process, fieldGaps, gaps);
            }

            var evidence = $"父进程 PID {match.Ui.ProcessId}（{NameOf(match.Ui)}，启动 {TimeText(match.Ui.StartTime)}）"
                + $"与核心 PID {process.ProcessId}（启动 {TimeText(process.StartTime)}）构成可验证父子关系。"
                + (ownsListener ? "该核心实际拥有监听端点。" : string.Empty);
            return new CoreCandidate(process, match.ClientId, "parent_child", evidence, fieldGaps, string.Join("；", gaps));
        }

        if (sameDirectoryClients.Count == 1)
        {
            var evidence = $"可执行文件目录与 {DisplayNameOf(sameDirectoryClients[0])} 的进程目录相同"
                + (ownsListener ? "，且该进程实际拥有监听端点" : string.Empty)
                + "；这是路径关联，不是父子实例验证。";
            return new CoreCandidate(process, sameDirectoryClients[0], "same_install_directory", evidence, fieldGaps, string.Join("；", gaps));
        }

        if (sameDirectoryClients.Count > 1)
        {
            gaps.Add("该内核候选的目录同时匹配多个客户端安装目录，归属存在歧义；未归属。");
        }

        if (named && gaps.Count == 0)
        {
            gaps.Add("名称像内核候选，但缺少可验证父子/安装目录关联；未归属到任何客户端。");
        }

        if (gaps.Count == 0)
        {
            gaps.Add("没有可验证的父子或安装目录关联；只记录进程身份。");
        }

        return CoreCandidate.Unattributed(process, fieldGaps, gaps);
    }

    private static EndpointSets AttributeListeners(
        Collected<IReadOnlyList<ListenerObservation>> listenerResult,
        IdentityView identity,
        IReadOnlyList<ProcessObservation> processes)
    {
        var endpointsByClient = new Dictionary<string, List<ClientEndpoint>>(StringComparer.Ordinal);
        foreach (var client in ClientCatalog.All)
        {
            endpointsByClient[client.ClientId] = [];
        }

        var unattributed = new List<ClientEndpoint>();
        var ownerNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var gaps = new List<string>();
        var ownerUnreadable = 0;
        var unsupportedFamily = 0;
        var ownerRowMissing = 0;
        var pidReuse = 0;
        var unsupportedOwner = 0;
        var ownerNotInProcessList = 0;
        var processByPid = processes.ToDictionary(process => process.ProcessId, process => process);
        var coresByPid = identity.Cores
            .GroupBy(core => core.Process.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());

        if (listenerResult.Value is not null && listenerResult.Status == EventStatus.Partial)
        {
            gaps.Add("监听端点列表不完整，核对时可能缺少端点。");
        }

        foreach (var listener in listenerResult.Value ?? Array.Empty<ListenerObservation>())
        {
            if (listener.Port is < 1 or > 65535)
            {
                gaps.Add("忽略了一个端口不在 1..65535 范围内的监听观测。");
                continue;
            }

            var endpoint = CreateEndpoint(listener);
            var ownerPid = listener.OwnerProcessId;
            if (ownerPid is null)
            {
                if (string.Equals(listener.OwnerReadState, "unsupported_family", StringComparison.Ordinal))
                {
                    unsupportedFamily++;
                }
                else if (string.Equals(listener.OwnerReadReason, "owner_row_not_found", StringComparison.Ordinal))
                {
                    ownerRowMissing++;
                }
                else
                {
                    ownerUnreadable++;
                }

                unattributed.Add(endpoint);
                continue;
            }

            if (!processByPid.TryGetValue(ownerPid.Value, out var owner))
            {
                ownerNotInProcessList++;
                unattributed.Add(endpoint with
                {
                    AttributionEvidence = $"所有者 PID {ownerPid.Value} 不在本次进程快照中；可能是采样竞态或进程已退出。",
                });
                continue;
            }

            var ownerName = string.IsNullOrWhiteSpace(listener.OwnerProcessName) ? owner.Name : listener.OwnerProcessName;
            endpoint = endpoint with { OwnerProcessName = ownerName };

            if (IsReusedPid(listener, owner))
            {
                pidReuse++;
                unattributed.Add(endpoint with
                {
                    AttributionEvidence = $"监听端点创建时间早于当前 PID {ownerPid.Value} 进程启动时间，疑似 PID 复用；不归属。",
                });
                continue;
            }

            if (identity.UiByPid.TryGetValue(ownerPid.Value, out var uiClient))
            {
                endpointsByClient[uiClient].Add(endpoint with
                {
                    Attribution = "ui",
                    AttributionEvidence = $"所有者进程就是 {DisplayNameOf(uiClient)} 的界面进程（PID {ownerPid.Value}，启动 {TimeText(owner.StartTime)}）。"
                        + OwnerMatchNote(listener),
                });
                continue;
            }

            if (coresByPid.TryGetValue(ownerPid.Value, out var core) && core.ClientId is not null)
            {
                endpointsByClient[core.ClientId].Add(endpoint with
                {
                    Attribution = "core",
                    AttributionEvidence = core.Evidence + $" 实际监听者为该核心 PID {ownerPid.Value}。" + OwnerMatchNote(listener),
                });
                continue;
            }

            if (coresByPid.ContainsKey(ownerPid.Value))
            {
                unattributed.Add(endpoint with
                {
                    AttributionEvidence = "所有者是内核候选进程，但该核心未获得可验证的客户端归属。",
                });
            }
            else
            {
                unsupportedOwner++;
                unattributed.Add(endpoint with
                {
                    AttributionEvidence = "所有者进程不是受支持客户端，也不是已归属的内核候选。",
                });
            }

            if (!string.IsNullOrWhiteSpace(ownerName))
            {
                ownerNames.Add(ownerName);
            }
        }

        return new EndpointSets(
            endpointsByClient,
            unattributed,
            ownerNames,
            gaps,
            ownerUnreadable,
            unsupportedFamily,
            ownerRowMissing,
            pidReuse,
            unsupportedOwner,
            ownerNotInProcessList);
    }

    private async Task<ClientStatus> BuildStatusAsync(
        KnownClient client,
        IdentityView identity,
        EndpointSets endpointSets,
        Collected<IReadOnlyList<ProcessObservation>> processResult,
        Collected<IReadOnlyList<ListenerObservation>> listenerResult,
        TunView tun,
        Collected<IReadOnlyList<NetworkAdapterObservation>> adapterResult,
        Collected<IReadOnlyList<ProxyLayerObservation>> proxyResult,
        DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        var gaps = new List<string>();
        var notes = new List<string>();
        var uiProcesses = identity.Ui[client.ClientId];
        var cores = identity.Cores.Where(core => string.Equals(core.ClientId, client.ClientId, StringComparison.Ordinal)).ToArray();
        var first = uiProcesses.Count > 0 ? uiProcesses[0] : null;

        bool? running;
        if (!processResult.HasValue)
        {
            running = null;
            gaps.Add($"进程列表不可读：{processResult.Reason ?? "unknown"}");
            notes.Add("运行状态未知：进程列表不可读；未发现进程不能当作未运行。");
        }
        else if (processResult.Status == EventStatus.Partial)
        {
            running = first is null && cores.Length == 0 ? null : true;
            if (running is null)
            {
                gaps.Add("进程列表只读取了一部分；未发现该客户端进程不能证明其未运行。");
                notes.Add("运行状态未知：进程列表不完整；未发现进程不能当作未运行。");
            }
            else
            {
                notes.Add(RunningNote(first, cores));
            }
        }
        else
        {
            running = first is not null || cores.Length > 0;
            notes.Add(running == true
                ? RunningNote(first, cores)
                : "未运行：进程源中未发现该客户端的受支持进程。配置证据只表示磁盘上存在配置，不代表已加载。");
        }

        if (first is not null && !first.WasRead(ObservationFieldNames.ExecutablePath))
        {
            gaps.Add($"已匹配客户端进程，但可执行文件路径不可读（{ReasonOf(first, ObservationFieldNames.ExecutablePath)}）；路径保持未知。");
        }

        if (first is not null && !first.WasRead(ObservationFieldNames.StartTime))
        {
            notes.Add($"客户端进程启动时间不可读（{ReasonOf(first, ObservationFieldNames.StartTime)}）；PID 复用无法排除。");
        }

        if (first is not null && !string.IsNullOrWhiteSpace(first.Version))
        {
            notes.Add($"版本：{first.Version}。");
        }

        foreach (var core in cores)
        {
            if (!string.IsNullOrWhiteSpace(core.Gap))
            {
                gaps.Add($"内核候选 PID {core.Process.ProcessId}：{core.Gap}");
            }

            if (core.Process.WasRead(ObservationFieldNames.ExecutablePath) && !string.IsNullOrWhiteSpace(core.Process.ExecutablePath))
            {
                continue;
            }

            gaps.Add($"内核候选 PID {core.Process.ProcessId} 的路径不可读（{ReasonOf(core.Process, ObservationFieldNames.ExecutablePath)}）；归属证据可能不完整。");
        }

        notes.Add(
            $"身份匹配：按 {ClientCatalog.IdentityVersion} 白名单（{string.Join("、", client.ProcessNames)}）；"
            + "内核名称只作为候选，需父子/目录关联才归属。");

        if (!listenerResult.HasValue)
        {
            gaps.Add($"监听端点不可读：{listenerResult.Reason ?? "unknown"}");
            notes.Add("端点未知：监听列表不可读，没有把空列表当作无监听。");
        }

        gaps.AddRange(endpointSets.Gaps);

        var endpoints = endpointSets.ByClient[client.ClientId];
        notes.Add(DescribeEndpoints(endpoints));

        if (tun.Gap is not null)
        {
            gaps.Add(tun.Gap);
        }

        notes.Add(DescribeClientTun(client.DisplayName, tun, adapterResult.HasValue));

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
            // A valid subset is not complete evidence: missing required fields and ignored
            // structures stay visible at the client-status boundary as well as on the evidence.
            gaps.AddRange(evidence.Gaps);
            if (evidence.Notices.Count > 0)
            {
                notes.Add("配置提示（不计为缺口）：" + string.Join("；", evidence.Notices));
            }

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
            Processes = DescribeProcesses(uiProcesses, cores),
            Endpoints = endpoints,
            TunOwnership = adapterResult.HasValue ? "unconfirmed" : "unknown",
            TunEnabled = null,
            TunAddress = null,
            Evidence = evidence is null ? Array.Empty<ClientConfigEvidence>() : new[] { evidence },
            Gaps = gaps,
            Notes = notes,
        };
    }

    private static ClientStatus BuildSystemTunStatus(
        TunView tun,
        Collected<IReadOnlyList<ProxyLayerObservation>> proxyResult)
    {
        var gaps = new List<string>();
        if (tun.Gap is not null)
        {
            gaps.Add(tun.Gap);
        }

        var notes = new List<string>
        {
            tun.Note ?? "未观测到 TUN 候选。",
            "归属未确认：只读采集只命名候选适配器，没有可信 owner，不把它归给任何客户端，也不声称它承载了本次流量。",
        };

        if (tun.Candidates.Count > 0)
        {
            notes.Add("候选依据仅为接口类型/名称/地址启发式；候选存在不等于 TUN 生效，也不等于默认路由经过它。");
        }

        var proxyView = DescribeProxyLayers(proxyResult);
        notes.Add(proxyView.Note);
        if (proxyView.Gap is not null)
        {
            gaps.Add(proxyView.Gap);
        }

        return new ClientStatus
        {
            ClientId = KnownClientIds.SystemTun,
            DisplayName = "系统 TUN 候选（不归属客户端）",
            Running = null,
            TunOwnership = tun.Enabled switch
            {
                true => "system_unconfirmed",
                false => "none_observed",
                _ => "unknown",
            },
            TunEnabled = tun.Enabled,
            TunAddress = tun.AddressSummary,
            TunCandidates = tun.Candidates,
            Evidence = Array.Empty<ClientConfigEvidence>(),
            Gaps = gaps,
            Notes = notes,
        };
    }

    private static ClientStatus BuildUnattributedCoreStatus(
        IReadOnlyList<CoreCandidate> cores,
        TunView tun,
        Collected<IReadOnlyList<ProxyLayerObservation>> proxyResult)
    {
        var gaps = cores.Select(core => $"{NameOf(core.Process)}（PID {core.Process.ProcessId}）：{core.Gap}").ToArray();
        if (tun.Gap is not null)
        {
            gaps = gaps.Append(tun.Gap).ToArray();
        }

        var proxyView = DescribeProxyLayers(proxyResult);
        if (proxyView.Gap is not null)
        {
            gaps = gaps.Append(proxyView.Gap).ToArray();
        }

        return new ClientStatus
        {
            ClientId = KnownClientIds.UnattributedCore,
            DisplayName = "未归属的内核候选进程",
            Running = true,
            Processes = cores.Select(core => ToClientProcess(core.Process, "core", core.Association, core.Evidence, core.FieldGaps)).ToArray(),
            TunOwnership = "unconfirmed",
            Evidence = Array.Empty<ClientConfigEvidence>(),
            Gaps = gaps,
            Notes = new[]
            {
                "这里只记录内核候选的身份和关联缺口；不宣称它们属于 Nice 或 Apex，也不提供停止/切换入口。",
                "两个同名核心来自不同安装目录时分别列出，不会因为名称相同而串归属。",
                proxyView.Note,
            },
        };
    }

    private static ClientStatus BuildUnidentifiedStatus(
        EndpointSets endpointSets,
        TunView tun,
        Collected<IReadOnlyList<ProxyLayerObservation>> proxyResult)
    {
        var gaps = new List<string>
        {
            "以下监听端点不属于任何受支持客户端；仅记录端点与所有者事实，不推断客户端身份。",
        };
        gaps.AddRange(endpointSets.Gaps);

        if (endpointSets.OwnerUnreadable > 0)
        {
            gaps.Add($"有 {endpointSets.OwnerUnreadable} 个监听端点无法读取所有者进程；未归属到任何客户端。");
        }

        if (endpointSets.UnsupportedFamily > 0)
        {
            gaps.Add($"有 {endpointSets.UnsupportedFamily} 个 IPv6 监听端点暂不支持读取所有者；未借用 IPv4 结果。");
        }

        if (endpointSets.OwnerRowMissing > 0)
        {
            gaps.Add($"有 {endpointSets.OwnerRowMissing} 个监听端点未在所有者表中定位；可能是采样竞态。");
        }

        if (endpointSets.PidReuse > 0)
        {
            gaps.Add($"有 {endpointSets.PidReuse} 个监听端点疑似 PID 复用，已保留端点事实但不归属。");
        }

        if (endpointSets.OwnerNotInProcessList > 0)
        {
            gaps.Add($"有 {endpointSets.OwnerNotInProcessList} 个所有者 PID 不在本次进程快照中；可能是采样竞态或进程已退出。");
        }

        if (endpointSets.UnsupportedOwner > 0)
        {
            gaps.Add($"有 {endpointSets.UnsupportedOwner} 个监听端点属于未支持的进程；未归类为受支持客户端。");
        }

        if (tun.Gap is not null)
        {
            gaps.Add(tun.Gap);
        }

        var proxyView = DescribeProxyLayers(proxyResult);
        if (proxyView.Gap is not null)
        {
            gaps.Add(proxyView.Gap);
        }

        var notes = new List<string>
        {
            "未识别：该组端点用于展示未知监听，不推断客户端身份，不判定冲突，也不提供停止/切换入口。",
            DescribeUnattributedEndpoints(endpointSets.Unattributed),
        };

        if (endpointSets.OwnerNames.Count > 0)
        {
            notes.Add($"监听者进程：{string.Join("、", endpointSets.OwnerNames)}（均非已归属的受支持客户端）。");
        }

        notes.Add(proxyView.Note);

        return new ClientStatus
        {
            ClientId = KnownClientIds.UnknownListener,
            DisplayName = "未识别的监听进程",
            Running = true,
            Endpoints = endpointSets.Unattributed,
            TunOwnership = "unconfirmed",
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

    private static IReadOnlyList<ClientProcess> DescribeProcesses(
        IReadOnlyList<ProcessObservation> uiProcesses,
        IReadOnlyList<CoreCandidate> cores)
    {
        var rows = new List<ClientProcess>(uiProcesses.Count + cores.Count);
        rows.AddRange(uiProcesses.Select(process => ToClientProcess(process, "ui", null, null, FieldGaps(process))));
        rows.AddRange(cores.Select(core => ToClientProcess(
            core.Process, "core", core.Association, core.Evidence, core.FieldGaps)));
        return rows;
    }

    private static ClientProcess ToClientProcess(
        ProcessObservation process,
        string role,
        string? association,
        string? evidence,
        IReadOnlyList<string> fieldGaps) => new()
    {
        Role = role,
        ProcessId = process.ProcessId,
        StartTime = process.StartTime,
        Name = process.Name,
        ExecutablePath = process.ExecutablePath,
        Version = process.Version,
        ParentProcessId = process.ParentProcessId,
        Association = association,
        AssociationEvidence = evidence,
        FieldGaps = fieldGaps,
    };

    private static IReadOnlyList<string> FieldGaps(ProcessObservation process) =>
        process.UnreadableFields
            .Select(pair => $"{pair.Key}={pair.Value}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

    private static ClientEndpoint CreateEndpoint(ListenerObservation listener) => new()
    {
        Role = "listen",
        Host = string.IsNullOrWhiteSpace(listener.Host) ? null : listener.Host.Trim(),
        Port = listener.Port,
        Source = listener.OwnerProcessId is null ? "tcp_listener:owner_unknown" : "tcp_listener:owner_pid",
        AddressFamily = listener.AddressFamily,
        OwnerProcessId = listener.OwnerProcessId,
        OwnerProcessName = listener.OwnerProcessName,
        OwnerReadState = listener.OwnerReadState,
        OwnerReadReason = listener.OwnerReadReason,
        Attribution = "unassigned",
        AttributionEvidence = listener.OwnerProcessId is null
            ? $"所有者 PID 未读取（{listener.OwnerReadReason ?? listener.OwnerReadState}）。"
            : null,
    };

    private static bool IsReusedPid(ListenerObservation listener, ProcessObservation owner)
    {
        // A listening socket closes with its process, so the only realistic reuse is between the
        // listener read and the process read. A socket created before the current instance started
        // cannot belong to it.
        return listener.OwnerInstanceStartTime is DateTimeOffset created
            && owner.StartTime is DateTimeOffset started
            && started > created + InstanceTimeTolerance;
    }

    private static string OwnerMatchNote(ListenerObservation listener) =>
        string.Equals(listener.OwnerReadReason, "address_mismatch_port_match", StringComparison.Ordinal)
            ? " 所有者 PID 通过唯一端口匹配得出（监听地址与所有者表地址表示不同）。"
            : string.Empty;

    private static string DescribeEndpoints(IReadOnlyList<ClientEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return "端点：本客户端没有可归属的实际监听；磁盘配置中的端口不当作实际监听。";
        }

        var parts = endpoints.Select(endpoint =>
        {
            var owner = endpoint.OwnerProcessId is null
                ? $"所有者未知（{endpoint.OwnerReadReason ?? endpoint.OwnerReadState ?? "unknown"}）"
                : $"所有者 PID {endpoint.OwnerProcessId}"
                    + (string.IsNullOrWhiteSpace(endpoint.OwnerProcessName) ? string.Empty : $" {endpoint.OwnerProcessName}")
                    + $"（读取 {endpoint.OwnerReadState ?? "unknown"}）";
            var attribution = endpoint.Attribution == "ui" ? "UI 进程直接监听"
                : endpoint.Attribution == "core" ? "核心进程监听"
                : "归属待确认";
            return $"{endpoint.Display} [{endpoint.AddressFamily}] {owner}；{attribution}。{endpoint.AttributionEvidence}";
        });

        return "端点：" + string.Join(" | ", parts);
    }

    private static string DescribeUnattributedEndpoints(IReadOnlyList<ClientEndpoint> endpoints)
    {
        var parts = endpoints.Select(endpoint =>
        {
            var owner = endpoint.OwnerProcessId is null
                ? $"所有者未读取：{endpoint.OwnerReadReason ?? endpoint.OwnerReadState ?? "unknown"}"
                : $"所有者 PID {endpoint.OwnerProcessId}"
                    + (string.IsNullOrWhiteSpace(endpoint.OwnerProcessName) ? string.Empty : $" {endpoint.OwnerProcessName}");
            return $"{endpoint.Display} [{endpoint.AddressFamily}] {owner}；{endpoint.AttributionEvidence}";
        });
        return "端点事实：" + string.Join(" | ", parts);
    }

    private static string DescribeClientTun(string displayName, TunView tun, bool adaptersReadable)
    {
        if (!adaptersReadable)
        {
            return "TUN 归属未知：网络适配器列表不可读。";
        }

        if (tun.Candidates.Count == 0)
        {
            return "TUN 归属未确认：未观测到系统级 TUN 候选（见系统观察卡片）；这不证明该客户端没有 TUN 能力或没有请求 TUN。";
        }

        return $"TUN 归属未确认：本机观测到 {tun.Candidates.Count} 个系统级 TUN 候选（见系统观察卡片）；"
            + $"适配器存在不等于由 {displayName} 创建或承载本次流量。";
    }

    private static TunView ObserveTun(Collected<IReadOnlyList<NetworkAdapterObservation>> result)
    {
        if (!result.HasValue)
        {
            return new TunView(
                Array.Empty<TunCandidate>(),
                null,
                null,
                $"网络适配器列表不可读：{result.Reason ?? "unknown"}",
                null);
        }

        var candidates = (result.Value ?? Array.Empty<NetworkAdapterObservation>())
            .Select(TunAdapterHeuristics.Describe)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Select(ToTunCandidate)
            .ToArray();

        var up = candidates
            .Where(candidate => string.Equals(candidate.OperationalStatus, "up", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        bool? enabled = up.Length > 0
            ? true
            : result.Status == EventStatus.Partial ? null : false;

        var addressSummary = candidates.Length == 0
            ? null
            : string.Join("；", candidates.Select(candidate =>
                $"{candidate.Name}：IPv4 {FamilyText(candidate.Ipv4Addresses)}；IPv6 {FamilyText(candidate.Ipv6Addresses)}"));

        string? gap = result.Status == EventStatus.Partial
            ? "网络适配器列表只读取了一部分，TUN 结论可能不完整。"
            : candidates.Length > 0 && candidates.All(candidate =>
                candidate.Ipv4Addresses.Count == 0 && candidate.Ipv6Addresses.Count == 0)
                ? "TUN 候选适配器未报告可用地址。"
                : null;

        var note = candidates.Length == 0
            ? "未观测到启用的 TUN 候选适配器；这只表示适配器列表中没有匹配项。"
            : $"系统级 TUN 候选 {candidates.Length} 个，其中操作状态为 up 的 {up.Length} 个；"
                + "候选来自名称/类型/地址启发式，不代表默认路由实际经过它。";

        return new TunView(candidates, enabled, addressSummary, gap, note);
    }

    private static NetScope.Application.TunCandidate ToTunCandidate(TunCandidateEvidence view)
    {
        var adapter = view.Adapter;
        var (ipv4, ipv6) = FamilyAddresses(adapter);
        return new TunCandidate
        {
            Name = adapter.Name,
            Description = adapter.Description,
            Identity = adapter.InterfaceId,
            InterfaceIndex = adapter.InterfaceIndex,
            Luid = adapter.Luid,
            OperationalStatus = adapter.OperationalStatus,
            Reason = view.Reason,
            Ipv4Addresses = ipv4,
            Ipv6Addresses = ipv6,
            FieldGaps = adapter.UnreadableFields
                .Select(pair => $"{pair.Key}={pair.Value}")
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static (IReadOnlyList<string> Ipv4, IReadOnlyList<string> Ipv6) FamilyAddresses(NetworkAdapterObservation adapter)
    {
        static bool IsV4(string address) => TryParseAddress(address, out var parsed)
            && parsed!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        static bool IsV6(string address) => TryParseAddress(address, out var parsed)
            && parsed!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

        var ipv4 = adapter.Ipv4Addresses.Count > 0
            ? adapter.Ipv4Addresses
            : adapter.Addresses.Where(IsV4).ToArray();
        var ipv6 = adapter.Ipv6Addresses.Count > 0
            ? adapter.Ipv6Addresses
            : adapter.Addresses.Where(IsV6).ToArray();
        return (ipv4, ipv6);
    }

    private static bool TryParseAddress(string text, out System.Net.IPAddress? address)
    {
        // Addresses are stored as "a.b.c.d/prefix"; strip the prefix only for family detection.
        var slash = text.IndexOf('/');
        var candidate = slash < 0 ? text : text[..slash];
        return System.Net.IPAddress.TryParse(candidate, out address);
    }

    private static string FamilyText(IReadOnlyList<string> addresses) =>
        addresses.Count == 0 ? "未报告" : string.Join("、", addresses);

    private static string EndpointKey(ClientEndpoint endpoint) =>
        $"{(string.IsNullOrWhiteSpace(endpoint.Host) ? "*" : endpoint.Host.Trim().ToLowerInvariant())}:{endpoint.Port}";

    private static string RunningNote(
        ProcessObservation? ui,
        IReadOnlyList<CoreCandidate> cores)
    {
        if (ui is null)
        {
            return $"运行中：只观测到 {cores.Count} 个已归属内核候选（PID {string.Join("、", cores.Select(core => core.Process.ProcessId))}），"
                + "未观测到界面进程；内核运行不证明界面在运行。";
        }

        var text = $"运行中：已观测到界面进程 {ui.Name ?? "<未知进程名>"}（PID {ui.ProcessId}，启动 {TimeText(ui.StartTime)}）";
        if (cores.Count > 0)
        {
            text += $"；另有 {cores.Count} 个已归属内核候选（PID {string.Join("、", cores.Select(core => core.Process.ProcessId))}）";
        }

        return text + "。运行状态不证明该进程正在承载代理流量。";
    }

    private static string NameOf(ProcessObservation process) =>
        string.IsNullOrWhiteSpace(process.Name) ? "<未知进程名>" : process.Name!;

    private static string TimeText(DateTimeOffset? time) =>
        time is null ? "未知" : time.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'");

    private static string ReasonOf(ProcessObservation process, string field) =>
        process.UnreadableFields.TryGetValue(field, out var reason) ? reason : "unknown";

    private static string DisplayNameOf(string clientId) =>
        ClientCatalog.All.FirstOrDefault(client => client.ClientId == clientId)?.DisplayName ?? clientId;

    private static string? ClientIdOfPid(
        IReadOnlyDictionary<string, List<ProcessObservation>> ui,
        int processId)
    {
        foreach (var (clientId, processes) in ui)
        {
            if (processes.Any(process => process.ProcessId == processId))
            {
                return clientId;
            }
        }

        return null;
    }

    private static bool SameDirectory(string? leftPath, string? rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
        {
            return false;
        }

        try
        {
            var left = Path.GetDirectoryName(Path.GetFullPath(leftPath));
            var right = Path.GetDirectoryName(Path.GetFullPath(rightPath));
            return !string.IsNullOrWhiteSpace(left)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

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

        var layers = result.Value ?? Array.Empty<ProxyLayerObservation>();
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

    private sealed record IdentityView(
        IReadOnlyDictionary<string, List<ProcessObservation>> Ui,
        IReadOnlyDictionary<int, string> UiByPid,
        IReadOnlyList<CoreCandidate> Cores)
    {
        public IReadOnlyList<CoreCandidate> UnattributedCores =>
            Cores.Where(core => core.ClientId is null).ToArray();
    }

    private sealed record CoreCandidate(
        ProcessObservation Process,
        string? ClientId,
        string? Association,
        string? Evidence,
        IReadOnlyList<string> FieldGaps,
        string Gap)
    {
        public static CoreCandidate Unattributed(
            ProcessObservation process,
            IReadOnlyList<string> fieldGaps,
            IReadOnlyList<string> reasons) => new(
            process,
            null,
            null,
            null,
            fieldGaps,
            string.Join("；", reasons));
    }

    private sealed record EndpointSets(
        IReadOnlyDictionary<string, List<ClientEndpoint>> ByClient,
        IReadOnlyList<ClientEndpoint> Unattributed,
        IReadOnlySet<string> OwnerNames,
        IReadOnlyList<string> Gaps,
        int OwnerUnreadable,
        int UnsupportedFamily,
        int OwnerRowMissing,
        int PidReuse,
        int UnsupportedOwner,
        int OwnerNotInProcessList);

    private sealed record TunView(
        IReadOnlyList<TunCandidate> Candidates,
        bool? Enabled,
        string? AddressSummary,
        string? Gap,
        string? Note);

    private sealed record ProxyView(string Note, string? Gap);
}
