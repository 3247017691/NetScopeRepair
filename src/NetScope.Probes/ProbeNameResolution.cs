using System.Net;
using System.Net.Sockets;

namespace NetScope.Probes;

/// <summary>
/// The address family a probe attempt is allowed to use. <see cref="Unspecified"/> means the plan
/// asked for no constraint, not "unknown".
/// </summary>
public enum ProbeAddressFamily
{
    Unspecified,
    IPv4,
    IPv6,
}

/// <summary>Parses the wire value of <c>ProbeGroupPlan.AddressFamily</c> without guessing.</summary>
public static class ProbeAddressFamilyPolicy
{
    /// <summary>
    /// True when the value is a family this transport can really enforce. A null or empty value
    /// means "no constraint"; an unrecognised value is rejected instead of being silently ignored.
    /// </summary>
    public static bool TryParse(string? value, out ProbeAddressFamily family)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            family = ProbeAddressFamily.Unspecified;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "any":
            case "dual":
            case "unspecified":
                family = ProbeAddressFamily.Unspecified;
                return true;
            case "ipv4":
            case "v4":
                family = ProbeAddressFamily.IPv4;
                return true;
            case "ipv6":
            case "v6":
                family = ProbeAddressFamily.IPv6;
                return true;
            default:
                family = ProbeAddressFamily.Unspecified;
                return false;
        }
    }

    public static bool Matches(IPAddress address, ProbeAddressFamily family)
    {
        ArgumentNullException.ThrowIfNull(address);
        return family switch
        {
            ProbeAddressFamily.IPv4 => address.AddressFamily == AddressFamily.InterNetwork,
            ProbeAddressFamily.IPv6 => address.AddressFamily == AddressFamily.InterNetworkV6,
            _ => true,
        };
    }

    public static string ToWireValue(ProbeAddressFamily family) => family switch
    {
        ProbeAddressFamily.IPv4 => "ipv4",
        ProbeAddressFamily.IPv6 => "ipv6",
        _ => "unspecified",
    };
}

/// <summary>
/// What one local name-resolution call really returned. A failure is kept as a reason code and an
/// empty address list; it is never replaced by a made-up answer.
/// </summary>
public sealed record ProbeNameResolution
{
    /// <summary>Short reason constants the transport maps onto a stage failure.</summary>
    public const string NameResolutionFailed = "name_resolution_failed";
    public const string NoAddressForRequestedFamily = "no_address_for_requested_family";

    public required string Host { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>Where the answer came from, for example "system-dns" or "literal".</summary>
    public string? Source { get; init; }

    /// <summary>Null when the call produced an answer; otherwise a short, non-sensitive reason code.</summary>
    public string? FailureReason { get; init; }

    public bool Succeeded => FailureReason is null;
}

/// <summary>
/// Injectable local name-resolution boundary. Implementations must perform a real local lookup and
/// must never substitute a guess; returning addresses of another family is allowed because the
/// transport enforces the requested family before dialling.
/// </summary>
public interface IProbeNameResolver
{
    Task<ProbeNameResolution> ResolveAsync(
        string host, ProbeAddressFamily family, CancellationToken cancellationToken);
}

/// <summary>
/// Default boundary: the operating system resolver through <see cref="Dns"/>. IP literals are
/// reported as such without any lookup.
/// </summary>
public sealed class SystemProbeNameResolver : IProbeNameResolver
{
    public const string SystemDnsSource = "system-dns";
    public const string LiteralSource = "literal";

    public async Task<ProbeNameResolution> ResolveAsync(
        string host, ProbeAddressFamily family, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();

        var literal = host.Trim('[', ']');
        if (IPAddress.TryParse(literal, out var parsed))
        {
            return new ProbeNameResolution
            {
                Host = host,
                Addresses = new[] { parsed.ToString() },
                Source = LiteralSource,
            };
        }

        try
        {
            var addresses = family switch
            {
                ProbeAddressFamily.IPv4 => await Dns.GetHostAddressesAsync(
                    host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false),
                ProbeAddressFamily.IPv6 => await Dns.GetHostAddressesAsync(
                    host, AddressFamily.InterNetworkV6, cancellationToken).ConfigureAwait(false),
                _ => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false),
            };

            var list = addresses.Select(address => address.ToString()).ToArray();
            return list.Length == 0
                ? Failed(host, ProbeNameResolution.NoAddressForRequestedFamily)
                : new ProbeNameResolution { Host = host, Addresses = list, Source = SystemDnsSource };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The exception text can contain the private host name; only the category is kept.
            return Failed(host, ProbeNameResolution.NameResolutionFailed);
        }
    }

    private static ProbeNameResolution Failed(string host, string reason) =>
        new() { Host = host, Source = SystemDnsSource, FailureReason = reason };
}
