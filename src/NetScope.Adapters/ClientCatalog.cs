using System.Security;

namespace NetScope.Adapters;

/// <summary>One supported client definition: stable id, display name and process-name allow-lists.</summary>
internal sealed record KnownClient
{
    public required string ClientId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Executable names accepted for the client's own UI process. Names outside this list are not
    /// guessed to be the client; they stay unknown or are reported as an unidentified listener.
    /// </summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }

    /// <summary>
    /// Executable names that may be a proxy core for this client. A matching name alone is only a
    /// candidate: attribution additionally requires a verifiable parent/child or install-directory
    /// relation to the client's UI process.
    /// </summary>
    public IReadOnlyList<string> CoreProcessNames { get; init; } = Array.Empty<string>();
}

/// <summary>Versioned identity catalogue. Extending it is an adapter change, not an inference.</summary>
internal static class ClientCatalog
{
    /// <summary>Bump when the process-name allow-list changes; exposed in status notes.</summary>
    public const string IdentityVersion = "client-identity/1.1.0";

    public static readonly KnownClient Nice = new()
    {
        ClientId = KnownClientIds.Nice,
        DisplayName = "Nice加速",
        ProcessNames = ["NiceProxy", "Nice加速", "Nice"],
        // The Nice UI and its FlClash/mihomo core are separate processes. These names only nominate
        // a core candidate; the ownership claim still needs parent/child or directory evidence.
        CoreProcessNames = ["FlClashCore", "FlClash", "NiceCore", "clash", "mihomo"],
    };

    public static readonly KnownClient Apex = new()
    {
        ClientId = KnownClientIds.Apex,
        DisplayName = "农夫山泉-极速版（Apex）",
        ProcessNames = ["Apex", "农夫山泉-极速版", "农夫山泉"],
        // Apex runs its mihomo core under a random executable name, so this list can only catch the
        // well-known case; child/directory evidence is what usually identifies the core.
        CoreProcessNames = ["ApexCore", "mihomo", "clash"],
    };

    public static readonly IReadOnlyList<KnownClient> All = [Nice, Apex];

    /// <summary>True when the process is the client's own UI process (not one of its cores).</summary>
    public static bool Matches(KnownClient client, ProcessObservation process) =>
        MatchesName(client.ProcessNames, process);

    /// <summary>True when the name/path looks like a core for this client; never an ownership claim.</summary>
    public static bool MatchesCoreName(KnownClient client, ProcessObservation process) =>
        MatchesName(client.CoreProcessNames, process);

    public static bool IsAnyCoreNameCandidate(ProcessObservation process) =>
        All.Any(client => MatchesCoreName(client, process));

    private static bool MatchesName(IReadOnlyList<string> names, ProcessObservation process)
    {
        if (names.Count == 0)
        {
            return false;
        }

        var name = NormalizeProcessName(process.Name);
        if (name.Length > 0 && MatchesAny(names, name))
        {
            return true;
        }

        var file = ExecutableName(process.ExecutablePath);
        return file.Length > 0 && MatchesAny(names, file);
    }

    private static bool MatchesAny(IReadOnlyList<string> names, string name) =>
        names.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

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
