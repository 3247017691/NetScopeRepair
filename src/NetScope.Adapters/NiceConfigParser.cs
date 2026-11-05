using System.Text.RegularExpressions;

namespace NetScope.Adapters;

/// <summary>
/// Minimal YAML whitelist parser for the Nice runtime configuration.
/// It deliberately does not implement YAML: only top-level scalar keys and the tun/dns blocks are
/// read. Unknown keys, lists, anchors, tags and full text are ignored, never interpreted or stored.
/// </summary>
internal static partial class NiceConfigParser
{
    public const string ParserVersion = "nice-runtime-yaml/1.0.1";

    private static readonly string[] ExpectedKeys =
    [
        "mixed_port",
        "mode",
        "tun_enable",
        "tun_mtu",
        "dns_fake_ip_range",
        "allow_lan",
    ];

    public static WhitelistParseResult Parse(string text)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var gaps = new List<string>();
        string? invalid = null;
        var sawUnknown = false;
        var recognized = 0;
        string? section = null;
        int? childIndent = null;
        var sectionIsList = false;
        var unknownChildMayOwnList = false;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (raw.Length == 0)
            {
                continue;
            }

            var trimmed = raw.TrimStart(' ');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = raw.Length - trimmed.Length;
            var isListItem = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-";
            if (indent == 0)
            {
                // Every root entry ends the previous block. Unknown parents cannot leave a
                // tun/dns context active, and none of their descendants are root fields.
                section = null;
                childIndent = null;
                sectionIsList = false;
                unknownChildMayOwnList = false;
                if (isListItem)
                {
                    sawUnknown = true;
                    continue;
                }
            }
            else
            {
                if (section is null || sectionIsList)
                {
                    sawUnknown = true;
                    continue;
                }

                childIndent ??= indent;
                if (indent != childIndent.Value)
                {
                    // Deeper descendants belong to a child subtree, even if their keys happen
                    // to be whitelisted. A mismatched dedent is not promoted to a direct child.
                    sawUnknown = true;
                    continue;
                }

                if (isListItem)
                {
                    sawUnknown = true;
                    // YAML permits an indentless sequence under an unknown mapping child.
                    // Otherwise a tun/dns list is outside the supported mapping grammar.
                    sectionIsList = !unknownChildMayOwnList;
                    continue;
                }
            }

            if (raw.Contains('\t'))
            {
                invalid ??= $"第 {index + 1} 行使用制表符，最小解析器不支持";
                break;
            }

            var match = KeyValueLine().Match(trimmed);
            if (!match.Success)
            {
                invalid ??= $"第 {index + 1} 行不是可识别的键值对";
                break;
            }

            var key = NormalizeKey(match.Groups["key"].Value);
            var rawValue = match.Groups["value"].Value;
            if (indent == 0 && key is "tun" or "dns")
            {
                var blockValue = rawValue.Trim();
                if (blockValue.Length == 0 || blockValue.StartsWith('#'))
                {
                    section = key;
                }
                else
                {
                    sawUnknown = true;
                }
                continue;
            }

            var factKey = indent == 0 ? MapRootKey(key) : MapChildKey(section!, key);
            unknownChildMayOwnList = factKey is null;
            if (factKey is null)
            {
                sawUnknown = true;
                continue;
            }

            var value = CleanValue(rawValue);
            if (value.Length == 0)
            {
                invalid ??= $"白名单字段 {factKey} 缺少取值";
                continue;
            }

            recognized++;
            if (!TryValidate(factKey, value, out var canonical, out var reason))
            {
                invalid ??= reason;
                continue;
            }

            if (facts.TryGetValue(factKey, out var existing))
            {
                if (!string.Equals(existing, canonical, StringComparison.Ordinal))
                {
                    invalid ??= $"白名单字段 {factKey} 出现多个取值，无法确定";
                }

                continue;
            }

            facts[factKey] = canonical;
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
            var reason = "未识别到白名单字段";
            return WhitelistParseResult.Invalid(reason);
        }

        if (sawUnknown)
        {
            gaps.Add("配置包含白名单之外的键或结构；其内容未被读取，也未进入事实或文本。");
        }

        foreach (var expected in ExpectedKeys)
        {
            if (!facts.ContainsKey(expected))
            {
                // Absence is unknown, not a default. No value is invented for a missing key.
                gaps.Add($"{expected}: 未提供，保持未知");
            }
        }

        return new WhitelistParseResult
        {
            Valid = true,
            Facts = facts,
            Gaps = gaps,
        };
    }

    private static bool TryValidate(string factKey, string value, out string canonical, out string? reason)
    {
        switch (factKey)
        {
            case "mixed_port":
                return ConfigValueValidation.TryPort(value, out canonical, out reason);
            case "mode":
                return ConfigValueValidation.TryMode(value, out canonical, out reason);
            case "tun_enable":
            case "allow_lan":
                return ConfigValueValidation.TryBoolean(value, out canonical, out reason);
            case "tun_mtu":
                return ConfigValueValidation.TryMtu(value, out canonical, out reason);
            case "dns_fake_ip_range":
                return ConfigValueValidation.TryFakeIpRange(value, out canonical, out reason);
            default:
                canonical = string.Empty;
                reason = "白名单字段未定义校验规则";
                return false;
        }
    }

    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>Root aliases are accepted only at column zero, never beneath another key or list.</summary>
    private static string? MapRootKey(string key) => key switch
    {
        "mixed-port" => "mixed_port",
        "mode" => "mode",
        "tun-enabled" => "tun_enable",
        "tun-mtu" => "tun_mtu",
        "fake-ip-range" or "dns-fake-ip-range" => "dns_fake_ip_range",
        "allow-lan" or "allowlan" => "allow_lan",
        _ => null,
    };

    /// <summary>Only immediate scalar children of these two root mappings are facts.</summary>
    private static string? MapChildKey(string section, string key) => (section, key) switch
    {
        ("tun", "enable") => "tun_enable",
        ("tun", "mtu") => "tun_mtu",
        ("dns", "fake-ip-range") => "dns_fake_ip_range",
        _ => null,
    };

    private static string CleanValue(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var quote = value[0];
        if (quote is '"' or '\'')
        {
            var end = value.IndexOf(quote, 1);
            return end < 0 ? value : value[1..end];
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value[..comment].TrimEnd();
        }

        return value;
    }

    [GeneratedRegex("^(?<key>[A-Za-z0-9_.\\-]+)\\s*:\\s*(?<value>.*)$")]
    private static partial Regex KeyValueLine();
}
