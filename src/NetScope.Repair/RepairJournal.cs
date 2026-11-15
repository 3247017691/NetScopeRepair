using System.Security.Cryptography;
using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

public sealed class RepairJournalException : Exception
{
    public RepairJournalException(RepairTransactionRefusalCode code, string message) : base(message) => Code = code;

    public RepairTransactionRefusalCode Code { get; }
}

/// <summary>
/// The authoritative append-only transaction journal. It validates the fixed binary header,
/// checksums and the strictly increasing sequence, keeps every durable record in memory, and
/// refuses to append while a corrupt tail exists until the tail is explicitly truncated to the
/// last complete checksummed record.
/// </summary>
public sealed class RepairJournal : IDisposable
{
    private readonly IRepairJournalDevice _device;
    private readonly List<RepairJournalRecord> _records = new();
    private readonly List<RepairTransactionRefusal> _problems = new();
    private bool _headerValid;
    private bool _tailCorrupt;
    private bool _sequenceGap;
    private long _lastGoodOffset;
    private long _corruptTailLength;

    private RepairJournal(IRepairJournalDevice device)
    {
        _device = device;
    }

    public string Path => _device.CanonicalPath;

    public long Length => _device.Length;

    public bool IsDevelopmentIsolation => _device.IsDevelopmentIsolation;

    public IReadOnlyList<RepairJournalRecord> Records => _records;

    public IReadOnlyList<RepairTransactionRefusal> Problems => _problems;

    public bool HeaderValid => _headerValid;

    public bool HasCorruptTail => _tailCorrupt;

    public bool HasSequenceGap => _sequenceGap;

    public long CorruptTailLength => _corruptTailLength;

    public bool IsUsable => _headerValid && !_tailCorrupt && !_sequenceGap;

    public static RepairJournal Open(IRepairJournalDevice device, DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var journal = new RepairJournal(device);
        journal.Load(createdAtUtc ?? DateTimeOffset.UtcNow);
        return journal;
    }

    private void Load(DateTimeOffset createdAtUtc)
    {
        var bytes = _device.ReadAll();
        if (bytes.Length == 0)
        {
            var journalId = RandomNumberGenerator.GetBytes(16);
            _device.Append(RepairJournalFormat.EncodeHeader(createdAtUtc, journalId));
            bytes = _device.ReadAll();
        }

        if (!RepairJournalFormat.TryReadHeader(bytes, out _, out var headerProblem))
        {
            _problems.Add(RepairTransactionRefusal.Of(
                headerProblem ?? RepairTransactionRefusalCode.JournalCorrupt,
                "The journal header is invalid, truncated or from an unsupported version."));
            _headerValid = false;
            _lastGoodOffset = 0;
            return;
        }

        _headerValid = true;
        var offset = RepairJournalFormat.HeaderLength;
        long expectedSequence = 1;
        _lastGoodOffset = offset;

        while (offset < bytes.Length)
        {
            var remaining = bytes.AsSpan(offset);
            if (!RepairJournalFormat.TryReadRecord(remaining, expectedSequence, out var record, out var problem)
                || record is null)
            {
                _tailCorrupt = true;
                _corruptTailLength = bytes.Length - offset;
                if (problem == RepairTransactionRefusalCode.JournalSequenceGap) _sequenceGap = true;
                _problems.Add(RepairTransactionRefusal.Of(
                    problem ?? RepairTransactionRefusalCode.JournalCorrupt,
                    "The journal tail is incomplete or corrupt; it must be truncated to the last complete record."));
                return;
            }

            _records.Add(record with { Offset = offset });
            offset += record.Length;
            _lastGoodOffset = offset;
            expectedSequence++;
        }
    }

    /// <summary>Appends one entry and makes it durable before returning; sequence is contiguous.</summary>
    public RepairJournalRecord Append(RepairJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsUsable)
        {
            throw new RepairJournalException(
                _tailCorrupt ? RepairTransactionRefusalCode.JournalCorrupt
                : _sequenceGap ? RepairTransactionRefusalCode.JournalSequenceGap
                : RepairTransactionRefusalCode.JournalVersionUnsupported,
                "The journal is not usable until its problems are resolved.");
        }

        var sequence = _records.Count + 1;
        var offset = _lastGoodOffset;
        var bytes = RepairJournalFormat.EncodeRecord(sequence, entry);
        try
        {
            _device.Append(bytes);
        }
        catch (RepairJournalDeviceException exception)
        {
            throw new RepairJournalException(MapFailure(exception), "The journal append could not be made durable.");
        }

