using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Broker;

/// <summary>
/// Frozen protocol constants from design 05 §3. Every value here is a hard bound: a frame or a
/// session that exceeds it is rejected, never clamped into an accepted request.
/// </summary>
public static class BrokerProtocol
{
    public const string Version = "1.0";

    /// <summary>Largest accepted frame body, excluding the 4-byte big-endian length prefix.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>At most 16 actions in one transaction (shared with the T13 plan limit).</summary>
    public const int MaxActionsPerTransaction = RepairLimits.MaxActionsPerPlan;

    /// <summary>Hello to authenticated: one handshake attempt only, no retries.</summary>
    public static TimeSpan HandshakeTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>No frame within this window closes the session.</summary>
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Whole transaction budget; a normal budget, not a hard guarantee of recovery.</summary>
    public static TimeSpan TotalTransactionBudget { get; } = TimeSpan.FromSeconds(180);

    /// <summary>The technical observation window is capped at 60 seconds inside the budget.</summary>
    public static TimeSpan MaxTechnicalObservation { get; } = TimeSpan.FromSeconds(60);

    /// <summary>At least 60 seconds must stay reserved for compensation after the forward window.</summary>
    public static TimeSpan CompensationReserve { get; } = TimeSpan.FromSeconds(60);

    /// <summary>120 seconds: the latest point at which a new forward action or observation may start.</summary>
    public static TimeSpan LatestForwardActionStart { get; } = TotalTransactionBudget - CompensationReserve;

    /// <summary>A broker nonce is bound to one handshake and expires quickly even if unused.</summary>
    public static TimeSpan NonceLifetime { get; } = TimeSpan.FromSeconds(60);

    /// <summary>32 random bytes = 256 bits, hex encoded on the wire.</summary>
    public const int NonceBytes = 32;

    public const int NonceHexLength = NonceBytes * 2;
}

/// <summary>
/// The complete set of IPC message kinds. There is deliberately no "execute command",
/// "set property" or "run script" message: a widening would require a new protocol version.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<BrokerMessageKind>))]
public enum BrokerMessageKind
{
    [JsonStringEnumMemberName("hello")]
    Hello,

    [JsonStringEnumMemberName("challenge")]
    Challenge,

    [JsonStringEnumMemberName("authenticate")]
    Authenticate,

    [JsonStringEnumMemberName("submit_transaction")]
    SubmitTransaction,

    [JsonStringEnumMemberName("transaction_result")]
    TransactionResult,

    [JsonStringEnumMemberName("cancel")]
    Cancel,

    [JsonStringEnumMemberName("close")]
    Close,

    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>
/// Stable broker refusal codes. They are never collapsed into one generic failure: tests assert
/// the code, and the UI translates it. No code carries user data or paths.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<BrokerRefusalCode>))]
public enum BrokerRefusalCode
{
    [JsonStringEnumMemberName("protocol_version_mismatch")]
    ProtocolVersionMismatch,

    [JsonStringEnumMemberName("unknown_message_kind")]
    UnknownMessageKind,

    [JsonStringEnumMemberName("frame_too_large")]
    FrameTooLarge,

    [JsonStringEnumMemberName("frame_empty")]
    FrameEmpty,

    [JsonStringEnumMemberName("invalid_payload")]
    InvalidPayload,

    [JsonStringEnumMemberName("unexpected_message")]
    UnexpectedMessage,

    [JsonStringEnumMemberName("invalid_identifier")]
    InvalidIdentifier,

    [JsonStringEnumMemberName("handshake_timeout")]
    HandshakeTimeout,

    [JsonStringEnumMemberName("handshake_rejected")]
    HandshakeRejected,

    [JsonStringEnumMemberName("idle_timeout")]
    IdleTimeout,

    [JsonStringEnumMemberName("total_budget_exhausted")]
    TotalBudgetExhausted,

    [JsonStringEnumMemberName("forward_window_closed")]
    ForwardWindowClosed,

