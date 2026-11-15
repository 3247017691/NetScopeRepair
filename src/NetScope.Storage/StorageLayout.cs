namespace NetScope.Storage;

/// <summary>
/// Local data layout used by retention and recovery. It mirrors the product folders without
/// depending on Desktop: sessions and indexes, exports, application logs, pinned incidents and the
/// reserved repair-journal directory all live below one root.
/// </summary>
public sealed class StorageLayout
{
    public StorageLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A storage root is required.", nameof(root));
        Root = Path.GetFullPath(root);
        SessionsDirectory = Path.Combine(Root, "sessions");
        ExportsDirectory = Path.Combine(Root, "exports");
        LogsDirectory = Path.Combine(Root, "logs");
        PinnedDirectory = Path.Combine(Root, "pinned");
        JournalDirectory = Path.Combine(Root, "journal");
    }

    public string Root { get; }

    public string SessionsDirectory { get; }

    public string ExportsDirectory { get; }

    public string LogsDirectory { get; }

    /// <summary>Pinned incidents have their own quota pool and are never deleted by normal retention.</summary>
    public string PinnedDirectory { get; }

    /// <summary>Reserved for repair journals. Pending journals are excluded from normal cleanup.</summary>
    public string JournalDirectory { get; }

    public void EnsureCreated(IStorageFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        files.CreateDirectory(Root);
        files.CreateDirectory(SessionsDirectory);
        files.CreateDirectory(ExportsDirectory);
        files.CreateDirectory(LogsDirectory);
    }

    public string SessionPath(string sessionId) =>
        Path.Combine(SessionsDirectory, sessionId + ".ndjson");

    public static string SessionIdOf(string sessionPath)
    {
        var name = Path.GetFileNameWithoutExtension(sessionPath);
        return string.IsNullOrWhiteSpace(name) ? "session" : name;
    }

    public static string IndexPathFor(string sessionPath) => Path.ChangeExtension(sessionPath, ".db");

    public static string SqliteSidecarPathFor(string databasePath) => databasePath + ".wal";

    public static string AggregatePathFor(string sessionPath) => sessionPath + ".aggregate.json";

    public static string SealedPathFor(string sessionPath) => sessionPath + ".sealed.json";

    public static string IndexCursorPathFor(string databasePath) => databasePath + ".cursor.json";

    public static string IndexStatePathFor(string databasePath) => databasePath + ".state.json";

    public static string QuarantinePathFor(string sessionPath, DateTimeOffset atUtc) =>
        sessionPath + ".tail-" + atUtc.UtcDateTime.ToString("yyyyMMddHHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture) + ".quarantine";
}
