namespace NetScope.Storage;

/// <summary>One file discovered by <see cref="IStorageFileSystem.EnumerateFiles"/>.</summary>
public sealed record StorageFileEntry(string Path, long Bytes, DateTimeOffset LastWriteTimeUtc);

/// <summary>
/// Injectable local-file boundary for quota and crash-recovery tests. It offers only plain local
/// file and directory operations; there is no network, registry or system configuration access.
/// A test double can make any single operation fail (disk full, access denied) so no real disk has
/// to be filled and no system clock has to be changed.
/// </summary>
public interface IStorageFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    Stream OpenRead(string path);

    /// <summary>Opens an existing file for read/write without truncating it. Used to cut a tail.</summary>
    Stream OpenReadWrite(string path);

    /// <summary>Creates or truncates one file for writing.</summary>
    Stream OpenWrite(string path);

    /// <summary>Flushes a stream to durable storage where the platform can do so.</summary>
    void FlushToDisk(Stream stream);

    void Delete(string path);

    void Move(string source, string destination, bool overwrite);

    IReadOnlyList<StorageFileEntry> EnumerateFiles(string directory, string searchPattern);

    DateTimeOffset GetLastWriteTimeUtc(string path);

    long GetFileLength(string path);

    /// <summary>
    /// Cheap write probe for one directory: create a temporary file, write a byte, delete it.
    /// Returns false when the directory cannot currently be written to (full disk, ACL, read-only).
    /// </summary>
    bool ProbeWritable(string directory);
}

/// <summary>Real local file system used by the product; the only implementation with side effects.</summary>
public sealed class StorageFileSystem : IStorageFileSystem
{
    public static StorageFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public Stream OpenReadWrite(string path) => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    public Stream OpenWrite(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    public void FlushToDisk(Stream stream)
    {
        stream.Flush();
        if (stream is FileStream file) file.Flush(flushToDisk: true);
    }

    public void Delete(string path) => File.Delete(path);

    public void Move(string source, string destination, bool overwrite) =>
        File.Move(source, destination, overwrite);

    public IReadOnlyList<StorageFileEntry> EnumerateFiles(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory)) return Array.Empty<StorageFileEntry>();
        var result = new List<StorageFileEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            result.Add(new StorageFileEntry(info.FullName, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        return result;
    }

    public DateTimeOffset GetLastWriteTimeUtc(string path) =>
        new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public bool ProbeWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".netscope-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or System.Security.SecurityException
                                              or ArgumentException)
        {
            return false;
        }
    }
}
