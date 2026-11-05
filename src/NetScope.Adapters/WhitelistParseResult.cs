namespace NetScope.Adapters;

/// <summary>
/// Result of one whitelist parse. <see cref="Facts"/> contains only recognized diagnostics fields;
/// <see cref="ValidityReason"/> and <see cref="Gaps"/> never contain raw configuration text.
/// </summary>
internal sealed record WhitelistParseResult
{
    public required bool Valid { get; init; }

    public string? ValidityReason { get; init; }

    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public static WhitelistParseResult Invalid(string reason, IReadOnlyDictionary<string, string>? facts = null) => new()
    {
        Valid = false,
        ValidityReason = reason,
        Facts = facts ?? new Dictionary<string, string>(StringComparer.Ordinal),
        Gaps = new[] { reason },
    };
}
