using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Cross-process clock rules: monotonic values only compare inside one domain + boot; freshness
/// uses UTC with a skew tolerance; a future authorization lease must be measured by the broker.
/// </summary>
public sealed class RepairClockTests
{
    [Fact]
    public void Monotonic_values_are_incomparable_across_process_domains_but_utc_order_is_shared()
    {
        var desktop = RepairFixtures.Fact("desktop", "fact:a", monotonicMs: 5000, clockDomainId: "desktop-test");
        var broker = RepairFixtures.Fact("broker", "fact:b", monotonicMs: 100, clockDomainId: "broker-test");

        Assert.Equal(RepairTimeRelation.Incomparable, RepairTimeOrdering.ByMonotonic(desktop, broker));
        Assert.Equal(RepairTimeRelation.Same, RepairTimeOrdering.ByUtc(desktop, broker));
    }

    [Fact]
    public void Monotonic_values_are_incomparable_across_boots_even_in_the_same_domain()
    {
        var thisBoot = RepairFixtures.Fact("a", "fact:a", monotonicMs: 100);
        var previousBoot = RepairFixtures.Fact("b", "fact:b", monotonicMs: 900, bootId: "boot-previous");

        Assert.Equal(RepairTimeRelation.Incomparable, RepairTimeOrdering.ByMonotonic(thisBoot, previousBoot));
    }

    [Fact]
    public void Freshness_is_utc_based_and_rejects_stale_or_future_facts()
    {
        var now = RepairFixtures.Now;
        var tolerance = TimeSpan.FromSeconds(5);

        var fresh = RepairFixtures.Fact("x", "fact:fresh", observedAt: now.AddSeconds(-5));
        Assert.Equal(RepairFactFreshness.Fresh, RepairFactFreshnessEvaluator.Assess(fresh, now, tolerance));

        var stale = RepairFixtures.Fact("x", "fact:stale", observedAt: now.AddSeconds(-60), ttl: TimeSpan.FromSeconds(30));
        Assert.Equal(RepairFactFreshness.Stale, RepairFactFreshnessEvaluator.Assess(stale, now, tolerance));

        var future = RepairFixtures.Fact("x", "fact:future", observedAt: now.AddSeconds(30));
        Assert.Equal(RepairFactFreshness.NotYetValid, RepairFactFreshnessEvaluator.Assess(future, now, tolerance));

        var withinSkew = RepairFixtures.Fact("x", "fact:skew", observedAt: now.AddSeconds(2));
        Assert.Equal(RepairFactFreshness.Fresh, RepairFactFreshnessEvaluator.Assess(withinSkew, now, tolerance));

        var unknown = RepairFixtures.Fact<string>(null, "fact:unknown", status: EventStatus.AccessDenied);
        Assert.Equal(RepairFactFreshness.Unknown, RepairFactFreshnessEvaluator.Assess(unknown, now, tolerance));
    }

    [Fact]
    public void A_broker_lease_uses_the_brokers_own_monotonic_clock()
    {
        var lease = new RepairMonotonicLease
        {
            LeaseId = "lease-t13",
            Clock = RepairFixtures.BrokerClock,
            StartedMs = 10_000,
            Duration = TimeSpan.FromSeconds(20),
        };

        Assert.Null(lease.RemainingMs(RepairFixtures.DesktopClock, 10_500));
        Assert.Equal(15_000, lease.RemainingMs(RepairFixtures.BrokerClock, 15_000));
        Assert.Equal(0, lease.RemainingMs(RepairFixtures.BrokerClock, 40_000));
    }
}
