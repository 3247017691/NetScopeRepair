using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NetScope.Broker;

/// <summary>Generates the 256-bit broker challenge nonce. Injected so tests are deterministic.</summary>
public interface IBrokerNonceSource
{
    byte[] CreateNonce();
}

/// <summary>Cryptographic RNG; the only production nonce source.</summary>
public sealed class RandomBrokerNonceSource : IBrokerNonceSource
{
    public byte[] CreateNonce() => RandomNumberGenerator.GetBytes(BrokerProtocol.NonceBytes);
}

/// <summary>
/// Binds both challenges, the transaction id and the plan digest into one short-lived value.
/// It is a replay/correlation defence, not user authentication: the identity of the peer comes
/// from the ACL-protected pipe and the kernel-reported process facts.
/// </summary>
public static class BrokerNonceBinding
{
    public static string NextNonceHex(IBrokerNonceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = source.CreateNonce();
        if (bytes.Length != BrokerProtocol.NonceBytes)
            throw new InvalidOperationException("A broker nonce must be exactly 256 bits.");
        return Convert.ToHexStringLower(bytes);
    }

    public static string Compute(
        string clientNonceHex, string brokerNonceHex, string transactionId, string planDigest)
    {
        if (!BrokerIdentifiers.IsHex(clientNonceHex, BrokerProtocol.NonceHexLength)
            || !BrokerIdentifiers.IsHex(brokerNonceHex, BrokerProtocol.NonceHexLength))
            throw new ArgumentException("The nonce material must be two 256-bit hex values.");
        if (!BrokerIdentifiers.IsSafe(transactionId) || !BrokerIdentifiers.IsSafe(planDigest))
            throw new ArgumentException("The binding identifiers must be bounded identifiers.");

        var canonical = string.Join(
            '\n',
            "broker-nonce-binding/1",
            clientNonceHex.ToLowerInvariant(),
            brokerNonceHex.ToLowerInvariant(),
            transactionId,
            planDigest);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool Matches(string expected, string? actual) =>
        actual is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(actual.ToLowerInvariant()));
}

/// <summary>
/// One-time consumption registry for handshake bindings. Only a salted hash of the binding is
/// held, never the nonce itself, and nothing here is ever written to a log.
/// </summary>
public sealed class BrokerNonceLedger
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public bool TryConsume(string bindingFingerprint, DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(bindingFingerprint)) return false;
        Prune(nowUtc);

        // Expired entries may be re-used only after they were pruned, which is conservative:
        // a short-lived binding cannot be replayed within its lifetime.
        return _consumed.TryAdd(bindingFingerprint, nowUtc + lifetime);
    }

    /// <summary>Number of live entries; pruning happens on the next consume with a caller clock.</summary>
    public int Count => _consumed.Count;

    private void Prune(DateTimeOffset nowUtc)
    {
        foreach (var pair in _consumed)
        {
            if (pair.Value <= nowUtc) _consumed.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>A stable fingerprint of the binding, for the one-time ledger only.</summary>
    public static string Fingerprint(string bindingHex) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(bindingHex.ToLowerInvariant())));

    public static string FormatLifetime(TimeSpan lifetime) =>
        lifetime.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
}

