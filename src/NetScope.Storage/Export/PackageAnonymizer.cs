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
        "evidence_id", "evidence_ids", "counter_evidence_ids",
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

    /// <summary>
    /// A device/interface name quoted after an explicit entity keyword, for example
    /// <c>adapter "Ethernet 2"</c> or <c>interface 'vEthernet (Default Switch)'</c>. Error text is
    /// the only place such a name can appear, so the keyword plus quoting is the structure that
    /// makes it redactable instead of relying on the name already being listed in
    /// <c>interfaces.value</c>.
    /// </summary>
    private static readonly Regex NamedDeviceQuoted = new(
        @"(?<prefix>(?i:\b(?:adapter|interface|nic|switch|filter|port|device)\b)|网卡|接口|适配器|交换机|过滤器|设备)\s*(?:(?i:name|named|called)\s*|名称\s*[:：]?\s*|名为\s*)?(?<quote>[""'])(?<name>[^""'\r\n]{1,80})\k<quote>",
        RegexOptions.Compiled);

    /// <summary>An unquoted multi-part device name introduced by an entity keyword, e.g. <c>interface Synthetic-Uplink-A</c>.</summary>
    private static readonly Regex NamedDeviceUnquoted = new(
        @"(?<prefix>(?i:\b(?:adapter|interface|nic|device|switch|filter|port)\b)\s+|(?:网卡|接口|适配器|交换机|过滤器|设备)\s*[:：]?\s*)(?<name>[A-Za-z][A-Za-z0-9]*(?:[-_.][A-Za-z0-9]+){1,8})(?![\w.\-])",
        RegexOptions.Compiled);

    /// <summary>
    /// Composite adapter/switch/filter names that are identity on their own, including nested
    /// vendor combinations such as <c>vEthernet (WSL (Hyper-V firewall))</c> or a virtual switch
    /// extension filter.
    /// </summary>
    private static readonly Regex CompositeDeviceToken = new(
        @"(?i)(?<![\w.\-/])(?:" +
        @"(?:vEthernet|vSwitch|vNIC)(?:\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\))?(?:\s*#?\d+)?" +
        @"|WAN\s+Miniport(?:\s*\([^)]*\))?" +
        @"|(?:Hyper-V\s+)?Virtual\s+(?:Ethernet\s+|Switch\s+)?(?:Adapter|Switch|Filter)(?:\s+Extension)?(?:\s+Filter)?(?:\s*#?\d+)?" +
        @"|Microsoft\s+Wi-?Fi\s+Direct\s+Virtual\s+Adapter(?:\s*#?\d+)?" +
        @"|Npcap\s+Loopback\s+Adapter" +
        @"|TAP-Windows\s+Adapter(?:\s+V\d+)?" +
        @"|(?:WireGuard|Tailscale|Wintun|OpenVPN|ProtonVPN|Mullvad)\s+(?:Tunnel|Adapter)" +
        @"|Loopback\s+Pseudo-Interface\s+\d+" +
        @"|(?:Ethernet|Wi-?Fi|WLAN|Local\s+Area\s+Connection|Bluetooth\s+Network\s+Connection)(?:\s+\d+)?" +
        @"|(?:以太网|本地连接|无线网络连接|虚拟交换机|虚拟网卡|网络适配器|网卡|交换机|过滤器)(?:\s*\d+)?" +
        @")(?![\w.\-])", RegexOptions.Compiled);

    /// <summary>A quoted token whose content contains a device word, e.g. <c>'Nice WFP Filter'</c>.</summary>
    private static readonly Regex QuotedToken = new(
        @"(?<quote>[""'])(?<name>[^""'\r\n]{2,80}?)\k<quote>", RegexOptions.Compiled);

    /// <summary>A capitalized multi-word filter/switch name such as <c>Default Switch</c> or <c>Nice WFP Filter</c>.</summary>
    private static readonly Regex CombinedCapitalizedDevice = new(
        @"(?<![\w.\-/])(?:[A-Z][\w-]*\s+){1,3}(?:Adapter|Miniport|Switch|Filter|Tunnel)(?![\w.\-])",
        RegexOptions.Compiled);

    private static readonly Regex AliasLabel = new(
        @"^(?:user|device|path|private-host|target)-[A-Z]{1,4}$", RegexOptions.Compiled);

    private static readonly string[] DeviceWords =
    {
        "adapter", "miniport", "switch", "filter", "tunnel", "ethernet", "wi-fi", "wifi", "wlan",
        "vethernet", "vswitch", "vnic", "hyper-v", "npcap", "tap-windows", "wireguard", "tailscale",
        "wintun", "loopback", "以太网", "本地连接", "无线网络连接", "网卡", "接口", "交换机", "过滤器",
        "适配器", "虚拟",
    };

    private static readonly HashSet<string> IdentityStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "none", "null", "true", "false", "default", "synthetic", "adapter", "interface",
        "miniport", "switch", "filter", "tunnel", "device", "port", "网卡", "接口", "适配器",
    };

    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _identityReplacements = new(StringComparer.Ordinal);
    private readonly List<string> _replacementKeys = new();
    private Regex? _replacementPattern;
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

    /// <summary>
    /// First pass over the whole package. Registers user, device and interface identities before
    /// any value is rewritten, so a name that only appears in an error message, or appears in a
    /// different traversal order than the structured field, still resolves to the same stable
    /// alias instead of leaking as free text.
    /// </summary>
    public void Precollect(JsonNode? node) => WalkIdentities(node, string.Empty);

    /// <summary>Pre-collects identities from a free-text value such as an event source.</summary>
    public void PrecollectText(string? value)
    {
        if (!string.IsNullOrEmpty(value)) ScanIdentityText(value);
    }

    private void WalkIdentities(JsonNode? node, string path)
    {
        switch (node)
        {
            case null:
                return;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    var key = property.Key;
                    var childPath = path.Length == 0 ? key : path + "/" + key;
                    if (ProtectedKeys.Contains(key)) continue;
                    var lowerKey = key.ToLowerInvariant();
                    if (IsInterfaceIdentity(lowerKey, childPath))
                    {
                        if (property.Value is JsonValue interfaceValue
                            && interfaceValue.TryGetValue<string>(out var interfaceText))
                        {
                            RegisterDeviceIdentity(interfaceText);
                        }

                        continue;
                    }

                    if (UserKeys.Contains(lowerKey))
                    {
                        if (property.Value is JsonValue userValue && userValue.TryGetValue<string>(out var userText))
                            RegisterUserIdentity(userText);
                        continue;
                    }

                    if (DeviceKeys.Contains(lowerKey))
                    {
                        if (property.Value is JsonValue deviceValue && deviceValue.TryGetValue<string>(out var deviceText))
                            RegisterDeviceIdentity(deviceText);
                        continue;
                    }

                    WalkIdentities(property.Value, childPath);
                }

                return;
            case JsonArray array:
                var index = 0;
                foreach (var item in array)
                {
                    WalkIdentities(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                    index++;
                }

                return;
            case JsonValue value when value.TryGetValue<string>(out var text):
                ScanIdentityText(text);
                return;
        }
    }

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
            return RedactUserIdentity(text);
        }

        if (DeviceKeys.Contains(lowerKey))
        {
            return RedactDeviceValue(text);
        }

        if (IsInterfaceIdentity(lowerKey, path))
        {
            // Interface name/description/alias and wireless SSID are device identity even when
            // the generic key name ("name", "description") is used elsewhere in a payload. The
            // same identity may already be known from a different event (pre-collection), so the
            // alias must be the same one used when it was registered first-seen.
            return RedactDeviceValue(text);
        }

        var result = ReplaceKnownIdentities(text);
        result = RedactDeviceNames(result);
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
        if (_replacementKeys.Count == 0) return value;
        _replacementPattern ??= BuildReplacementPattern();
        return _replacementPattern.Replace(value, match => _identityReplacements[match.Value]);
    }

    private Regex BuildReplacementPattern()
    {
        // Longest first so "Ethernet 2" wins over a registered "Ethernet"; the lookarounds keep a
        // short user or device name from being replaced inside an unrelated larger word.
        var alternatives = _replacementKeys
            .OrderByDescending(key => key.Length)
            .Select(Regex.Escape);
        return new Regex(
            @"(?<![A-Za-z0-9_])(?:" + string.Join("|", alternatives) + @")(?![A-Za-z0-9_])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private void RegisterIdentityReplacement(string original, string alias)
    {
        if (original.Length < 2 || original.Length > 120) return;
        if (IdentityStopWords.Contains(original) || IsAliasLabel(original)) return;
        if (_identityReplacements.TryGetValue(original, out var existing) && existing == alias) return;
        _identityReplacements[original] = alias;
        if (!_replacementKeys.Contains(original, StringComparer.Ordinal)) _replacementKeys.Add(original);
        _replacementPattern = null;
    }

    private void RegisterDeviceIdentity(string original)
    {
        var trimmed = original.Trim();
        if (trimmed.Length == 0 || IdentityStopWords.Contains(trimmed) || IsAliasLabel(trimmed)) return;
        var alias = Alias(RedactionCategories.DeviceName, trimmed, "device");
        RegisterIdentityReplacement(trimmed, alias);
    }

    private void RegisterUserIdentity(string original)
    {
        var trimmed = original.Trim();
        if (trimmed.Length < 3 || IdentityStopWords.Contains(trimmed) || IsAliasLabel(trimmed)) return;
        var alias = Alias(RedactionCategories.UserName, trimmed, "user");
        RegisterIdentityReplacement(trimmed, alias);
    }

    private string RedactUserIdentity(string text)
    {
        var trimmed = text.Trim();
        if (IsAliasLabel(trimmed)) return trimmed;
        var alias = Alias(RedactionCategories.UserName, trimmed, "user");
        if (trimmed.Length >= 3) RegisterIdentityReplacement(trimmed, alias);
        Ledger.Record(RedactionCategories.UserName);
        return alias;
    }

    private string RedactDeviceValue(string text)
    {
        var trimmed = text.Trim();
        if (IsAliasLabel(trimmed)) return trimmed;
        var alias = Alias(RedactionCategories.DeviceName, trimmed, "device");
        RegisterIdentityReplacement(trimmed, alias);
        Ledger.Record(RedactionCategories.DeviceName);
        return alias;
    }

    private static bool IsAliasLabel(string value) => AliasLabel.IsMatch(value);

    private static bool ContainsDeviceWord(string value)
    {
        var lower = value.ToLowerInvariant();
        return DeviceWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Registers every identity that can be recognised structurally inside one free-text value:
    /// path user/UNC device parts and adapter/switch/filter names quoted or introduced by an
    /// entity keyword. Pre-collection runs this over the whole package before redaction, so the
    /// same name maps to one alias no matter which event mentions it first.
    /// </summary>
    private void ScanIdentityText(string text)
    {
        foreach (var pathMatch in ExtendedPath.Matches(text).Cast<Match>()
                     .Concat(UncPath.Matches(text).Cast<Match>())
                     .Concat(WindowsPath.Matches(text).Cast<Match>()))
        {
            RegisterPathIdentities(pathMatch.Value);
        }

        foreach (Match match in NamedDeviceQuoted.Matches(text))
            RegisterDeviceIdentity(match.Groups["name"].Value);

        foreach (Match match in NamedDeviceUnquoted.Matches(text))
            RegisterDeviceIdentity(match.Groups["name"].Value);

        foreach (Match match in CompositeDeviceToken.Matches(text))
            RegisterDeviceIdentity(match.Value);

        foreach (Match match in QuotedToken.Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (ContainsDeviceWord(name)) RegisterDeviceIdentity(name);
        }

        foreach (Match match in CombinedCapitalizedDevice.Matches(text))
            RegisterDeviceIdentity(match.Value);
    }

    /// <summary>
    /// Replaces adapter, virtual switch and filter names that exist only in free text (typically
    /// a collector error or a diagnosis summary). Replacing by structure is deliberate: matching
    /// only the names already listed under <c>interfaces.value</c> misses names Windows reports
    /// after enumeration itself failed, and it makes the result depend on traversal order.
    /// </summary>
    private string RedactDeviceNames(string value)
    {
        var result = NamedDeviceQuoted.Replace(value, match =>
        {
            var name = match.Groups["name"].Value.Trim();
            if (!LooksLikeDeviceName(name)) return match.Value;
            var alias = AliasDeviceToken(name);
            var quote = match.Groups["quote"].Value;
            var prefixLength = match.Groups["prefix"].Length;
            var quoteIndex = match.Groups["quote"].Index - match.Index;
            return match.Groups["prefix"].Value + match.Value[prefixLength..quoteIndex] + quote + alias + quote;
        });

        result = NamedDeviceUnquoted.Replace(result, match =>
        {
            var name = match.Groups["name"].Value;
            if (!LooksLikeDeviceName(name)) return match.Value;
            return match.Groups["prefix"].Value + AliasDeviceToken(name);
        });

        result = CompositeDeviceToken.Replace(result, match => AliasDeviceToken(match.Value));
        result = QuotedToken.Replace(result, match =>
        {
            var name = match.Groups["name"].Value;
            if (!ContainsDeviceWord(name)) return match.Value;
            var alias = AliasDeviceToken(name.Trim());
            return match.Groups["quote"].Value + alias + match.Groups["quote"].Value;
        });
        result = CombinedCapitalizedDevice.Replace(result, match => AliasDeviceToken(match.Value));
        return result;
    }

    private string AliasDeviceToken(string original)
    {
        var trimmed = original.Trim();
        if (trimmed.Length == 0 || IsAliasLabel(trimmed) || IdentityStopWords.Contains(trimmed)) return original;
        var alias = Alias(RedactionCategories.DeviceName, trimmed, "device");
        RegisterIdentityReplacement(trimmed, alias);
        Ledger.Record(RedactionCategories.DeviceName);
        return alias;
    }

    private static bool LooksLikeDeviceName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is < 2 or > 120) return false;
        if (trimmed.Any(char.IsControl)) return false;
        return true;
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
            if (extendedDevice.Length > 0)
                RegisterIdentityReplacement(extendedDevice,
                    Alias(RedactionCategories.DeviceName, extendedDevice, "device"));
        }
        else
        {
            var unc = UncHost.Match(path);
            if (unc.Success)
            {
                var device = unc.Value.Trim('\\');
                if (device.Length > 0)
                    RegisterIdentityReplacement(device,
                        Alias(RedactionCategories.DeviceName, device, "device"));
            }
        }

        var user = PathUser.Match(path);
        if (!user.Success) user = UnixPathUser.Match(path);
        if (user.Success && !string.IsNullOrWhiteSpace(user.Groups["name"].Value))
            RegisterUserIdentity(user.Groups["name"].Value);
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
