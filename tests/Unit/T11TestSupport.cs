using System.Text;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Unit.Tests;

/// <summary>Deterministic clock for T11 retention/recovery tests; tests move it explicitly.</summary>
internal sealed class T11FakeClock : IClock
{
    public T11FakeClock(DateTimeOffset? start = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; set; }

    public double MonotonicMs => 0;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// In-memory local file seam for quota/crash tests. It never touches a real disk, so quota tests
/// can simulate 500 MB / 1 GB sizes and disk-full/delete failures without filling the user's disk.
/// </summary>
internal sealed class FakeStorageFileSystem : IStorageFileSystem
{
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnwritableDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailOpenWritePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailOpenReadWritePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailDeletePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailMovePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool FailAllWrites { get; set; }

    /// <summary>Number of successful read/write opens before every later one fails (simulated crash point).</summary>
    public int OpenReadWriteAllowed { get; set; } = int.MaxValue;

    private int _openReadWriteCalls;

    public int ProbeCount { get; private set; }

    public IReadOnlyCollection<string> Paths => _files.Keys;

    public void AddText(string path, string text, DateTimeOffset lastWrite) =>
        AddBytes(path, Encoding.UTF8.GetBytes(text), lastWrite);

    public void AddBytes(string path, byte[] bytes, DateTimeOffset lastWrite) =>
        _files[Normalize(path)] = new Entry(bytes, bytes.LongLength, lastWrite);

    /// <summary>Declares a large file without allocating its bytes; used for 500 MB/1 GB quota shapes.</summary>
    public void AddSparse(string path, long declaredLength, DateTimeOffset lastWrite) =>
        _files[Normalize(path)] = new Entry(Array.Empty<byte>(), declaredLength, lastWrite);

    public bool Exists(string path) => _files.ContainsKey(Normalize(path));

    public string ReadText(string path) => Encoding.UTF8.GetString(_files[Normalize(path)].Bytes);

    public byte[] ReadBytes(string path) => _files[Normalize(path)].Bytes;

    public long LengthOf(string path) => _files[Normalize(path)].DeclaredLength;

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        if (_directories.Contains(normalized)) return true;
        var prefix = normalized.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return _files.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void CreateDirectory(string path)
    {
        ThrowIfUnwritable(path);
        _directories.Add(Normalize(path));
    }

    public Stream OpenRead(string path)
    {
        var entry = _files[Normalize(path)];
        return new MemoryStream(entry.Bytes, writable: false);
    }

    public Stream OpenReadWrite(string path)
    {
        var key = Normalize(path);
        if (FailOpenReadWritePaths.Contains(key) || FailAllWrites || UnwritableDirectories.Contains(DirectoryOf(key)))
            throw new IOException("simulated read/write open failure");
        _openReadWriteCalls++;
        if (_openReadWriteCalls > OpenReadWriteAllowed)
            throw new IOException("simulated disk full after a crash point");
        if (!_files.TryGetValue(key, out var entry)) throw new FileNotFoundException("missing", path);
        return new PersistingStream(entry.Bytes, bytes => _files[key] = entry with { Bytes = bytes, DeclaredLength = bytes.LongLength });
    }

    public Stream OpenWrite(string path)
    {
        var key = Normalize(path);
        if (FailOpenWritePaths.Contains(key) || FailAllWrites || UnwritableDirectories.Contains(DirectoryOf(key)))
            throw new IOException("simulated disk full");
        return new PersistingStream(Array.Empty<byte>(), bytes =>
            _files[key] = new Entry(bytes, bytes.LongLength, LastKnownNow ?? DateTimeOffset.UnixEpoch));
    }

    public void FlushToDisk(Stream stream)
    {
        // In-memory seam: there is no durable medium to flush to.
    }

    public DateTimeOffset? LastKnownNow { get; set; }

    public void Delete(string path)
    {
        var key = Normalize(path);
        if (FailDeletePaths.Contains(key) || FailAllWrites) throw new IOException("simulated delete failure");
        if (!_files.Remove(key)) throw new FileNotFoundException("missing", path);
    }

    public void Move(string source, string destination, bool overwrite)
    {
        var from = Normalize(source);
        var to = Normalize(destination);
        if (FailMovePaths.Contains(to) || FailAllWrites) throw new IOException("simulated move failure");
        if (!_files.TryGetValue(from, out var entry)) throw new FileNotFoundException("missing", source);
        if (!overwrite && _files.ContainsKey(to)) throw new IOException("destination exists");
        _files[to] = entry with { LastWrite = LastKnownNow ?? entry.LastWrite };
        _files.Remove(from);
    }

