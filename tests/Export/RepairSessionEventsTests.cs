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
}