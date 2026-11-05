namespace NetScope.Desktop.Services;

/// <summary>
/// User-visible settings. This file only holds local preferences; it never represents network
/// configuration and M1 never applies it to the system.
/// </summary>
public sealed record AppSettings
{
    public TimeSpan ObservationInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Empty means "no external probe is configured"; the UI then disables probing.</summary>
    public string ProbeTargetEndpoint { get; init; } = string.Empty;

    public string ProbeTargetId { get; init; } = "target-user-configured";

    public int ExpectedStatus { get; init; } = 204;

    public string? ExplicitProxyEndpoint { get; init; }

    /// <summary>Null keeps the platform default. Forcing a family is not enforced by M1 transport.</summary>
    public string? AddressFamily { get; init; }

    /// <summary>Null asks the user on close; true hides to tray, false exits.</summary>
    public bool? HideOnClose { get; init; }

    public string? LastSessionPath { get; init; }
}

public sealed class SettingsStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsStore(AppPaths paths) => Paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public AppPaths Paths { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return new AppSettings();
            var json = File.ReadAllText(Paths.SettingsFile);
            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            // A damaged settings file must not stop the diagnostic tool from starting.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Paths.EnsureCreated();
        var temporary = Paths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Paths.SettingsFile, overwrite: true);
    }
}