    [JsonStringEnumMemberName("nonce_missing")]
    NonceMissing,

    [JsonStringEnumMemberName("nonce_mismatch")]
    NonceMismatch,

    [JsonStringEnumMemberName("nonce_replayed")]
    NonceReplayed,

    [JsonStringEnumMemberName("nonce_expired")]
    NonceExpired,

    [JsonStringEnumMemberName("peer_remote_client")]
    PeerRemoteClient,

    [JsonStringEnumMemberName("peer_identity_unknown")]
    PeerIdentityUnknown,

    [JsonStringEnumMemberName("peer_user_mismatch")]
    PeerUserMismatch,

    [JsonStringEnumMemberName("peer_session_mismatch")]
    PeerSessionMismatch,

    [JsonStringEnumMemberName("peer_pid_mismatch")]
    PeerPidMismatch,

    [JsonStringEnumMemberName("peer_creation_time_mismatch")]
    PeerCreationTimeMismatch,

    [JsonStringEnumMemberName("peer_path_untrusted")]
    PeerPathUntrusted,

    [JsonStringEnumMemberName("peer_signature_untrusted")]
    PeerSignatureUntrusted,

    [JsonStringEnumMemberName("peer_parent_untrusted")]
    PeerParentUntrusted,

    [JsonStringEnumMemberName("pipe_not_first_instance")]
    PipeNotFirstInstance,

    [JsonStringEnumMemberName("pipe_name_not_random")]
    PipeNameNotRandom,

    [JsonStringEnumMemberName("trusted_deployment_unavailable")]
    TrustedDeploymentUnavailable,

    [JsonStringEnumMemberName("broker_not_elevated")]
    BrokerNotElevated,

    [JsonStringEnumMemberName("signature_not_trusted")]
    SignatureNotTrusted,

    [JsonStringEnumMemberName("install_directory_user_writable")]
    InstallDirectoryUserWritable,

    [JsonStringEnumMemberName("development_isolation_only")]
    DevelopmentIsolationOnly,

    [JsonStringEnumMemberName("real_elevation_disabled")]
    RealElevationDisabled,

    [JsonStringEnumMemberName("execution_not_implemented")]
    ExecutionNotImplemented,

    [JsonStringEnumMemberName("journal_unavailable")]
    JournalUnavailable,

    [JsonStringEnumMemberName("journal_corrupt")]
    JournalCorrupt,

    [JsonStringEnumMemberName("execution_write_failed")]
    ExecutionWriteFailed,

    [JsonStringEnumMemberName("execution_observation_failed")]
    ExecutionObservationFailed,

    [JsonStringEnumMemberName("execution_rollback_conflict")]
    ExecutionRollbackConflict,

    [JsonStringEnumMemberName("execution_rollback_failed")]
    ExecutionRollbackFailed,

    [JsonStringEnumMemberName("execution_recovery_required")]
    ExecutionRecoveryRequired,

    [JsonStringEnumMemberName("synthetic_plan_not_executable")]
    SyntheticPlanNotExecutable,

    [JsonStringEnumMemberName("transaction_journal_not_implemented")]
    TransactionJournalNotImplemented,

    [JsonStringEnumMemberName("plan_digest_mismatch")]
    PlanDigestMismatch,

    [JsonStringEnumMemberName("plan_not_executable")]
    PlanNotExecutable,

    [JsonStringEnumMemberName("plan_expired")]
    PlanExpired,

    [JsonStringEnumMemberName("authorization_mismatch")]
    AuthorizationMismatch,

    [JsonStringEnumMemberName("consent_missing")]
    ConsentMissing,

    [JsonStringEnumMemberName("capability_unavailable")]
    CapabilityUnavailable,

    [JsonStringEnumMemberName("capability_disallowed")]
    CapabilityDisallowed,

    [JsonStringEnumMemberName("action_not_allowed")]
    ActionNotAllowed,

    [JsonStringEnumMemberName("action_field_mismatch")]
    ActionFieldMismatch,

