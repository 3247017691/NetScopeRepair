using NetScope.Application;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;

namespace NetScope.Desktop.Services;

/// <summary>
/// Whether one probe group may be compared across its three paths. Every field is an observation
/// about the recorded results; a missing value always makes the answer more conservative.
/// </summary>
public sealed record ProbeComparability(
    bool Comparable,
    bool ProducerConfirmed,
    bool SameRequestOptions,
    bool SameObservationWindow,
    bool SameBoot,
    bool AllThreePathsPresent,
    bool SameAddressFamily,
    bool FamilySpecified,
    bool StartTimesWithinWindow,
    bool ComparableStatuses,
    bool NoTopologyChange,
    IReadOnlyList<string> Unmet)
{
    public bool HasUnmet => Unmet.Count > 0;
}

/// <summary>What one path proved about forcing IPv4/IPv6, or why it proved nothing.</summary>
public enum FamilyCapabilityKind
{
    Enforced,
    UnsupportedGap,
    UnknownGap,
    InvalidRequest,
    ResolveFailed,
    NotRequested,
    NotObserved,
}

public sealed record FamilyCapability(ProbePath Path, string? RequestedFamily, FamilyCapabilityKind Kind, string? Code);

/// <summary>One probe group: its three path results and what may honestly be compared.</summary>
public sealed record ProbeGroupComparison(
    string ProbeGroupId,
    string TargetId,
    string? Method,
    string? AddressFamily,
    string? ObservationWindowId,
    string? BootId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<ProbeResult> Results,
    ProbeComparability Comparability,
    IReadOnlyList<FamilyCapability> FamilyCapabilities)
{
    public ProbeResult? For(ProbePath path) => Results.FirstOrDefault(result => result.Path == path);
}

/// <summary>An aggregated partial/unsupported/failed reason of the evidence a diagnosis cites.</summary>
public sealed record EvidenceGapReason(
    string Source,
    EventStatus Status,
    string Summary,
    string Detail,
    int Count);

/// <summary>
/// Pure local explanation of probe results. It never sends a request, never re-runs a rule and
/// never upgrades a result: an unproven comparability stays "not comparable".
/// </summary>
public static class ProbeComparisonExplainer
{
    public const double ComparabilityWindowMs = 2000;
    public const string FamilyGapThroughProxy = "address_family_not_enforced_through_proxy";
    public const string FamilyGapSystemDefault = "address_family_unknown_for_system_default";
    public const string FamilyGapUnsupportedValue = "unsupported_address_family";
    public const string FamilyGapNoAddress = "no_address_for_requested_family";

    /// <summary>Fixed product boundary shown next to every probe comparison.</summary>
    public const string ProbeBoundaryText =
        ".NET 本机探测只在受控目标上检查路径、阶段与地址族；它不是 DSH、Antigravity 或 Chrome 的实际业务结果，"
        + "也不读取这些应用的运行状态。没有用户实际复测时，不能据此声称业务已恢复。";

    private static readonly ProbePath[] PathOrder =
    {
        ProbePath.SystemDefault,
        ProbePath.NoExplicitProxy,
        ProbePath.ExplicitHttpProxy,
    };

    public static IReadOnlyList<ProbeGroupComparison> Compare(IReadOnlyList<ProbeResult>? probes)
    {
        var items = probes ?? Array.Empty<ProbeResult>();
        return items
            .Where(probe => probe is not null)
            .GroupBy(probe => probe.ProbeGroupId ?? string.Empty, StringComparer.Ordinal)
            .Select(group => Build(group.Key, group.ToArray()))
            .OrderBy(group => group.StartedAt)
            .ThenBy(group => group.ProbeGroupId, StringComparer.Ordinal)
            .ToArray();
    }

    public static ProbePath[] Paths => PathOrder;

