using System.IO.Compression;
using System.Security.Cryptography;
using NetScope.Application;

namespace NetScope.Storage.Export;

/// <summary>
/// Raised when a session cannot be exported without inventing facts. The caller already has the
/// same reasons from <see cref="IIncidentExporter.Preview"/>; no package file is created.
/// </summary>
public sealed class ExportBlockedException : InvalidOperationException
{
    public ExportBlockedException(IReadOnlyList<string> blockedReasons)
        : base("Incident export is blocked: " + string.Join("; ", blockedReasons))
    {
        BlockedReasons = blockedReasons.ToArray();
    }

    public IReadOnlyList<string> BlockedReasons { get; }
}

/// <summary>
/// Creates the local, redacted incident package described by design 04 §5-§6. The exporter only
/// reads one NDJSON session, validates it with the same strict reader used for replay, and holds
/// the complete package in memory before writing a single file. It has no network client, no
/// import entry point and no repair capability.
/// </summary>
public sealed class IncidentExporter : IIncidentExporter, IExportPackageDetailsProvider
{
    private const int IncidentIdHexLength = 24;
    private const int MaxNameAttempts = 10;

    public ExportPreview Preview(string sessionPath) => PreviewDetails(sessionPath).Preview;

    public ExportPackageDetails PreviewDetails(string sessionPath)
    {
        var analysis = Analyze(sessionPath);
        IReadOnlyList<ExportFileSummary> files = Array.Empty<ExportFileSummary>();
        IReadOnlyList<ExportRedactionSummary> redactions = Array.Empty<ExportRedactionSummary>();

        if (analysis.BlockedReasons.Count == 0 && analysis.Replay is not null)
        {
            var previewSalt = RandomNumberGenerator.GetBytes(32);
            var built = IncidentPackageBuilder.Build(analysis.Replay, new string('0', IncidentIdHexLength),
                previewSalt, analysis.Warnings, analysis.Statistics);
            files = built.Entries
                .Select(entry => new ExportFileSummary
                {
                    Name = entry.Name,
                    Bytes = entry.Bytes.LongLength,
                    Purpose = entry.Purpose,
                })
                .ToArray();
            redactions = built.Redactions;
        }

        var preview = new ExportPreview
        {
            // The preview is displayed without revealing a full local path (D2). The caller
            // already knows the path it passed; only the log file name is echoed back.
            SessionPath = FileNameOnly(analysis.FullPath),
            GeneratedAt = DateTimeOffset.UtcNow,
            EventCount = analysis.Replay?.Log.Events.Count ?? 0,
            GapCount = analysis.Replay?.Summary.ObservationGapCount ?? 0,
            DiagnosisCount = analysis.Replay?.Diagnoses.Count ?? 0,
            Files = files,
            Redactions = redactions,
            Warnings = analysis.Warnings,
            BlockedReasons = analysis.BlockedReasons,
        };

        return new ExportPackageDetails
        {
            Preview = preview,
            Statistics = analysis.Statistics,
        };
    }

    public async Task<ExportResult> ExportAsync(string sessionPath, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) throw new ArgumentException("A session path is required.", nameof(sessionPath));
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("A destination directory is required.", nameof(destinationDirectory));
        cancellationToken.ThrowIfCancellationRequested();

        var analysis = Analyze(sessionPath);
        if (analysis.BlockedReasons.Count != 0 || analysis.Replay is null)
        {
            throw new ExportBlockedException(analysis.BlockedReasons.Count == 0
                ? new[] { "The session could not be analyzed." }
                : analysis.BlockedReasons);
        }

