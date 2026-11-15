using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>
/// T08 export semantics must survive T11 retention: the ordinary-log quota never deletes an
/// exported package (design 04 handles exports separately), so an existing package keeps its
/// bytes and its internal checksums after a quota-enforcement pass.
/// </summary>
public sealed class T11QuotaExportTests
{
    [Fact]
    public async Task Retention_never_touches_an_exported_package_or_its_checksums()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("export-source.ndjson"));
        var exported = await new IncidentExporter()
            .ExportAsync(session, workspace.Subdirectory("raw-exports"), CancellationToken.None);

        var layout = new StorageLayout(workspace.Root);
        layout.EnsureCreated(StorageFileSystem.Instance);
        var packagePath = Path.Combine(layout.ExportsDirectory, Path.GetFileName(exported.PackagePath));
        File.Copy(exported.PackagePath, packagePath);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(packagePath, (now - TimeSpan.FromDays(10)).UtcDateTime);

        // A genuinely expired session so quota enforcement has something real to remove.
        var oldSession = layout.SessionPath("old-success-session");
        File.WriteAllText(oldSession, EnvelopeJson.Serialize(new EventEnvelope
        {
            SchemaVersion = EventEnvelope.SupportedSchemaVersion,
            EventId = "event-t11-export-001",
            SessionId = "session-t11-export",
            BootId = "boot-t11-export",
            Sequence = 1,
            ObservedAt = now - TimeSpan.FromDays(10),
            MonotonicMs = 0,
            EventType = EventType.Snapshot,
            Status = EventStatus.Ok,
            Source = "synthetic.t11",
            SourceVersion = "1.0",
            Synthetic = true,
            Refs = new EventRefs(),
            Payload = new JsonObject { ["note"] = "synthetic T11 fixture" },
        }) + "\n");
        File.SetLastWriteTimeUtc(oldSession, (now - TimeSpan.FromDays(10)).UtcDateTime);

        var manager = new QuotaManager(layout,
            new RetentionPolicy { NormalMaxBytes = 512, NormalRetention = TimeSpan.FromDays(7) },
            StorageFileSystem.Instance, new FixedClock(now));
        var result = manager.Enforce();

        Assert.DoesNotContain(result.Plan.Actions, action => action.Path == packagePath);
        Assert.True(File.Exists(packagePath), "exported package must not be removed by ordinary log retention");
        Assert.False(File.Exists(oldSession), "the expired session detail should have been removed to prove enforcement ran");

        // The package is bit-identical and internally consistent after the quota pass.
        var before = SHA256.HashData(await File.ReadAllBytesAsync(exported.PackagePath));
        var after = SHA256.HashData(await File.ReadAllBytesAsync(packagePath));
        Assert.Equal(Convert.ToHexStringLower(before), Convert.ToHexStringLower(after));

        var entries = PackageReader.Read(packagePath);
        var checksumLines = PackageReader.Text(entries["checksums.sha256"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToArray();
        Assert.NotEmpty(checksumLines);
        foreach (var line in checksumLines)
        {
            Assert.Equal(2, line.Length);
            Assert.True(entries.ContainsKey(line[1]), line[1]);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(entries[line[1]])), line[0]);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }

        public double MonotonicMs => 0;
    }
}
