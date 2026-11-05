namespace NetScope.Desktop.Services;

/// <summary>Minimal local application log. It records event types and timestamps only.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string? DirectoryOverride { get; set; }

    public static void Write(string message)
    {
        try
        {
            var directory = DirectoryOverride ?? Path.Combine(AppPaths.Default.Root, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            lock (Gate)
            {
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging the log failure is not possible; the UI already surfaces persistence problems.
        }
    }
}
