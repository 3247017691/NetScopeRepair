using System.Text.Json;

namespace NetScope.Adapters;

/// <summary>
/// Whitelist parser for the Apex (mihomo-based) shared_preferences.json file.
/// Only five diagnostics facts are read: mixed port, mode, tun auto-route, dns mode and
/// fake-ip-range. Every other property — subscriptions, credentials, provider lists, full config
/// text — is never traversed, stored or echoed. Flutter's "flutter." key prefix is stripped before
/// matching; dotted and one-level tun/dns containers are accepted.
/// </summary>
internal static class ApexConfigParser
{
    public const string ParserVersion = "apex-shared-preferences-json/1.0.0";

    private static readonly string[] ExpectedKeys =
    [
        "mixed_port",
        "mode",
        "tun_auto_route",
        "dns_mode",
        "dns_fake_ip_range",
    ];

    public static WhitelistParseResult Parse(string text)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var gaps = new List<string>();
        string? invalid = null;
        var sawUnknown = false;
        var recognized = 0;

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return WhitelistParseResult.Invalid("shared_preferences.json 根节点不是对象");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                Visit(property.Name, property.Value, prefix: null);
            }
        }
        catch (JsonException exception)
        {
            var reason = $"JSON 无法解析（行 {exception.LineNumber}, 列 {exception.BytePositionInLine}）";
            return WhitelistParseResult.Invalid(reason);
        }

        if (invalid is not null)
        {
            gaps.Add(invalid);
            return new WhitelistParseResult
            {
                Valid = false,
                ValidityReason = invalid,
                Facts = facts,
                Gaps = gaps,
            };
        }

        if (recognized == 0)
        {
            return WhitelistParseResult.Invalid("未识别到白名单字段");
        }

        if (sawUnknown)
        {
            gaps.Add("配置包含白名单之外的键；其内容未被读取，也未进入事实或文本。");
        }

        foreach (var expected in ExpectedKeys)
        {
            if (!facts.ContainsKey(expected))
            {
                gaps.Add($"{expected}: 未提供，保持未知");
            }
        }

        return new WhitelistParseResult
        {
            Valid = true,
            Facts = facts,
            Gaps = gaps,
        };

        void Visit(string name, JsonElement value, string? prefix)
        {
            var normalized = StripFlutterPrefix(name);
            var full = prefix is null ? normalized : prefix + "." + normalized;
            var factKey = MapKey(full);

            if (factKey is null)
            {
                var lower = normalized.ToLowerInvariant();
                if (prefix is null && lower is "tun" or "dns" && value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nested in value.EnumerateObject())
                    {
                        Visit(nested.Name, nested.Value, lower);
                    }

                    return;
                }

                sawUnknown = true;
                return;
            }

            recognized++;
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined)
            {
                invalid ??= $"白名单字段 {factKey} 的类型不是标量";
                return;
            }

            var raw = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };

            if (!TryValidate(factKey, raw, out var canonical, out var reason))
            {
                invalid ??= reason;
                return;
            }

            if (facts.TryGetValue(factKey, out var existing))
            {
                if (!string.Equals(existing, canonical, StringComparison.Ordinal))
                {
                    invalid ??= $"白名单字段 {factKey} 出现多个取值，无法确定";
                }

                return;
            }

            facts[factKey] = canonical;
        }
    }

    /// <summary>Validates already-minimized runtime-API facts, dropping anything outside the whitelist.</summary>
    public static IReadOnlyDictionary<string, string> NormalizeRuntimeFacts(IReadOnlyDictionary<string, string> facts)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in facts)
        {
            var factKey = MapKey(key);
            if (factKey is null || normalized.ContainsKey(factKey))
            {
                continue;
            }

            if (TryValidate(factKey, value, out var canonical, out _))
            {
                normalized[factKey] = canonical;
            }
        }

        return normalized;
    }

    private static bool TryValidate(string factKey, string value, out string canonical, out string? reason)
    {
        switch (factKey)
        {
            case "mixed_port":
                return ConfigValueValidation.TryPort(value, out canonical, out reason);
            case "mode":
            case "dns_mode":
                return ConfigValueValidation.TryMode(value, out canonical, out reason);
            case "tun_auto_route":
                return ConfigValueValidation.TryBoolean(value, out canonical, out reason);
            case "dns_fake_ip_range":
                return ConfigValueValidation.TryFakeIpRange(value, out canonical, out reason);
            default:
                canonical = string.Empty;
                reason = "白名单字段未定义校验规则";
                return false;
        }
    }

    private static string StripFlutterPrefix(string name)
    {
        var value = name.Trim();
        while (value.StartsWith("flutter.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["flutter.".Length..];
        }

        return value;
    }

    private static string? MapKey(string key)
    {
        var normalized = key.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "mixed-port" or "port" or "mixedport" or "proxy-port" => "mixed_port",
            "mode" => "mode",
            "tun-auto-route" or "tun.auto-route" or "auto-route" or "tunautoroute" => "tun_auto_route",
            "dns-mode" or "dns.mode" or "dnsmode" => "dns_mode",
            "dns-fake-ip-range" or "dns.fake-ip-range" or "fake-ip-range" => "dns_fake_ip_range",
            _ => null,
        };
    }
}