        var record = new RepairJournalRecord
        {
            Sequence = sequence,
            Entry = entry,
            Offset = offset,
            Length = bytes.Length,
        };
        _records.Add(record);
        _lastGoodOffset = offset + bytes.Length;
        return record;
    }

    /// <summary>
    /// Truncates only unreadable tail bytes, never a complete record. The bad intent is reported,
    /// not silently repaired into a plausible record.
    /// </summary>
    public long TruncateCorruptTail()
    {
        if (!_tailCorrupt) return 0;
        var truncated = _corruptTailLength;
        try
        {
            _device.Truncate(_lastGoodOffset);
        }
        catch (RepairJournalDeviceException exception)
        {
            throw new RepairJournalException(MapFailure(exception), "The corrupt journal tail could not be truncated.");
        }

        _tailCorrupt = false;
        _corruptTailLength = 0;
        return truncated;
    }

    public IReadOnlyList<RepairJournalRecord> EntriesOf(string transactionId) => _records
        .Where(record => string.Equals(record.Entry.TransactionId, transactionId, StringComparison.Ordinal))
        .ToArray();

    /// <summary>The last recorded state for one transaction, or null when it never reached the journal.</summary>
    public RepairTransactionState? LatestState(string transactionId)
    {
        var last = _records.LastOrDefault(record =>
            string.Equals(record.Entry.TransactionId, transactionId, StringComparison.Ordinal));
        return last?.Entry.State;
    }

    /// <summary>Distinct transactions whose latest state is not terminal, oldest first.</summary>
    public IReadOnlyList<string> UnresolvedTransactionIds()
    {
        var result = new List<string>();
        foreach (var transactionId in _records
                     .Select(record => record.Entry.TransactionId)
                     .Distinct(StringComparer.Ordinal))
        {
            var state = LatestState(transactionId);
            // recovery_required is terminal for the state machine but still needs a user-confirmed
            // recovery, so it must keep blocking a second transaction.
            if (state is { } value
                && (!RepairTransactionStateRules.IsTerminal(value)
                    || value == RepairTransactionState.RecoveryRequired))
                result.Add(transactionId);
        }

        return result;
    }

    public static RepairTransactionRefusalCode MapFailure(RepairJournalDeviceException exception) => exception.Failure switch
    {
        RepairJournalDeviceFailure.LockUnavailable => RepairTransactionRefusalCode.JournalLocked,
        RepairJournalDeviceFailure.ReadFailure => RepairTransactionRefusalCode.JournalCorrupt,
        RepairJournalDeviceFailure.FlushFailure => RepairTransactionRefusalCode.JournalFlushFailed,
        _ => RepairTransactionRefusalCode.JournalFlushFailed,
    };

    public void Dispose() => _device.Dispose();
}

/// <summary>Directory observations used before the journal is opened. Paths are never user-chosen.</summary>
public sealed record RepairJournalDirectoryFacts
{
    public required string RootPath { get; init; }

    public required bool Exists { get; init; }

    public required bool IsReparsePoint { get; init; }

    /// <summary>True when the initiating user can create/replace files in the fixed directory.</summary>
    public required bool IsUserWritable { get; init; }

    public required bool IsSignedDeployment { get; init; }

    public required bool IsElevated { get; init; }

    /// <summary>Explicitly requested development isolation for tests.</summary>
    public required bool DevelopmentIsolation { get; init; }
}

public sealed record RepairJournalDirectoryDecision
{
    public required bool Allowed { get; init; }

    /// <summary>True for an explicitly requested test/development directory. Never a production boundary.</summary>
    public required bool IsDevelopmentIsolation { get; init; }

    public required string RootPath { get; init; }

    public required IReadOnlyList<RepairTransactionRefusal> Refusals { get; init; }
}

/// <summary>
/// Fixed transaction directory policy. Production requires the fixed ProgramData root to exist,
/// not be a reparse point, not be user-writable, and the deployment to be signed and elevated.
/// A development root is allowed only when isolation is explicitly requested, is clearly marked
/// synthetic, and can never satisfy the production rule.
/// </summary>
public sealed record RepairJournalDirectoryPolicy
{
    public required string ProductionRoot { get; init; }

    public string? DevelopmentRoot { get; init; }

    public required bool AllowDevelopmentIsolation { get; init; }

