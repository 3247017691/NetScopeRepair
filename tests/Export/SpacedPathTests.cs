using System.Text;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>A path whose user-name segment contains a space must be removed as a whole.</summary>
public sealed class SpacedPathTests
{
    [Fact]
    public async Task Spaced_user_directory_is_not_partially_left_in_the_package()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("spaced-path.ndjson");
        await ExportSession.CreateAsync(path);
        var original = await File.ReadAllTextAsync(path);
        var replaced = original.Replace(
            "C:\\\\Users\\\\alice",
            "C:\\\\Users\\\\Alice Smith",
            StringComparison.Ordinal);
        Assert.NotEqual(original, replaced);
        await File.WriteAllTextAsync(path, replaced, new UTF8Encoding(false));

        var replay = new ReplayService().Replay(path);
        Assert.True(replay.Log.IsClean);

        var exporter = new IncidentExporter();
        var destination = workspace.Subdirectory("spaced-out");
        var result = await exporter.ExportAsync(path, destination, CancellationToken.None);
        var files = PackageReader.Read(result.PackagePath);
        var all = string.Join("\n", files.Values.Select(PackageReader.Text));

        Assert.DoesNotContain("Alice Smith", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Smith\\AppData", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", all, StringComparison.Ordinal);
    }
}
