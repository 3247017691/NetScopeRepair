using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// One process's monotonic clock origin. <see cref="DomainId"/> and <see cref="BootId"/> are both
/// part of the identity: a desktop planner and an elevated broker are different processes, and
/// two processes can even use the same domain id in different boots.
/// </summary>
public sealed record RepairClockDomain
{
    public required string DomainId { get; init; }

    /// <summary>For example "desktop-as-invoker", "broker", "test-fixture".</summary>
    public required string ProcessRole { get; init; }

    public required string BootId { get; init; }

    /// <summary>Arbitrary monotonic origin, meaningful only inside this domain/boot.</summary>
    public required long MonotonicOrigin { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Monotonic values may only be compared when both domain id and boot id match.</summary>
    public bool IsComparableTo(RepairClockDomain other) =>
        string.Equals(DomainId, other.DomainId, StringComparison.Ordinal)
        && string.Equals(BootId, other.BootId, StringComparison.Ordinal);

    /// <summary>The shared wall clock, with skew and NTP adjustment explicitly tolerated.</summary>
    public static RepairClockDomain SharedSystemUtc(string bootId, string processRole) => new()
    {
        DomainId = "system-utc",
        ProcessRole = processRole,
        BootId = bootId,
        MonotonicOrigin = 0,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
    };
}

/// <summary>
/// The written cross-process clock contract. It is a constant because it must not drift between
/// the planner, the UI and the future broker.
/// </summary>
public static class RepairClockNotice
{
    public const string Text =
        "Desktop（asInvoker）与 Broker（UAC）是不同进程：各自的单调时钟起点不同，MonotonicMs 只能在同一个 ClockDomainId + BootId 内比较，不能跨进程相减。"
        + "跨进程的新鲜度/TTL 只使用 UTC 观察时间加容忍窗口（默认 5 秒），并拒绝未来时间；"
        + "UTC 也不是严格单调，系统时间跳变时以重新采集为准。"
        + "授权租约必须由 broker 用自己的单调时钟计时，不能信任主进程传入的剩余时间或到期时刻。";
}

/// <summary>
/// One observation with provenance, source time, status, boot/clock domain and TTL. A missing or
/// failed fact is preserved as a gap; it is never turned into an empty success.
/// </summary>
public sealed record SourcedFact<T>
    where T : class
{
    public required string FactId { get; init; }

    public required string Source { get; init; }

    public required string SourceVersion { get; init; }

    public required EventStatus Status { get; init; }

    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Which process/clock produced this fact; compared only with an identical domain.</summary>
    public required string ClockDomainId { get; init; }

    public required string BootId { get; init; }

    public TimeSpan Ttl { get; init; } = RepairLimits.DefaultFactTtl;

    public double? MonotonicMs { get; init; }

    public T? Value { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>True only for a complete, readable observation.</summary>
    public bool IsKnown => Status == EventStatus.Ok && Value is not null;

    /// <summary>True when the attempt produced at least part of the data.</summary>
    public bool HasData => Status.ProducedData() && Value is not null;
}

public enum RepairFactFreshness
{
    Fresh,
    Stale,
    NotYetValid,
    Unknown,
}

public static class RepairFactFreshnessEvaluator
{
    public static RepairFactFreshness Assess<T>(
        SourcedFact<T> fact,
        DateTimeOffset nowUtc,
        TimeSpan skewTolerance)
        where T : class
    {
        if (!fact.HasData)
        {
            return RepairFactFreshness.Unknown;
        }

        var age = nowUtc - fact.ObservedAtUtc;
        if (age < -skewTolerance)
        {
            return RepairFactFreshness.NotYetValid;
        }

        return age > fact.Ttl ? RepairFactFreshness.Stale : RepairFactFreshness.Fresh;
    }
}

public enum RepairTimeRelation
{
    Earlier,
    Same,
    Later,
    Incomparable,
}

public static class RepairTimeOrdering
{
    /// <summary>Monotonic order is only defined inside one clock domain and one boot.</summary>
    public static RepairTimeRelation ByMonotonic<T>(SourcedFact<T> left, SourcedFact<T> right)
        where T : class
    {
        if (!string.Equals(left.BootId, right.BootId, StringComparison.Ordinal)
            || !string.Equals(left.ClockDomainId, right.ClockDomainId, StringComparison.Ordinal)
            || left.MonotonicMs is not { } leftMs
            || right.MonotonicMs is not { } rightMs)
        {
            return RepairTimeRelation.Incomparable;
        }

        return leftMs.CompareTo(rightMs) switch
        {
            < 0 => RepairTimeRelation.Earlier,
            0 => RepairTimeRelation.Same,
            _ => RepairTimeRelation.Later,
        };
    }

    /// <summary>UTC order is the only cross-process order; it is not a duration guarantee.</summary>
    public static RepairTimeRelation ByUtc<T>(SourcedFact<T> left, SourcedFact<T> right)
        where T : class
    {
        var comparison = left.ObservedAtUtc.CompareTo(right.ObservedAtUtc);
        return comparison switch
        {
            < 0 => RepairTimeRelation.Earlier,
            0 => RepairTimeRelation.Same,
            _ => RepairTimeRelation.Later,
        };
    }
}

/// <summary>
/// A lease measured by one process's own monotonic clock. The future broker must use this shape
/// so a planner-supplied remaining time cannot extend an authorization.
/// </summary>
public sealed record RepairMonotonicLease
{
    public required string LeaseId { get; init; }

    public required RepairClockDomain Clock { get; init; }

    public required double StartedMs { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Null means the caller's clock is not comparable with this lease, not "expired".</summary>
    public double? RemainingMs(RepairClockDomain callerClock, double callerMs)
    {
        if (!Clock.IsComparableTo(callerClock))
        {
            return null;
        }

        var elapsed = callerMs - StartedMs;
        var remaining = Duration.TotalMilliseconds - elapsed;
        return remaining < 0 ? 0 : remaining;
    }
}

/// <summary>One field value observed in one store, with its own source time and status.</summary>
public sealed record RepairFieldState
{
    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required RepairFieldValue Value { get; init; }

    public string Key => WireNames.Of(Store) + "|" + WireNames.Of(Field);
}
