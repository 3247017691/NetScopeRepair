using NetScope.Application;

namespace NetScope.Desktop.Tests;

/// <summary>
/// Business observation (10 minutes / 3 rounds, low-privilege main process).
/// No test performs a real business request or a paid call.
/// </summary>
public sealed class T16BusinessObservationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static BusinessTargetResult[] AllPassed() => Enum.GetValues<BusinessObservationTarget>()
        .Select(target => new BusinessTargetResult
        {
            Target = target,
            Result = BusinessObservationResult.Passed,
            Detail = "本地记录通过",
        })
        .ToArray();

    private static BusinessTargetResult[] OneFailed(BusinessObservationTarget target) =>
        AllPassed().Select(result => result.Target == target
            ? result with { Result = BusinessObservationResult.Failed, Detail = "本地记录失败" }
            : result).ToArray();

    [Fact]
    public void Three_passing_rounds_before_the_ten_minute_window_are_not_stable_yet()
    {
        var observation = new RepairBusinessObservation();
        observation.Start(Start);
        for (var round = 0; round < 3; round++)
        {
            Assert.True(observation.TryRecordRound(AllPassed(), Start.AddMinutes(round), out _));
        }

        var view = observation.Evaluate(Start.AddMinutes(3));

        Assert.Equal(BusinessObservationStatus.InProgress, view.Status);
        Assert.False(view.IsComplete);
        Assert.Contains("不宣称稳定", view.StatusText, StringComparison.Ordinal);
        Assert.Contains("窗口未满", view.RoundsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Three_passing_rounds_inside_a_full_ten_minute_window_pass()
    {
        var observation = new RepairBusinessObservation();
        observation.Start(Start);
        for (var round = 0; round < 3; round++)
        {
            Assert.True(observation.TryRecordRound(AllPassed(), Start.AddMinutes(round * 2), out _));
        }

        var view = observation.Evaluate(Start.AddMinutes(10));

        Assert.Equal(BusinessObservationStatus.Passed, view.Status);
        Assert.True(view.IsComplete);
        Assert.True(view.IsWindowComplete);
        Assert.All(view.Targets, row => Assert.Equal(BusinessObservationResult.Passed, row.Result));
    }

    [Fact]
    public void A_failed_round_is_counted_as_business_failure_and_never_rewritten()
    {
        var observation = new RepairBusinessObservation();
        observation.Start(Start);
        observation.TryRecordRound(AllPassed(), Start, out _);
        observation.TryRecordRound(OneFailed(BusinessObservationTarget.ChromeOverseas), Start.AddMinutes(2), out _);
        observation.TryRecordRound(AllPassed(), Start.AddMinutes(4), out _);

        var view = observation.Evaluate(Start.AddMinutes(10));

        Assert.Equal(BusinessObservationStatus.Failed, view.Status);
        Assert.True(view.IsComplete);
        var chrome = view.Targets.Single(row => row.TargetText.Contains("国外", StringComparison.Ordinal));
        Assert.Equal(BusinessObservationResult.Failed, chrome.Result);
        Assert.Contains("失败统计", chrome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_target_is_not_tested_and_never_counts_as_a_pass()
    {
        var observation = new RepairBusinessObservation();
        observation.Start(Start);
        var partial = new[]
        {
            new BusinessTargetResult
            {
                Target = BusinessObservationTarget.Dsh,
                Result = BusinessObservationResult.Passed,
            },
        };
        observation.TryRecordRound(partial, Start, out _);

        var view = observation.Evaluate(Start);

        var antigravity = view.Targets.Single(row => row.TargetText == "Antigravity");
        Assert.Equal(BusinessObservationResult.NotTested, antigravity.Result);
        Assert.Equal("未测试", antigravity.ResultText);
        Assert.NotEqual(BusinessObservationStatus.Passed, view.Status);
    }

    [Fact]
    public void Rounds_outside_the_window_or_after_three_rounds_are_rejected()
    {
        var observation = new RepairBusinessObservation();
        observation.Start(Start);
        Assert.False(observation.TryRecordRound(AllPassed(), Start.AddMinutes(11), out var late));
        Assert.Contains("10 分钟", late, StringComparison.Ordinal);

        for (var round = 0; round < 3; round++)
        {
            Assert.True(observation.TryRecordRound(AllPassed(), Start.AddMinutes(round), out _));
        }

        Assert.False(observation.TryRecordRound(AllPassed(), Start.AddMinutes(4), out var extra));
        Assert.Contains("3 轮", extra, StringComparison.Ordinal);
    }
}

