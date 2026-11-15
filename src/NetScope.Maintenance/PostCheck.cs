namespace NetScope.Maintenance;

/// <summary>One post-repair measurement round. Every flag is measured, never assumed.</summary>
public sealed record PostCheckObservation(
    int BestRouteIndex, bool PhysicalInternet, bool DnsOk, bool DirectOk, bool ProxyOk, bool ProxyAlive);

/// <summary>Classified post-check outcome. CauseClass: None / A/B / C / D / E (desktop script semantics).</summary>
public sealed record PostCheckResult(
    bool Success, string CauseClass, IReadOnlyList<string> Details, PostCheckObservation Observation);

public interface IPostCheckProbe
{
    Task<PostCheckObservation> ObserveAsync(CancellationToken cancellationToken);
}

/// <summary>Pure classification; no I/O. Only "route still not via TUN" (A/B) may trigger auto-rollback.</summary>
public static class PostCheckClassifier
{
    public static PostCheckResult Classify(PostCheckObservation observation, int? expectedRouteIndex)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var routeOk = expectedRouteIndex is null || observation.BestRouteIndex == expectedRouteIndex;
        var proxyOk = !observation.ProxyAlive || observation.ProxyOk;
        var success = routeOk && observation.DnsOk && observation.DirectOk && proxyOk;
        var cause = success ? "None"
            : expectedRouteIndex is not null && !routeOk ? "A/B"
            : !observation.PhysicalInternet ? "C"
            : !observation.DirectOk && !observation.ProxyOk ? "D"
            : "E";
        var details = new List<string>
        {
            "默认路由 if=" + observation.BestRouteIndex
                + (expectedRouteIndex is null ? "" : "（期望 if=" + expectedRouteIndex + "）")
                + (routeOk ? " ✔" : " X"),
            "物理出口 Internet：" + (observation.PhysicalInternet ? "有" : "无"),
            "DNS 解析：" + (observation.DnsOk ? "通" : "失败"),
            "直连（不走系统/环境变量代理；TUN 开启时仍可能经 TUN）：" + (observation.DirectOk ? "通" : "失败"),
            observation.ProxyAlive ? "经代理：" + (observation.ProxyOk ? "通" : "失败") : "经代理：未测（无可用代理端口）",
        };
        return new PostCheckResult(success, cause, details, observation);
    }
}