    public static RepairJournalDirectoryPolicy Design05 { get; } = new()
    {
        ProductionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetScopeRepair",
            "Transactions"),
        DevelopmentRoot = null,
        AllowDevelopmentIsolation = false,
    };

    public RepairJournalDirectoryDecision Evaluate(RepairJournalDirectoryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var refusals = new List<RepairTransactionRefusal>();
        var canonical = SafeFullPath(facts.RootPath);
        if (canonical is null)
        {
            return new RepairJournalDirectoryDecision
            {
                Allowed = false,
                IsDevelopmentIsolation = false,
                RootPath = facts.RootPath,
                Refusals = new[]
                {
                    RepairTransactionRefusal.Of(
                        RepairTransactionRefusalCode.JournalDirectoryNotFixed,
                        "The transaction directory path is not a valid absolute path."),
                },
            };
        }

        var productionRoot = SafeFullPath(ProductionRoot);
        var isProductionRoot = productionRoot is not null
            && string.Equals(canonical, productionRoot, StringComparison.OrdinalIgnoreCase);

        if (facts.IsReparsePoint)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalDirectoryReparsePoint,
                "The transaction directory is a reparse point and cannot be trusted as a fixed path."));
        }

        if (!facts.Exists)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalDirectoryNotFixed,
                "The fixed transaction directory does not exist."));
        }

        var production = isProductionRoot
            && facts.Exists
            && !facts.IsReparsePoint
            && !facts.IsUserWritable
            && facts.IsSignedDeployment
            && facts.IsElevated;
        if (production)
        {
            return new RepairJournalDirectoryDecision
            {
                Allowed = true,
                IsDevelopmentIsolation = false,
                RootPath = canonical,
                Refusals = Array.Empty<RepairTransactionRefusal>(),
            };
        }

        if (!facts.IsUserWritable && isProductionRoot)
        {
            // Reported below; keep the specific reason visible instead of a generic refusal.
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalCapabilityClosed,
                "The signed, elevated, ACL-protected deployment required for a production journal is not available."));
        }
        else if (facts.IsUserWritable)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalDirectoryUserWritable,
                "The journal directory is writable by the initiating user; it is not a production boundary."));
        }

        var developmentRoot = DevelopmentRoot is null ? null : SafeFullPath(DevelopmentRoot);
        var development = AllowDevelopmentIsolation
            && facts.DevelopmentIsolation
            && developmentRoot is not null
            && string.Equals(canonical, developmentRoot, StringComparison.OrdinalIgnoreCase)
            && facts.Exists
            && !facts.IsReparsePoint;

        if (development)
        {
            return new RepairJournalDirectoryDecision
            {
                Allowed = true,
                IsDevelopmentIsolation = true,
                RootPath = canonical,
                Refusals = new[]
                {
                    RepairTransactionRefusal.Of(
                        RepairTransactionRefusalCode.JournalCapabilityClosed,
                        "Development isolation: the transaction directory is user-writable and must never be treated as a production boundary."),
                },
            };
        }

        return new RepairJournalDirectoryDecision
        {
            Allowed = false,
            IsDevelopmentIsolation = false,
            RootPath = canonical,
            Refusals = refusals.Count > 0
                ? refusals
                : new[]
                {
                    RepairTransactionRefusal.Of(
                        RepairTransactionRefusalCode.JournalCapabilityClosed,
                        "No fixed, ACL-protected, signed transaction directory is available for this deployment."),
                },
        };
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>Real filesystem probe: fixed root, reparse point and user-writable ACL checks.</summary>
public interface IRepairJournalPathProbe
{
    RepairJournalDirectoryFacts Inspect(string rootPath, bool developmentIsolation);
}

public sealed class RepairWindowsJournalPathProbe : IRepairJournalPathProbe
{
    private readonly Func<string, bool> _isUserWritable;
    private readonly Func<bool> _isElevated;

    public RepairWindowsJournalPathProbe(Func<string, bool>? isUserWritable = null, Func<bool>? isElevated = null)
    {
        _isUserWritable = isUserWritable ?? BrokenDevelopmentWritableProbe;
        _isElevated = isElevated ?? (() => false);
    }

    public RepairJournalDirectoryFacts Inspect(string rootPath, bool developmentIsolation)
    {
        var exists = Directory.Exists(rootPath);
        var isReparse = false;
        try
        {
            if (exists)
                isReparse = (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            isReparse = true;
        }

        return new RepairJournalDirectoryFacts
        {
            RootPath = rootPath,
            Exists = exists,
            IsReparsePoint = isReparse,
            IsUserWritable = exists && _isUserWritable(rootPath),
            IsSignedDeployment = false,
            IsElevated = _isElevated(),
            DevelopmentIsolation = developmentIsolation,
        };
    }

    /// <summary>Without a signed/ACL deployment the writable check is conservative: assume writable.</summary>
    private static bool BrokenDevelopmentWritableProbe(string _) => true;
}

