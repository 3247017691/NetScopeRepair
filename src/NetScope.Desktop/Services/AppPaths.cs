namespace NetScope.Desktop.Services;

/// <summary>
/// Local data locations derived from Known Folders, never from a hardcoded user name.
/// Everything stays under the current user's profile and is created on demand.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string localAppDataRoot, string productFolder = "NetScopeRepair")
    {
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
            throw new ArgumentException("A local application data root is required.", nameof(localAppDataRoot));
        Root = Path.Combine(localAppDataRoot, productFolder);
    }

    public static AppPaths Default { get; } = new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SessionsDirectory => Path.Combine(Root, "sessions");

    public string ExportsDirectory => Path.Combine(Root, "exports");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string NewSessionPath(string sessionId) => Path.Combine(SessionsDirectory, sessionId + ".ndjson");
}
