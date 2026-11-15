using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Broker;

[JsonConverter(typeof(WireEnumJsonConverter<BrokerAuditEventKind>))]
public enum BrokerAuditEventKind
{
    [JsonStringEnumMemberName("host_refused")]
    HostRefused,

    [JsonStringEnumMemberName("listener_started")]
    ListenerStarted,

    [JsonStringEnumMemberName("peer_rejected")]
    PeerRejected,

    [JsonStringEnumMemberName("challenge_issued")]
    ChallengeIssued,

    [JsonStringEnumMemberName("identity_verified")]
    IdentityVerified,

    [JsonStringEnumMemberName("nonce_rejected")]
    NonceRejected,

    [JsonStringEnumMemberName("transaction_submitted")]
    TransactionSubmitted,

    [JsonStringEnumMemberName("transaction_refused")]
    TransactionRefused,

    [JsonStringEnumMemberName("transaction_accepted")]
    TransactionAccepted,

    [JsonStringEnumMemberName("execution_unavailable")]
    ExecutionUnavailable,

    [JsonStringEnumMemberName("transaction_executed")]
    TransactionExecuted,

    [JsonStringEnumMemberName("transaction_rolled_back")]
    TransactionRolledBack,

    [JsonStringEnumMemberName("recovery_required")]
    RecoveryRequired,

    [JsonStringEnumMemberName("journal_refused")]
    JournalRefused,

    [JsonStringEnumMemberName("cancel_requested")]
    CancelRequested,

    [JsonStringEnumMemberName("close_requested")]
    CloseRequested,

    [JsonStringEnumMemberName("disconnected")]
    Disconnected,

    [JsonStringEnumMemberName("handshake_timeout")]
    HandshakeTimeout,

    [JsonStringEnumMemberName("idle_timeout")]
    IdleTimeout,

    [JsonStringEnumMemberName("budget_exhausted")]
    BudgetExhausted,

    [JsonStringEnumMemberName("protocol_error")]
    ProtocolError,

    [JsonStringEnumMemberName("session_ended")]
    SessionEnded,
}

/// <summary>
/// One audit entry. It carries stable codes, counts and bounded identifiers only: never a nonce,
/// never a binding, never a plan payload, never a path or a user name.
/// </summary>
public sealed record BrokerAuditEntry
{
    public required DateTimeOffset AtUtc { get; init; }

    public required BrokerAuditEventKind Kind { get; init; }

    public IReadOnlyList<BrokerRefusalCode> Codes { get; init; } = Array.Empty<BrokerRefusalCode>();

    /// <summary>Bounded transaction id, when one was bound. Never secret material.</summary>
    public string? TransactionId { get; init; }

    public int? PeerProcessId { get; init; }

    public int? ActionCount { get; init; }
}

/// <summary>
/// In-memory audit trail for one broker run. This delivery has no journal or log file (T15); the
/// trail exists so tests can prove that the nonce and the plan never leak into audit output.
/// </summary>
public sealed class BrokerAuditTrail
{
    private readonly object _sync = new();
    private readonly List<BrokerAuditEntry> _entries = new();
    private readonly TimeProvider _time;

    public BrokerAuditTrail(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public void Record(
        BrokerAuditEventKind kind,
        IEnumerable<BrokerRefusalCode>? codes = null,
        string? transactionId = null,
        int? peerProcessId = null,
        int? actionCount = null)
    {
        lock (_sync)
        {
            if (_entries.Count >= 512) return;
            _entries.Add(new BrokerAuditEntry
            {
                AtUtc = _time.GetUtcNow(),
                Kind = kind,
                Codes = codes?.Distinct().ToArray() ?? Array.Empty<BrokerRefusalCode>(),
                TransactionId = BrokerIdentifiers.IsSafe(transactionId) ? transactionId : null,
                PeerProcessId = peerProcessId,
                ActionCount = actionCount,
            });
        }
    }

    public IReadOnlyList<BrokerAuditEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    /// <summary>Stable text form used by tests; it must never contain secret material.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries)
        {
            builder.Append(entry.AtUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(WireNames.Of(entry.Kind));
            if (entry.Codes.Count > 0)
            {
                builder.Append(' ').Append(string.Join(",", entry.Codes.Select(WireNames.Of)));
            }

            if (entry.TransactionId is not null)
            {
                builder.Append(" tx=").Append(entry.TransactionId);
            }

            if (entry.PeerProcessId is { } pid)
            {
                builder.Append(" pid=").Append(pid.ToString(CultureInfo.InvariantCulture));
            }

            if (entry.ActionCount is { } count)
            {
                builder.Append(" actions=").Append(count.ToString(CultureInfo.InvariantCulture));
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}

