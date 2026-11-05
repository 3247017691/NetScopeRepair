using NetScope.Application;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>Clock whose value advances a fixed amount per read, so time-based logic is testable.</summary>
internal sealed class FakeClock : IClock
{
    private int _reads;

    public FakeClock(DateTimeOffset? start = null, TimeSpan? stepPerRead = null)
    {
        Start = start ?? new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8));
        StepPerRead = stepPerRead ?? TimeSpan.Zero;
    }

    public DateTimeOffset Start { get; }

    public TimeSpan StepPerRead { get; }

    public DateTimeOffset UtcNow
    {
        get
        {
            var offset = StepPerRead * _reads;
            _reads++;
            return Start + offset;
        }
    }

    public double MonotonicMs
    {
        get
        {
            var offset = StepPerRead * _reads;
            _reads++;
            return offset.TotalMilliseconds;
        }
    }
}

/// <summary>Transport that answers from a delegate and records every request it received.</summary>
internal sealed class FakeTransport : IProbeTransport
{
    private readonly Func<ProbeRequest, CancellationToken, Task<TransportOutcome>> _handler;

    public FakeTransport(Func<ProbeRequest, CancellationToken, Task<TransportOutcome>> handler)
    {
        _handler = handler;
    }

    public List<ProbeRequest> Requests { get; } = new();

    public Task<TransportOutcome> SendAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return _handler(request, cancellationToken);
    }
}

/// <summary>Manual monotonic clock and one-shot timer source for group-budget tests.</summary>
internal sealed class ManualProbeTimeProvider : TimeProvider, IClock
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = new();
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp()
    {
        lock (_sync) return _ticks;
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public DateTimeOffset UtcNow => GetUtcNow();
    public double MonotonicMs => (double)GetTimestamp() / TimeSpan.TicksPerMillisecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        ManualTimer[] due;
        lock (_sync)
        {
            _ticks = checked(_ticks + elapsed.Ticks);
            due = _timers.Where(timer => timer.TakeIfDue(_ticks)).ToArray();
        }

        // Cancellation callbacks may dispose timers. Invoke them outside the provider lock.
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualProbeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long? _dueTicks;
        private bool _disposed;

        public ManualTimer(ManualProbeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
                throw new NotSupportedException("This test timer supports one-shot scheduling only.");
            lock (_owner._sync)
            {
                if (_disposed) return false;
                _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : checked(_owner._ticks + dueTime.Ticks);
                return true;
            }
        }

        // Called while the provider lock is held.
        public bool TakeIfDue(long now)
        {
            if (_disposed || _dueTicks is not { } due || due > now) return false;
            _dueTicks = null;
            return true;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._sync)
            {
                _disposed = true;
                _dueTicks = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class ProbePlans
{
    public static ProbeGroupPlan Default(
        TimeSpan? perProbeTimeout = null,
        TimeSpan? groupBudget = null,
        int maxConcurrency = 4) => new()
    {
        ProbeGroupId = "group-test",
        Target = new ProbeTarget { TargetId = "target-test", Endpoint = "https://example.invalid/health" },
        ExplicitProxyEndpoint = "http://127.0.0.1:7890",
        ProxyEndpointId = "proxy-test",
        AddressFamily = "ipv4",
        ObservationWindowId = "window-test",
        BootId = "boot-test",
        PerProbeTimeout = perProbeTimeout ?? TimeSpan.FromSeconds(8),
        GroupBudget = groupBudget ?? TimeSpan.FromSeconds(30),
        MaxConcurrency = maxConcurrency,
        StartedAt = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.FromHours(8)),
    };
}
