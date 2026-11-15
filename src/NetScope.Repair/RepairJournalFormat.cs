using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>Journal record kinds from design 05 §5/§6. The set is closed; unknown kinds are corrupt.</summary>
[JsonConverter(typeof(WireEnumJsonConverter<RepairJournalKind>))]
public enum RepairJournalKind
{
    [JsonStringEnumMemberName("transaction_planned")]
    TransactionPlanned,

    [JsonStringEnumMemberName("transaction_authorized")]
    TransactionAuthorized,

    [JsonStringEnumMemberName("transaction_prepared")]
    TransactionPrepared,

    [JsonStringEnumMemberName("step_intent")]
    StepIntent,

    [JsonStringEnumMemberName("step_applied")]
    StepApplied,

    [JsonStringEnumMemberName("observation_started")]
    ObservationStarted,

    [JsonStringEnumMemberName("transaction_committed")]
    TransactionCommitted,

    [JsonStringEnumMemberName("transaction_aborted")]
    TransactionAborted,

    [JsonStringEnumMemberName("rollback_intent")]
    RollbackIntent,

    [JsonStringEnumMemberName("rollback_applied")]
    RollbackApplied,

    [JsonStringEnumMemberName("transaction_rolled_back")]
    TransactionRolledBack,

    [JsonStringEnumMemberName("transaction_rollback_conflict")]
    TransactionRollbackConflict,

    [JsonStringEnumMemberName("transaction_rollback_failed")]
    TransactionRollbackFailed,

    [JsonStringEnumMemberName("transaction_recovery_required")]
    TransactionRecoveryRequired,

    [JsonStringEnumMemberName("transaction_no_op")]
    TransactionNoOp,

    [JsonStringEnumMemberName("transaction_state_changed")]
    TransactionStateChanged,
}

/// <summary>
/// Flat, typed value form stored in the journal. It keeps the canonical text used by the plan plus
/// the raw typed fields needed to reconstruct a restore value without polymorphic JSON.
/// </summary>
public sealed record RepairJournalValue
{
    public required string Canonical { get; init; }

    public required RepairField Field { get; init; }

    public MetricValueKind? MetricKind { get; init; }

    public uint? MetricRaw { get; init; }

    public bool? Enabled { get; init; }

    public bool? Ignored { get; init; }

    public static RepairJournalValue From(RepairFieldValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            InterfaceMetricValue metric => new RepairJournalValue
            {
                Canonical = metric.CanonicalText,
                Field = RepairField.InterfaceMetric,
                MetricKind = metric.Kind,
                MetricRaw = metric.RawValue,
            },
            AutomaticMetricValue automatic => new RepairJournalValue
            {
                Canonical = automatic.CanonicalText,
                Field = RepairField.AutomaticMetric,
                Enabled = automatic.Enabled,
            },
            IgnoreDefaultRoutesValue ignore => new RepairJournalValue
            {
                Canonical = ignore.CanonicalText,
                Field = RepairField.IgnoreDefaultRoutes,
                Ignored = ignore.Ignore,
            },
            _ => throw new ArgumentException("The field value type has no journal form.", nameof(value)),
        };
    }

    /// <summary>Rebuilds the typed value; the canonical text must match exactly or the record is bad.</summary>
    public RepairFieldValue ToValue()
    {
        RepairFieldValue value = Field switch
        {
            RepairField.InterfaceMetric => new InterfaceMetricValue
            {
                RawValue = MetricRaw ?? throw new ArgumentException("journal metric value is missing raw_value"),
                Kind = MetricKind ?? throw new ArgumentException("journal metric value is missing kind"),
            },
            RepairField.AutomaticMetric => new AutomaticMetricValue
            {
                Enabled = Enabled ?? throw new ArgumentException("journal automatic metric value is missing enabled"),
            },
            _ => new IgnoreDefaultRoutesValue
            {
                Ignore = Ignored ?? throw new ArgumentException("journal ignore-default-routes value is missing ignore"),
            },
        };

        if (!string.Equals(value.CanonicalText, Canonical, StringComparison.Ordinal))
            throw new ArgumentException("journal value canonical text does not match its typed fields");
        return value;
    }
}

