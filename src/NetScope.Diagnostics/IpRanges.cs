using System.Net;
using System.Net.Sockets;

namespace NetScope.Diagnostics;

/// <summary>An IPv4 or IPv6 prefix, used to classify observed addresses semantically.</summary>
public sealed record IpRange
{
    private IpRange(byte[] network, int prefixLength, AddressFamily family)
    {
        Network = network;
        PrefixLength = prefixLength;
        Family = family;
    }

    public byte[] Network { get; }

    public int PrefixLength { get; }

    public AddressFamily Family { get; }

    public static bool TryParse(string cidr, out IpRange? range)
    {
        range = null;

        var parts = cidr.Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var network)
            || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maxPrefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        range = new IpRange(network.GetAddressBytes(), prefixLength, network.AddressFamily);
        return true;
    }

    public bool Contains(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != Family)
        {
            return false;
        }

        var candidate = parsed.GetAddressBytes();
        var fullBytes = PrefixLength / 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (candidate[index] != Network[index])
            {
                return false;
            }
        }

        var remainingBits = PrefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (candidate[fullBytes] & mask) == (Network[fullBytes] & mask);
    }
}

/// <summary>
/// Semantic classification of addresses. The product never reports a raw address when a range
/// category is enough, and a reserved range is never on its own evidence of interception.
/// </summary>
public static class IpRangeClassifier
{
    /// <summary>The range a fake-IP resolver commonly returns; a mapping, not a fault.</summary>
    public static readonly IpRange FakeIpRange = Parse("198.18.0.0/15");

    private static readonly IpRange[] PrivateOrReservedRanges =
    {
        Parse("10.0.0.0/8"),
        Parse("172.16.0.0/12"),
        Parse("192.168.0.0/16"),
        Parse("100.64.0.0/10"),
        Parse("127.0.0.0/8"),
        Parse("169.254.0.0/16"),
        Parse("0.0.0.0/8"),
        Parse("198.18.0.0/15"),
        Parse("224.0.0.0/4"),
        Parse("fc00::/7"),
        Parse("fe80::/10"),
        Parse("::1/128"),
    };

    public static bool IsFakeIpAddress(string address) => FakeIpRange.Contains(address);

    /// <summary>True for addresses that are not publicly routable.</summary>
    public static bool IsPrivateOrReserved(string address) =>
        PrivateOrReservedRanges.Any(range => range.Contains(address));

    private static IpRange Parse(string cidr) =>
        IpRange.TryParse(cidr, out var range)
            ? range!
            : throw new InvalidOperationException($"Built-in range '{cidr}' is not a valid prefix.");
}
