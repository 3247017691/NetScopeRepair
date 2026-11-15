using NetScope.Domain;

namespace NetScope.Application;

/// <summary>
/// The explicit scope of one observation cycle. A source that projects adapter facts into
/// evidence must stamp every record with these identifiers; a record that claims another boot,
/// window or snapshot is rejected instead of being attached to this cycle.
/// </summary>
public sealed record ObservationEvidenceContext
{
    public required string SessionId { get; init; }

    public required string BootId { get; init; }

    public required string SnapshotId { get; init; }

    /// <summary>One local sample or one explicit probe group; never shared between cycles.</summary>
    public required string ObservationWindowId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required double MonotonicMs { get; init; }

    public required bool Synthetic { get; init; }

    /// <summary>
    /// Set only when this cycle explicitly probes one proxy endpoint. The listening sample then
    /// carries the same endpoint id so a rule can compare the sample with that path and nothing
    /// else; a local sample leaves it null.
    /// </summary>
    public string? ProxyEndpointId { get; init; }

    /// <summary>The frozen target of an explicitly requested comparison group; null locally.</summary>
    public string? TargetId { get; init; }

    /// <summary>The address family frozen by an explicit probe group; null when none was fixed.</summary>
    public string? AddressFamily { get; init; }
}

/// <summary>
/// One data domain of the observation that could not be read (or only partly read). It is kept
/// separate from the records so an empty record list is never presented as a complete sample.
/// </summary>
public sealed record ObservationEvidenceGap
{
    public required string Source { get; init; }

    public required EventStatus Status { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Minimized adapter facts plus the gaps of the domains that could not be observed.</summary>
public sealed record ObservationEvidenceResult
{
    public required IReadOnlyList<EvidenceRecord> Records { get; init; }

    public IReadOnlyList<ObservationEvidenceGap> Gaps { get; init; } = Array.Empty<ObservationEvidenceGap>();

    public static ObservationEvidenceResult Empty { get; } = new()
    {
        Records = Array.Empty<EvidenceRecord>(),
    };
}

/// <summary>
/// Read-only bridge from the adapter observation to the diagnostic evidence set. Implementations
/// may read processes, listeners and allow-listed configuration only; they must not connect,
/// write, start a client or invent a fact that no source produced.
/// </summary>
public interface IObservationEvidenceSource
{
    Task<ObservationEvidenceResult> CollectAsync(
        ObservationEvidenceContext context, CancellationToken cancellationToken);
}
