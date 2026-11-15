namespace NetScope.Adapters;

/// <summary>
/// Result of one whitelist parse. <see cref="Facts"/> contains only recognized diagnostics fields;
/// <see cref="ValidityReason"/>, <see cref="Notices"/> and <see cref="Gaps"/> never contain raw
/// configuration text.
/// </summary>
internal sealed record WhitelistParseResult
{
    public required bool Valid { get; init; }

    public string? ValidityReason { get; init; }

    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Ignored structures and absent optional fields; shown, never treated as a defect.</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Every expected fact -> present / missing_required / missing_optional / invalid.</summary>
    public IReadOnlyDictionary<string, string> Coverage { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Only real problems: malformed file, invalid value, or a missing required fact.</summary>
    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public static WhitelistParseResult Invalid(string reason, IReadOnlyDictionary<string, string>? facts = null) => new()
    {
        Valid = false,
        ValidityReason = reason,
        Facts = facts ?? new Dictionary<string, string>(StringComparer.Ordinal),
        Gaps = new[] { reason },
    };
}
