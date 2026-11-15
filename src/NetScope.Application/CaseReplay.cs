using NetScope.Domain;

namespace NetScope.Application;

/// <summary>
/// Admission limits for an untrusted incident package. The defaults are the frozen values from
/// design 04 §6; a caller may tighten them for a test or a restricted import profile, never loosen
/// them silently.
/// </summary>
public sealed record CaseReplayLimits
{
    public int MaxEntries { get; init; } = 1000;

    public long MaxTotalUncompressedBytes { get; init; } = 100L * 1024 * 1024;

    public long MaxEntryUncompressedBytes { get; init; } = 20L * 1024 * 1024;

    public int MaxPathDepth { get; init; } = 8;

    public static CaseReplayLimits Default { get; } = new();
}

public enum CaseReplayStatus
{
    /// <summary>The package was admitted and the fixed rule set produced a comparison.</summary>
    Ok,

    /// <summary>Admission or integrity failed; no analysis is claimed.</summary>
    Rejected,

    /// <summary>Readable but outside this build's supported format/version; recorded facts only.</summary>
    Unsupported,
}

public enum CaseReplayIssueSeverity
{
    Info,
    Warning,
    Blocking,
}

/// <summary>One fail-closed admission, integrity, compatibility or explanation item.</summary>
public sealed record CaseReplayIssue
{
    public required string Code { get; init; }

    public required CaseReplayIssueSeverity Severity { get; init; }

    public required string Message { get; init; }

    public string? EntryName { get; init; }
}

/// <summary>What the untrusted-package admission proved before any content was interpreted.</summary>
public sealed record CaseAdmissionSummary
{
    public required bool Accepted { get; init; }

    public required int EntryCount { get; init; }

    public required long TotalUncompressedBytes { get; init; }

    public required int MaxObservedPathDepth { get; init; }

    public IReadOnlyList<CaseReplayIssue> Problems { get; init; } = Array.Empty<CaseReplayIssue>();
}

public sealed record CaseRuleVersion(string RuleId, string Version);

/// <summary>
/// The fixed rule-set identity used for recomputation. It is supplied by the running build, is
/// shown next to every recomputed diagnosis, and is never presented as the package's own version.
/// </summary>
public sealed record CaseRuleSetInfo
{
    public required string RuleSetVersion { get; init; }

    public required string AppVersion { get; init; }

    public IReadOnlyList<CaseRuleVersion> Rules { get; init; } = Array.Empty<CaseRuleVersion>();

    public string? Disclaimer { get; init; }
}

public interface IRuleSetVersionProvider
{
    CaseRuleSetInfo RuleSet { get; }
}

/// <summary>
/// A pure evaluator bound to one fixed rule-set version. It must not read the system, open a socket
/// or write anything; only the in-memory evidence is evaluated.
/// </summary>
public interface ICaseRuleEvaluator : IRuleSetVersionProvider
{
    DiagnosticEvaluationResult Evaluate(EvidenceSet evidence);
}

/// <summary>One diagnosis recorded inside the package, with the compatibility facts for its rule.</summary>
public sealed record CaseRecordedDiagnosis
{
    /// <summary>Snapshot scope the diagnosis event was saved in; null for unscoped/legacy events.</summary>
    public string? ScopeSnapshotEventId { get; init; }

    public required Diagnosis Diagnosis { get; init; }

    /// <summary>True when this build contains the recorded rule id.</summary>
    public required bool RuleSupported { get; init; }

    /// <summary>True when the installed rule version equals the recorded version.</summary>
    public required bool VersionMatchesCurrent { get; init; }

    public string? CurrentRuleVersion { get; init; }
}

/// <summary>A diagnosis produced right now by the pinned rule set (kept separate from the package).</summary>
public sealed record CaseRecomputedDiagnosis
{
    public string? ScopeSnapshotEventId { get; init; }

    public required Diagnosis Diagnosis { get; init; }
}

public enum CaseDiagnosisDisposition
{
    Match,
    OnlyRecorded,
    OnlyRecomputed,
    LevelChanged,
    EvidenceChanged,
    MissingEvidenceChanged,
    Unsupported,
}

/// <summary>Recorded-vs-recomputed difference within one snapshot scope and one rule id.</summary>
public sealed record CaseDiagnosisDifference
{
    public required string RuleId { get; init; }

    public string? ScopeSnapshotEventId { get; init; }

    public required CaseDiagnosisDisposition Disposition { get; init; }

    public string? RecordedRuleVersion { get; init; }

    public string? RecomputedRuleVersion { get; init; }

    public EvidenceGrade? RecordedLevel { get; init; }

    public EvidenceGrade? RecomputedLevel { get; init; }