        var destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);

        // Per-package random salt: used to derive the otherwise meaningless incident id and held
        // in memory only. It is never passed to the builder, so it cannot reach manifest, preview
        // or any package byte.
        var salt = RandomNumberGenerator.GetBytes(32);
        BuiltIncidentPackage? built = null;
        var packagePath = string.Empty;
        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var incidentId = DeriveIncidentId(salt, attempt);
            var candidate = Path.Combine(destination, "incident-" + incidentId + ".zip");
            if (File.Exists(candidate)) continue;
            built = IncidentPackageBuilder.Build(analysis.Replay, incidentId, salt, analysis.Warnings,
                analysis.Statistics);
            packagePath = candidate;
            break;
        }

        if (built is null)
        {
            throw new IOException("A new incident package name could not be allocated in the destination directory.");
        }

        var archive = CreateZip(built);
        var temporary = Path.Combine(destination,
            "." + Path.GetFileName(packagePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, archive, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, packagePath, overwrite: false);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // Best effort cleanup; the original exception is the one that matters.
            }

            throw;
        }

        return new ExportResult
        {
            PackagePath = packagePath,
            Bytes = new FileInfo(packagePath).Length,
            Warnings = analysis.Warnings,
        };
    }

    private static SessionAnalysis Analyze(string sessionPath)
    {
        var blocked = new List<string>();
        var warnings = new List<string>();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sessionPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            blocked.Add("The session path is not a valid local file path.");
            return new SessionAnalysis(string.Empty, null, PackageStatistics.Empty, blocked, warnings);
        }

        if (!File.Exists(fullPath))
        {
            blocked.Add("The session log was not found.");
            return new SessionAnalysis(fullPath, null, PackageStatistics.Empty, blocked, warnings);
        }

        ReplayResult replay;
        try
        {
            replay = new ReplayService().Replay(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
        {
            // Do not echo the exception text: an I/O message can contain a full local path.
            blocked.Add("The session log could not be read.");
            return new SessionAnalysis(fullPath, null, PackageStatistics.Empty, blocked, warnings);
        }

        var log = replay.Log;
        if (log.Events.Count == 0) blocked.Add("The session log contains no events.");
        if (log.Problems.Count > 0)
        {
            blocked.Add($"The session log has {log.Problems.Count} unreadable line(s)"
                + (log.HasTruncatedTail ? " including an interrupted tail." : "."));
        }

        if (log.SequenceGaps.Count > 0)
            blocked.Add($"The session log has {log.SequenceGaps.Count} sequence gap(s).");
        if (log.DuplicateCount > 0)
            blocked.Add($"The session log has {log.DuplicateCount} duplicate event id(s).");
        if (!log.IsClean)
            blocked.Add("The event log is not clean; export would preserve an ambiguous session.");

        if (replay.Evidence is null)
        {
            blocked.Add("Typed evidence is unavailable; only envelope-level events were found.");
        }
        else if (replay.EvidenceProblems.Count > 0)
        {
            // Only a count: decoder problem text can quote payload field names or identifiers,
            // and the preview must not become a side channel for original values.
            blocked.Add($"Typed evidence is inconsistent in {replay.EvidenceProblems.Count} place(s); export is refused.");
        }

        var statistics = PackageStatisticsBuilder.Build(replay);
        AddShapeWarnings(statistics, warnings);
        if (replay.Evidence?.Snapshot is null) warnings.Add("The package contains no network snapshot.");
        if (replay.Diagnoses.Count == 0) warnings.Add("The package contains no diagnoses.");
        if (replay.Summary.SyntheticCount > 0 && replay.Summary.SyntheticCount != replay.Summary.EventCount)
            warnings.Add("The session mixes synthetic and measured events; the package synthetic flag is false.");

        return new SessionAnalysis(fullPath, replay, statistics, blocked, warnings);
    }

    /// <summary>
    /// Turns the separated counters into user-facing warnings. A clean sequence (gaps=0) must not
    /// be presented as a complete capture, and zero probes must not be presented as health.
    /// </summary>
    private static void AddShapeWarnings(PackageStatistics statistics, List<string> warnings)
    {
        if (statistics.ProbeCoverage.ProbeEvents == 0)
        {
            warnings.Add("The package contains no probe results; local snapshot status is not a reachability test.");
        }

        if (statistics.Completeness.SnapshotCount > 0 && statistics.Completeness.IncompleteSnapshotCount > 0)
        {
            warnings.Add($"{statistics.Completeness.IncompleteSnapshotCount} of "
                + $"{statistics.Completeness.SnapshotCount} snapshot(s) have incomplete data domains; "
                + $"gaps={statistics.Gaps.SequenceGapCount} counts sequence gaps and gap_events="
                + $"{statistics.Gaps.GapEventCount} counts independent gap records, so neither means the domains were read completely.");
        }

        if (statistics.Gaps.GapEventCount > 0 && statistics.Completeness.IncompleteSnapshotCount == 0)
        {
            warnings.Add($"The package has {statistics.Gaps.GapEventCount} independent gap event(s); this is a recorded capture gap.");
        }
    }

    private static string FileNameOnly(string path)
    {
        if (string.IsNullOrEmpty(path)) return "session";
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? "session" : name;
    }

    private static string DeriveIncidentId(byte[] salt, int attempt)
    {
        var digest = HMACSHA256.HashData(salt, System.Text.Encoding.UTF8.GetBytes("incident-id:" + attempt));
        return Convert.ToHexStringLower(digest)[..IncidentIdHexLength];
    }

    private static byte[] CreateZip(BuiltIncidentPackage package)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var timestamp = ZipTimestamp(package.StartedAt);
            foreach (var entry in package.Entries)
            {
                var item = archive.CreateEntry(entry.Name, CompressionLevel.Optimal);
                item.LastWriteTime = timestamp;
                using var target = item.Open();
                target.Write(entry.Bytes, 0, entry.Bytes.Length);
            }
        }

        return stream.ToArray();
    }

    private static DateTimeOffset ZipTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var minimum = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maximum = new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        if (utc < minimum) return minimum;
        if (utc > maximum) return maximum;
        return utc;
    }

    private sealed record SessionAnalysis(string FullPath, ReplayResult? Replay,
        PackageStatistics Statistics, IReadOnlyList<string> BlockedReasons, IReadOnlyList<string> Warnings);
}
