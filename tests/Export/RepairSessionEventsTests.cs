using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Export.Tests;

public sealed class RepairSessionEventsTests
{
    [Fact]
    public void Repair_events_become_valid_repair_event_envelopes()
    {
        var events = new[]
        {
            new RepairEvent("batch1", "action_failed", DateTimeOffset.Now, "clear-env:User:HTTP_PROXY",
                "ClearProxyEnv", "清除 User\\HTTP_PROXY", "UnauthorizedAccessException", false, null, null, null),
            new RepairEvent("batch1", "repair_applied", DateTimeOffset.Now, null, null, "应用 1 项",
                null, false, null, null, null),
        };

        var envelopes = RepairSessionEvents.Create(events, "session-x", "boot-x", synthetic: false, 123.0);

        Assert.Equal(2, envelopes.Count);
        Assert.All(envelopes, e =>
        {
            Assert.Equal(EventType.RepairEvent, e.EventType);
            Assert.Equal("batch1", e.Refs.RepairPlanId);
            Assert.Equal("maintenance.quick-repair", e.Source);
            _ = EnvelopeJson.Serialize(e); // 契约校验不抛
        });
        Assert.Equal(EventStatus.Failed, envelopes[0].Status);
        Assert.Equal(EventStatus.Ok, envelopes[1].Status);
    }

    [Fact]
    public void Elevation_skip_maps_to_access_denied()
    {
        var events = new[]
        {
            new RepairEvent("b", "action_failed", DateTimeOffset.Now, "fix-tun-priority:7", "FixTunPriority",
                "修复 TUN", "需要管理员权限：已跳过", true, null, null, null),
        };
        var envelope = Assert.Single(RepairSessionEvents.Create(events, "s", "b", false, 1.0));
        Assert.Equal(EventStatus.AccessDenied, envelope.Status);
    }

    [Fact]
    public void Full_payload_with_nested_postcheck_and_snapshots_stays_valid_snake_case()
    {
        var snapshot = new MaintenanceSnapshot(
            DateTimeOffset.Now, true, 21, "WLAN", 7890, new[] { 7890 },
            new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, true) },
            true, "127.0.0.1:7890", 7890, false, 0,
            Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(), Array.Empty<string>());
        var postCheck = new PostCheckResult(false, "C", new[] { "物理出口 Internet：无" },
            new PostCheckObservation(21, false, true, false, false, false));
        var events = new[]
        {
            new RepairEvent("batch-full", "repair_applied", DateTimeOffset.Now, null, null, "应用 1 项",
                null, false, postCheck, snapshot, snapshot),
        };

        var envelope = Assert.Single(RepairSessionEvents.Create(events, "s", "b", false, 1.0));

        _ = EnvelopeJson.Serialize(envelope); // 契约校验不抛
        Assert.True(envelope.Payload.ContainsKey("batch_id"));
        Assert.True(envelope.Payload.ContainsKey("elevation_skipped"));
        Assert.Equal("C", envelope.Payload["post_check"]!["cause_class"]!.GetValue<string>());
        Assert.Equal("WLAN", envelope.Payload["before"]!["active_egress_alias"]!.GetValue<string>());
        Assert.Equal("HTTP_PROXY", envelope.Payload["after"]!["proxy_layers"]![0]!["name"]!.GetValue<string>());
    }
}
