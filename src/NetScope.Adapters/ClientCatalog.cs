using System.Security;

namespace NetScope.Adapters;

/// <summary>One supported client definition: stable id, display name and process-name allow-list.</summary>
internal sealed record KnownClient
{
    public required string ClientId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Executable names accepted for this adapter version. Names outside this list are not
    /// guessed to be the client; they stay unknown or are reported as an unidentified listener.
    /// </summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }
}

/// <summary>Versioned identity catalogue. Extending it is an adapter change, not an inference.</summary>
internal static class ClientCatalog
{
    /// <summary>Bump when the process-name allow-list changes; exposed in status notes.</summary>
    public const string IdentityVersion = "client-identity/1.0.0";

    public static readonly KnownClient Nice = new()
    {
        ClientId = KnownClientIds.Nice,
        DisplayName = "Nice加速",
        ProcessNames = ["NiceProxy", "Nice加速", "Nice"],
    };

    public static readonly KnownClient Apex = new()
    {
        ClientId = KnownClientIds.Apex,
        DisplayName = "农夫山泉-极速版（Apex）",
        ProcessNames = ["Apex", "农夫山泉-极速版", "农夫山泉"],
    };

    public static readonly IReadOnlyList<KnownClient> All = [Nice, Apex];

    public static bool Matches(KnownClient client, ProcessObservation process)
    {
        var name = NormalizeProcessName(process.Name);
        if (name.Length > 0 && MatchesAny(client, name))
        {
            return true;
        }

        var file = ExecutableName(process.ExecutablePath);
        return file.Length > 0 && MatchesAny(client, file);
    }

    private static bool MatchesAny(KnownClient client, string name) =>
        client.ProcessNames.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var value = name.Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    private static string ExecutableName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return string.Empty;
        }
    }
}
