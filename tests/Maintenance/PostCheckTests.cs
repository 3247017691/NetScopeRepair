using NetScope.Maintenance;

namespace NetScope.Maintenance.Tests;

public sealed class PostCheckTests
{
    private static PostCheckObservation Obs(int route = 7, bool internet = true, bool dns = true,
        bool direct = true, bool proxy = true, bool proxyAlive = true)
        => new(route, internet, dns, direct, proxy, proxyAlive);

    [Fact]
    public void All_green_is_success_with_cause_none()
    {
        var result = PostCheckClassifier.Classify(Obs(), expectedRouteIndex: 7);
        Assert.True(result.Success);
        Assert.Equal("None", result.CauseClass);
    }

    [Fact]
    public void Route_not_via_tun_is_cause_ab()
    {
        var result = PostCheckClassifier.Classify(Obs(route: 21), expectedRouteIndex: 7);
        Assert.False(result.Success);
        Assert.Equal("A/B", result.CauseClass);
    }

    [Fact]
    public void No_physical_internet_is_cause_c()
    {
        var result = PostCheckClassifier.Classify(Obs(internet: false, direct: false, proxy: false), expectedRouteIndex: null);
        Assert.Equal("C", result.CauseClass);
    }

    [Fact]
    public void Route_ok_but_everything_dead_is_cause_d()
    {
        var result = PostCheckClassifier.Classify(Obs(direct: false, proxy: false), expectedRouteIndex: 7);
        Assert.Equal("D", result.CauseClass);
    }

    [Fact]
    public void Partial_connectivity_is_cause_e()
    {
        var result = PostCheckClassifier.Classify(Obs(proxy: false), expectedRouteIndex: 7);
        Assert.Equal("E", result.CauseClass);
    }

    [Fact]
    public void Dead_proxy_port_is_not_tested_and_not_a_failure()
    {
        var result = PostCheckClassifier.Classify(Obs(proxy: false, proxyAlive: false), expectedRouteIndex: null);
        Assert.True(result.Success);
    }
}