    [JsonStringEnumMemberName("store_not_supported")]
    StoreNotSupported,

    [JsonStringEnumMemberName("family_not_supported")]
    FamilyNotSupported,

    [JsonStringEnumMemberName("no_actions")]
    NoActions,

    [JsonStringEnumMemberName("action_value_invalid")]
    ActionValueInvalid,

    [JsonStringEnumMemberName("action_limit_exceeded")]
    ActionLimitExceeded,

    [JsonStringEnumMemberName("duplicate_action")]
    DuplicateAction,

    [JsonStringEnumMemberName("target_mismatch")]
    TargetMismatch,

    [JsonStringEnumMemberName("recover_requires_journal")]
    RecoverRequiresJournal,

    [JsonStringEnumMemberName("preflight_unavailable")]
    PreflightUnavailable,

    [JsonStringEnumMemberName("preflight_mismatch")]
    PreflightMismatch,

    [JsonStringEnumMemberName("requires_new_preview")]
    RequiresNewPreview,

    [JsonStringEnumMemberName("disconnected")]
    Disconnected,

    [JsonStringEnumMemberName("session_closed")]
    SessionClosed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("internal_error")]
    InternalError,
}

/// <summary>One refusal with a stable code and a short, non-identifying detail.</summary>
public sealed record BrokerRefusal
{
    public required BrokerRefusalCode Code { get; init; }

    public required string Detail { get; init; }

    public static BrokerRefusal Of(BrokerRefusalCode code, string detail) => new() { Code = code, Detail = detail };
}

/// <summary>One IPC frame envelope. The payload is parsed per kind, never into an arbitrary type.</summary>
public sealed record BrokerFrame
{
    public required string ProtocolVersion { get; init; }

    public required BrokerMessageKind Kind { get; init; }

    public required string MessageId { get; init; }

    public JsonElement? Payload { get; init; }
}

public sealed record BrokerHelloMessage
{
    public required string ProtocolVersion { get; init; }

    /// <summary>32 random bytes chosen by the client, hex encoded. Never logged.</summary>
    public required string ClientNonce { get; init; }

    public required int ClientProcessId { get; init; }

    public required DateTimeOffset ClientProcessCreationUtc { get; init; }
}

public sealed record BrokerChallengeMessage
{
    public required string ProtocolVersion { get; init; }

    /// <summary>32 random bytes chosen by the broker, hex encoded. Never logged.</summary>
    public required string BrokerNonce { get; init; }

    public required DateTimeOffset IssuedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>
/// Binding of both challenges, the transaction id and the plan digest into one one-time value.
/// The binding is a correlation/replay defence; identity comes from the pipe and process checks.
/// </summary>
public sealed record BrokerAuthenticateMessage
{
    public required string BrokerNonce { get; init; }

    public required string ClientNonce { get; init; }

    public required string TransactionId { get; init; }

    public required string PlanDigest { get; init; }

    public required string NonceBinding { get; init; }
}

public sealed record BrokerSubmitTransactionMessage
{
    public required string TransactionId { get; init; }

    public required string PlanDigest { get; init; }

    public required string TargetFingerprint { get; init; }

    /// <summary>The typed T13 plan. Nothing in it can execute a write; it is data to validate.</summary>
    public required RepairPlan Plan { get; init; }

