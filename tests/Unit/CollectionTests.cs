using NetScope.Application;
using NetScope.Application.Mock;
using NetScope.Domain;

namespace NetScope.Unit.Tests;

/// <summary>
/// Verifies that an unavailable data domain stays unavailable. M0's most dangerous bug class is
/// rendering "could not read" as "nothing is wrong", so these tests pin the distinction.
/// </summary>
public sealed class CollectionTests
{
    private static CollectionContext Context(bool synthetic = true) => new()
    {
        SessionId = "session",
        BootId = "boot",
        SnapshotId = "snapshot",
        ObservedAt = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8)),
        MonotonicMs = 0,
        Synthetic = synthetic,
    };

    [Fact]
    public void Ok_result_exposes_its_value()
    {
        var collected = Collected<string>.Ok("value");

        Assert.Equal(EventStatus.Ok, collected.Status);
        Assert.True(collected.HasValue);
        Assert.Equal("value", collected.Value);
        Assert.Null(collected.Reason);
    }

    [Theory]
    [InlineData(EventStatus.Timeout)]
    [InlineData(EventStatus.Failed)]
    [InlineData(EventStatus.AccessDenied)]
    [InlineData(EventStatus.Unsupported)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Unknown)]
    public void Unavailable_result_never_exposes_a_value(EventStatus status)
    {
        var collected = Collected<string>.Unavailable(status, "reason");

        Assert.Equal(status, collected.Status);
        Assert.False(collected.HasValue);
        Assert.Null(collected.Value);
        Assert.False(collected.Status.IsSuccess());
        Assert.True(collected.Status.IsObservationGap());
    }

    [Fact]
    public void Partial_result_keeps_data_but_is_neither_success_nor_gap()
    {
        var collected = Collected<string>.Partial("partial", "only some fields were readable");

        Assert.True(collected.HasValue);
        Assert.False(collected.Status.IsSuccess());
        Assert.False(collected.Status.IsObservationGap());
        Assert.True(collected.Status.ProducedData());
    }

    [Fact]
    public async Task Synthetic_snapshot_reports_every_domain_and_marks_unreadable_layers()
    {
        var collector = new SyntheticSnapshotCollector();

        var snapshot = await collector.CollectAsync(Context(), CancellationToken.None);
        var completeness = snapshot.Completeness();

        Assert.True(snapshot.Synthetic);
        Assert.Equal(EventStatus.Ok, completeness["interfaces"]);
        Assert.Equal(EventStatus.Ok, completeness["routes"]);
        Assert.Equal(EventStatus.Ok, completeness["dns"]);
        Assert.Equal(EventStatus.Partial, completeness["proxy"]);

        // A layer that is not read must say so instead of looking like an empty, healthy value.
        Assert.Equal(EventStatus.Unsupported, snapshot.Proxies.Value!.WinHttp.Status);
        Assert.False(snapshot.Proxies.Value!.WinHttp.HasValue);
        Assert.NotNull(snapshot.Proxies.Value!.WinHttp.Reason);
    }

    [Fact]
    public async Task Synthetic_snapshot_contains_a_tunnel_and_a_physical_candidate()
    {
        var collector = new SyntheticSnapshotCollector();

        var snapshot = await collector.CollectAsync(Context(), CancellationToken.None);
        var interfaces = snapshot.Interfaces.Value!;

        var tunnel = Assert.Single(interfaces, item => item.Kind == "tunnel");
        Assert.Equal(1, tunnel.Metric);
        Assert.False(tunnel.AutomaticMetric);

        var tunnelDefaultRoute = snapshot.Routes.Value!
            .Single(route => route.IfIndex == tunnel.IfIndex && route.DestinationPrefix == "0.0.0.0/0");
        Assert.Equal(0, tunnelDefaultRoute.RouteMetric);
        Assert.Equal(1, tunnelDefaultRoute.InterfaceMetric);
    }

    [Fact]
    public async Task Collection_respects_a_pre_cancelled_token()
    {
        var collector = new SyntheticSnapshotCollector();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(Context(), source.Token));
    }
}
