using NetScope.Domain;

namespace NetScope.Application;

/// <summary>Injected time source so durations are monotonic and tests can pin the clock.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic milliseconds since an arbitrary origin; never compared across boots.</summary>
    double MonotonicMs { get; }
}

/// <summary>Read-only collection of the local network state. Implementations must not write.</summary>
public interface ISnapshotCollector
{
    string Source { get; }

    string SourceVersion { get; }

    Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken);
}

/// <summary>Runs one comparison group of explicit probe paths.</summary>
public interface IProbeRunner
{
    Task<ProbeGroupResult> RunAsync(ProbeGroupPlan plan, CancellationToken cancellationToken);
}

/// <summary>A pure evidence rule. It must not open sockets, start processes or touch the system.</summary>
public interface IDiagnosticRule
{
    string RuleId { get; }

    string Version { get; }

    /// <summary>Returns null when the rule's minimum evidence is not present.</summary>
    Diagnosis? Evaluate(EvidenceSet evidence);
}

/// <summary>Append-only event log with replay.</summary>
public interface IEventStore
{
    Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventEnvelope>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Wall-clock and monotonic clock backed by the operating system.</summary>
public sealed class SystemClock : IClock
{
    private readonly long _origin = Environment.TickCount64;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public double MonotonicMs => Environment.TickCount64 - _origin;
}
