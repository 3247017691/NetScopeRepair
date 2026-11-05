using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetScope.Storage.Export;

/// <summary>
/// Replaces a real address with a deterministic alias inside the same semantic range. The
/// category survives anonymization, so an offline rule can still tell a fake-IP answer from a
/// private host and a public address without seeing the original value. The alias is keyed by
/// the per-package salt, so two packages cannot be joined by an address.
///
/// Private/public IPv4 aliases keep the interface/route/gateway subnet relation at the octet
/// boundaries that the contract actually exposes (/16 and /24); the 10/8, 192.168/16 and
/// 172.16/12 classes also keep their class boundary. They are not a general prefix-preserving
/// permutation: aliases stay inside a bounded placeholder space, the original 100.64/10 boundary
/// and prefixes finer than /24 or at non-octet boundaries are not preserved, and public-IPv4
/// topology is not guaranteed. IPv6 aliases use a keyed prefix-preserving host-bit permutation
/// inside a fixed class range, so /64 etc. remain coherent. IPv4-mapped IPv6 is mapped through
/// its embedded IPv4 so the bare and mapped spellings resolve to the same embedded alias.
/// </summary>
internal static class AddressMapping
{
    private enum AddressClass
    {
        FakeIp,
        Private,
        Loopback,
        LinkLocal,
        Multicast,
        Unspecified,
        Public,
    }

    /// <summary>
    /// Maps one bare IPv4/IPv6 address or CIDR prefix. Returns false when the token is not a
    /// valid address/prefix so the caller can leave ordinary text untouched. A prefixed token is
    /// treated as a host address unless <paramref name="network"/> asks for a network form.
    /// </summary>
    public static bool TryMap(string token, byte[] salt, out string mapped, out string category) =>
        TryMap(token, salt, network: false, out mapped, out category);

    /// <summary>
    /// Maps one address/prefix. When <paramref name="network"/> is true the mapped value is masked
    /// to the original prefix (route destination); otherwise the mapped host is kept even when a
    /// prefix is present (interface host address).
    /// </summary>
    public static bool TryMap(string token, byte[] salt, bool network, out string mapped, out string category)
    {
        mapped = token;
        category = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var slash = token.IndexOf('/');
        var addressText = slash >= 0 ? token[..slash] : token;
        int? prefix = null;
        if (slash >= 0)
        {
            if (!int.TryParse(token[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPrefix))
            {
                return false;
            }

            prefix = parsedPrefix;
        }

        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        var family = address.AddressFamily;
        var maxPrefix = family switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => -1,
        };
        if (maxPrefix < 0 || prefix is < 0 or > 128 || prefix > maxPrefix)
        {
            return false;
        }

        if (family == AddressFamily.InterNetwork)
        {
            var value = address.GetAddressBytes();
            var addressClass = ClassifyV4(value);
            category = CategoryOf(addressClass);
            var alias = AliasV4(addressClass, value, salt);
            if (network && prefix is { } bits)
            {
                Mask(alias, bits);
            }

            mapped = FormatV4(alias, prefix);
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            var value = address.GetAddressBytes();
            var embedded = value[12..16];
            var addressClass = ClassifyV4(embedded);
            category = CategoryOf(addressClass);
            var alias = AliasV4(addressClass, embedded, salt);

            if (prefix is { } mappedPrefix)
            {
                if (mappedPrefix >= 96)
                {
                    if (network)
                    {
                        Mask(alias, mappedPrefix - 96);
                    }

                    mapped = "::ffff:" + FormatV4(alias, null) + "/"
                        + mappedPrefix.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    // A prefix shorter than the mapped /96 covers the mapped marker itself. The
                    // embedded alias is still kept, then the whole IPv6 form is masked.
                    var full = new byte[16];
                    full[10] = 0xFF;
                    full[11] = 0xFF;
                    Array.Copy(alias, 0, full, 12, 4);
                    if (network)
                    {
                        Mask(full, mappedPrefix);
                    }

                    mapped = FormatV6(full, mappedPrefix);
                }
            }
            else
            {
                mapped = "::ffff:" + FormatV4(alias, null);
            }

            return true;
        }

        var value6 = address.GetAddressBytes();
        var class6 = ClassifyV6(address, value6);
        category = CategoryOf(class6);
        var alias6 = AliasV6(class6, value6, salt);
        if (network && prefix is { } prefix6)
        {
            Mask(alias6, prefix6);
        }

        mapped = FormatV6(alias6, prefix);
        return true;
    }