/// <summary>The frozen target identity recorded before any write.</summary>
public sealed record RepairJournalTarget
{
    public required Guid InterfaceGuid { get; init; }

    public required ulong? Luid { get; init; }

    public required int? IfIndex { get; init; }

    public required RepairAddressFamily Family { get; init; }

    public required int? CompartmentId { get; init; }

    public required string? DeviceInstanceId { get; init; }

    public required string BootId { get; init; }

    public required string TargetFingerprint { get; init; }

    public static RepairJournalTarget From(InterfaceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new RepairJournalTarget
        {
            InterfaceGuid = identity.InterfaceGuid,
            Luid = identity.Luid,
            IfIndex = identity.IfIndex,
            Family = identity.Family,
            CompartmentId = identity.CompartmentId,
            DeviceInstanceId = identity.DeviceInstanceId,
            BootId = identity.BootId,
            TargetFingerprint = identity.TargetFingerprint,
        };
    }

    /// <summary>True when the current identity is the same interface/family/boot/device instance.</summary>
    public bool Matches(InterfaceIdentity current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return InterfaceGuid == current.InterfaceGuid
            && Family == current.Family
            && CompartmentId == current.CompartmentId
            && string.Equals(DeviceInstanceId, current.DeviceInstanceId, StringComparison.Ordinal)
            && string.Equals(BootId, current.BootId, StringComparison.Ordinal);
    }
}

/// <summary>One planned step with its full before/after values, persisted before any write.</summary>
public sealed record RepairJournalPlannedStep
{
    public required int Index { get; init; }

    public required string ActionId { get; init; }

    public required RepairActionKind Kind { get; init; }

    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required RepairAddressFamily Family { get; init; }

    public required RepairJournalValue Before { get; init; }

    public required RepairJournalValue After { get; init; }

    public IReadOnlyList<string> DependsOnActionIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One durable journal entry. All sections are flat and optional; which sections are required is
/// fixed by <see cref="Kind"/> and enforced by the journal reader.
/// </summary>
public sealed record RepairJournalEntry
{
    public required string TransactionId { get; init; }

    public required RepairJournalKind Kind { get; init; }

    public required RepairTransactionState State { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    // Transaction identity, present on the planned/authorized/prepared entries.
    public string? PlanId { get; init; }

    public string? PlanDigest { get; init; }

    public string? AuthorizationDigest { get; init; }

    public string? UserSid { get; init; }

    public string? LogonSessionId { get; init; }

    public RepairJournalTarget? Target { get; init; }

    public int? ObservationWindowMs { get; init; }

    public IReadOnlyList<RepairJournalPlannedStep>? PlannedSteps { get; init; }

    // Step identity, present on intent/applied/rollback entries.
    public int? StepIndex { get; init; }

    public string? ActionId { get; init; }

    public RepairField? Field { get; init; }

    public RepairJournalValue? BeforeValue { get; init; }

    public RepairJournalValue? AfterValue { get; init; }

    public RepairJournalValue? ReadbackValue { get; init; }

    /// <summary>For rollback records: the field already held its before value, so no write happened.</summary>
    public bool? AlreadyRestored { get; init; }

    // State transition and reason, present on state-change entries.
    public RepairTransactionState? FromState { get; init; }

    public RepairTransactionRefusalCode? RefusalCode { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Serves as both the wire-framing envelope and the parsed record.</summary>
public sealed record RepairJournalRecord
{
    public required long Sequence { get; init; }

    public required RepairJournalEntry Entry { get; init; }

    public required long Offset { get; init; }

    public required int Length { get; init; }
}

/// <summary>Strict JSON for journal payloads: unknown members, bad enums and deep nesting are corrupt.</summary>
public static class RepairJournalJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
        WriteIndented = false,
    };
}

/// <summary>
/// Binary journal format: a fixed header plus a sequence of
/// <c>magic | version | kind | sequence | payloadLength | payload | checksum</c> records. The
/// checksum covers the header fields and payload, so a torn write is detectable and truncatable;
/// the sequence makes a missing record explicit instead of silent.
/// </summary>
public static class RepairJournalFormat
{
    public static ReadOnlySpan<byte> FileMagic => "NSRJTXN1"u8;