    private static ProbeGroupComparison Build(string groupId, ProbeResult[] results)
    {
        var ordered = results.OrderBy(result => result.StartedAt).ToArray();
        var first = ordered[0];
        var started = ordered.Min(result => result.StartedAt);
        DateTimeOffset? ended = ordered.All(result => double.IsFinite(result.DurationMs) && result.DurationMs >= 0)
            ? ordered.Max(result => result.StartedAt.AddMilliseconds(result.DurationMs))
            : null;

        var sameRequestOptions = AllEqual(ordered, result => result.RequestOptionsKey);
        var sameWindow = AllEqual(ordered, result => result.ObservationWindowId);
        var sameBoot = AllEqual(ordered, result => result.BootId);
        var sameFamily = ordered.Select(result => result.AddressFamily?.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Count() == 1;
        var familySpecified = ordered.All(result => result.AddressFamily is "ipv4" or "ipv6");
        var allPaths = ordered.Length == PathOrder.Length
            && PathOrder.All(path => ordered.Count(result => result.Path == path) == 1);
        var monotonic = ordered.Select(result => result.StartedMonotonicMs).ToArray();
        var startSpread = monotonic.All(value => value is { } time && double.IsFinite(time) && time >= 0)
            ? monotonic.Max(value => value!.Value) - monotonic.Min(value => value!.Value)
            : double.NaN;
        var withinWindow = double.IsFinite(startSpread) && startSpread <= ComparabilityWindowMs;
        var comparableStatuses = ordered.All(result =>
            result.Status is EventStatus.Ok or EventStatus.Failed or EventStatus.Timeout);
        var noTopologyChange = ordered.All(result => !result.TopologyChanged);
        var producerConfirmed = ordered.All(result => result.TimingComparable);

        var unmet = new List<string>();
        if (!sameRequestOptions) unmet.Add("request_options_missing_or_different");
        if (!sameWindow) unmet.Add("observation_window_missing_or_different");
        if (!sameBoot) unmet.Add("boot_missing_or_different");
        if (!sameFamily) unmet.Add("address_family_missing_or_different");
        if (!familySpecified) unmet.Add("address_family_not_specified");
        if (!allPaths) unmet.Add("paths_missing");
        if (!withinWindow) unmet.Add("start_times_outside_window");
        if (!comparableStatuses) unmet.Add("incomparable_status");
        if (!noTopologyChange) unmet.Add("topology_changed");
        if (!producerConfirmed) unmet.Add("producer_not_confirmed");

        var comparable = unmet.Count == 0;
        var comparability = new ProbeComparability(comparable, producerConfirmed, sameRequestOptions, sameWindow,
            sameBoot, allPaths, sameFamily, familySpecified, withinWindow, comparableStatuses, noTopologyChange,
            unmet);

        var capabilities = ordered
            .OrderBy(result => Array.IndexOf(PathOrder, result.Path))
            .ThenBy(result => result.StartedAt)
            .Select(CapabilityOf)
            .ToArray();

        return new ProbeGroupComparison(groupId, first.TargetId, first.Method, first.AddressFamily,
            first.ObservationWindowId, first.BootId, started, ended, ordered, comparability, capabilities);
    }

    private static bool AllEqual(IReadOnlyList<ProbeResult> results, Func<ProbeResult, string?> selector)
    {
        var values = results.Select(selector).ToArray();
        return values.All(value => !string.IsNullOrWhiteSpace(value))
            && values.Distinct(StringComparer.Ordinal).Count() == 1;
    }

    private static FamilyCapability CapabilityOf(ProbeResult probe)
    {
        var code = FamilyCodeOf(probe);
        if (code is not null)
        {
            var kind = code switch
            {
                FamilyGapUnsupportedValue => FamilyCapabilityKind.InvalidRequest,
                FamilyGapNoAddress => FamilyCapabilityKind.ResolveFailed,
                FamilyGapSystemDefault => FamilyCapabilityKind.UnknownGap,
                _ => FamilyCapabilityKind.UnsupportedGap,
            };
            return new FamilyCapability(probe.Path, probe.AddressFamily, kind, code);
        }

        if (string.IsNullOrWhiteSpace(probe.AddressFamily))
        {
            return new FamilyCapability(probe.Path, null, FamilyCapabilityKind.NotRequested, null);
        }

        // Only the path that dials the target itself can prove which family really connected.
        if (probe.Path == ProbePath.NoExplicitProxy
            && probe.AddressFamily is "ipv4" or "ipv6"
            && probe.Status is EventStatus.Ok or EventStatus.Failed or EventStatus.Timeout)
        {
            return new FamilyCapability(probe.Path, probe.AddressFamily, FamilyCapabilityKind.Enforced, null);
        }

        return new FamilyCapability(probe.Path, probe.AddressFamily, FamilyCapabilityKind.NotObserved, null);
    }

    /// <summary>The transport publishes a family gap as a reason code; it is never guessed from the path.</summary>
    public static string? FamilyCodeOf(ProbeResult probe)
    {
        var text = (probe.TransportError ?? string.Empty) + " " + (probe.Notice ?? string.Empty);
        foreach (var code in new[]
                 {
                     FamilyGapThroughProxy, FamilyGapSystemDefault, FamilyGapUnsupportedValue, FamilyGapNoAddress,
                 })
        {
            if (text.Contains(code, StringComparison.Ordinal)) return code;
        }

        return null;
    }

    /// <summary>
    /// Reasons why the evidence a diagnosis cites is only partial/unsupported/failed. The reason
    /// set is scoped to the cited ids so an unrelated cycle cannot make the detail look worse.
    /// Identical reasons are aggregated by count, never removed.
    /// </summary>
    public static IReadOnlyList<EvidenceGapReason> AggregateReasons(
        EvidenceSet? evidence, IReadOnlyCollection<string>? referencedIds)
    {
        if (evidence is null || referencedIds is null || referencedIds.Count == 0)
            return Array.Empty<EvidenceGapReason>();

        var ids = referencedIds as ISet<string> ?? new HashSet<string>(referencedIds, StringComparer.Ordinal);
        var rows = new List<EvidenceGapReason>();

        foreach (var probe in evidence.Probes.Where(probe => ids.Contains(probe.ProbeId) && probe.Status != EventStatus.Ok))
        {
            var summary = UiText.ClassificationText(probe.Classification)
                + (probe.HttpStatus is { } status ? " · HTTP " + status : string.Empty)
                + (string.IsNullOrWhiteSpace(probe.TransportError) ? string.Empty : " · " + probe.TransportError);
            rows.Add(new EvidenceGapReason(
                "探测 · " + UiText.PathText(probe.Path),
                probe.Status,
                summary,
                "阶段 " + UiText.StageText(probe.FailedStage)
                + " · 失败端点 " + UiText.EndpointText(probe.FailedEndpoint)
                + " · 地址族 " + (probe.AddressFamily ?? "未指定")
                + " · 解析位置 " + (probe.ResolutionLocation ?? "unknown")
                + (probe.Notice is { Length: > 0 } notice ? " · 提示 " + UiText.Shorten(notice, 220) : string.Empty),
                1));
        }

        if (evidence.Snapshot is { } snapshot && ids.Contains(snapshot.SnapshotId))
        {
            foreach (var domain in snapshot.Completeness())
            {
                if (domain.Value == EventStatus.Ok) continue;
                var reason = ReasonOf(snapshot, domain.Key);
                rows.Add(new EvidenceGapReason(
                    "数据域 · " + domain.Key,
                    domain.Value,
                    "该数据域不是完整读取",
                    string.IsNullOrWhiteSpace(reason) ? "没有给出原因；不能当作正常。" : reason,
                    1));
            }
        }

        foreach (var record in evidence.Records.Where(record => ids.Contains(record.EvidenceId) && record.Status != EventStatus.Ok))
        {
            rows.Add(new EvidenceGapReason(
                "证据记录 · " + record.Kind,
                record.Status,
                UiText.Shorten(record.Summary, 160),
                "来源 " + (record.Source ?? "未知") + " · 版本 " + record.TransformationVersion,
                1));
        }

        return rows
            .GroupBy(row => (row.Source, row.Status, row.Summary, row.Detail))
            .Select(group => group.First() with { Count = group.Count() })
            .OrderBy(row => row.Source, StringComparer.Ordinal)
            .ThenBy(row => row.Status)
            .ThenBy(row => row.Summary, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReasonOf(NetworkSnapshot snapshot, string domain) => domain switch
    {
        "interfaces" => snapshot.Interfaces.Reason,
        "routes" => snapshot.Routes.Reason,
        "dns" => snapshot.Dns.Reason,
        "proxy" => ProxyReason(snapshot.Proxies),
        _ => null,
    };

    private static string? ProxyReason(Collected<ProxyState> domain)
    {
        if (domain.Status != EventStatus.Ok) return domain.Reason;
        if (domain.Value is not { } proxy) return domain.Reason;
        var layers = new[]
        {
            ("进程环境", proxy.ToolProcessEnvironment), ("用户环境", proxy.UserEnvironment),
            ("机器环境", proxy.MachineEnvironment), ("WinINET", proxy.WinInet), ("WinHTTP", proxy.WinHttp),
        };
        var incomplete = layers
            .Where(layer => layer.Item2.Status != EventStatus.Ok)
            .Select(layer => layer.Item1 + " " + UiText.StatusText(layer.Item2.Status)
                + (string.IsNullOrWhiteSpace(layer.Item2.Reason) ? string.Empty : "：" + layer.Item2.Reason))
            .ToArray();
        return incomplete.Length == 0 ? null : string.Join("；", incomplete);
    }
}
