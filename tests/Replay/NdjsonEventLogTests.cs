using System.Text;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// Append and read behaviour of the session log: what is durable, what is deduplicated and what
/// happens when the file ends in the middle of a write.
/// </summary>
public sealed class NdjsonEventLogTests
{
    [Fact]
    public async Task Appended_events_are_read_back_in_order()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");

        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(0, "event-1", 1), CancellationToken.None);
        await log.AppendAsync(Fixtures.Event(1, "event-2", 2), CancellationToken.None);

        var events = await log.ReadAsync(CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "event-1", "event-2" }, events.Select(item => item.EventId).ToArray());
    }

    [Fact]
    public async Task A_flushed_event_is_visible_to_a_second_reader_immediately()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");

        using (var writer = new NdjsonEventLog(path))
        {
            await writer.AppendAsync(Fixtures.Event(0, "event-1", 1), CancellationToken.None);
        }

        using var reader = new NdjsonEventLog(path);
        var read = reader.Read();

        Assert.Single(read.Events);
        Assert.True(read.IsClean);
    }

    [Fact]
    public async Task Appending_the_same_event_id_twice_is_rejected()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");

        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(0, "event-1", 1), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DuplicateEventException>(
            () => log.AppendAsync(Fixtures.Event(0, "event-1", 2), CancellationToken.None));

        Assert.Equal("event-1", exception.EventId);
        Assert.Single(log.Read().Events);
    }

    [Fact]
    public void A_duplicate_line_on_disk_is_collapsed_to_the_first_occurrence()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");
        var line = Fixtures.RawLines()[0];

        File.WriteAllLines(path, new[] { line, line });

        using var log = new NdjsonEventLog(path);
        var read = log.Read();

        Assert.Single(read.Events);
        Assert.Equal(1, read.DuplicateCount);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void A_truncated_tail_is_reported_and_the_valid_prefix_is_kept()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");

        File.WriteAllText(
            path,
            Fixtures.RawLines()[0] + "\n" + Fixtures.RawLines()[1] + "\n{\"schema_version\":\"1.0\",\"event_id\":\"event-trunca",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var log = new NdjsonEventLog(path);
        var read = log.Read();

        Assert.Equal(2, read.Events.Count);
        Assert.True(read.HasTruncatedTail);
        Assert.Single(read.Problems);
        Assert.Equal(3, read.Problems[0].LineNumber);
        Assert.False(read.IsClean);
    }

    [Fact]
    public async Task A_sequence_gap_is_reported_instead_of_being_silently_closed()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");

        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(0, "event-1", 1), CancellationToken.None);
        await log.AppendAsync(Fixtures.Event(1, "event-2", 2), CancellationToken.None);
        await log.AppendAsync(Fixtures.Event(2, "event-5", 5), CancellationToken.None);

        var read = log.Read();

        var gap = Assert.Single(read.SequenceGaps);
        Assert.Equal(3, gap.Expected);
        Assert.Equal(5, gap.Found);
        Assert.Equal(2, gap.Missing);
        Assert.Equal(3, read.Events.Count);
    }

    [Fact]
    public async Task Out_of_order_sequence_is_reported_as_a_problem()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.ndjson");

        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(0, "event-1", 2), CancellationToken.None);
        await log.AppendAsync(Fixtures.Event(1, "event-2", 1), CancellationToken.None);

        var read = log.Read();

        Assert.Equal(2, read.Events.Count);
        Assert.Contains(read.Problems, problem => problem.Message.Contains("not greater", StringComparison.Ordinal));
    }

    [Fact]
    public void Reading_a_missing_file_returns_an_empty_clean_result()
    {
        using var workspace = new TempWorkspace();

        using var log = new NdjsonEventLog(workspace.File("nothing.ndjson"));
        var read = log.Read();

        Assert.Empty(read.Events);
        Assert.Empty(read.Problems);
        Assert.True(read.IsClean);
    }

    [Fact]
    public async Task Appending_creates_the_session_directory()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "nested", "deeper", "session.ndjson");

        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(0, "event-1", 1), CancellationToken.None);

        Assert.True(File.Exists(path));
    }
}
