// tests/Desktop/NetworkPathModelTests.cs
using NetScope.Maintenance;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class NetworkPathModelTests
{
    private static MaintenanceSnapshot Snap(int? workingPort, bool sysProxyEnabled, int sysPort,
        int egressIndex = 21, string egressAlias = "WLAN") => new(
        DateTimeOffset.Now, true, egressIndex, egressAlias, workingPort, Array.Empty<int>(),
        Array.Empty<ProxyLayerSnapshot>(), sysProxyEnabled, "127.0.0.1:" + sysPort, sysPort,
        false, 0, Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());

    [Fact]
    public void Dead_configured_port_is_fault()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(null, true, 7890), null);
        Assert.Equal(PathStationState.Fault, model.Proxy.State);
        Assert.Equal("7890", model.Proxy.ValueText);
        Assert.Equal("无监听", model.Proxy.Note);
        Assert.Equal("7890", model.PortReadout);
    }

    [Fact]
    public void Aligned_live_port_is_ok()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), null);
        Assert.Equal(PathStationState.Ok, model.Proxy.State);
        Assert.Equal("7897", model.Proxy.ValueText);
    }

    [Fact]
    public void No_proxy_configured_is_unknown()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(null, false, 0), null);
        Assert.Equal(PathStationState.Unknown, model.Proxy.State);
        Assert.Equal("—", model.PortReadout);
    }

    [Fact]
    public void Internet_unknown_without_postcheck()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), null);
        Assert.Equal(PathStationState.Unknown, model.Internet.State);
        Assert.Equal("—", model.LatencyReadout);
    }

    [Fact]
    public void Internet_reflects_postcheck_physical_internet()
    {
        var post = new PostCheckResult(true, "None", Array.Empty<string>(),
            new PostCheckObservation(21, true, true, true, true, true));
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), post);
        Assert.Equal(PathStationState.Ok, model.Internet.State);
    }

    [Fact]
    public void Missing_egress_is_unknown()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897, egressIndex: 0, egressAlias: ""), null);
        Assert.Equal(PathStationState.Unknown, model.Egress.State);
    }

    [Fact]
    public void Unmeasured_is_all_unknown()
    {
        var model = NetworkPathModel.Unmeasured;
        Assert.Equal(PathStationState.Unknown, model.Local.State);
        Assert.Equal(PathStationState.Unknown, model.Proxy.State);
        Assert.Equal(PathStationState.Unknown, model.Egress.State);
        Assert.Equal(PathStationState.Unknown, model.Internet.State);
        Assert.Equal("—", model.PortReadout);
    }

    [Fact]
    public void Internet_fault_when_physical_internet_unreachable()
    {
        var post = new PostCheckResult(true, "None", Array.Empty<string>(),
            new PostCheckObservation(21, false, true, true, true, true));
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), post);
        Assert.Equal(PathStationState.Fault, model.Internet.State);
        Assert.Equal("不可达", model.Internet.ValueText);
        Assert.Equal("实测", model.Internet.Note);
    }

    [Fact]
    public void Egress_ok_shows_alias_and_interface_note()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7897), null);
        Assert.Equal(PathStationState.Ok, model.Egress.State);
        Assert.Equal("WLAN", model.Egress.ValueText);
        Assert.Equal("if=21", model.Egress.Note);
        // Local is always Ok once a real snapshot exists (it is the machine itself).
        Assert.Equal(PathStationState.Ok, model.Local.State);
    }

    [Fact]
    public void Misaligned_but_live_port_is_fault()
    {
        // Safety-critical: a live listener exists (7897) but config points at 7890 — must show Fault,
        // never imply health just because some port is listening.
        var model = NetworkPathModel.FromSnapshot(Snap(7897, true, 7890), null);
        Assert.Equal(PathStationState.Fault, model.Proxy.State);
        Assert.Equal("7890", model.Proxy.ValueText);
        Assert.Equal("无监听", model.Proxy.Note);
        Assert.Equal("7890", model.PortReadout);
    }

    [Fact]
    public void Enabled_but_port_zero_is_unknown()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(null, true, 0), null);
        Assert.Equal(PathStationState.Unknown, model.Proxy.State);
        Assert.Equal("—", model.Proxy.ValueText);
        Assert.Equal("未配置代理", model.Proxy.Note);
        Assert.Equal("—", model.PortReadout);
    }

    [Fact]
    public void Proxy_off_but_listener_alive_is_ok()
    {
        var model = NetworkPathModel.FromSnapshot(Snap(7897, false, 0), null);
        Assert.Equal(PathStationState.Ok, model.Proxy.State);
        Assert.Equal("7897", model.Proxy.ValueText);
        Assert.Equal("监听中", model.Proxy.Note);
    }
}
