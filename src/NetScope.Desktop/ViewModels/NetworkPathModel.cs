// src/NetScope.Desktop/ViewModels/NetworkPathModel.cs
using NetScope.Maintenance;

namespace NetScope.Desktop.ViewModels;

public enum PathStationState { Ok, Fault, Unknown }

public sealed record PathStation(string Label, string ValueText, PathStationState State, string Note);

public sealed record NetworkPathModel(
    PathStation Local, PathStation Proxy, PathStation Egress, PathStation Internet,
    string PortReadout, string LatencyReadout)
{
    public static NetworkPathModel Unmeasured { get; } = new(
        new PathStation("本机", "本机", PathStationState.Unknown, ""),
        new PathStation("代理端口", "—", PathStationState.Unknown, "未测得"),
        new PathStation("出口", "—", PathStationState.Unknown, "未测得"),
        new PathStation("互联网", "—", PathStationState.Unknown, "未测得"),
        "—", "—");

    public static NetworkPathModel FromSnapshot(MaintenanceSnapshot snapshot, PostCheckResult? postCheck)
    {
        var local = new PathStation("本机", "本机", PathStationState.Ok, "");

        PathStation proxy;
        if (snapshot.WorkingProxyPort is int live && (!snapshot.SystemProxyEnabled || snapshot.SystemProxyPort == live))
            proxy = new PathStation("代理端口", live.ToString(), PathStationState.Ok, "监听中");
        else if (snapshot.SystemProxyEnabled && snapshot.SystemProxyPort > 0)
            proxy = new PathStation("代理端口", snapshot.SystemProxyPort.ToString(), PathStationState.Fault, "无监听");
        else
            proxy = new PathStation("代理端口", "—", PathStationState.Unknown, "未配置代理");

        var egress = snapshot.ActiveEgressIndex > 0 && !string.IsNullOrWhiteSpace(snapshot.ActiveEgressAlias)
            ? new PathStation("出口", snapshot.ActiveEgressAlias, PathStationState.Ok, "if=" + snapshot.ActiveEgressIndex)
            : new PathStation("出口", "—", PathStationState.Unknown, "未测得");

        PathStation internet;
        if (postCheck is null)
            internet = new PathStation("互联网", "—", PathStationState.Unknown, "未测得");
        else
            internet = postCheck.Observation.PhysicalInternet
                ? new PathStation("互联网", "可达", PathStationState.Ok, "实测")
                : new PathStation("互联网", "不可达", PathStationState.Fault, "实测");

        return new NetworkPathModel(local, proxy, egress, internet, proxy.ValueText, "—");
    }
}