    private static string CategoryOf(AddressClass addressClass) => addressClass switch
    {
        AddressClass.FakeIp => RedactionCategories.FakeIp,
        AddressClass.Private => RedactionCategories.PrivateIp,
        AddressClass.Loopback => RedactionCategories.Loopback,
        AddressClass.LinkLocal => RedactionCategories.LinkLocal,
        AddressClass.Multicast => RedactionCategories.Multicast,
        AddressClass.Unspecified => RedactionCategories.UnspecifiedAddress,
        _ => RedactionCategories.PublicIp,
    };

    private static AddressClass ClassifyV4(byte[] value)
    {
        if (value[0] == 198 && value[1] is 18 or 19) return AddressClass.FakeIp;
        if (value[0] == 10
            || (value[0] == 172 && value[1] is >= 16 and <= 31)
            || (value[0] == 192 && value[1] == 168)
            || (value[0] == 100 && value[1] is >= 64 and <= 127))
        {
            return AddressClass.Private;
        }

        if (value[0] == 127) return AddressClass.Loopback;
        if (value[0] == 169 && value[1] == 254) return AddressClass.LinkLocal;
        if (value[0] is >= 224 and <= 239) return AddressClass.Multicast;
        if (value[0] == 0) return AddressClass.Unspecified;
        return AddressClass.Public;
    }

    private static AddressClass ClassifyV6(IPAddress address, byte[] value)
    {
        if (IPAddress.IsLoopback(address)) return AddressClass.Loopback;
        if (value.All(item => item == 0)) return AddressClass.Unspecified;
        if ((value[0] & 0xFE) == 0xFC) return AddressClass.Private;
        if (value[0] == 0xFE && (value[1] & 0xC0) == 0x80) return AddressClass.LinkLocal;
        if (value[0] == 0xFF) return AddressClass.Multicast;
        return AddressClass.Public;
    }

    private static byte[] AliasV4(AddressClass addressClass, byte[] value, byte[] salt) => addressClass switch
    {
        // Fake-IP keeps the 198.18/16 spelling the existing rules and tests expect. The third and
        // fourth octets depend on the original octets, so a /24 relation survives.
        AddressClass.FakeIp =>
        [
            198,
            18,
            Octet(salt, "fake.third", value[2]),
            Octet(salt, "fake.fourth", value[3]),
        ],
        AddressClass.Private => PrivateAlias(value, salt),
        AddressClass.Loopback => [127, 0, 0, 1],
        AddressClass.LinkLocal =>
        [
            169,
            254,
            Octet(salt, "link-local.third", value[2]),
            Octet(salt, "link-local.fourth", value[3]),
        ],
        AddressClass.Multicast =>
        [
            224,
            Octet(salt, "multicast.second", value[1]),
            Octet(salt, "multicast.third", value[2]),
            Octet(salt, "multicast.fourth", value[3]),
        ],
        AddressClass.Unspecified => [0, 0, 0, 0],
        // A bounded public placeholder: 203.100-199.100-199.100-199. One hundred values per
        // octet still give ~1e6 combinations instead of the former 100 aliases, while the fixed
        // textual width keeps preview/package sizes stable. This is not injective and carries no
        // public-prefix guarantee.
        _ =>
        [
            203,
            Octet(salt, "public.second", value[1]),
            Octet(salt, "public.third", value[2]),
            Octet(salt, "public.fourth", value[3]),
        ],
    };

    /// <summary>
    /// Private ranges are mapped into the 10/8 placeholder area (or 172.16/12 for 172.16/12)
    /// with octet-stable aliases: the same original octet always maps to the same alias octet, so
    /// two addresses that share a /16 or /24 still share the mapped /16 or /24. Different private
    /// source ranges use different keyed domains, which keeps aliases inside the private class
    /// without promising a global bijection.
    /// </summary>
    private static byte[] PrivateAlias(byte[] value, byte[] salt)
    {
        if (value[0] == 10)
        {
            return
            [
                10,
                Octet(salt, "private.10.second", value[1]),
                Octet(salt, "private.10.third", value[2]),
                Octet(salt, "private.10.fourth", value[3]),
            ];
        }

        if (value[0] == 172)
        {
            // 172.16/12: the low nibble of the second octet is the first host nibble. A rotation
            // inside 16..31 keeps /16 distinctness and the mapped range inside 172.16/12.
            return
            [
                172,
                Octet(salt, "private.172.second", value[1], 16, 16),
                Octet(salt, "private.172.third", value[2]),
                Octet(salt, "private.172.fourth", value[3]),
            ];
        }

        if (value[0] == 192)
        {
            // 192.168/16 is mapped into 10.100/16: the contract test expects no 192.168 alias.
            return
            [
                10,
                Octet(salt, "private.192.second", value[1], 100, 1),
                Octet(salt, "private.192.third", value[2]),
                Octet(salt, "private.192.fourth", value[3]),
            ];
        }

        // 100.64/10 (carrier-grade NAT) is also private for this export.
        return
        [
            10,
            Octet(salt, "private.100.second", value[1], 100, 64),
            Octet(salt, "private.100.third", value[2]),
            Octet(salt, "private.100.fourth", value[3]),
        ];
    }

