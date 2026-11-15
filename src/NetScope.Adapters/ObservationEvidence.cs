using System.Globalization;
using System.Net;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Adapters;

/// <summary>One read of every adapter source plus the derived client rows, kept together.</summary>
public sealed record AdapterObservationRead(
    DateTimeOffset ReadAt,
    Collected<IReadOnlyList<ProcessObservation>> Processes,
    Collected<IReadOnlyList<ListenerObservation>> Listeners,
    Collected<IReadOnlyList<NetworkAdapterObservation>> NetworkAdapters,
    Collected<IReadOnlyList<ProxyLayerObservation>> ProxyLayers,
    IReadOnlyList<ClientStatus> Clients);

/// <summary>
/// Projects the read-only adapter facts of one cycle into minimized <see cref="EvidenceRecord"/>s.
/// Only facts a diagnostic rule actually reads are mapped (see the rule-to-record matrix in
/// docs/reports/T06-R3-report.md); no probe result, bind failure, runtime configuration or
/// fake-IP assertion is invented here.
/// </summary>
public static class ObservationEvidenceBuilder
{
    public const string Source = "adapter.observation";

    public const string Provenance = "adapter_read";

    public const string BuilderVersion = "observation-evidence/1.0.0";

    /// <summary>Record kinds this builder can emit today; the unsupported kinds stay absent.</summary>
    public static IReadOnlyList<string> RecordKinds { get; } = new[]
    {
        "client_process",
        "client_config_file",
        "listening_ports",
        "port_ownership",
    };

    public static ObservationEvidenceResult Build(AdapterObservationRead read, ObservationEvidenceContext context)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(context);

        var records = new List<EvidenceRecord>();
        var gaps = new List<ObservationEvidenceGap>();
        var ids = new IdSequence(context.SnapshotId);

        AddProcessGaps(read, gaps);
        AddProcesses(read, context, ids, records);
        AddConfigurations(read, context, ids, records, gaps);
        AddListeningSamples(read, context, ids, records);
        AddPortOwnership(read, context, ids, records);

