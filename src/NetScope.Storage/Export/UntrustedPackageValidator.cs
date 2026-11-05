using System.IO.Compression;
using System.Text.RegularExpressions;

namespace NetScope.Storage.Export;

/// <summary>Hard limits for an untrusted incident package. They are intentionally not configurable per import.</summary>
public sealed record PackageAdmissionLimits
{
    /// <summary>Maximum number of central-directory entries, including directories.</summary>
    public int MaxEntries { get; init; } = 1000;

    /// <summary>Maximum sum of declared uncompressed sizes.</summary>
    public long MaxTotalUncompressedBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Maximum declared uncompressed size of one entry.</summary>
    public long MaxEntryUncompressedBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>Maximum number of path segments in one normalized entry name.</summary>
    public int MaxPathDepth { get; init; } = 8;
}

/// <summary>One fail-closed reason why an untrusted package was not admitted.</summary>
public sealed record PackageAdmissionProblem(string Code, string? EntryName, string Message);

/// <summary>Structured admission result. An accepted package still has no import authority.</summary>
public sealed record PackageAdmissionResult
{
    public required IReadOnlyList<PackageAdmissionProblem> Problems { get; init; }

    public required int EntryCount { get; init; }

    public required long TotalUncompressedBytes { get; init; }

    public required int MaxObservedPathDepth { get; init; }

    public bool IsAccepted => Problems.Count == 0;
}

/// <summary>
/// Admission checks for an incident package produced elsewhere. It reads only ZIP central
/// directory metadata (no entry is decompressed or extracted) and reports every boundary that
/// fails. There is deliberately no import execution entry point in M1.
/// </summary>
public static class UntrustedPackageValidator
{
    private static readonly Regex DriveLetter = new(@"^[A-Za-z]:", RegexOptions.Compiled);
    private static readonly Regex DrivePrefix = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    public static PackageAdmissionResult Validate(string path, PackageAdmissionLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A package path is required.", nameof(path));
        limits ??= new PackageAdmissionLimits();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Validate(stream, limits);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
        {
            return Failure("unreadable_package", null, "The package file could not be opened for admission.");
        }
    }

    public static PackageAdmissionResult Validate(Stream stream, PackageAdmissionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= new PackageAdmissionLimits();
        if (limits.MaxEntries <= 0 || limits.MaxTotalUncompressedBytes <= 0
            || limits.MaxEntryUncompressedBytes <= 0 || limits.MaxPathDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Admission limits must be positive.");
        }

        Stream source = stream;
        MemoryStream? buffered = null;
        if (!stream.CanSeek)
        {
            buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            source = buffered;
        }

        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            return Inspect(archive, limits);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return Failure("invalid_zip", null, "The file is not a readable ZIP archive.");
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    private static PackageAdmissionResult Inspect(ZipArchive archive, PackageAdmissionLimits limits)
    {
        var problems = new List<PackageAdmissionProblem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        long totalBytes = 0;
        var maxDepth = 0;

        foreach (var entry in archive.Entries)
        {
            entryCount++;
            if (entryCount > limits.MaxEntries)
            {
                problems.Add(new PackageAdmissionProblem("too_many_entries", null,
                    $"The package declares more than {limits.MaxEntries} entries."));
                break;
            }

            var name = entry.FullName;
            var depth = InspectName(name, limits, names, problems);
            maxDepth = Math.Max(maxDepth, depth);
            if (InspectSymbolicLink(entry, name, problems)) continue;

            var length = entry.Length;
            if (length < 0)
            {
                problems.Add(new PackageAdmissionProblem("invalid_entry_size", name,
                    "The entry declares a negative uncompressed size."));
                continue;
            }

            if (length > limits.MaxEntryUncompressedBytes)
            {
                problems.Add(new PackageAdmissionProblem("entry_too_large", name,
                    $"The entry declares more than {limits.MaxEntryUncompressedBytes} uncompressed bytes."));
            }

            if (length > limits.MaxTotalUncompressedBytes - totalBytes)
            {
                problems.Add(new PackageAdmissionProblem("total_too_large", name,
                    $"The package declares more than {limits.MaxTotalUncompressedBytes} uncompressed bytes in total."));
                break;
            }

            totalBytes += length;
        }

        return new PackageAdmissionResult
        {
            Problems = problems,
            EntryCount = entryCount,
            TotalUncompressedBytes = totalBytes,
            MaxObservedPathDepth = maxDepth,
        };
    }

    private static int InspectName(string name, PackageAdmissionLimits limits, HashSet<string> names,
        List<PackageAdmissionProblem> problems)
    {
        if (string.IsNullOrEmpty(name))
        {
            problems.Add(new PackageAdmissionProblem("empty_entry_name", name, "An entry has an empty name."));
            return 0;
        }

        if (name.Any(char.IsControl))
        {
            problems.Add(new PackageAdmissionProblem("control_character_in_name", name,
                "An entry name contains a control character."));
        }

        if (DriveLetter.IsMatch(name) || DrivePrefix.IsMatch(name))
        {
            problems.Add(new PackageAdmissionProblem("drive_letter_path", name,
                "An entry name starts with a drive letter."));
        }

        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            problems.Add(new PackageAdmissionProblem("absolute_path", name,
                "An entry name is absolute."));
        }

        var trimmed = normalized.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            problems.Add(new PackageAdmissionProblem("empty_entry_name", name, "An entry name has no path."));
            return 0;
        }

        var segments = trimmed.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                problems.Add(new PackageAdmissionProblem("empty_path_segment", name,
                    "An entry name contains an empty path segment."));
            }
            else if (segment == "..")
            {
                problems.Add(new PackageAdmissionProblem("parent_traversal", name,
                    "An entry name attempts to leave the package root with '..'."));
            }
            else if (segment == ".")
            {
                problems.Add(new PackageAdmissionProblem("dot_segment", name,
                    "An entry name contains a '.' path segment."));
            }
        }

        if (trimmed.Contains(':'))
        {
            problems.Add(new PackageAdmissionProblem("colon_in_name", name,
                "An entry name contains ':' outside a rejected drive prefix."));
        }

        if (segments.Length > limits.MaxPathDepth)
        {
            problems.Add(new PackageAdmissionProblem("path_too_deep", name,
                $"An entry name is deeper than {limits.MaxPathDepth} path segments."));
        }

        if (!names.Add(trimmed))
        {
            problems.Add(new PackageAdmissionProblem("duplicate_entry_name", name,
                "The package declares the same entry name more than once."));
        }

        return segments.Length;
    }

    private static bool InspectSymbolicLink(ZipArchiveEntry entry, string name,
        List<PackageAdmissionProblem> problems)
    {
        // Unix file type lives in the upper 16 bits of the external attributes. S_IFLNK is
        // 0xA000; a symlink would otherwise leave the package root after a naive extraction.
        var unixMode = (uint)entry.ExternalAttributes >> 16;
        if ((unixMode & 0xF000) != 0xA000) return false;
        problems.Add(new PackageAdmissionProblem("symbolic_link_entry", name,
            "The entry is marked as a symbolic link."));
        return true;
    }

    private static PackageAdmissionResult Failure(string code, string? entryName, string message) => new()
    {
        Problems = new[] { new PackageAdmissionProblem(code, entryName, message) },
        EntryCount = 0,
        TotalUncompressedBytes = 0,
        MaxObservedPathDepth = 0,
    };
}