    public required RepairAuthorizationBinding Authorization { get; init; }
}

public sealed record BrokerCancelMessage
{
    public required string TransactionId { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The broker's answer to a submitted transaction. "accepted" means the protocol/identity/plan
/// validation passed; it never means a privileged write happened.
/// </summary>
public sealed record BrokerTransactionResultMessage
{
    public required string TransactionId { get; init; }

    public required string PlanDigest { get; init; }

    public required bool Accepted { get; init; }

    /// <summary>Always false in this delivery: no privileged execution path exists.</summary>
    public required bool Executed { get; init; }

    public required bool RequiresNewPreview { get; init; }

    public IReadOnlyList<BrokerRefusalCode> Codes { get; init; } = Array.Empty<BrokerRefusalCode>();
}

public sealed record BrokerErrorMessage
{
    public required BrokerRefusalCode Code { get; init; }

    public required string Detail { get; init; }

    public IReadOnlyList<BrokerRefusalCode> Codes { get; init; } = Array.Empty<BrokerRefusalCode>();
}

/// <summary>
/// Strict serialization for every frame. Unknown members are rejected globally; get-only computed
/// properties are not written to the wire, so a plan cannot smuggle derived state.
/// </summary>
public static class BrokerJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 32,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveComputedProperties } },
        };
        options.Converters.Add(new BrokerFieldValueJsonConverter());
        return options;
    }

    private static void RemoveComputedProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        foreach (var property in info.Properties.Where(property => property.Set is null).ToArray())
        {
            info.Properties.Remove(property);
        }
    }
}

/// <summary>
/// Discriminated, closed-set wire form of <see cref="RepairFieldValue"/>. Unknown fields, unknown
/// field names and values of the wrong type are rejected at deserialization instead of guessed.
/// </summary>
public sealed class BrokerFieldValueJsonConverter : JsonConverter<RepairFieldValue>
{
    public override RepairFieldValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A field value must be an object.");

        string? field = null;
        var names = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            names.Add(property.Name);
            if (property.Name == "field")
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new JsonException("field value 'field' must be a string.");
                field = property.Value.GetString();
            }
        }

        switch (field)
        {
            case "interface_metric":
                RequireExactMembers(names, "field", "raw_value", "kind");
                if (!root.TryGetProperty("raw_value", out var rawElement))
                    throw new JsonException("interface_metric requires 'raw_value'.");
                if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                    throw new JsonException("interface_metric requires a string 'kind'.");
                uint raw;
                try
                {
                    raw = rawElement.GetUInt32();
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException)
                {
                    throw new JsonException("interface_metric raw_value must be an unsigned 32-bit integer.");
                }

                var kind = kindElement.Deserialize<MetricValueKind>(BrokerJson.Options);
                return new InterfaceMetricValue { RawValue = raw, Kind = kind };

            case "automatic_metric":
                RequireExactMembers(names, "field", "enabled");
                if (!root.TryGetProperty("enabled", out var enabledElement)
                    || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("automatic_metric requires a boolean 'enabled'.");
                return new AutomaticMetricValue { Enabled = enabledElement.GetBoolean() };

            case "ignore_default_routes":
                RequireExactMembers(names, "field", "ignore");
                if (!root.TryGetProperty("ignore", out var ignoreElement)
                    || ignoreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("ignore_default_routes requires a boolean 'ignore'.");
                return new IgnoreDefaultRoutesValue { Ignore = ignoreElement.GetBoolean() };

            case null:
                throw new JsonException("A field value must declare 'field'.");
            default:
                throw new JsonException("Unknown field value discriminator.");
        }
    }

    public override void Write(Utf8JsonWriter writer, RepairFieldValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        switch (value)
        {
            case InterfaceMetricValue metric:
                writer.WriteString("field", "interface_metric");
                writer.WriteNumber("raw_value", metric.RawValue);
                writer.WriteString("kind", WireNames.Of(metric.Kind));
                break;
            case AutomaticMetricValue automatic:
                writer.WriteString("field", "automatic_metric");
                writer.WriteBoolean("enabled", automatic.Enabled);
                break;
            case IgnoreDefaultRoutesValue ignore:
                writer.WriteString("field", "ignore_default_routes");
                writer.WriteBoolean("ignore", ignore.Ignore);
                break;
            default:
                throw new JsonException("The field value type has no broker wire form.");
        }

        writer.WriteEndObject();
    }

    private static void RequireExactMembers(List<string> names, params string[] expected)
    {
        if (names.Count != expected.Length)
            throw new JsonException("The field value contains an unknown or missing member.");
        foreach (var name in names)
        {
            if (!expected.Contains(name, StringComparer.Ordinal))
                throw new JsonException("The field value contains an unknown member.");
        }
    }
}

