namespace NetScope.Application;

/// <summary>One file that would appear in an incident package, described without its contents.</summary>
public sealed record ExportFileSummary
{
    public required string Name { get; init; }

    public required long Bytes { get; init; }

    public required string Purpose { get; init; }
}

/// <summary>A redaction category and how many values it replaced. Original values are never shown.</summary>
public sealed record ExportRedactionSummary
{
    public required string Category { get; init; }

    public required int ReplacedFields { get; init; }

    public required string Strategy { get; init; }
}

/// <summary>
/// What an export would contain. The preview must be enough to decide whether to share the
/// package without displaying the sensitive values themselves.
/// </summary>
public sealed record ExportPreview
{
    public required string SessionPath { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required int EventCount { get; init; }

    public required int GapCount { get; init; }

    public required int DiagnosisCount { get; init; }

    public IReadOnlyList<ExportFileSummary> Files { get; init; } = Array.Empty<ExportFileSummary>();

    public IReadOnlyList<ExportRedactionSummary> Redactions { get; init; } = Array.Empty<ExportRedactionSummary>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public bool CanExport => BlockedReasons.Count == 0;
}

public sealed record ExportResult
{
    public required string PackagePath { get; init; }

    public required long Bytes { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Creates a minimized, checksummed incident package from a local session log.</summary>
public interface IIncidentExporter
{
    ExportPreview Preview(string sessionPath);

    Task<ExportResult> ExportAsync(string sessionPath, string destinationDirectory, CancellationToken cancellationToken);
}