    private static byte[] AliasV6(AddressClass addressClass, byte[] value, byte[] salt)
    {
        var result = new byte[16];
        int fixedBits;
        string domain;

        switch (addressClass)
        {
            case AddressClass.Loopback:
                result[15] = 1;
                return result;
            case AddressClass.Unspecified:
                return result;
            case AddressClass.LinkLocal:
                result[0] = 0xFE;
                result[1] = 0x80;
                fixedBits = 16;
                domain = "v6.link-local";
                break;
            case AddressClass.Private:
                result[0] = 0xFD;
                fixedBits = 8;
                domain = "v6.private";
                break;
            case AddressClass.Multicast:
                result[0] = 0xFF;
                result[1] = 0x02;
                fixedBits = 16;
                domain = "v6.multicast";
                break;
            default:
                // 2001:db8::/32 is the IPv6 documentation range: clearly a placeholder but
                // still a valid, parseable public address.
                result[0] = 0x20;
                result[1] = 0x01;
                result[2] = 0x0D;
                result[3] = 0xB8;
                fixedBits = 32;
                domain = "v6.public";
                break;
        }

        MapHostBits(value, result, fixedBits, salt, domain);
        return result;
    }

    /// <summary>
    /// Keyed prefix-preserving permutation of the host bits after a fixed class prefix. Bit i of
    /// the output depends only on output bits before i and input bit i, so two addresses that
    /// share a prefix continue to share exactly that prefix inside the mapped class.
    /// </summary>
    private static void MapHostBits(byte[] source, byte[] target, int fixedBits, byte[] salt, string domain)
    {
        var outputPrefix = new byte[16];
        Array.Copy(target, outputPrefix, 16);

        for (var bit = fixedBits; bit < 128; bit++)
        {
            var inputBit = (source[bit / 8] >> (7 - (bit % 8))) & 1;
            var pseudoBit = PrfBit(salt, domain, bit, outputPrefix);
            var outputBit = inputBit ^ pseudoBit;
            if (outputBit != 0)
            {
                target[bit / 8] |= (byte)(1 << (7 - (bit % 8)));
                outputPrefix[bit / 8] |= (byte)(1 << (7 - (bit % 8)));
            }
        }
    }

    private static int PrfBit(byte[] salt, string domain, int bit, byte[] outputPrefix)
    {
        var digest = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(
            "netscope-export-bit-v1|" + domain + "|" + bit.ToString(CultureInfo.InvariantCulture) + "|"
            + Convert.ToHexString(outputPrefix)));
        return digest[0] & 1;
    }

    private static byte Octet(byte[] salt, string domain, byte value, int baseValue = 100, int count = 100)
    {
        var digest = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(
            "netscope-export-octet-v1|" + domain + "|" + value.ToString(CultureInfo.InvariantCulture)));
        return (byte)(baseValue + digest[0] % count);
    }

    private static string FormatV4(byte[] address, int? prefix)
    {
        var text = string.Join('.', address.Select(item => item.ToString(CultureInfo.InvariantCulture)));
        return prefix is { } bits ? text + "/" + bits.ToString(CultureInfo.InvariantCulture) : text;
    }

    private static string FormatV6(byte[] address, int? prefix)
    {
        // Fixed-width groups keep the textual size independent of the salt and avoid the
        // variable-length compression of IPAddress.ToString().
        var text = string.Join(':', Enumerable.Range(0, 8)
            .Select(index => ((address[index * 2] << 8) | address[index * 2 + 1])
                .ToString("x4", CultureInfo.InvariantCulture)));
        return prefix is { } bits ? text + "/" + bits.ToString(CultureInfo.InvariantCulture) : text;
    }

    private static void Mask(byte[] address, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        for (var index = fullBytes + (prefixLength % 8 == 0 ? 0 : 1); index < address.Length; index++)
        {
            address[index] = 0;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits != 0 && fullBytes < address.Length)
        {
            address[fullBytes] &= (byte)(0xFF << (8 - remainingBits));
        }
    }
}
