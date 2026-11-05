namespace NetScope.Adapters;

/// <summary>A platform adapter that looks like a TUN adapter, plus the evidence used to say so.</summary>
internal sealed record TunCandidate(NetworkAdapterObservation Adapter, string Reason, bool Up);

/// <summary>
/// Deliberately narrow TUN heuristic. It names the adapter and the matching evidence, so the UI
/// can show why a candidate was listed; it never claims which client created the adapter.
/// </summary>
internal static class TunAdapterHeuristics
{
    private static readonly string[] Tokens =
    [
        "tun",
        "wintun",
        "tunnel",
        "tap",
        "tapwindows",
        "clash",
        "mihomo",
        "flclash",
        "nice",
        "apex",
    ];

    public static TunCandidate? Describe(NetworkAdapterObservation adapter)
    {
        if (string.Equals(adapter.Kind, "tunnel", StringComparison.OrdinalIgnoreCase))
        {
            return new TunCandidate(adapter, "接口类型为 tunnel", IsUp(adapter));
        }

        var token = FindToken(adapter.Name) ?? FindToken(adapter.Description);
        if (token is not null)
        {
            return new TunCandidate(adapter, $"名称包含 {token}", IsUp(adapter));
        }

        if (adapter.Addresses.Any(address => address.StartsWith("198.18.", StringComparison.Ordinal)))
        {
            return new TunCandidate(adapter, "地址位于 198.18.0.0/15（TUN 风格）", IsUp(adapter));
        }

        return null;
    }

    private static bool IsUp(NetworkAdapterObservation adapter) =>
        string.Equals(adapter.OperationalStatus, "up", StringComparison.OrdinalIgnoreCase);

    private static string? FindToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var token in value.Split([' ', '\t', '_', '-', '(', ')', '[', ']', ':', '/', '\\', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = token.ToLowerInvariant();
            foreach (var candidate in Tokens)
            {
                if (string.Equals(normalized, candidate, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
