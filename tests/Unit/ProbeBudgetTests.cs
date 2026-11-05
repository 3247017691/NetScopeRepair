using System.Diagnostics;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

public sealed class ProbeBudgetTests
{
    [Fact]
    public async Task Group_budget_cancels_every_active_request_before_its_individual_timeout()
    {
        var time = new ManualProbeTimeProvider();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var plan = ProbePlans.Default(perProbeTimeout: TimeSpan.FromMinutes(1),
            groupBudget: TimeSpan.FromMilliseconds(60));
        var started = 0;
        var transport = new FakeTransport(async (_, token) =>
        {
            tokens.Add(token);
            if (Interlocked.Increment(ref started) == plan.Paths.Count) allStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportOutcome.Reachable(204);
        });
        using var stopSource = new CancellationTokenSource();
        var running = new HttpProbeRunner(transport, time, time).RunAsync(plan, stopSource.Token);
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, transport.Requests.Count);
            Assert.False(running.IsCompleted);
            time.Advance(plan.GroupBudget - TimeSpan.FromMilliseconds(1));
            Assert.False(running.IsCompleted);
            Assert.All(tokens, token => Assert.False(token.IsCancellationRequested));

            time.Advance(TimeSpan.FromMilliseconds(1));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(plan.GroupBudget.TotalMilliseconds, result.DurationMs);
            Assert.Equal(3, tokens.Count);
            Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
            Assert.True(result.BudgetExhausted);
            Assert.Empty(result.MissingPaths);
            Assert.Equal(3, result.Results.Count);
            Assert.All(result.Results, item =>
            {
                Assert.Equal(EventStatus.Cancelled, item.Status);
                Assert.Equal("group_budget_exhausted", item.TransportError);
                Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
                Assert.False(item.TimingComparable);
            });
            Assert.False(result.TimingComparable);
        }
        finally
        {
            stopSource.Cancel();
        }
    }

    [Fact]
    public async Task Budget_cancels_queued_paths_and_preserves_a_reason_for_each()
    {
        var time = new ManualProbeTimeProvider();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        var transport = new FakeTransport(async (_, token) =>
        {
            activeToken = token;
            firstStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportOutcome.Reachable(204);
        });
        var plan = ProbePlans.Default(perProbeTimeout: TimeSpan.FromMinutes(1),
            groupBudget: TimeSpan.FromMilliseconds(40), maxConcurrency: 1);
        using var stopSource = new CancellationTokenSource();
        var running = new HttpProbeRunner(transport, time, time).RunAsync(plan, stopSource.Token);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(transport.Requests);
            Assert.False(running.IsCompleted);
            time.Advance(plan.GroupBudget - TimeSpan.FromMilliseconds(1));
            Assert.False(running.IsCompleted);
            Assert.False(activeToken.IsCancellationRequested);
            Assert.Single(transport.Requests);

            time.Advance(TimeSpan.FromMilliseconds(1));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(activeToken.IsCancellationRequested);
            Assert.Single(transport.Requests);
            Assert.Equal(plan.GroupBudget.TotalMilliseconds, result.DurationMs);
            Assert.True(result.BudgetExhausted);
            Assert.Equal(3, result.Results.Count);
            Assert.Equal(plan.Paths.Skip(1).ToArray(), result.MissingPaths);
            Assert.Equal("group_budget_exhausted", result.Results[0].TransportError);
            Assert.All(result.Results.Skip(1), item =>
            {
                Assert.Equal("not_started_group_budget_exhausted", item.TransportError);
                Assert.Equal(EventStatus.Cancelled, item.Status);
                Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
                Assert.Equal(0, item.DurationMs);
                Assert.Null(item.HttpStatus);
            });
        }
        finally
        {
            stopSource.Cancel();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_probe_timeout_never_starts_an_unbounded_request(int milliseconds)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(
            ProbePlans.Default(perProbeTimeout: TimeSpan.FromMilliseconds(milliseconds)), CancellationToken.None);

        Assert.Empty(transport.Requests);
        Assert.Equal(3, result.Results.Count);
        Assert.Equal(3, result.MissingPaths.Count);
        Assert.False(result.BudgetExhausted);
        Assert.False(result.TimingComparable);
        Assert.All(result.Results, item =>
        {
            Assert.Equal(EventStatus.Unsupported, item.Status);
            Assert.Equal("not_started_invalid_probe_timeout", item.TransportError);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.False(item.TimingComparable);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_group_budget_preserves_all_paths_without_sending(int milliseconds)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(
            ProbePlans.Default(groupBudget: TimeSpan.FromMilliseconds(milliseconds)), CancellationToken.None);

        Assert.Empty(transport.Requests);
        Assert.Equal(3, result.MissingPaths.Count);
        Assert.All(result.Results, item =>
        {
            Assert.Equal("not_started_invalid_group_budget", item.TransportError);
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
        });
        Assert.False(result.TimingComparable);
    }

    [Fact]
    public async Task Invalid_request_options_do_not_create_comparable_observations()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));
        var plan = ProbePlans.Default() with { MaxResponseBytes = -1 };
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.Empty(transport.Requests);
        Assert.Equal(3, result.Results.Count);
        Assert.All(result.Results, item => Assert.Equal("not_started_invalid_response_limit", item.TransportError));
        Assert.False(result.TimingComparable);
    }

    [Fact]
    public async Task Wall_clock_rollback_does_not_extend_group_budget()
    {
        var transport = new FakeTransport(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportOutcome.Reachable(204);
        });
        var clock = new RollingBackClock();
        var result = await new HttpProbeRunner(transport, clock).RunAsync(
            ProbePlans.Default(groupBudget: TimeSpan.FromMilliseconds(40), maxConcurrency: 1), CancellationToken.None);

        Assert.True(result.BudgetExhausted);
        Assert.Equal(3, result.Results.Count);
        Assert.All(result.Results, item => Assert.Equal(EventStatus.Cancelled, item.Status));
        Assert.InRange(result.DurationMs, 10, 2000);
    }

    private sealed class RollingBackClock : IClock
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private int _reads;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddHours(-Interlocked.Increment(ref _reads));
        public double MonotonicMs => _elapsed.Elapsed.TotalMilliseconds;
    }
}
