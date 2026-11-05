using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetScope.Storage.Export;

/// <summary>
/// Anonymizes one incident package. Aliases are assigned in first-seen order and stay stable
/// within the package. The per-package salt keys alias labels and the random incident id, so the
/// same original value maps to the same alias inside one package but not predictably across
/// packages. Original values and the alias map never leave this class.
/// </summary>
internal sealed class PackageAnonymizer
{
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema_version", "payload_version", "transformation_version", "transform_version",
        "event_id", "event_ids", "session_id", "boot_id", "incident_id", "snapshot_id", "snapshot_ids",
        "probe_id", "probe_ids", "probe_group_id", "diagnosis_id", "diagnosis_ids",
        "evidence_id", "evidence_ids", "counter_evidence_ids", "missing_evidence",
        "repair_plan_id", "transaction_id", "proxy_endpoint_id", "observation_window_id",
        "request_options_key", "target_id", "source_event_ids", "interface_guid", "luid",
        "if_index", "rule_id", "rule_version", "address_family", "family", "status", "event_type",
        "sequence", "monotonic_ms", "observed_at", "started_at", "ended_at", "duration_ms",
        "started_monotonic_ms", "valid_until", "synthetic", "source_version", "app_version",
        "parser_version", "client_version", "contract_version", "package_format_version", "version",
    };

    private static readonly HashSet<string> UserKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "username", "user_name", "account", "account_name", "owner", "user_sid", "sid",
        "email", "upn", "display_name",
    };

    private static readonly HashSet<string> DeviceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "hostname", "host_name", "computer_name", "machine_name", "netbios_name", "device_name",
        "device", "computer",
    };

    private static readonly Regex SchemeUserInfo = new(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)(?<info>[^/@\s]+)@", RegexOptions.Compiled);

    private static readonly Regex BareAuthority = new(
        @"(?<![\w:/])(?<info>[^@\s/:]+:[^@\s/]+)@(?<host>[A-Za-z0-9][A-Za-z0-9.\-]*(?::\d{1,5})?)",
        RegexOptions.Compiled);

    private static readonly Regex HeaderLine = new(
        @"(?<name>authorization|proxy-authorization|cookie|set-cookie)\s*:\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BasicToken = new(
        @"\bBasic\s+[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SecretAssignment = new(
        @"\b(?<name>access_token|refresh_token|id_token|token|api[_-]?key|apikey|client_secret|secret|password|passwd|pwd|signature|sig|auth)\s*=\s*(?<value>[^&;\s""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PasswordMarker = new(
        @"\{\{(?:PASSWORD|SECRET|TOKEN)_[A-Za-z0-9_]+\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\b", RegexOptions.Compiled);

    private static readonly Regex UrlToken = new(
        @"\b(?:https?|socks(?:4a?|5h?)?|ftp|file)://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExtendedPath = new(
        @"\\\\\?\\[^""'<>|*\r\n]*", RegexOptions.Compiled);

    private static readonly Regex UncPath = new(
        @"\\\\[^""'<>|?*\r\n]*", RegexOptions.Compiled);

    private static readonly Regex WindowsPath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^""'<>|?*\r\n]*", RegexOptions.Compiled);

    private static readonly Regex EnvironmentPath = new(
        @"%[A-Za-z_][A-Za-z0-9_]*%[\\/][^""'<>|?*\r\n]*", RegexOptions.Compiled);

    private static readonly Regex UnixPath = new(
        @"(?<![A-Za-z0-9_])/(?:home|Users|var|etc|opt|tmp|private|root|mnt|srv|usr|Applications|Library)/[^""'<>|?*\r\n]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TildePath = new(
        @"(?<![A-Za-z0-9_])~[\\/][^""'<>|?*\r\n]*", RegexOptions.Compiled);

    private static readonly Regex AddressToken = new(
        @"(?<![0-9A-Za-z_.])(?:(?:[0-9A-Fa-f]{0,4}:){2,7}(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,3})?|(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,3})?|(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:/\d{1,3})?)(?![0-9A-Za-z_.])",
        RegexOptions.Compiled);

    private static readonly Regex PathUser = new(
        @"(?i)[\\/]Users[\\/](?<name>[^\\/]+)", RegexOptions.Compiled);

    private static readonly Regex UnixPathUser = new(
        @"(?i)/(?:home|Users)/(?<name>[^/]+)", RegexOptions.Compiled);

    private static readonly Regex UncHost = new(
        @"^\\\\[^\\]+\\", RegexOptions.Compiled);

    private static readonly Regex ExtendedUncHost = new(
        @"^\\\\\?\\UNC\\(?<host>[^\\]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProxyVariable = new(
        @"^\s*(?<name>(?:HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|NO_PROXY))\s*=\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InlineProxyEndpoint = new(
        @"(?<scheme>\b(?:https?|socks(?:4a?|5h?)?)=)(?<endpoint>(?:\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9][A-Za-z0-9._-]*)(?::\d{1,5})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InternalHostToken = new(
        @"(?<host>[A-Za-z0-9][A-Za-z0-9.\-]*\.(?:corp|local|lan|internal|home|localdomain))(?::(?<port>\d{1,5}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _identityReplacements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _usedLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _aliasValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _syntheticAddresses = new(StringComparer.Ordinal);
    private readonly byte[] _salt;
    private int _nextPublicTarget;

    public PackageAnonymizer(RedactionLedger ledger, byte[] salt)
    {
        Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _salt = salt is { Length: > 0 } ? salt : throw new ArgumentException("A package salt is required.", nameof(salt));
    }

    public RedactionLedger Ledger { get; }

    /// <summary>Returns a transformed deep copy; the source payload is not mutated.</summary>
    public JsonObject RedactObject(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = source.DeepClone().AsObject();
        return (JsonObject)RedactNode(clone, string.Empty, string.Empty)!;
    }

    /// <summary>Redacts free text such as an event source without changing identity fields.</summary>
    public string RedactText(string value) => string.IsNullOrEmpty(value) ? value : RedactValue(value, "text", string.Empty);

    private JsonNode? RedactNode(JsonNode? node, string key, string path)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var property in obj)
                {
                    var childPath = path.Length == 0 ? property.Key : path + "/" + property.Key;
                    result[property.Key] = ProtectedKeys.Contains(property.Key)
                        ? property.Value?.DeepClone()
                        : RedactNode(property.Value, property.Key, childPath);
                }

                return result;
            }
            case JsonArray array:
            {
                var result = new JsonArray();
                var index = 0;
                foreach (var item in array)
                {
                    result.Add(RedactNode(item, key, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"));
                    index++;
                }

                return result;
            }
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(RedactValue(text, key, path));
            default:
                return node.DeepClone();
        }
    }

    private string RedactValue(string text, string key, string path)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lowerKey = key.ToLowerInvariant();
        if (ProtectedKeys.Contains(key)) return text;

        if (lowerKey is "authorization" or "proxy-authorization" or "proxy_authorization")
        {
            Ledger.Record(RedactionCategories.Authorization);
            return "[redacted:authorization]";
        }

        if (lowerKey is "cookie" or "set-cookie" or "set_cookie")
        {
            Ledger.Record(RedactionCategories.Cookie);
            return "[redacted:cookie]";
        }

        if (IsSubscriptionKey(lowerKey))
        {
            Ledger.Record(RedactionCategories.SubscriptionUrl);
            return "[redacted:subscription-url]";
        }

        if (IsPasswordKey(lowerKey))
        {
            Ledger.Record(RedactionCategories.Password);
            return "[redacted:password]";
        }

        if (IsTokenKey(lowerKey))
        {
            Ledger.Record(RedactionCategories.Token);
            return "[redacted:token]";
        }

        if (UserKeys.Contains(lowerKey))
        {
            Ledger.Record(RedactionCategories.UserName);
            return Alias(RedactionCategories.UserName, text.Trim(), "user");
        }

        if (DeviceKeys.Contains(lowerKey))
        {
            Ledger.Record(RedactionCategories.DeviceName);
            return Alias(RedactionCategories.DeviceName, text.Trim(), "device");
        }

        if (IsInterfaceIdentity(lowerKey, path))
        {
            // Interface name/description/alias and wireless SSID are device identity even when
            // the generic key name ("name", "description") is used elsewhere in a payload.
            var original = text.Trim();
            var deviceAlias = Alias(RedactionCategories.DeviceName, original, "device");
            if (original.Length >= 4)
            {
                // Later free text (diagnosis summary, probe notice) can quote the same identity;
                // replace the exact known value rather than leaving it in prose.
                _identityReplacements[original] = deviceAlias;
            }

            Ledger.Record(RedactionCategories.DeviceName);
            return deviceAlias;
        }

        var result = ReplaceKnownIdentities(text);
        if (IsProxyServerKey(lowerKey))
        {
            result = RedactProxyServerValue(result);
        }

        var proxyVariable = ProxyVariable.Match(result);
        if (proxyVariable.Success)
        {
            result = proxyVariable.Groups["name"].Value + "="
                + RedactProxyVariableValue(proxyVariable.Groups["name"].Value, proxyVariable.Groups["value"].Value);
        }
        else if (IsNoProxyKey(lowerKey))
        {
            result = RedactNoProxyList(result);
        }

        result = StripUrlUserInfo(result);
        result = RedactBareAuthorities(result);
        result = RedactHeaders(result);
        result = RedactSecretAssignments(result);
        result = RedactUrls(result);
        result = RedactInlineProxyEndpoints(result);
        result = RedactInternalHostTokens(result);
        result = RedactPaths(result);
        result = RedactAddresses(result, IsNetworkAddressKey(lowerKey));

        if (IsHostKey(lowerKey)) result = RedactBareHost(result);
        return result;
    }

    private static bool IsInterfaceIdentity(string key, string path) =>
        key is "ssid" or "wlan_ssid" or "wifi_ssid" or "network_name" or "interface_alias"
            or "interface_name" or "interface_description" or "adapter_name" or "friendly_name"
        || (key is "name" or "description" or "alias" && IsInterfacePath(path));

    private static bool IsInterfacePath(string path) =>
        path.Contains("interfaces[", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/interfaces/", StringComparison.OrdinalIgnoreCase);

    private static bool IsProxyServerKey(string key) =>
        key == "proxy"
        || key.Contains("proxy_server", StringComparison.Ordinal)
        || key.Contains("proxy-server", StringComparison.Ordinal)
        || key.Contains("proxyserver", StringComparison.Ordinal)
        || key.Contains("proxy_endpoint", StringComparison.Ordinal);

    private static bool IsNoProxyKey(string key) =>
        key.Contains("no_proxy", StringComparison.Ordinal)
        || key.Contains("no-proxy", StringComparison.Ordinal)
        || key == "noproxy";

    private static bool IsNetworkAddressKey(string key) =>
        key is "destination_prefix" or "destinationprefix" or "destination-prefix"
            or "route_prefix" or "routeprefix" or "route-prefix"
            or "network_prefix" or "networkprefix" or "network-prefix"
            or "cidr";

    private string ReplaceKnownIdentities(string value)
    {
        if (_identityReplacements.Count == 0) return value;
        var result = value;
        foreach (var pair in _identityReplacements.OrderByDescending(item => item.Key.Length))
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }

        return result;
    }

    private static bool IsSubscriptionKey(string key) =>
        key.Contains("subscription", StringComparison.Ordinal)
        || key.Contains("subscribe", StringComparison.Ordinal)
        || key is "sub_url" or "suburl" or "subscription";

    private static bool IsPasswordKey(string key) =>
        key.Contains("password", StringComparison.Ordinal)
        || key.Contains("passwd", StringComparison.Ordinal)
        || key.Contains("secret", StringComparison.Ordinal)
        || key is "pwd" or "private_key" or "proxy_password";

    private static bool IsTokenKey(string key) =>
        key.Contains("token", StringComparison.Ordinal)
        || key is "api_key" or "apikey" or "access_key" or "accesskey" or "credential" or "credentials"
            or "auth_key" or "proxy_username" or "proxy_user";

    private static bool IsHostKey(string key) =>
        key.Contains("host", StringComparison.Ordinal)
        || key.Contains("server", StringComparison.Ordinal)
        || key.Contains("domain", StringComparison.Ordinal)
        || key.Contains("suffix", StringComparison.Ordinal)
        || key.Contains("search", StringComparison.Ordinal);

    private string StripUrlUserInfo(string value) => SchemeUserInfo.Replace(value, match =>
    {
        var info = match.Groups["info"].Value;
        Ledger.Record(info.Contains(':') ? RedactionCategories.ProxyCredential : RedactionCategories.UserName);
        return match.Groups["scheme"].Value;
    });

    private string RedactBareAuthorities(string value) => BareAuthority.Replace(value, match =>
    {
        var host = match.Groups["host"].Value;
        var port = string.Empty;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host[(colon + 1)..].All(char.IsAsciiDigit) && host[(colon + 1)..].Length is > 0 and <= 5)
        {
            port = host[colon..];
            host = host[..colon];
        }

        Ledger.Record(RedactionCategories.ProxyCredential);
        return AliasHost(host) + port;
    });

    private string RedactHeaders(string value)
    {
        var result = HeaderLine.Replace(value, match =>
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            Ledger.Record(name.Contains("cookie", StringComparison.Ordinal)
                ? RedactionCategories.Cookie
                : RedactionCategories.Authorization);
            return name + ": [redacted]";
        });
        result = BearerToken.Replace(result, _ =>
        {
            Ledger.Record(RedactionCategories.Authorization);
            return "Bearer [redacted]";
        });
        result = BasicToken.Replace(result, _ =>
        {
            Ledger.Record(RedactionCategories.Authorization);
            return "Basic [redacted]";
        });
        return result;
    }

    private string RedactSecretAssignments(string value)
    {
        var result = PasswordMarker.Replace(value, _ =>
        {
            Ledger.Record(RedactionCategories.Password);
            return "[redacted:password]";
        });
        result = Jwt.Replace(result, _ =>
        {
            Ledger.Record(RedactionCategories.Token);
            return "[redacted:token]";
        });
        result = SecretAssignment.Replace(result, match =>
        {
            var name = match.Groups["name"].Value;
            var lower = name.ToLowerInvariant();
            Ledger.Record(IsPasswordKey(lower) ? RedactionCategories.Password
                : lower is "sig" or "signature" or "auth" ? RedactionCategories.QuerySecret
                : RedactionCategories.Token);
            return name + "=[redacted]";
        });
        return result;
    }

    private string RedactUrls(string value) => UrlToken.Replace(value, match =>
    {
        var raw = match.Value;
        if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            Ledger.Record(RedactionCategories.LocalPath);
            return Alias(RedactionCategories.LocalPath, raw, "path");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            Ledger.Record(RedactionCategories.UnparseableUrl);
            return "[redacted:url]";
        }

        try
        {
            if (uri.AbsolutePath.Contains("subscribe", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains("subscription", StringComparison.OrdinalIgnoreCase))
            {
                Ledger.Record(RedactionCategories.SubscriptionUrl);
                return "[redacted:subscription-url]";
            }

            if (uri.UserInfo.Length > 0)
            {
                Ledger.Record(uri.UserInfo.Contains(':')
                    ? RedactionCategories.ProxyCredential
                    : RedactionCategories.UserName);
            }

            if (uri.AbsolutePath is not ("" or "/")) Ledger.Record(RedactionCategories.UrlPath);
            if (uri.Query.Length > 0) Ledger.Record(RedactionCategories.QuerySecret);
            if (uri.Fragment.Length > 0) Ledger.Record(RedactionCategories.UrlPath);

            var host = uri.IdnHost;
            var alias = AliasHost(host);
            var builder = new StringBuilder(uri.Scheme).Append("://");
            if (IPAddress.TryParse(host.Trim('[', ']'), out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                builder.Append('[').Append(alias).Append(']');
            }
            else
            {
                builder.Append(alias);
            }

            if (!uri.IsDefaultPort && uri.Port > 0)
                builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
            return builder.Append('/').ToString();
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            Ledger.Record(RedactionCategories.UnparseableUrl);
            return "[redacted:url]";
        }
    });

    private string RedactPaths(string value)
    {
        var result = ReplacePath(value, ExtendedPath);
        result = ReplacePath(result, UncPath);
        result = ReplacePath(result, WindowsPath);
        result = ReplacePath(result, EnvironmentPath);
        result = ReplacePath(result, UnixPath);
        result = ReplacePath(result, TildePath);
        return result;
    }

    private string ReplacePath(string value, Regex pattern) => pattern.Replace(value, match =>
    {
        RegisterPathIdentities(match.Value);
        Ledger.Record(RedactionCategories.LocalPath);
        return Alias(RedactionCategories.LocalPath, match.Value, "path");
    });

    private void RegisterPathIdentities(string path)
    {
        var extendedUnc = ExtendedUncHost.Match(path);
        if (extendedUnc.Success)
        {
            var extendedDevice = extendedUnc.Groups["host"].Value;
            if (extendedDevice.Length > 0) Alias(RedactionCategories.DeviceName, extendedDevice, "device");
        }
        else
        {
            var unc = UncHost.Match(path);
            if (unc.Success)
            {
                var device = unc.Value.Trim('\\');
                if (device.Length > 0) Alias(RedactionCategories.DeviceName, device, "device");
            }
        }

        var user = PathUser.Match(path);
        if (!user.Success) user = UnixPathUser.Match(path);
        if (user.Success && !string.IsNullOrWhiteSpace(user.Groups["name"].Value))
            Alias(RedactionCategories.UserName, user.Groups["name"].Value, "user");
    }

    private string RedactAddresses(string value, bool network)
    {
        var builder = new StringBuilder(value.Length);
        var last = 0;
        foreach (Match match in AddressToken.Matches(value))
        {
            var token = match.Value;
            var slash = token.IndexOf('/');
            var baseToken = slash >= 0 ? token[..slash] : token;
            if (_syntheticAddresses.Contains(baseToken)) continue;

            if (!AddressMapping.TryMap(token, _salt, network, out var mapped, out var category)) continue;
            builder.Append(value, last, match.Index - last).Append(mapped);
            last = match.Index + match.Length;
            _syntheticAddresses.Add(slash >= 0 ? mapped[..mapped.IndexOf('/')] : mapped);
            Ledger.Record(category);
        }

        if (last == 0) return value;
        builder.Append(value, last, value.Length - last);
        return builder.ToString();
    }

    private string RedactInlineProxyEndpoints(string value) => InlineProxyEndpoint.Replace(value, match =>
    {
        var endpoint = match.Groups["endpoint"].Value;
        return match.Groups["scheme"].Value + RedactProxyEndpoint(endpoint);
    });

    private string RedactInternalHostTokens(string value) => InternalHostToken.Replace(value, match =>
        AliasHost(match.Groups["host"].Value) + (match.Groups["port"].Success ? ":" + match.Groups["port"].Value : string.Empty));

    private string RedactProxyServerValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var parts = value.Split(';');
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = RedactProxyServerPart(parts[index]);
        }

        return string.Join(';', parts);
    }

    private string RedactProxyServerPart(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal))
        {
            var parts = value.Split(',');
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = RedactProxyServerToken(parts[index]);
            }

            return string.Join(',', parts);
        }

        return RedactProxyServerToken(value);
    }

    private string RedactProxyServerToken(string value)
    {
        var equals = value.IndexOf('=');
        if (equals > 0)
        {
            var scheme = value[..equals].Trim();
            if (scheme.Length > 0 && scheme.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '+' or '-' or '.'))
            {
                return value[..(equals + 1)] + RedactProxyEndpoint(value[(equals + 1)..]);
            }
        }

        return RedactProxyEndpoint(value);
    }

    private string RedactProxyEndpoint(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return value;

        var hadUserInfo = false;
        var at = trimmed.LastIndexOf('@');
        if (at > 0)
        {
            hadUserInfo = true;
            var info = trimmed[..at];
            Ledger.Record(info.Contains(':') ? RedactionCategories.ProxyCredential : RedactionCategories.UserName);
            trimmed = trimmed[(at + 1)..];
        }

        if (trimmed.Contains("://", StringComparison.Ordinal)) return RedactUrls(trimmed);
        if (IsProxyKeyword(trimmed)) return trimmed;

        var (host, port, bracketed) = SplitHostPort(trimmed);
        if (host.Length == 0) return hadUserInfo ? "[redacted:proxy-endpoint]" : value;
        if (!IPAddress.TryParse(host, out _) && !IsDnsName(host))
        {
            return hadUserInfo ? "[redacted:proxy-endpoint]" : value;
        }

        var alias = AliasHost(host);
        return (bracketed ? "[" + alias + "]" : alias) + port;
    }

    private string RedactProxyVariableValue(string name, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return value;
        if (IsNoProxyKey(name.ToLowerInvariant())) return RedactNoProxyList(trimmed);
        if (trimmed.Contains("://", StringComparison.Ordinal)) return RedactUrls(trimmed);
        return RedactProxyServerValue(trimmed);
    }

    private string RedactNoProxyList(string value)
    {
        var parts = value.Split(',');
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = RedactNoProxyEntry(parts[index]);
        }

        return string.Join(',', parts);
    }

    private string RedactNoProxyEntry(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed == "*") return "*";

        var prefix = string.Empty;
        var rest = trimmed;
        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            prefix = "*.";
            rest = trimmed[2..];
        }
        else if (trimmed.StartsWith('.'))
        {
            prefix = ".";
            rest = trimmed[1..];
        }

        if (rest.Length == 0) return trimmed;
        if (rest.Contains('/'))
        {
            if (AddressMapping.TryMap(rest, _salt, network: true, out var mapped, out var category))
            {
                var slash = mapped.IndexOf('/');
                _syntheticAddresses.Add(slash >= 0 ? mapped[..slash] : mapped);
                Ledger.Record(category);
                return prefix + mapped;
            }

            return prefix + "[redacted:address]";
        }

        var (host, port, bracketed) = SplitHostPort(rest);
        if (host.Length == 0) return trimmed;
        if (IPAddress.TryParse(host, out _) || IsDnsName(host))
        {
            var alias = AliasHost(host);
            return prefix + (bracketed ? "[" + alias + "]" : alias) + port;
        }

        return prefix + "[redacted:host]";
    }

    private static (string Host, string Port, bool Bracketed) SplitHostPort(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close > 0)
            {
                var host = trimmed[1..close];
                var remainder = trimmed[(close + 1)..];
                if (remainder.Length > 1 && remainder[0] == ':' && remainder[1..].All(char.IsAsciiDigit))
                {
                    return (host, remainder, true);
                }

                return (host, string.Empty, true);
            }
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && colon < trimmed.Length - 1)
        {
            var suffix = trimmed[(colon + 1)..];
            if (suffix.Length <= 5 && suffix.All(char.IsAsciiDigit))
            {
                return (trimmed[..colon], trimmed[colon..], false);
            }
        }

        return (trimmed, string.Empty, false);
    }

    private static bool IsProxyKeyword(string value) =>
        value.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("PROXY", StringComparison.OrdinalIgnoreCase)
        || value.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    private string RedactBareHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var candidate = value.Trim();
        if (candidate.Contains(' ') || candidate.Contains('/') || candidate.Contains('@')) return value;
        if (IPAddress.TryParse(candidate, out _)) return value;
        if (_aliasValues.Contains(candidate)) return value;

        var (host, port, bracketed) = SplitHostPort(candidate);
        if ((port.Length > 0 || bracketed)
            && host.Length > 0
            && (IPAddress.TryParse(host, out _) || IsDnsName(host)))
        {
            var alias = AliasHost(host);
            return (bracketed ? "[" + alias + "]" : alias) + port;
        }

        if (!IsDnsName(candidate)) return value;

        if (IsInternalHostname(candidate))
        {
            Ledger.Record(RedactionCategories.PrivateHost);
            return Alias(RedactionCategories.PrivateHost, candidate.ToLowerInvariant(), "private-host");
        }

        Ledger.Record(RedactionCategories.PublicTarget);
        return Alias(RedactionCategories.PublicTarget, candidate.ToLowerInvariant(), "target");
    }

    private string AliasHost(string host)
    {
        var candidate = host.Trim().Trim('[', ']');
        if (candidate.Length == 0) return host;
        if (_aliasValues.Contains(candidate)) return candidate;

        if (IPAddress.TryParse(candidate, out var address))
        {
            if (_syntheticAddresses.Contains(candidate)) return candidate;
            if (AddressMapping.TryMap(candidate, _salt, out var mapped, out var category))
            {
                _syntheticAddresses.Add(mapped);
                Ledger.Record(category);
                return mapped;
            }

            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "[redacted-address]"
                : "redacted-address";
        }

        if (IsInternalHostname(candidate))
        {
            Ledger.Record(RedactionCategories.PrivateHost);
            return Alias(RedactionCategories.PrivateHost, candidate.ToLowerInvariant(), "private-host");
        }

        Ledger.Record(RedactionCategories.PublicTarget);
        return Alias(RedactionCategories.PublicTarget, candidate.ToLowerInvariant(), "target");
    }

    private static bool IsDnsName(string value)
    {
        if (value.Length is 0 or > 253) return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.');
    }

    private static bool IsInternalHostname(string value)
    {
        var name = value.TrimEnd('.');
        if (name.Length == 0) return true;
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".localhost", StringComparison.Ordinal)
            || lower.EndsWith(".local", StringComparison.Ordinal)
            || lower.EndsWith(".localdomain", StringComparison.Ordinal)
            || lower.EndsWith(".internal", StringComparison.Ordinal)
            || lower.EndsWith(".invalid", StringComparison.Ordinal)
            || lower.EndsWith(".lan", StringComparison.Ordinal)
            || lower.EndsWith(".home", StringComparison.Ordinal)
            || lower.EndsWith(".corp", StringComparison.Ordinal))
        {
            return true;
        }

        // A single-label name is a Windows computer name more often than a public domain.
        return !name.Contains('.');
    }

    private string Alias(string category, string original, string prefix)
    {
        var key = category + "\u001f" + original;
        if (_aliases.TryGetValue(key, out var existing)) return existing;

        if (!_usedLabels.TryGetValue(prefix, out var used))
        {
            used = new HashSet<string>(StringComparer.Ordinal);
            _usedLabels[prefix] = used;
        }

        if (category == RedactionCategories.PublicTarget)
        {
            // Design 04 §5: a public business target is exported as target-A, target-B, ...
            // Public domains are not user identity, so a deterministic per-package ordinal is
            // sufficient and keeps the documented spelling.
            string ordinal;
            do
            {
                ordinal = prefix + "-" + Letters(_nextPublicTarget++);
            }
            while (used.Contains(ordinal));

            used.Add(ordinal);
            _aliases[key] = ordinal;
            _aliasValues.Add(ordinal);
            return ordinal;
        }

        var digest = System.Security.Cryptography.HMACSHA256.HashData(_salt,
            System.Text.Encoding.UTF8.GetBytes(category + "\u001f" + original));
        var baseCode = digest[0] % 26 * 26 + digest[1] % 26;
        var alias = AssignLabel(used, prefix, baseCode);
        used.Add(alias);
        _aliases[key] = alias;
        _aliasValues.Add(alias);
        return alias;
    }

    private static string AssignLabel(HashSet<string> used, string prefix, int baseCode)
    {
        for (var offset = 0; offset < 676; offset++)
        {
            var candidate = prefix + "-" + TwoLetters(baseCode + offset);
            if (!used.Contains(candidate)) return candidate;
        }

        // More than 676 distinct values in one category is not expected for a single session.
        for (var fallback = 0; ; fallback++)
        {
            var candidate = prefix + "-" + TwoLetters(baseCode) + Letters(fallback);
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string TwoLetters(int code)
    {
        code %= 676;
        return new string(new[]
        {
            (char)('A' + code / 26),
            (char)('A' + code % 26),
        });
    }

    private static string Letters(int index)
    {
        var result = string.Empty;
        do
        {
            result = (char)('A' + index % 26) + result;
            index = index / 26 - 1;
        }
        while (index >= 0);

        return result;
    }
}