    public const int Version = 1;
    public const int HeaderLength = 36;
    public const int RecordOverhead = 36;
    public const int MaxPayloadBytes = 256 * 1024;
    private const int ChecksumBytes = 16;
    private const uint RecordMagic = 0x4E534A52; // 'NSJR'

    public static byte[] EncodeHeader(DateTimeOffset createdAtUtc, byte[] journalId)
    {
        ArgumentNullException.ThrowIfNull(journalId);
        if (journalId.Length != 16) throw new ArgumentException("journal id must be 16 bytes", nameof(journalId));

        var buffer = new byte[HeaderLength];
        FileMagic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), 0);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(12), createdAtUtc.ToUniversalTime().UtcTicks);
        journalId.CopyTo(buffer.AsSpan(20));
        Checksum(buffer.AsSpan(0, 20)).CopyTo(buffer.AsSpan(20));
        return buffer;
    }

    public static bool TryReadHeader(
        ReadOnlySpan<byte> data, out DateTimeOffset createdAtUtc, out RepairTransactionRefusalCode? problem)
    {
        createdAtUtc = default;
        problem = null;
        if (data.Length < HeaderLength
            || !data[..8].SequenceEqual(FileMagic)
            || !Checksum(data[..20]).SequenceEqual(data.Slice(20, ChecksumBytes)))
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (version != Version)
        {
            problem = RepairTransactionRefusalCode.JournalVersionUnsupported;
            return false;
        }

        createdAtUtc = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(data[12..]), TimeSpan.Zero);
        return true;
    }

    public static byte[] EncodeRecord(long sequence, RepairJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, RepairJournalJson.Options);
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentException("journal payload exceeds the bounded record size", nameof(entry));

        var record = new byte[RecordOverhead + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, RecordMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)entry.Kind);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), payload.Length);
        payload.CopyTo(record.AsSpan(20));
        Checksum(record.AsSpan(4, 16 + payload.Length)).CopyTo(record.AsSpan(20 + payload.Length));
        return record;
    }

    public static bool TryReadRecord(
        ReadOnlySpan<byte> data, long expectedSequence, out RepairJournalRecord? record,
        out RepairTransactionRefusalCode? problem)
    {
        record = null;
        problem = null;
        if (data.Length < RecordOverhead)
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != RecordMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != Version)
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        var kindValue = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (!Enum.IsDefined(typeof(RepairJournalKind), (int)kindValue))
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes
            || data.Length < RecordOverhead + payloadLength)
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        if (sequence != expectedSequence)
        {
            problem = RepairTransactionRefusalCode.JournalSequenceGap;
            return false;
        }

        var expected = Checksum(data.Slice(4, 16 + payloadLength));
        if (!expected.SequenceEqual(data.Slice(20 + payloadLength, ChecksumBytes)))
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        RepairJournalEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<RepairJournalEntry>(
                data.Slice(20, payloadLength), RepairJournalJson.Options);
        }
        catch (JsonException)
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        if (entry is null || entry.Kind != (RepairJournalKind)kindValue || entry.TransactionId is null)
        {
            problem = RepairTransactionRefusalCode.JournalCorrupt;
            return false;
        }

        record = new RepairJournalRecord
        {
            Sequence = sequence,
            Entry = entry,
            Offset = 0,
            Length = RecordOverhead + payloadLength,
        };
        return true;
    }

    private static byte[] Checksum(ReadOnlySpan<byte> data) =>
        SHA256.HashData(data)[..ChecksumBytes];

    public static string FormatSequence(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Where journal bytes live. Implementations must make Append durable before returning.</summary>
public interface IRepairJournalDevice : IDisposable
{
    string CanonicalPath { get; }

    long Length { get; }

    bool HoldsExclusiveLock { get; }

    byte[] ReadAll();

    /// <summary>Append bytes and flush to durable storage. Throws <see cref="RepairJournalDeviceException"/>.</summary>
    void Append(byte[] data);

    /// <summary>Truncate and flush to durable storage.</summary>
    void Truncate(long length);

    /// <summary>Designation for development isolation; production journal devices must be false.</summary>
    bool IsDevelopmentIsolation { get; }
}

public enum RepairJournalDeviceFailure
{
    LockUnavailable,
    ReadFailure,
    WriteFailure,
    FlushFailure,
}

public sealed class RepairJournalDeviceException : Exception
{
    public RepairJournalDeviceException(RepairJournalDeviceFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    public RepairJournalDeviceFailure Failure { get; }
}

/// <summary>
/// Real file-backed journal. It opens with an exclusive share mode (a second broker instance is
/// refused), writes through the OS cache and flushes with <c>Flush(true)</c> so a returned append
/// is durable. It is only reachable through a journal directory decision that allows it.
/// </summary>
public sealed class RepairFileJournalDevice : IRepairJournalDevice
{
    private readonly FileStream _stream;
    private readonly bool _developmentIsolation;

    private RepairFileJournalDevice(FileStream stream, string canonicalPath, bool developmentIsolation)
    {
        _stream = stream;
        CanonicalPath = canonicalPath;
        _developmentIsolation = developmentIsolation;
    }

    public string CanonicalPath { get; }

    public long Length => _stream.Length;

    public bool HoldsExclusiveLock => true;

    public bool IsDevelopmentIsolation => _developmentIsolation;

    public static RepairFileJournalDevice Open(string path, bool developmentIsolation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        try
        {
            var stream = new FileStream(
                canonical, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 16 * 1024, FileOptions.WriteThrough);
            return new RepairFileJournalDevice(stream, canonical, developmentIsolation);
        }
        catch (IOException exception)
        {
            throw new RepairJournalDeviceException(
                RepairJournalDeviceFailure.LockUnavailable,
                "The journal is already locked by another instance.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new RepairJournalDeviceException(
                RepairJournalDeviceFailure.LockUnavailable,
                "The journal cannot be opened for exclusive write.", exception);
        }
    }

    public byte[] ReadAll()
    {
        try
        {
            var length = checked((int)_stream.Length);
            var buffer = new byte[length];
            _stream.Position = 0;
            var offset = 0;
            while (offset < length)
            {
                var read = _stream.Read(buffer, offset, length - offset);
                if (read <= 0) break;
                offset += read;
            }

            return offset == length ? buffer : buffer[..offset];
        }
        catch (IOException exception)
        {
            throw new RepairJournalDeviceException(
                RepairJournalDeviceFailure.ReadFailure, "The journal could not be read.", exception);
        }
    }

    public void Append(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            _stream.Position = _stream.Length;
            _stream.Write(data, 0, data.Length);
            _stream.Flush(flushToDisk: true);
        }
        catch (IOException exception)
        {
            throw new RepairJournalDeviceException(
                RepairJournalDeviceFailure.FlushFailure, "The journal append could not be made durable.", exception);
        }
    }

    public void Truncate(long length)
    {
        try
        {
            _stream.SetLength(length);
            _stream.Flush(flushToDisk: true);
        }
        catch (IOException exception)
        {
            throw new RepairJournalDeviceException(
                RepairJournalDeviceFailure.FlushFailure, "The journal truncation could not be made durable.", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}

