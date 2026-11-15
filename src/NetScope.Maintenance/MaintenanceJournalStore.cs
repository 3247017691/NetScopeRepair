using System.Text.Json;

namespace NetScope.Maintenance;

/// <summary>
/// Append-merge backup for every applied change. Repeated runs keep the EARLIEST Old value for a
/// key, so a single Restore always returns the machine to the state before the first run.
/// </summary>
public sealed class MaintenanceJournalStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public MaintenanceJournalStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetScopeRepair", "maintenance", "journal.json");
    }

    public string Path { get; }

    public MaintenanceJournalDocument? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MaintenanceJournalDocument>(File.ReadAllText(Path), Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool HasBackup => Load() is { Entries.Count: > 0 };

    /// <summary>Merges new entries into the existing document, keeping the oldest Old per key.</summary>
    public string Save(IReadOnlyList<JournalEntry> entries)
    {
        var document = Load() ?? new MaintenanceJournalDocument
        {
            CreatedAt = DateTimeOffset.Now,
            Machine = Environment.MachineName,
        };

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in document.Entries) known.Add(existing.Key);
        foreach (var entry in entries)
        {
            if (!known.Add(entry.Key)) continue;
            document.Entries.Add(entry);
        }
        document.UpdatedAt = DateTimeOffset.Now;
        document.Machine = Environment.MachineName;

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(Path, JsonSerializer.Serialize(document, Options));
        return Path;
    }

    public void Clear()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