public sealed class BrokerProtocolException : Exception
{
    public BrokerProtocolException(BrokerRefusalCode code, string message) : base(message) => Code = code;

    public BrokerRefusalCode Code { get; }
}

/// <summary>
/// Length-prefixed, size-bounded frame codec. A frame is either fully valid or rejected: there is
/// no partial parse that could let a truncated action through.
/// </summary>
public static class BrokerFrameCodec
{
    public static async Task WriteAsync(Stream stream, BrokerFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, BrokerJson.Options);
        if (bytes.Length > BrokerProtocol.MaxFrameBytes)
            throw new BrokerProtocolException(BrokerRefusalCode.FrameTooLarge, "The frame exceeds the 64 KiB limit.");
        if (bytes.Length == 0)
            throw new BrokerProtocolException(BrokerRefusalCode.FrameEmpty, "The frame is empty.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BrokerFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0)
            throw new BrokerProtocolException(BrokerRefusalCode.FrameEmpty, "The frame declares an empty body.");
        if (length > BrokerProtocol.MaxFrameBytes)
            throw new BrokerProtocolException(BrokerRefusalCode.FrameTooLarge, "The frame exceeds the 64 KiB limit.");

        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);

        BrokerFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize<BrokerFrame>(body, BrokerJson.Options);
        }
        catch (JsonException)
        {
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidPayload, "The frame body is not a valid protocol frame.");
        }

        if (frame is null)
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidPayload, "The frame body is empty.");
        if (!string.Equals(frame.ProtocolVersion, BrokerProtocol.Version, StringComparison.Ordinal))
            throw new BrokerProtocolException(BrokerRefusalCode.ProtocolVersionMismatch, "The protocol version is not supported.");
        if (!BrokerIdentifiers.IsSafe(frame.MessageId))
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidIdentifier, "The message id is not a bounded identifier.");
        return frame;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("The peer closed the stream before a full frame arrived.");
            offset += read;
        }
    }
}

/// <summary>Bounded identifiers and digests: no paths, no whitespace, no traversal, no separators.</summary>
public static class BrokerIdentifiers
{
    public const int MaxIdentifierLength = 128;

    /// <summary>Stable, path-free identifier shape used by ids, digests and fingerprints.</summary>
    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > MaxIdentifierLength) return false;
        foreach (var character in value)
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '-' or '.' or ':' or '/' or '|';
            if (!allowed) return false;
        }

        // Reject traversal, absolute paths, drive letters and UNC-like prefixes even when the
        // character set above would allow the individual characters.
        if (value.Contains("..", StringComparison.Ordinal)) return false;
        if (value.StartsWith('/') || value.StartsWith('\\')) return false;
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':') return false;
        if (value.Contains('\\', StringComparison.Ordinal)) return false;
        if (value.Contains("//", StringComparison.Ordinal)) return false;
        return true;
    }

    public static bool IsHex(string? value, int length)
    {
        if (value is null || value.Length != length) return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        return true;
    }
}

/// <summary>Parses a frame payload into exactly one typed message, never into an arbitrary type.</summary>
public static class BrokerMessages
{
    public static BrokerFrame Frame<T>(BrokerMessageKind kind, T payload, string? messageId = null) where T : class => new()
    {
        ProtocolVersion = BrokerProtocol.Version,
        Kind = kind,
        MessageId = messageId ?? "msg-" + Guid.NewGuid().ToString("N"),
        Payload = JsonSerializer.SerializeToElement(payload, BrokerJson.Options),
    };

    public static BrokerFrame EmptyFrame(BrokerMessageKind kind, string? messageId = null) => new()
    {
        ProtocolVersion = BrokerProtocol.Version,
        Kind = kind,
        MessageId = messageId ?? "msg-" + Guid.NewGuid().ToString("N"),
        Payload = null,
    };