    public IReadOnlyList<StorageFileEntry> EnumerateFiles(string directory, string searchPattern)
    {
        var prefix = Normalize(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return _files
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(pair => Matches(Path.GetFileName(pair.Key), searchPattern))
            .Select(pair => new StorageFileEntry(pair.Key, pair.Value.DeclaredLength, pair.Value.LastWrite))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public DateTimeOffset GetLastWriteTimeUtc(string path) => _files[Normalize(path)].LastWrite;

    public long GetFileLength(string path) => _files[Normalize(path)].DeclaredLength;

    public bool ProbeWritable(string directory)
    {
        ProbeCount++;
        return !FailAllWrites && !UnwritableDirectories.Contains(Normalize(directory));
    }

    private void ThrowIfUnwritable(string path)
    {
        if (FailAllWrites || UnwritableDirectories.Contains(Normalize(path)))
            throw new IOException("simulated disk full");
    }

    private static string DirectoryOf(string normalizedPath) =>
        Path.GetDirectoryName(normalizedPath) ?? normalizedPath;

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static bool Matches(string name, string pattern) => pattern == "*"
        || name.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);

    private sealed record Entry(byte[] Bytes, long DeclaredLength, DateTimeOffset LastWrite);

    private sealed class PersistingStream : MemoryStream
    {
        private readonly Action<byte[]> _persist;
        private bool _persisted;

        public PersistingStream(byte[] initial, Action<byte[]> persist)
        {
            if (initial.Length > 0) Write(initial, 0, initial.Length);
            _persist = persist;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_persisted)
            {
                _persisted = true;
                _persist(ToArray());
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Append seam that always fails, standing in for a full disk.</summary>
internal sealed class FailingEventLogFileAccess : IEventLogFileAccess
{
    public Stream OpenForAppend(string path) => throw new IOException("simulated disk full");

    public void FlushToDisk(Stream stream) => throw new IOException("simulated disk full");
}

internal static class T11Data
{
    public static readonly DateTimeOffset Origin = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    public const string SessionId = "session-t11";

    public const string BootId = "boot-t11";

    public static EventEnvelope Event(int sequence, EventStatus status,
        EventType eventType = EventType.Snapshot, DateTimeOffset? observedAt = null,
        JsonObject? payload = null, bool synthetic = false, string? eventId = null) => new()
    {
        SchemaVersion = EventEnvelope.SupportedSchemaVersion,
        EventId = eventId ?? $"event-t11-{sequence:000}",
        SessionId = SessionId,
        BootId = BootId,
        Sequence = sequence,
        ObservedAt = observedAt ?? Origin.AddSeconds(sequence),
        MonotonicMs = sequence * 1000d,
        EventType = eventType,
        Status = status,
        Source = "synthetic.t11",
        SourceVersion = "1.0",
        Synthetic = synthetic,
        Refs = new EventRefs(),
        Payload = payload ?? new JsonObject { ["note"] = "synthetic T11 fixture" },
    };

    public static EventEnvelope Failure(int sequence, EventStatus status = EventStatus.Timeout) =>
        Event(sequence, status);

    public static EventEnvelope IncidentMarker(int sequence, string incidentId, string state,
        DateTimeOffset? observedAt = null) => Event(sequence, EventStatus.Ok, EventType.IncidentMarker,
        observedAt, new JsonObject
        {
            ["kind"] = "state_change",
            ["state"] = state,
            ["incident"] = new JsonObject { ["incident_id"] = incidentId },
        });

    public static string LogText(IEnumerable<EventEnvelope> events) =>
        string.Join("\n", events.Select(EnvelopeJson.Serialize)) + "\n";

    /// <summary>Log with a syntactically interrupted final line (the crash-during-append shape).</summary>
    public static string TruncatedLogText(IEnumerable<EventEnvelope> events) =>
        string.Join("\n", events.Select(EnvelopeJson.Serialize)) + "\n{\"schema_version\":";

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>Disposable real temp directory for the SQLite rebuild tests only.</summary>
internal sealed class T11TempDirectory : IDisposable
{
    public T11TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "netscope-t11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must not fail an already-passing test.
        }
    }
}
