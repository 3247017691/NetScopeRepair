using NetScope.Storage;

namespace NetScope.Unit.Tests;

/// <summary>T11 contract of the documented retention defaults and 80/90/100 levels.</summary>
public sealed class T11RetentionPolicyTests
{
    [Fact]
    public void Defaults_match_design_04()
    {
        var policy = RetentionPolicy.Default;

        Assert.Equal(TimeSpan.FromDays(7), policy.NormalRetention);
        Assert.Equal(500L * 1024 * 1024, policy.NormalMaxBytes);
        Assert.Equal(1024L * 1024 * 1024, policy.PinnedMaxBytes);
        Assert.Equal(100L * 1024 * 1024, policy.DailyBudgetBytes);
        Assert.Equal(80, policy.WarningPercent);
        Assert.Equal(90, policy.ReducedDetailPercent);
        Assert.Equal(100, policy.StopOptionalDetailPercent);
        Assert.False(policy.IncludeExportsInNormalQuota);
        policy.Validate();
    }

    [Fact]
    public void Levels_switch_at_80_90_and_100_percent()
    {
        var policy = RetentionPolicy.Default;
        var limit = policy.NormalMaxBytes;

        Assert.Equal(QuotaLevel.Normal, policy.LevelFor(0, limit));
        Assert.Equal(QuotaLevel.Normal, policy.LevelFor(limit * 79 / 100, limit));
        Assert.Equal(QuotaLevel.Warning, policy.LevelFor(limit * 80 / 100, limit));
        Assert.Equal(QuotaLevel.ReducedDetail, policy.LevelFor(limit * 90 / 100, limit));
        Assert.Equal(QuotaLevel.StopOptionalDetail, policy.LevelFor(limit, limit));

        Assert.True(policy.DirectiveFor(QuotaLevel.Normal).AllowRawSnapshotDetail);
        Assert.True(policy.DirectiveFor(QuotaLevel.Warning).AllowRawSnapshotDetail);
        Assert.False(policy.DirectiveFor(QuotaLevel.ReducedDetail).AllowRawSnapshotDetail);
        Assert.True(policy.DirectiveFor(QuotaLevel.ReducedDetail).AllowOptionalDetail);
        Assert.False(policy.DirectiveFor(QuotaLevel.StopOptionalDetail).AllowOptionalDetail);
        Assert.True(policy.DirectiveFor(QuotaLevel.StopOptionalDetail).KeepMinimalCountsOnly);
        Assert.Equal(2, policy.DirectiveFor(QuotaLevel.ReducedDetail).SamplingIntervalMultiplier);
    }

    [Fact]
    public void Pinned_pool_is_evaluated_separately_from_the_normal_pool()
    {
        var policy = RetentionPolicy.Default;
        var evaluation = policy.Evaluate(
            normalBytes: policy.NormalMaxBytes / 2,
            pinnedBytes: policy.PinnedMaxBytes,
            dailyBytes: 0,
            nowUtc: new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.False(evaluation.OverNormalLimit);
        Assert.True(evaluation.OverPinnedLimit);
        Assert.Equal(QuotaLevel.StopOptionalDetail, evaluation.Level);
        Assert.Contains(evaluation.Warnings, warning => warning.Contains("Pinned incident storage", StringComparison.Ordinal));
    }

    [Fact]
    public void Daily_budget_is_a_warning_not_a_quota_level()
    {
        var policy = RetentionPolicy.Default;
        var evaluation = policy.Evaluate(
            normalBytes: 1024,
            pinnedBytes: 0,
            dailyBytes: policy.DailyBudgetBytes + 1,
            nowUtc: new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.True(evaluation.DailyBudgetExceeded);
        Assert.Equal(QuotaLevel.Normal, evaluation.Level);
        Assert.Contains(evaluation.Warnings, warning => warning.Contains("performance warning", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_threshold_combinations_and_negative_usage_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetentionPolicy { WarningPercent = 95, ReducedDetailPercent = 90 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetentionPolicy { NormalRetention = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetentionPolicy { NormalMaxBytes = 0 }.Validate());

        var policy = RetentionPolicy.Default;
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(-1, 0, 0, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(0, -1, 0, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(0, 0, -1, now));
    }

    [Fact]
    public void Clock_rollback_clamps_retention_age_and_never_moves_the_cutoff_into_fresh_data()
    {
        var policy = RetentionPolicy.Default;
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var evaluation = policy.Evaluate(0, 0, 0, now, clockRollbackSuspected: true);

        Assert.True(evaluation.ClockRollbackSuspected);
        Assert.Equal(now - policy.NormalRetention, evaluation.RetentionCutoff);
        Assert.Contains(evaluation.Warnings, warning => warning.Contains("moved backwards", StringComparison.Ordinal));
    }
}
