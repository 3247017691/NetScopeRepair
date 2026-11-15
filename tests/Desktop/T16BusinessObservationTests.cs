using NetScope.Application;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

/// <summary>
/// Business observation (10 minutes / 3 rounds, low-privilege main process) and the synthetic
/// preview closed loop. No test performs a real business request or a paid call.
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

    [Fact]
    public async Task A_business_failure_does_not_change_a_technical_committed_result()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            Outcome = T16Fixtures.Outcome(),
        };
        var clock = new FakeClock(Start);
        var vm = new RepairViewModel(gateway, clock);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;
        await vm.ExecuteSelectedAsync(userConfirmed: true);

        vm.StartBusinessObservation();
        Assert.True(vm.RecordBusinessRound(OneFailed(BusinessObservationTarget.Dsh), Start));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(vm.RecordBusinessRound(AllPassed(), Start.AddMinutes(2)));
        Assert.True(vm.RecordBusinessRound(AllPassed(), Start.AddMinutes(4)));

        Assert.Equal("committed", vm.LastOutcome!.StateText);
        Assert.Equal(BusinessObservationStatus.Failed, vm.BusinessView.Status);
        Assert.Contains("committed", vm.TransactionStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_business_observation_never_calls_the_repair_gateway()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate);
        var vm = new RepairViewModel(gateway, new FakeClock(Start));

        vm.StartBusinessObservation();
        vm.RecordBusinessRound(AllPassed(), Start);
        vm.RecordBusinessRound(AllPassed(), Start.AddMinutes(1));

        Assert.Equal(0, gateway.ExecuteCalls);
        Assert.Equal(0, gateway.PreviewCalls);
        Assert.Equal(0, gateway.RecoveryCalls);
    }

    [Fact]
    public async Task The_synthetic_demo_preview_has_selectable_changed_actions_and_a_no_op_field()
    {
        var source = new SyntheticRepairPreviewSource();
        var preview = await source.PreviewAsync(new RepairPreviewRequest
        {
            SnapshotId = "snapshot-demo",
            NowUtc = Start,
            CurrentBootId = "boot-demo",
            InputTrust = RepairInputTrust.Unverified,
        }, CancellationToken.None);

        Assert.True(preview.Synthetic);
        Assert.Equal(2, preview.Actions.Count(action => action.IsChanged));
        Assert.True(preview.Actions.Count(action => action.CanSelect) >= 2);
        Assert.Single(preview.NoOpFields);
        Assert.False(preview.Gate.Enabled);
        Assert.Contains("synthetic", string.Join("；", preview.Refusals), StringComparison.OrdinalIgnoreCase);
        Assert.All(preview.Actions, action => Assert.True(action.IsChanged));

        var gateway = new FakeRepairGateway(preview, preview.Gate);
        var vm = new RepairViewModel(gateway, new FakeClock(Start));
        vm.ApplyPreview(preview);
        Assert.All(vm.Actions, row => Assert.False(row.IsSelected));
        Assert.False(vm.CanExecute);
    }
}