    public static BrokerHelloMessage ReadHello(BrokerFrame frame)
    {
        RequireKind(frame, BrokerMessageKind.Hello);
        var hello = Parse<BrokerHelloMessage>(frame);
        if (!BrokerIdentifiers.IsHex(hello.ClientNonce, BrokerProtocol.NonceHexLength))
            throw new BrokerProtocolException(BrokerRefusalCode.NonceMissing, "The client nonce must be 256 bits of hex.");
        if (hello.ClientProcessId <= 0)
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidIdentifier, "The client process id is not valid.");
        return hello;
    }

    public static BrokerAuthenticateMessage ReadAuthenticate(BrokerFrame frame)
    {
        RequireKind(frame, BrokerMessageKind.Authenticate);
        var authenticate = Parse<BrokerAuthenticateMessage>(frame);
        if (!BrokerIdentifiers.IsHex(authenticate.BrokerNonce, BrokerProtocol.NonceHexLength)
            || !BrokerIdentifiers.IsHex(authenticate.ClientNonce, BrokerProtocol.NonceHexLength)
            || !BrokerIdentifiers.IsHex(authenticate.NonceBinding, 64))
            throw new BrokerProtocolException(BrokerRefusalCode.NonceMissing, "The handshake nonce material is malformed.");
        if (!BrokerIdentifiers.IsSafe(authenticate.TransactionId))
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidIdentifier, "The transaction id is not a bounded identifier.");
        if (!BrokerIdentifiers.IsSafe(authenticate.PlanDigest))
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidIdentifier, "The plan digest is not a bounded identifier.");
        return authenticate;
    }

    public static BrokerSubmitTransactionMessage ReadSubmitTransaction(BrokerFrame frame)
    {
        RequireKind(frame, BrokerMessageKind.SubmitTransaction);
        var submit = Parse<BrokerSubmitTransactionMessage>(frame);
        if (!BrokerIdentifiers.IsSafe(submit.TransactionId)
            || !BrokerIdentifiers.IsSafe(submit.PlanDigest)
            || !BrokerIdentifiers.IsSafe(submit.TargetFingerprint))
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidIdentifier, "The transaction ids are not bounded identifiers.");
        return submit;
    }

    public static BrokerCancelMessage ReadCancel(BrokerFrame frame)
    {
        RequireKind(frame, BrokerMessageKind.Cancel);
        var cancel = Parse<BrokerCancelMessage>(frame);
        if (!BrokerIdentifiers.IsSafe(cancel.TransactionId))
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidIdentifier, "The transaction id is not a bounded identifier.");
        return cancel;
    }

    public static BrokerFrame Error(BrokerRefusalCode code, string detail, IEnumerable<BrokerRefusalCode>? codes = null) =>
        Frame(BrokerMessageKind.Error, new BrokerErrorMessage
        {
            Code = code,
            Detail = detail,
            Codes = codes?.Distinct().ToArray() ?? Array.Empty<BrokerRefusalCode>(),
        });

    public static BrokerFrame TransactionResult(BrokerTransactionResultMessage message) =>
        Frame(BrokerMessageKind.TransactionResult, message);

    private static void RequireKind(BrokerFrame frame, BrokerMessageKind expected)
    {
        if (frame.Kind != expected)
            throw new BrokerProtocolException(BrokerRefusalCode.UnexpectedMessage, "The frame kind is not valid in this session state.");
    }

    private static T Parse<T>(BrokerFrame frame)
    {
        if (frame.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object)
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidPayload, "The frame payload must be an object.");
        try
        {
            return payload.Deserialize<T>(BrokerJson.Options)
                ?? throw new BrokerProtocolException(BrokerRefusalCode.InvalidPayload, "The frame payload is empty.");
        }
        catch (JsonException)
        {
            throw new BrokerProtocolException(BrokerRefusalCode.InvalidPayload, "The frame payload does not match the protocol.");
        }
    }
}




