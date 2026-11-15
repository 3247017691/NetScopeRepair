using NetScope.Application;
using NetScope.Desktop.Services;
using NetScope.Desktop.ViewModels;
using NetScope.Domain;

namespace NetScope.Desktop.Tests;

/// <summary>
/// T07-R2 adjacent-observation comparison. The source shape (metric 0 → 4225, disable-default-routes
/// false → true, one interface appearing, another not observed) is reproduced with synthetic ids.
/// The assertions also pin that no operator or cause is inferred.
/// </summary>
public sealed class T07R2ChangeComparisonTests
{
    [Fact]
    public void Metric_and_flag_changes_keep_one_guid_identity_and_only_state_before_and_after()
    {
        var first = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface("guid-a", metric: 0, automaticMetric: false, disableDefaultRoutes: false) });
        var second = T07R2Data.Snapshot("snapshot-2", T07R2Data.BaseTime.AddSeconds(118.76), 119760,
            new[] { T07R2Data.Interface("guid-a", metric: 4225, automaticMetric: false, disableDefaultRoutes: true) });

        var report = ObservationChangeExplainer.Compare(new[] { first, second });

        Assert.Equal(2, report.TotalSnapshots);
        Assert.Equal(1, report.ComparedPairs);
        Assert.Equal(1, report.PairsWithChanges);
        var pair = Assert.Single(report.Pairs);
        var metric = Assert.Single(pair.Changes, change => change.FieldText == "接口度量 metric");
        Assert.Equal("GUID", metric.IdentityBasis);
        Assert.Contains("guid-a", metric.IdentityText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0", metric.Before);
        Assert.Equal("4225", metric.After);
        var flag = Assert.Single(pair.Changes, change => change.FieldText == "禁用默认路由 disable_default_routes");
        Assert.Equal("否", flag.Before);
        Assert.Equal("是", flag.After);

        var text = string.Join(" ", pair.Changes.Select(change => change.Note))
            + " " + string.Join(" ", pair.Notes);
        Assert.DoesNotContain("操作者", text);
        Assert.DoesNotContain("用户修改", text);
        Assert.DoesNotContain("因为", text);
        Assert.Contains("不推断暂停", string.Join(" ", pair.Notes));
    }

    [Fact]
    public void An_interface_missing_from_the_next_observation_is_not_reported_as_deleted()
    {
        var first = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000, new[]
        {
            T07R2Data.Interface("guid-a"),
            T07R2Data.Interface("guid-ppp", name: "PPP", family: "ipv6", ifIndex: 23, kind: "ppp"),
        });
        var second = T07R2Data.Snapshot("snapshot-2", T07R2Data.BaseTime.AddSeconds(93), 94000, new[]
        {
            T07R2Data.Interface("guid-a"),
            T07R2Data.Interface("guid-b", name: "新的虚拟网卡", ifIndex: 77),
        });

        var pair = Assert.Single(ObservationChangeExplainer.Compare(new[] { first, second }).Pairs);
        var rows = UiRows.ObservationPair(pair).Changes;

        var appeared = Assert.Single(rows, row => row.KindText == "出现");
        Assert.Contains("guid-b", appeared.IdentityText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("未观测到", appeared.BeforeText);
        Assert.Contains("此前未观测到", appeared.Note);

        var missing = Assert.Single(rows, row => row.KindText == "未观测到");
        Assert.Contains("guid-ppp", missing.IdentityText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("未观测到", missing.AfterText);
        Assert.Contains("不表示已删除或关闭", missing.Note);
    }

    [Fact]
    public void A_volatile_ifindex_change_does_not_create_a_second_interface()
    {
        var first = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface("guid-a", ifIndex: 44) });
        var second = T07R2Data.Snapshot("snapshot-2", T07R2Data.BaseTime.AddSeconds(10), 11000,
            new[] { T07R2Data.Interface("guid-a", ifIndex: 55) });

        var report = ObservationChangeExplainer.Compare(new[] { first, second });
        var pair = Assert.Single(report.Pairs);

        Assert.DoesNotContain(pair.Changes, change => change.Kind == ObservationChangeKind.Appeared);
        Assert.DoesNotContain(pair.Changes, change => change.Kind == ObservationChangeKind.NotObserved);
        var index = Assert.Single(pair.Changes, change => change.FieldText.StartsWith("ifIndex", StringComparison.Ordinal));
        Assert.Equal("44", index.Before);
        Assert.Equal("55", index.After);
        Assert.StartsWith("GUID", index.IdentityBasis, StringComparison.Ordinal);
    }

    [Fact]
    public void Luid_is_the_identity_when_no_guid_was_read()
    {
        var first = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface(string.Empty, metric: 0, luid: "00000000000000aa") });
        var second = T07R2Data.Snapshot("snapshot-2", T07R2Data.BaseTime.AddSeconds(10), 11000,
            new[] { T07R2Data.Interface(string.Empty, metric: 1500, luid: "00000000000000aa") });

        var pair = Assert.Single(ObservationChangeExplainer.Compare(new[] { first, second }).Pairs);
        var metric = Assert.Single(pair.Changes, change => change.FieldText == "接口度量 metric");
        Assert.Equal("LUID", metric.IdentityBasis);
        Assert.Contains("00000000000000aa", metric.IdentityText);
        Assert.Equal("0", metric.Before);
        Assert.Equal("1500", metric.After);
    }

    [Fact]
    public void A_family_flip_is_marked_incomparable_and_scalar_metrics_are_not_compared()
    {
        var first = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface("guid-a", family: "ipv4", ifIndex: 44, metric: 0) });
        var second = T07R2Data.Snapshot("snapshot-2", T07R2Data.BaseTime.AddSeconds(10), 11000,
            new[] { T07R2Data.Interface("guid-a", family: "ipv6", ifIndex: 46, metric: 4225) });

        var pair = Assert.Single(ObservationChangeExplainer.Compare(new[] { first, second }).Pairs);

        var family = Assert.Single(pair.Changes, change => change.FieldText == "地址族 family");
        Assert.Equal("ipv4", family.Before);
        Assert.Equal("ipv6", family.After);
        var incomparable = Assert.Single(pair.Changes, change => change.Kind == ObservationChangeKind.Incomparable);
        Assert.Contains("族相关标量", incomparable.FieldText);
        Assert.Contains("不能作为前后值比较", incomparable.Note);
        Assert.DoesNotContain(pair.Changes, change =>
            change.Kind == ObservationChangeKind.Changed && change.FieldText == "接口度量 metric");
    }

    [Fact]
    public void A_single_observation_is_explicitly_not_comparable()
    {
        var report = ObservationChangeExplainer.Compare(new[]
        {
            T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000, new[] { T07R2Data.Interface("guid-a") }),
        });

        Assert.Equal(1, report.TotalSnapshots);
        Assert.Equal(0, report.PairsWithChanges);
        Assert.Empty(report.Pairs);
        Assert.Contains("至少需要 2 次相邻观察", report.CannotCompareReason);
    }

    [Fact]
    public void Interfaces_that_were_not_read_are_not_turned_into_disappeared_interfaces()
    {
        var first = T07R2Data.Snapshot("snapshot-1", T07R2Data.BaseTime, 1000,
            new[] { T07R2Data.Interface("guid-a") });
        var second = T07R2Data.Snapshot("snapshot-2", T07R2Data.BaseTime.AddSeconds(10), 11000,
            interfacesStatus: EventStatus.AccessDenied, interfacesReason: "access_denied");

        var pair = Assert.Single(ObservationChangeExplainer.Compare(new[] { first, second }).Pairs);
        Assert.Contains(pair.Notes, note => note.Contains("没有可用数据", StringComparison.Ordinal));
        Assert.DoesNotContain(pair.Changes, change => change.Kind == ObservationChangeKind.NotObserved);
    }

    [Fact]
    public async Task Timeline_page_shows_adjacent_changes_from_a_saved_synthetic_session()
    {
        using var temp = new TempAppPaths();
        var clock = new TestClock();
        await using var session = new ObservationSession(new VaryingInterfaceCollector(), new StubProbeRunner(), clock,
            temp.Paths, sessionId: "session-t07r2-change", bootId: "boot-t07r2");
        await session.CollectOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        await session.CollectOnceAsync(CancellationToken.None);
        await session.StopAsync();

        var timeline = new TimelineViewModel();
        Assert.True(await timeline.LoadAsync(session.SessionPath));

        var pair = Assert.Single(timeline.ObservationPairs);
        Assert.Contains("2 次网络观察", timeline.ChangeSummaryText);
        Assert.Contains(pair.Changes, row => row.FieldText == "接口度量 metric"
            && row.BeforeText == "0" && row.AfterText == "4225");
        Assert.Contains(pair.Changes, row => row.FieldText == "禁用默认路由 disable_default_routes"
            && row.BeforeText == "否" && row.AfterText == "是");
        Assert.True(timeline.HasObservationPairs);
        Assert.All(pair.Changes, row => Assert.DoesNotContain("操作者", row.Note));
    }
}
