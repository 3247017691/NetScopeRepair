using System.Globalization;
using System.Text.RegularExpressions;

namespace NetScope.Adapters;

/// <summary>
/// Value validation shared by the two whitelist parsers. The parsers only ever accept the
/// documented keys; this type decides whether the accepted values are well formed. Reasons never
/// echo a raw value, so an unexpected secret cannot leak through a validation message.
/// </summary>
internal static partial class ConfigValueValidation
{
    public static bool TryPort(string value, out string canonical, out string? reason)
    {
        reason = null;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535)
        {
            canonical = port.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        canonical = string.Empty;
        reason = "端口取值不是 1..65535 的整数";
        return false;
    }

    public static bool TryMtu(string value, out string canonical, out string? reason)
    {
        reason = null;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mtu)
            && mtu is >= 68 and <= 65535)
        {
            canonical = mtu.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        canonical = string.Empty;
        reason = "MTU 取值不是 68..65535 的整数";
        return false;
    }

    public static bool TryBoolean(string value, out string canonical, out string? reason)
    {
        reason = null;
        var normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "true":
            case "yes":
            case "on":
            case "1":
                canonical = "true";
                return true;
            case "false":
            case "no":
            case "off":
            case "0":
                canonical = "false";
                return true;
            default:
                canonical = string.Empty;
                reason = "取值不是布尔值";
                return false;
        }
    }

    /// <summary>Mode is a small vocabulary token, not free text.</summary>
    public static bool TryMode(string value, out string canonical, out string? reason)
    {
        reason = null;
        if (value.Length is >= 1 and <= 32 && ModePattern().IsMatch(value))
        {
            canonical = value;
            return true;
        }

        canonical = string.Empty;
        reason = "mode 取值不是可识别的模式标识";
        return false;
    }

    public static bool TryFakeIpRange(string value, out string canonical, out string? reason)
    {
        reason = null;
        var match = FakeIpRangePattern().Match(value);
        if (match.Success
            && byte.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a) && a <= 255
            && byte.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b <= 255
            && byte.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c <= 255
            && byte.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d <= 255
            && int.TryParse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) && prefix <= 32)
        {
            canonical = value;
            return true;
        }

        canonical = string.Empty;
        reason = "fake-ip-range 不是有效的 IPv4 CIDR";
        return false;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$")]
    private static partial Regex ModePattern();

    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})/(\d{1,2})$")]
    private static partial Regex FakeIpRangePattern();
}
