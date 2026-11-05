using NetScope.Domain;

namespace NetScope.Application;

/// <summary>
/// One referenced piece of evidence. Rules cite evidence ids instead of restating facts, so a
/// diagnosis can always be traced back to what was actually observed.
/// </summary>
public sealed record EvidenceRecord
{
    public required string EvidenceId { get; init; }

    /// <summary>For example snapshot / probe_result / user_report / collector_error.</summary>
    public required string Kind { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    /// <summary>True when a human reported it rather than a collector observing it.</summary>
    public bool IsUserReport { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public double? MonotonicMs { get; init; }

    public string? BootId { get; init; }

    public string? Source { get; init; }

    public string? Provenance { get; init; }

    public EventStatus Status { get; init; } = EventStatus.Unknown;

    public string? ProxyEndpointId { get; init; }

    public string? TargetId { get; init; }

    public string? AddressFamily { get; init; }

    public string? ObservationWindowId { get; init; }

    public IReadOnlyList<string> SourceEventIds { get; init; } = Array.Empty<string>();

    public string TransformationVersion { get; init; } = "1";
}

/// <summary>
/// A declarative change someone proposes, never a command. A rule may compare it with the
/// current state, for example to explain that applying it would be a no-op, but nothing in M0
/// can execute it.
/// </summary>
public sealed record ProposedChange
{
    /// <summary>For example interface_metric, disable_default_routes or route_prefix.</summary>
    public required string Field { get; init; }

    public required string TargetInterfaceGuid { get; init; }

    public required string DesiredValue { get; init; }

    public string? Rationale { get; init; }
}

/// <summary>Everything the diagnostic rules may look at. Rules never fetch their own data.</summary>
public sealed record EvidenceSet
{
    public required IReadOnlyList<EvidenceRecord> Records { get; init; }

    public required IReadOnlyList<ProbeResult> Probes { get; init; }

    public NetworkSnapshot? Snapshot { get; init; }

    /// <summary>Proposed, not applied. Used by DX-010 to detect a no-op.</summary>
    public IReadOnlyList<ProposedChange> ProposedChanges { get; init; } = Array.Empty<ProposedChange>();

    public IReadOnlyList<InterfaceState> Interfaces => Snapshot?.Interfaces.Value ?? Array.Empty<InterfaceState>();

    public IReadOnlyList<RouteEntry> Routes => Snapshot?.Routes.Value ?? Array.Empty<RouteEntry>();

    public EvidenceRecord[] ByKind(string kind) =>
        Records.Where(record => string.Equals(record.Kind, kind, StringComparison.Ordinal)).ToArray();

    public static EvidenceSet Empty { get; } = new()
    {
        Records = Array.Empty<EvidenceRecord>(),
        Probes = Array.Empty<ProbeResult>(),
    };
}

/// <summary>Limits the scope of a diagnosis so an old conclusion cannot drive a current repair.</summary>
public sealed record DiagnosisScope
{
    public string? ProbeGroupId { get; init; }

    public string? TargetId { get; init; }

    public string? AddressFamily { get; init; }

    public DateTimeOffset? ValidUntil { get; init; }

    public ProbePath? Path { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string? ObservationWindowId { get; init; }

    public string? ProxyEndpointId { get; init; }

    public string? BootId { get; init; }

    public string? RequestOptionsKey { get; init; }
}

/// <summary>An execution error is separate from a diagnosis and carries no invented evidence references.</summary>
public sealed record RuleEvaluationFailure
{
    public required string RuleId { get; init; }

    public required string RuleVersion { get; init; }

    public required string ErrorType { get; init; }

    public required string Summary { get; init; }
}

public sealed record DiagnosticEvaluationResult
{
    public required IReadOnlyList<Diagnosis> Diagnoses { get; init; }

    public required IReadOnlyList<RuleEvaluationFailure> Failures { get; init; }
}

/// <summary>
/// One diagnostic statement. <see cref="Summary"/> is display text only and must never become
/// a repair parameter; repairs use declarative action ids.
/// </summary>
public sealed record Diagnosis
{
    public required string DiagnosisId { get; init; }

    public required string RuleId { get; init; }

    public required string RuleVersion { get; init; }

    public required EvidenceGrade Level { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<string> EvidenceIds { get; init; }

    public IReadOnlyList<string> CounterEvidenceIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingEvidence { get; init; } = Array.Empty<string>();

    /// <summary>Always empty in M0: no diagnostic rule may produce a repair action.</summary>
    public IReadOnlyList<string> AllowedRepairActions { get; init; } = Array.Empty<string>();

    public required string SuggestedNextStep { get; init; }

    public required DiagnosisScope Scope { get; init; }
}
