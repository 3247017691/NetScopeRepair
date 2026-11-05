using NetScope.Domain;

namespace NetScope.Replay.Tests;

/// <summary>Per-test temporary directory that is removed even when an assertion fails.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "netscope-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail the test that already passed.
        }
    }
}

internal static class Fixtures
{
    public static string SyntheticPath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-incident.ndjson");

    public static string[] RawLines() => File
        .ReadAllLines(SyntheticPath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToArray();

    /// <summary>A fixture envelope, re-identified so tests can control sequence and identity.</summary>
    public static EventEnvelope Event(int index, string eventId, int sequence) =>
        EnvelopeJson.Deserialize(RawLines()[index]) with
        {
            EventId = eventId,
            Sequence = sequence,
        };
}