        return new ObservationEvidenceResult
        {
            Records = records,
            Gaps = gaps,
        };
    }

    private static void AddProcessGaps(AdapterObservationRead read, List<ObservationEvidenceGap> gaps)
    {
        AddGap("processes", read.Processes, "进程列表不可读", gaps);
        AddGap("listeners", read.Listeners, "监听端点列表不可读", gaps);
    }

    private static void AddGap<T>(
        string source,
        Collected<IReadOnlyList<T>> read,
        string fallbackReason,
        List<ObservationEvidenceGap> gaps)
        where T : class
    {
        if (read.HasValue && read.Status == EventStatus.Ok)
        {
            return;
        }

        gaps.Add(new ObservationEvidenceGap
        {
            Source = source,
            Status = read.Status,
            Reason = string.IsNullOrWhiteSpace(read.Reason) ? fallbackReason : read.Reason!,
        });
    }

    private static void AddProcesses(
        AdapterObservationRead read,
        ObservationEvidenceContext context,
        IdSequence ids,
        List<EvidenceRecord> records)
    {
        foreach (var client in read.Clients)
        {
            foreach (var process in client.Processes)
            {
                var facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["client"] = client.ClientId,
                    ["role"] = process.Role,
                    ["pid"] = process.ProcessId.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrWhiteSpace(process.Name))
                {
                    facts["name"] = process.Name!;
                }

                if (process.StartTime is { } startedAt)
                {
                    facts["started_at"] = startedAt.ToString("O", CultureInfo.InvariantCulture);
                }
                else
                {
                    facts["start_time_state"] = FieldGap(process, ObservationFieldNames.StartTime) ?? "unreadable";
                }

                if (process.ParentProcessId is { } parent)
                {
                    facts["parent_pid"] = parent.ToString(CultureInfo.InvariantCulture);
                }

                if (!string.IsNullOrWhiteSpace(process.Association))
                {
                    facts["association"] = process.Association!;
                }

                if (process.FieldGaps.Count > 0)
                {
                    facts["field_gaps"] = string.Join(";", process.FieldGaps);
                }

                facts["snapshot_id"] = context.SnapshotId;
                var status = process.FieldGaps.Count == 0 ? EventStatus.Ok : EventStatus.Partial;
                var startText = process.StartTime is { } started
                    ? "启动 " + started.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : "启动时间不可读（" + facts["start_time_state"] + "）";
                records.Add(new EvidenceRecord
                {
                    EvidenceId = ids.Next("client_process"),
                    Kind = "client_process",
                    Summary = $"进程 {NameOrPid(process.Name, process.ProcessId)}（PID {process.ProcessId}，角色 {process.Role}，客户端 {client.ClientId}）{startText}。",
                    Facts = facts,
                    ObservedAt = context.ObservedAt,
                    MonotonicMs = context.MonotonicMs,
                    BootId = context.BootId,
                    ObservationWindowId = context.ObservationWindowId,
                    Source = Source,
                    Provenance = Provenance,
                    Status = status,
                    TransformationVersion = "1",
                });
            }
        }
    }

    private static void AddConfigurations(
        AdapterObservationRead read,
        ObservationEvidenceContext context,
        IdSequence ids,
        List<EvidenceRecord> records,
        List<ObservationEvidenceGap> gaps)
    {
        foreach (var client in read.Clients)
        {
            foreach (var evidence in client.Evidence)
            {
                if (evidence.Provenance == ConfigProvenance.RuntimeApi)
                {
                    // A runtime API result is not a disk file and this task has no runtime
                    // producer in the default composition. Never downgrade it to disk evidence.
                    gaps.Add(new ObservationEvidenceGap
                    {
                        Source = "client_runtime_config",
                        Status = EventStatus.Unsupported,
                        Reason = $"{client.ClientId}: 运行态配置 API 未接入，本轮只投影磁盘白名单证据；磁盘内容不冒充运行态。",
                    });
                    continue;
                }

                if (evidence.Provenance is not (ConfigProvenance.DiskOnly or ConfigProvenance.ProcessStartReference))
                {
                    gaps.Add(new ObservationEvidenceGap
                    {
                        Source = "client_config_file",
                        Status = EventStatus.Unsupported,
                        Reason = $"{client.ClientId}: 配置证据来源 {evidence.Provenance} 不是磁盘文件，本轮不投影。",
                    });
                    continue;
                }

                var facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["client"] = evidence.ClientId,
                    ["parser_version"] = evidence.ParserVersion,
                    ["provenance"] = evidence.Provenance == ConfigProvenance.DiskOnly ? "disk_only" : "process_start_reference",
                };
                if (!string.IsNullOrWhiteSpace(evidence.SourcePath))
                {
                    facts["config_path"] = evidence.SourcePath!;
                }

                if (!string.IsNullOrWhiteSpace(evidence.ContentHash))
                {
                    facts["config_hash"] = evidence.ContentHash!;
                }

                if (evidence.FileModifiedAt is { } modifiedAt)
                {
                    facts["mtime"] = modifiedAt.ToString("O", CultureInfo.InvariantCulture);
                }

                if (evidence.ReadAt is { } readAt)
                {
                    facts["read_at"] = readAt.ToString("O", CultureInfo.InvariantCulture);
                }

                facts["valid"] = evidence.Valid switch
                {
                    true => "true",
                    false => "false",
                    _ => "unknown",
                };
                if (!string.IsNullOrWhiteSpace(evidence.ValidityReason))
                {
                    facts["validity_reason"] = evidence.ValidityReason!;
                }

                if (evidence.Gaps.Count > 0)
                {
                    facts["gaps"] = string.Join("；", evidence.Gaps.Select(SingleLine));
                }

                // Only the documented whitelist values the parser already minimized. The kind
                // stays client_config_file: a disk file is never labelled as the running config.
                foreach (var (key, value) in evidence.Facts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    facts[key] = SingleLine(value);
                }

                facts["snapshot_id"] = context.SnapshotId;
                var status = evidence.Valid switch
                {
                    true => evidence.Gaps.Count == 0 ? EventStatus.Ok : EventStatus.Partial,
                    false => evidence.ContentHash is null ? EventStatus.Unknown : EventStatus.Failed,
                    _ => EventStatus.Unknown,
                };
                records.Add(new EvidenceRecord
                {
                    EvidenceId = ids.Next("client_config_file"),
                    Kind = "client_config_file",
                    Summary = $"磁盘配置白名单（{client.DisplayName}）：{FileNameOf(evidence.SourcePath)}，"
                        + (evidence.Valid == true ? (evidence.Gaps.Count == 0 ? "解析有效。" : "解析有效但有关键缺口。") : "不可用。")
                        + "只代表文件内容，不代表客户端已加载。",
                    Facts = facts,
                    ObservedAt = context.ObservedAt,
                    MonotonicMs = context.MonotonicMs,
                    BootId = context.BootId,
                    ObservationWindowId = context.ObservationWindowId,
                    Source = Source,
                    Provenance = facts["provenance"],
                    Status = status,
                    TransformationVersion = "1",
                });
            }
        }
    }

    private static void AddListeningSamples(
        AdapterObservationRead read,
        ObservationEvidenceContext context,
        IdSequence ids,
        List<EvidenceRecord> records)
    {
        foreach (var family in new[] { "ipv4", "ipv6" })
        {
            var count = read.Listeners.Value?
                .Count(listener => IsUsable(listener) && FamilyOf(listener) == family);
            var (status, sampleState) = DescribeSample(read.Listeners, count);
            var facts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["snapshot_id"] = context.SnapshotId,
                ["address_family"] = family,
                ["sample_state"] = sampleState,
            };
            if (count is { } value)
            {
                facts["count"] = value.ToString(CultureInfo.InvariantCulture);
            }

            records.Add(new EvidenceRecord
            {
                EvidenceId = ids.Next("listening_ports"),
                Kind = "listening_ports",
                Summary = $"监听端点采样（{family}）："
                    + (count is { } total ? $"样本 {total} 个，" : "")
                    + $"读取状态 {sampleState}。列表样本不足或读取受限时，不能据此报告端口已关闭。",
                Facts = facts,
                ObservedAt = context.ObservedAt,
                MonotonicMs = context.MonotonicMs,
                BootId = context.BootId,
                ObservationWindowId = context.ObservationWindowId,
                Source = Source,
                Provenance = Provenance,
                Status = status,
                AddressFamily = family,
                ProxyEndpointId = context.ProxyEndpointId,
                TargetId = context.TargetId,
                TransformationVersion = "1",
            });
        }
    }

    private static void AddPortOwnership(
        AdapterObservationRead read,
        ObservationEvidenceContext context,
        IdSequence ids,
        List<EvidenceRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listener in read.Listeners.Value ?? Array.Empty<ListenerObservation>())
        {
            if (!IsUsable(listener) || listener.OwnerProcessId is not { } ownerPid)
            {
                continue;
            }

            var endpoint = EndpointText(listener);
            if (endpoint is null || !seen.Add(endpoint + "|" + ownerPid.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }

            var facts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["snapshot_id"] = context.SnapshotId,
                ["endpoint"] = endpoint,
                ["owner_pid"] = ownerPid.ToString(CultureInfo.InvariantCulture),
                ["owner_process"] = string.IsNullOrWhiteSpace(listener.OwnerProcessName)
                    ? "pid:" + ownerPid.ToString(CultureInfo.InvariantCulture)
                    : listener.OwnerProcessName!,
                ["address_family"] = FamilyOf(listener),
            };
            if (!string.IsNullOrWhiteSpace(listener.OwnerReadReason))
            {
                facts["owner_read_reason"] = listener.OwnerReadReason!;
            }

            if (listener.OwnerInstanceStartTime is { } instanceStart)
            {
                facts["owner_instance_started_at"] = instanceStart.ToString("O", CultureInfo.InvariantCulture);
            }

            records.Add(new EvidenceRecord
            {
                EvidenceId = ids.Next("port_ownership"),
                Kind = "port_ownership",
                Summary = $"监听端点 {endpoint} 的所有者读为 PID {ownerPid}"
                    + (string.IsNullOrWhiteSpace(listener.OwnerProcessName) ? "（进程名未读取）" : "（" + listener.OwnerProcessName + "）")
                    + "；只证明采样时段的所有者，不代表端口冲突。",
                Facts = facts,
                ObservedAt = context.ObservedAt,
                MonotonicMs = context.MonotonicMs,
                BootId = context.BootId,
                ObservationWindowId = context.ObservationWindowId,
                Source = Source,
                Provenance = Provenance,
                Status = EventStatus.Ok,
                AddressFamily = facts["address_family"],
                TransformationVersion = "1",
            });
        }
    }

    private static (EventStatus Status, string SampleState) DescribeSample(
        Collected<IReadOnlyList<ListenerObservation>> listeners,
        int? count)
    {
        if (!listeners.HasValue)
        {
            return (listeners.Status, listeners.Status switch
            {
                EventStatus.Partial => "partial",
                EventStatus.AccessDenied => "access_denied",
                EventStatus.Timeout => "timeout",
                EventStatus.Unsupported => "unsupported",
                EventStatus.Cancelled => "cancelled",
                EventStatus.Failed => "failed",
                _ => "unknown",
            });
        }

        if (listeners.Status == EventStatus.Partial)
        {
            return (EventStatus.Partial, "partial");
        }

        return listeners.Status == EventStatus.Ok
            ? (EventStatus.Ok, count == 0 ? "empty" : "observed")
            : (listeners.Status, "partial");
    }

    private static bool IsUsable(ListenerObservation listener) =>
        listener.Port is >= 1 and <= 65535;

    private static string? EndpointText(ListenerObservation listener)
    {
        if (string.IsNullOrWhiteSpace(listener.Host))
        {
            return null;
        }

        var host = listener.Host!.Trim();
        var text = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? "[" + host + "]:" + listener.Port.ToString(CultureInfo.InvariantCulture)
            : host + ":" + listener.Port.ToString(CultureInfo.InvariantCulture);
        return IPEndPoint.TryParse(text, out var parsed) && parsed.Port is >= 1 and <= 65535
            ? text
            : null;
    }

    private static string FamilyOf(ListenerObservation listener) =>
        listener.AddressFamily is "ipv4" or "ipv6" ? listener.AddressFamily : "unknown";

    private static string? FieldGap(ClientProcess process, string field) =>
        process.FieldGaps.FirstOrDefault(gap =>
            gap.StartsWith(field + "=", StringComparison.Ordinal))?[(field.Length + 1)..];
    private static string NameOrPid(string? name, int processId) =>
        string.IsNullOrWhiteSpace(name) ? "PID " + processId.ToString(CultureInfo.InvariantCulture) : name!;

    private static string FileNameOf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "未提供本地路径" : Path.GetFileName(path);

    private static string SingleLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private sealed class IdSequence(string snapshotId)
    {
        private int _next;

        public string Next(string kind) =>
            $"record-{snapshotId}-{_next++.ToString("000", CultureInfo.InvariantCulture)}-{kind}";
    }
}

/// <summary>
/// Reads the adapter once per observation cycle and projects the result with
/// <see cref="ObservationEvidenceBuilder"/>. It performs no probe, network request or system write.
/// </summary>
public sealed class AdapterObservationEvidenceSource : IObservationEvidenceSource
{
    private readonly GenericAdapter _adapter;

    public AdapterObservationEvidenceSource(GenericAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async Task<ObservationEvidenceResult> CollectAsync(
        ObservationEvidenceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var read = await _adapter.ReadObservationAsync(cancellationToken).ConfigureAwait(false);
        return ObservationEvidenceBuilder.Build(read, context);
    }
}
