using NetScope.Maintenance;

namespace NetScope.Maintenance.Tests;

public sealed class RepairEventTests
{
    private sealed class FakeSink : IRepairEventSink
    {
        public List<RepairEvent> Events { get; } = new();
        public bool Throw { get; set; }
        public Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken ct)
        {
            if (Throw) throw new IOException("disk full");
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe(MaintenanceSnapshot snapshot) : IMaintenanceProbe
    {
        public Task<MaintenanceSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FakeExecutor : IMaintenanceExecutor
    {
        public Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken ct)
            => Task.FromResult(new ActionResult(action.Id, action.Title, true, action.Current, action.Desired, null));
        public Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken ct)
            => Task.FromResult((true, "ok"));
        public Task BroadcastEnvironmentAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakePostCheck(PostCheckObservation obs) : IPostCheckProbe
    {
        public Task<PostCheckObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(obs);
    }

    private static MaintenanceSnapshot EmptySnapshot() => new(
        DateTimeOffset.Now, true, 21, "WLAN", null, Array.Empty<int>(), Array.Empty<ProxyLayerSnapshot>(),
        false, "", 0, false, 0, Array.Empty<InterfaceSnapshot>(), Array.Empty<GhostDeviceSnapshot>(),
        Array.Empty<string>());

    private static QuickRepairService Service(FakeSink sink, MaintenanceSnapshot? snapshot = null,
        PostCheckObservation? obs = null)
        => new(new FakeProbe(snapshot ?? EmptySnapshot()), new FakeExecutor(),
            new MaintenanceJournalStore(Path.Combine(Path.GetTempPath(), "netscope-event-test-" + Guid.NewGuid().ToString("N") + ".json")),
            new FakePostCheck(obs ?? new PostCheckObservation(21, true, true, true, true, false)), sink);

    [Fact]
    public async Task Applied_batch_emits_repair_applied_with_before_and_after_snapshots()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, false, CancellationToken.None);

        var applied = Assert.Single(sink.Events, e => e.Kind == "repair_applied");
        Assert.NotNull(applied.Before);
        Assert.NotNull(applied.After);
        Assert.NotEmpty(applied.BatchId);
    }

    [Fact]
    public async Task Failed_postcheck_emits_postcheck_failed_with_cause()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot, new PostCheckObservation(21, false, true, false, false, false));
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, false, CancellationToken.None);

        var failed = Assert.Single(sink.Events, e => e.Kind == "postcheck_failed");
        Assert.Equal("C", failed.PostCheck!.CauseClass);
    }

    [Fact]
    public async Task Failed_postcheck_does_not_rewrite_the_applied_batch()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot, new PostCheckObservation(21, false, true, false, false, false));
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.Single(sink.Events, e => e.Kind == "repair_applied");
        Assert.Single(sink.Events, e => e.Kind == "postcheck_failed");
        Assert.True(report.Applied > 0);
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task Dry_run_emits_nothing()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        await service.ApplyAsync(plan, null, true, CancellationToken.None);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task User_unresolved_references_the_last_batch()
    {
        var sink = new FakeSink();
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);
        await service.ApplyAsync(plan, null, false, CancellationToken.None);

        var marked = await service.MarkUnresolvedAsync(CancellationToken.None);

        Assert.True(marked);
        var report = Assert.Single(sink.Events, e => e.Kind == "user_unresolved");
        Assert.Equal(sink.Events[0].BatchId, report.BatchId);
    }

    [Fact]
    public async Task Mark_unresolved_without_a_batch_returns_false()
        => Assert.False(await Service(new FakeSink()).MarkUnresolvedAsync(CancellationToken.None));

    [Fact]
    public async Task Sink_failure_does_not_fail_the_repair()
    {
        var sink = new FakeSink { Throw = true };
        var snapshot = EmptySnapshot() with
        {
            ProxyLayers = new[] { new ProxyLayerSnapshot("User", "HTTP_PROXY", "http://127.0.0.1:7890", 7890, false) },
        };
        var service = Service(sink, snapshot);
        var plan = await service.PlanAsync(false, CancellationToken.None);

        var report = await service.ApplyAsync(plan, null, false, CancellationToken.None);

        Assert.Equal(0, report.Failed);
        Assert.Contains(report.Messages, m => m.Contains("事件写入失败"));
    }
}