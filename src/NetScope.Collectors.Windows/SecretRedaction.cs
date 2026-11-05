using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NetScope.Collectors.Windows;

/// <summary>
/// Applies an output whitelist at collection time. Proxy/PAC observations contain only a validated
/// scheme, host and port; userinfo, path, query and fragment never enter the public snapshot.
/// Unrecognized syntax is replaced wholesale rather than echoed as an attempted recovery.
/// </summary>
public static class SecretRedaction
{
    public const string Replacement = "***";
    private const int MaxInputLength = 8192;

    /// <summary>Sanitizes a proxy endpoint or a WinINET protocol-to-proxy map.</summary>
    public static string RedactUrl(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        if (value.Length > MaxInputLength || value.Any(char.IsControl))
        {
            return Replacement;
        }

        var equals = value.IndexOf('=');
        if (equals >= 0 && IsMapKey(value[..equals].Trim()))
        {
            var entries = new List<string>();
            foreach (var entry in value.Split(';'))
            {
                var separator = entry.IndexOf('=');
                if (separator <= 0 || !IsMapKey(entry[..separator].Trim()))
                {
                    return Replacement;
                }
                var endpoint = RedactEndpoint(entry[(separator + 1)..].Trim(), pac: false, mapEntry: true);
                if (endpoint == Replacement)
                {
                    return Replacement;
                }
                entries.Add(entry[..separator].Trim().ToLowerInvariant() + "=" + endpoint);
            }
            return string.Join(";", entries);
        }

        return RedactEndpoint(value.Trim(), pac: false);
    }

    /// <summary>Retains only the HTTP(S) PAC origin, including its non-default port.</summary>
    public static string RedactPacUrl(string value) => string.IsNullOrEmpty(value)
        ? value : RedactEndpoint(value.Trim(), pac: true);

    /// <summary>
    /// NO_PROXY is a bypass list, not a URL. Preserve valid domain, address, CIDR, wildcard and
    /// optional port entries exactly, including leading dots and IPv6 notation. Invalid entries
    /// are replaced individually so they cannot smuggle URL credentials or path/query secrets.
    /// </summary>
    public static string RedactBypassList(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        if (value.Length > MaxInputLength || value.Any(char.IsControl))
        {
            return Replacement;
        }

        return string.Join(",", value.Split(',').Select(entry =>
            IsBypassEntry(entry.Trim()) ? entry : Replacement));
    }

    /// <summary>Whether a proxy value still requires collection-time minimization.</summary>
    public static bool LooksUnredacted(string value) =>
        !string.Equals(value, RedactUrl(value), StringComparison.Ordinal);

    private static bool IsMapKey(string key) => key.ToLowerInvariant() is "http" or "https" or "ftp" or "socks";

    private static string RedactEndpoint(string value, bool pac, bool mapEntry = false)
    {
        if (value.Length == 0 || value.Length > MaxInputLength
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\'))
        {
            return Replacement;
        }

        var hasScheme = value.Contains("://", StringComparison.Ordinal);
        if (pac && !hasScheme)
        {
            return Replacement;
        }
        var candidate = hasScheme ? value : value.StartsWith("//", StringComparison.Ordinal)
            ? "http:" + value : "http://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return Replacement;
        }

        try
        {
            if (!(pac ? uri.Scheme is "http" or "https"
                    : uri.Scheme is "http" or "https" or "socks" or "socks4" or "socks4a" or "socks5" or "socks5h")
                || !TrySafeHost(uri.IdnHost, out var host)
                || uri.Port is < -1 or > 65535 or 0)
            {
                return Replacement;
            }

            // Proxy maps have no URL path/query grammar. Reject the whole map when those appear:
            // a semicolon inside a secret URL could otherwise masquerade as another map entry.
            if (mapEntry && (uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            {
                return Replacement;
            }

            // Never copy OriginalString, Authority or UserInfo. Rebuild from the small safe vocabulary.
            var port = uri.IsDefaultPort || uri.Port < 0 ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            return (hasScheme ? uri.Scheme + "://" : "") + host + port;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            // Some invalid IDN authorities fail only when IdnHost is accessed after TryCreate.
            return Replacement;
        }
    }

    private static bool TrySafeHost(string candidate, out string host)
    {
        host = "";
        // Percent-encoded authorities and scoped IPv6 are deliberately unsupported. Decoding
        // ambiguous delimiters risks turning encoded credential text into a displayed hostname.
        if (candidate.Length == 0 || candidate.Contains('%'))
        {
            return false;
        }

        var unbracketed = candidate.Trim('[', ']');
        if (IPAddress.TryParse(unbracketed, out var address))
        {
            host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            return true;
        }

        if (!IsDnsName(candidate))
        {
            return false;
        }
        host = candidate.ToLowerInvariant();
        return true;
    }

    private static bool IsDnsName(string value)
    {
        if (value.Length is 0 or > 253)
        {
            return false;
        }
        var name = value.EndsWith('.') ? value[..^1] : value;
        return name.Split('.').All(label => label.Length is > 0 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsBypassEntry(string token)
    {
        if (token.Length == 0 || token == "*" || token.Equals("<local>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || token.IndexOfAny(['@', '?', '#', '%', '\\', '=', ';']) >= 0)
        {
            return false;
        }

        var slash = token.IndexOf('/');
        if (slash >= 0)
        {
            return IPAddress.TryParse(token[..slash], out var network)
                && int.TryParse(token[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
                && bits >= 0 && bits <= (network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        }
        if (IPAddress.TryParse(token, out _))
        {
            return true;
        }

        var name = token;
        if (token.StartsWith('['))
        {
            var close = token.IndexOf(']');
            if (close < 0 || !IPAddress.TryParse(token[1..close], out var ip)
                || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
            return close == token.Length - 1
                || token[close + 1] == ':' && IsPort(token[(close + 2)..]);
        }

        var colon = token.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!IsPort(token[(colon + 1)..]))
            {
                return false;
            }
            name = token[..colon];
        }
        if (name.StartsWith("*.", StringComparison.Ordinal))
        {
            name = name[2..];
        }
        else if (name.StartsWith('.'))
        {
            name = name[1..];
        }
        return IsDnsName(name);
    }

    private static bool IsPort(string token) =>
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;
}