    public IReadOnlyList<string> RecordedEvidenceIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RecomputedEvidenceIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Replay of exactly one snapshot scope; evidence is never borrowed from another scope.</summary>
public sealed record CaseSnapshotReplay
{
    public required string SnapshotEventId { get; init; }

    public required int Sequence { get; init; }

    public required string SnapshotId { get; init; }

    public string? ObservationWindowId { get; init; }

    public int RecordCount { get; init; }

    public int ProbeCount { get; init; }

    public int RecordedDiagnosisCount { get; init; }

    public int RecomputedDiagnosisCount { get; init; }

    /// <summary>Stable interface identity plus the ifIndex exactly as that cycle recorded it.</summary>
    public IReadOnlyList<CaseInterfaceRef> Interfaces { get; init; } = Array.Empty<CaseInterfaceRef>();
}

/// <summary>One interface identity in one snapshot scope; the old ifIndex is never rewritten.</summary>
public sealed record CaseInterfaceRef
{
    public required string InterfaceGuid { get; init; }

    public required int IfIndex { get; init; }
}

/// <summary>One incident window reconstructed from incident markers, including cross-cycle references.</summary>
public sealed record CaseIncidentReplay
{
    public required string IncidentId { get; init; }

    public string? Trigger { get; init; }

    public IReadOnlyList<string> States { get; init; } = Array.Empty<string>();

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public bool? PreWindowAvailable { get; init; }

    public bool? PostWindowComplete { get; init; }

    public IReadOnlyList<string> MissingRanges { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EventIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SnapshotIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DiagnosisIds { get; init; } = Array.Empty<string>();

    /// <summary>Snapshot event ids whose scope is part of this incident window.</summary>
    public IReadOnlyList<string> SnapshotScopeEventIds { get; init; } = Array.Empty<string>();

    /// <summary>True when the incident references evidence from more than one snapshot cycle.</summary>
    public bool SpansMultipleSnapshotCycles { get; init; }

    public string? ClosureReason { get; init; }
}

/// <summary>Manifest/package facts extracted without trusting the package's text.</summary>
public sealed record CasePackageInfo
{
    public string? PackageFormat { get; init; }

    public string? PackageFormatVersion { get; init; }

    public string? IncidentId { get; init; }

    public string? AppVersion { get; init; }

    public string? AppBuildCommit { get; init; }

    public bool? Synthetic { get; init; }

    public IReadOnlyList<string> RecordedRuleVersions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidencePayloadVersions { get; init; } = Array.Empty<string>();

    public string? RedactionPolicyVersion { get; init; }

    public int EventCount { get; init; }

    public int SnapshotCount { get; init; }

    public int RecordedDiagnosisCount { get; init; }

    public bool HasTypedEvidence { get; init; }

    public bool HasProbeResults { get; init; }

    public bool HasClientEvidence { get; init; }
}

/// <summary>Complete offline case replay. Everything here was derived from local package bytes.</summary>
public sealed record CaseReplayResult
{
    public required CaseReplayStatus Status { get; init; }

    public required CaseAdmissionSummary Admission { get; init; }

    public required CaseRuleSetInfo RuleSet { get; init; }

    public CasePackageInfo? Package { get; init; }

    /// <summary>Diagnoses as they were recorded in the package; never rewritten.</summary>
    public IReadOnlyList<CaseRecordedDiagnosis> RecordedDiagnoses { get; init; } = Array.Empty<CaseRecordedDiagnosis>();

    /// <summary>Diagnoses recomputed now with <see cref="RuleSet"/>; separate from recorded facts.</summary>
    public IReadOnlyList<CaseRecomputedDiagnosis> RecomputedDiagnoses { get; init; } = Array.Empty<CaseRecomputedDiagnosis>();

    public IReadOnlyList<CaseDiagnosisDifference> Differences { get; init; } = Array.Empty<CaseDiagnosisDifference>();

    public IReadOnlyList<CaseSnapshotReplay> SnapshotScopes { get; init; } = Array.Empty<CaseSnapshotReplay>();

    public IReadOnlyList<CaseIncidentReplay> Incidents { get; init; } = Array.Empty<CaseIncidentReplay>();

    /// <summary>Warnings, compatibility notes and non-blocking integrity observations.</summary>
    public IReadOnlyList<CaseReplayIssue> Issues { get; init; } = Array.Empty<CaseReplayIssue>();

    /// <summary>Human-readable old-package/gap explanations; never a fabricated root cause.</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Hash of the deterministic public result fields; identical input gives identical value.</summary>
    public required string DeterministicFingerprint { get; init; }

    public bool Recomputed => Status == CaseReplayStatus.Ok;
}

/// <summary>Offline, read-only replay of one untrusted incident package.</summary>
public interface ICaseReplayService
{
    CaseReplayResult Replay(string packagePath);
}
