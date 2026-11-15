using System.Globalization;
using NetScope.Application;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;

namespace NetScope.Desktop.Services;

/// <summary>What kind of difference two adjacent observations show. Never implies a cause.</summary>
public enum ObservationChangeKind
{
    Changed,
    Appeared,
    NotObserved,
    Incomparable,
}

/// <summary>
/// One parameter difference between two adjacent observations. <see cref="Before"/> and
/// <see cref="After"/> are display texts; null means "that observation did not show the item",
/// which is different from a readable value such as "0".
/// </summary>
public sealed record ObservedChange(
    string Scope,
    string IdentityBasis,
    string IdentityText,
    string FieldText,
    ObservationChangeKind Kind,
    string? Before,
    string? After,
    string Note);

/// <summary>One adjacent pair of observations and the differences this app could actually read.</summary>
public sealed record AdjacentObservationChange(
    int PreviousIndex,
    int CurrentIndex,
    string PreviousSnapshotId,
    string CurrentSnapshotId,
    DateTimeOffset PreviousObservedAt,
    DateTimeOffset CurrentObservedAt,
    IReadOnlyList<ObservedChange> Changes,
    IReadOnlyList<string> Notes)
{
    public TimeSpan Interval => CurrentObservedAt - PreviousObservedAt;

    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Result of comparing every adjacent observation. Only the newest/most relevant pairs with
/// differences are kept for display; the counts still describe the whole session.
/// </summary>
public sealed record ObservationChangeReport(
    int TotalSnapshots,
    int ComparedPairs,
    int PairsWithChanges,
    IReadOnlyList<AdjacentObservationChange> Pairs,
    int OmittedChangedPairs,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt,
    string? CannotCompareReason)
{
    public bool CanCompare => TotalSnapshots >= 2 && CannotCompareReason is null;
}

/// <summary>
/// Pure local comparison of adjacent network snapshots. Interfaces are matched on their stable
/// identity (GUID, then LUID, then ifIndex+family as a locator only). The result only states what
/// the two observations contained: it never guesses who changed something, or why.
/// </summary>
public static class ObservationChangeExplainer
{
    public const int DefaultMaxPairs = 20;
    public const int DefaultMaxChangesPerPair = 24;
    private static readonly TimeSpan LongGap = TimeSpan.FromSeconds(60);

    public static ObservationChangeReport Compare(
        IReadOnlyList<NetworkSnapshot>? snapshots,
        int maxPairs = DefaultMaxPairs,
        int maxChangesPerPair = DefaultMaxChangesPerPair)
    {
        var items = snapshots ?? Array.Empty<NetworkSnapshot>();
        if (items.Count == 0)
        {
            return new ObservationChangeReport(0, 0, 0, Array.Empty<AdjacentObservationChange>(), 0, null, null,
                "当前会话没有网络快照，无法做相邻观察对比。");
        }

        if (items.Count == 1)
        {
            return new ObservationChangeReport(1, 0, 0, Array.Empty<AdjacentObservationChange>(), 0,
                items[0].ObservedAt, items[0].ObservedAt,
                "当前会话只有 1 次网络观察；至少需要 2 次相邻观察才能比较前后值。");
        }

        var kept = new List<AdjacentObservationChange>();
        var changed = 0;
        for (var index = 0; index < items.Count - 1; index++)
        {
            var pair = ComparePair(items[index], items[index + 1], index, index + 1, maxChangesPerPair);
            if (!pair.HasChanges) continue;
            changed++;
            if (kept.Count < Math.Max(0, maxPairs)) kept.Add(pair);
        }

        return new ObservationChangeReport(items.Count, items.Count - 1, changed, kept,
            Math.Max(0, changed - kept.Count), items[0].ObservedAt, items[^1].ObservedAt, null);
    }

    private static AdjacentObservationChange ComparePair(
        NetworkSnapshot previous, NetworkSnapshot current, int previousIndex, int currentIndex, int maxChanges)
    {
        var changes = new List<ObservedChange>();
        var notes = new List<string>();

        CompareDomains(previous, current, changes);
        CompareInterfaces(previous, current, changes, notes);
        CompareProxyLayers(previous, current, changes, notes);

        var interval = current.ObservedAt - previous.ObservedAt;
        if (interval <= TimeSpan.Zero)
        {
            notes.Add("两次观察的时间差为 " + Seconds(interval) + " 秒；时间不单调，只显示原始时间，"
                + "不推断暂停、卡顿或丢记录。");
        }
        else if (interval > LongGap)
        {
            notes.Add("两次观察间隔 " + Seconds(interval) + " 秒；这个间隔只由两次观测时间计算，"
                + "不推断暂停、卡顿或丢记录。");
        }

        if (previous.Synthetic || current.Synthetic)
            notes.Add("其中包含合成演示数据（synthetic），不是本机真实观测。");

        var omitted = changes.Count - Math.Max(0, maxChanges);
        if (omitted > 0)
        {
            changes = changes.Take(maxChanges).ToList();
            notes.Add("这对观察还有 " + omitted.ToString(CultureInfo.InvariantCulture) + " 项差异未展开。");
        }

        return new AdjacentObservationChange(previousIndex, currentIndex, previous.SnapshotId, current.SnapshotId,
            previous.ObservedAt, current.ObservedAt, changes, notes);
    }

    private static void CompareDomains(NetworkSnapshot previous, NetworkSnapshot current, List<ObservedChange> changes)
    {
        foreach (var domain in new[] { "interfaces", "routes", "dns", "proxy" })
        {
            var before = DescribeDomain(previous, domain);
            var after = DescribeDomain(current, domain);
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;
            changes.Add(new ObservedChange(
                "domain",
                "domain",
                "数据域 " + domain,
                "域状态",
                ObservationChangeKind.Changed,
                before,
                after,
                "数据域状态属于采集健康变化；它不表示网络路径本身发生了变化。"));
        }
    }

    private static string DescribeDomain(NetworkSnapshot snapshot, string domain)
    {
        var (status, reason) = domain switch
        {
            "interfaces" => (snapshot.Interfaces.Status, snapshot.Interfaces.Reason),
            "routes" => (snapshot.Routes.Status, snapshot.Routes.Reason),
            "dns" => (snapshot.Dns.Status, snapshot.Dns.Reason),
            _ => (snapshot.ProxyCompleteness(), snapshot.Proxies.Reason),
        };
        var text = UiText.StatusText(status);
        return string.IsNullOrWhiteSpace(reason) ? text : text + "（" + reason + "）";
    }

    private static void CompareInterfaces(NetworkSnapshot previous, NetworkSnapshot current,
        List<ObservedChange> changes, List<string> notes)
    {
        if (previous.Interfaces.Value is null || current.Interfaces.Value is null)
        {
            notes.Add("前一次或后一次观察的 interfaces 域没有可用数据（"
                + UiText.StatusText(previous.Interfaces.Status) + " → " + UiText.StatusText(current.Interfaces.Status)
                + "）；本轮不比较接口，也不把缺失当作消失。");
            return;
        }

        var before = Index(previous.Interfaces.Value);
        var after = Index(current.Interfaces.Value);
        var matchedAfter = new HashSet<string>(StringComparer.Ordinal);
        var matchedBefore = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            if (before.TryGetValue(key, out var previousItem) && after.TryGetValue(key, out var currentItem))
            {
                matchedBefore.Add(key);
                matchedAfter.Add(key);
                CompareInterface(previousItem, currentItem, previous.Interfaces, current.Interfaces, changes);
            }
        }

        // A field-level fallback only keeps an observation visible; it never upgrades the match
        // into a stable identity claim. The basis is displayed with every row.
        MatchByLuidOrIndex(before, after, matchedBefore, matchedAfter, changes, previous.Interfaces, current.Interfaces);
        MatchByLuidOrIndex(after, before, matchedAfter, matchedBefore, changes, current.Interfaces, previous.Interfaces,
            reverse: true);

        foreach (var entry in before.Values.Where(item => !matchedBefore.Contains(item.Key))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            changes.Add(new ObservedChange(
                "interface",
                entry.Basis,
                entry.IdentityText,
                "接口整体",
                ObservationChangeKind.NotObserved,
                DescribeInterface(entry.Item),
                null,
                "本次未观测到；不表示已删除或关闭。" + DomainGapSuffix(current.Interfaces, "本次")));
        }

        foreach (var entry in after.Values.Where(item => !matchedAfter.Contains(item.Key))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            changes.Add(new ObservedChange(
                "interface",
                entry.Basis,
                entry.IdentityText,
                "接口整体",
                ObservationChangeKind.Appeared,
                null,
                DescribeInterface(entry.Item),
                "此前未观测到；不表示新创建，也不表示配置发生了变化。" + DomainGapSuffix(previous.Interfaces, "上一次")));
        }
    }

    /// <summary>Fallback matching for one direction: LUID first, then family+ifIndex locator.</summary>
    private static void MatchByLuidOrIndex(
        Dictionary<string, IndexedInterface> source,
        Dictionary<string, IndexedInterface> target,
        HashSet<string> matchedSource,
        HashSet<string> matchedTarget,
        List<ObservedChange> changes,
        Collected<IReadOnlyList<InterfaceState>> sourceDomain,
        Collected<IReadOnlyList<InterfaceState>> targetDomain,
        bool reverse = false)
    {
        foreach (var entry in source.Values.Where(item => !matchedSource.Contains(item.Key))
                     .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            var luid = entry.Item.Luid;
            var candidate = target.Values.FirstOrDefault(other => !matchedTarget.Contains(other.Key)
                && !string.IsNullOrWhiteSpace(luid)
                && string.Equals(other.Item.Luid, luid, StringComparison.OrdinalIgnoreCase));
            var basis = "LUID";
            if (candidate is null)
            {
                var family = FamilyOf(entry.Item);
                candidate = target.Values.FirstOrDefault(other => !matchedTarget.Contains(other.Key)
                    && string.Equals(FamilyOf(other.Item), family, StringComparison.Ordinal)
                    && other.Item.IfIndex == entry.Item.IfIndex);
                basis = "ifIndex+family（仅定位线索）";
            }

            if (candidate is null) continue;
            matchedSource.Add(entry.Key);
            matchedTarget.Add(candidate.Key);
            if (!reverse)
            {
                CompareInterface(entry, candidate, sourceDomain, targetDomain, changes, basis);
            }
            else
            {
                CompareInterface(candidate, entry, targetDomain, sourceDomain, changes, basis);
            }
        }
    }

    private static void CompareInterface(IndexedInterface previous, IndexedInterface current,
        Collected<IReadOnlyList<InterfaceState>> previousDomain,
        Collected<IReadOnlyList<InterfaceState>> currentDomain,
        List<ObservedChange> changes, string? matchedBasis = null)
    {
        var basis = matchedBasis ?? previous.Basis;
        var identity = previous.IdentityText;
        if (!string.Equals(previous.Item.InterfaceGuid, current.Item.InterfaceGuid, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(previous.Item.InterfaceGuid))
        {
            identity = DescribeIdentity(current.Basis, current.Item);
        }

        var previousFamily = FamilyOf(previous.Item);
        var currentFamily = FamilyOf(current.Item);
        if (!string.Equals(previousFamily, currentFamily, StringComparison.Ordinal))
        {
            Add(identity, basis, "地址族 family", previousFamily, currentFamily,
                "同一稳定身份在两次观察中记录了不同地址族。");
            Add(identity, basis, "族相关标量（metric / automatic_metric / disable_default_routes / ifIndex）",
                ScalarText(previous.Item), ScalarText(current.Item),
                "这些标量属于不同地址族，不能作为前后值比较；本轮只标记差异，不做数值对比。",
                ObservationChangeKind.Incomparable);
        }
        else
        {
            Add(identity, basis, "ifIndex（当时定位线索）", Number(previous.Item.IfIndex), Number(current.Item.IfIndex),
                "ifIndex 只在当次观察中定位接口，不是稳定身份。");
            Add(identity, basis, "接口度量 metric", Number(previous.Item.Metric), Number(current.Item.Metric),
                "metric 未读取时显示“未读取”，不当作 0。");
            Add(identity, basis, "自动度量 automatic_metric",
                Truth(previous.Item.AutomaticMetric), Truth(current.Item.AutomaticMetric), "布尔值未读取时不当作否。");
            Add(identity, basis, "禁用默认路由 disable_default_routes",
                Truth(previous.Item.DisableDefaultRoutes), Truth(current.Item.DisableDefaultRoutes),
                "只陈述读取到的标志值，不推断路由意图。");
            Add(identity, basis, "LUID", Text(previous.Item.Luid), Text(current.Item.Luid),
                "LUID 由接口稳定身份推导；变化只表示本次读到的值不同。");
        }

        Add(identity, basis, "接口名称", Text(previous.Item.Name), Text(current.Item.Name),
            "名称变化只表示本次读到的文本不同。");
        Add(identity, basis, "接口类型", Text(previous.Item.Kind), Text(current.Item.Kind),
            "类型来自当次只读采集。");
        Add(identity, basis, "操作状态", Text(previous.Item.OperationalStatus), Text(current.Item.OperationalStatus),
            "操作状态来自当次只读采集。");
        Add(identity, basis, "地址列表", Addresses(previous.Item.Addresses), Addresses(current.Item.Addresses),
            "地址按列表显示；数量变化不等于配置被谁修改。");
        Add(identity, basis, "读取缺口", Reason(previous.Item.Reason), Reason(current.Item.Reason),
            "读取缺口描述本次采集的限制，不代表网络行为。");

        _ = previousDomain;
        _ = currentDomain;

        void Add(string identityText, string identityBasis, string field, string? before, string? after,
            string note, ObservationChangeKind kind = ObservationChangeKind.Changed)
        {
            if (string.Equals(before, after, StringComparison.Ordinal)) return;
            changes.Add(new ObservedChange("interface", identityBasis, identityText, field, kind, before, after, note));
        }
    }

    private static void CompareProxyLayers(NetworkSnapshot previous, NetworkSnapshot current,
        List<ObservedChange> changes, List<string> notes)
    {
        if (previous.Proxies.Value is not { } previousProxy || current.Proxies.Value is not { } currentProxy)
        {
            notes.Add("前一次或后一次观察的 proxy 域没有可用数据；本轮不比较代理层，也"
                + "不把缺失的层当作配置被删除。");
            return;
        }

        var before = Layers(previousProxy);
        var after = Layers(currentProxy);
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            var hasBefore = before.ContainsKey(name);
            var hasAfter = after.ContainsKey(name);
            var identity = "代理层 " + name;
            if (!hasBefore && hasAfter)
            {
                var onlyAfter = after[name];
                changes.Add(new ObservedChange("proxy", "layer", identity, "层整体", ObservationChangeKind.Appeared,
                    null, UiText.StatusText(onlyAfter.Status),
                    "此前未观测到该层；不表示新增了配置。"));
                continue;
            }

            if (hasBefore && !hasAfter)
            {
                var onlyBefore = before[name];
                changes.Add(new ObservedChange("proxy", "layer", identity, "层整体", ObservationChangeKind.NotObserved,
                    UiText.StatusText(onlyBefore.Status), null,
                    "本次未观测到该层；不表示配置被删除。"));
                continue;
            }

            var previousLayer = before[name];
            var currentLayer = after[name];
            if (previousLayer.Status != currentLayer.Status)
            {
                changes.Add(new ObservedChange("proxy", "layer", identity, "层读取状态", ObservationChangeKind.Changed,
                    UiText.StatusText(previousLayer.Status) + ReasonSuffix(previousLayer.Reason),
                    UiText.StatusText(currentLayer.Status) + ReasonSuffix(currentLayer.Reason),
                    "代理层读取状态属于采集健康变化；它不表示请求实际采用了哪一个代理。"));
            }

            if (!previousLayer.Status.ProducedData() || !currentLayer.Status.ProducedData()
                || previousLayer.Value is not { } previousValue || currentLayer.Value is not { } currentValue)
            {
                notes.Add(identity + " 在某一侧没有完整读取（" + UiText.StatusText(previousLayer.Status) + " → "
                    + UiText.StatusText(currentLayer.Status) + "）；本轮不比较它的值。");
                continue;
            }

            Add("启用 enabled", Truth(previousValue.Enabled), Truth(currentValue.Enabled));
            Add("代理服务器 proxy_server", Text(previousValue.ProxyServer), Text(currentValue.ProxyServer));
            Add("自动配置脚本 auto_config_url", Text(previousValue.AutoConfigUrl), Text(currentValue.AutoConfigUrl));
            Add("自动检测 auto_detect", Truth(previousValue.AutoDetect), Truth(currentValue.AutoDetect));
            Add("环境变量名", Variables(previousValue.Variables), Variables(currentValue.Variables));
            Add("来源 provenance", Text(previousValue.Provenance), Text(currentValue.Provenance));
            Add("读取说明 notice", Text(previousValue.Notice), Text(currentValue.Notice));

            void Add(string field, string? beforeText, string? afterText)
            {
                if (string.Equals(beforeText, afterText, StringComparison.Ordinal)) return;
                changes.Add(new ObservedChange("proxy", "layer", identity, field, ObservationChangeKind.Changed,
                    beforeText, afterText,
                    "只比较配置读取结果；不表示请求实际采用该代理，也不推断操作者。"));
            }
        }
    }

    private static Dictionary<string, IndexedInterface> Index(IReadOnlyList<InterfaceState> interfaces)
    {
        var result = new Dictionary<string, IndexedInterface>(StringComparer.Ordinal);
        foreach (var item in interfaces)
        {
            var (key, basis) = KeyOf(item);
            var uniqueKey = key;
            var suffix = 2;
            while (result.ContainsKey(uniqueKey))
            {
                uniqueKey = key + "#" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            result[uniqueKey] = new IndexedInterface(uniqueKey, basis, DescribeIdentity(basis, item), item);
        }

        return result;
    }

    private static (string Key, string Basis) KeyOf(InterfaceState item)
    {
        if (!string.IsNullOrWhiteSpace(item.InterfaceGuid))
            return ("guid:" + item.InterfaceGuid.Trim().ToLowerInvariant(), "GUID");
        if (!string.IsNullOrWhiteSpace(item.Luid))
            return ("luid:" + item.Luid.Trim().ToLowerInvariant(), "LUID");
        return ("ifindex:" + FamilyOf(item) + ":" + item.IfIndex.ToString(CultureInfo.InvariantCulture), "ifIndex+family");
    }

    private static string DescribeIdentity(string basis, InterfaceState item)
    {
        var value = basis switch
        {
            "GUID" => item.InterfaceGuid,
            "LUID" => item.Luid,
            _ => FamilyOf(item) + "/" + item.IfIndex.ToString(CultureInfo.InvariantCulture),
        };
        var name = string.IsNullOrWhiteSpace(item.Name) ? "未命名接口" : item.Name;
        return basis + " " + (value ?? "未读取") + " · " + name + " · " + FamilyOf(item);
    }

    private static string DescribeInterface(InterfaceState item) =>
        "名称 " + Text(item.Name)
        + " · 族 " + FamilyOf(item)
        + " · ifIndex " + Number(item.IfIndex)
        + " · 状态 " + Text(item.OperationalStatus)
        + " · metric " + Number(item.Metric)
        + " · automatic_metric " + Truth(item.AutomaticMetric)
        + " · disable_default_routes " + Truth(item.DisableDefaultRoutes)
        + " · 地址 " + Addresses(item.Addresses);

    private static string ScalarText(InterfaceState item) =>
        FamilyOf(item)
        + "：ifIndex=" + Number(item.IfIndex)
        + "，metric=" + Number(item.Metric)
        + "，automatic_metric=" + Truth(item.AutomaticMetric)
        + "，disable_default_routes=" + Truth(item.DisableDefaultRoutes);

    private static string FamilyOf(InterfaceState item) =>
        string.IsNullOrWhiteSpace(item.Family) ? "unknown" : item.Family.Trim().ToLowerInvariant();

    private static Dictionary<string, Collected<ProxyLayer>> Layers(ProxyState proxy)
    {
        var result = new Dictionary<string, Collected<ProxyLayer>>(StringComparer.Ordinal);
        foreach (var layer in new[]
                 {
                     proxy.ToolProcessEnvironment, proxy.UserEnvironment, proxy.MachineEnvironment,
                     proxy.WinInet, proxy.WinHttp,
                 })
        {
            if (layer.Value is not { } value) continue;
            var name = string.IsNullOrWhiteSpace(value.Layer) ? "未命名层" : value.Layer;
            result[name] = layer;
        }

        return result;
    }

    private static string DomainGapSuffix(Collected<IReadOnlyList<InterfaceState>> domain, string when) =>
        domain.Status == EventStatus.Ok
            ? string.Empty
            : "（" + when + " interfaces 域为 " + UiText.StatusText(domain.Status)
              + (string.IsNullOrWhiteSpace(domain.Reason) ? "" : "：" + domain.Reason)
              + "，缺失可能来自采集缺口）";

    private static string ReasonSuffix(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : "（" + reason + "）";

    private static string Seconds(TimeSpan interval) =>
        interval.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "未读取" : value;

    private static string Number(int? value) =>
        value is null ? "未读取" : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string Truth(bool? value) => value is null ? "未读取" : value.Value ? "是" : "否";

    private static string Reason(string? value) => string.IsNullOrWhiteSpace(value) ? "无读取缺口" : value;

    private static string Variables(IReadOnlyList<string>? variables)
    {
        if (variables is null) return "未读取";
        if (variables.Count == 0) return "无（0 个）";
        var shown = string.Join(", ", variables.Take(6));
        return variables.Count > 6
            ? shown + " …（共 " + variables.Count.ToString(CultureInfo.InvariantCulture) + " 个）"
            : shown + "（共 " + variables.Count.ToString(CultureInfo.InvariantCulture) + " 个）";
    }

    private static string Addresses(IReadOnlyList<string>? addresses)
    {
        if (addresses is null) return "未读取";
        if (addresses.Count == 0) return "无（0 个）";
        var shown = string.Join(", ", addresses.Take(4));
        return addresses.Count > 4
            ? shown + " …（共 " + addresses.Count.ToString(CultureInfo.InvariantCulture) + " 个）"
            : shown + "（共 " + addresses.Count.ToString(CultureInfo.InvariantCulture) + " 个）";
    }

    private sealed record IndexedInterface(string Key, string Basis, string IdentityText, InterfaceState Item);
}
