using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// The SQLite projection is a queryable index, not a second source of truth: it ignores
/// duplicates, can be rebuilt from the log, and never changes an event's identity.
/// </summary>
public sealed class SqliteEventIndexTests
{
    [Fact]
    public void Projection_indexes_an_event_once()
    {
        using var index = SqliteEventIndex.OpenInMemory();
        var envelope = Fixtures.Event(0, "event-1", 1);

        Assert.True(index.Project(envelope));
        Assert.False(index.Project(envelope));
        Assert.Equal(1, index.Count());
    }

    [Fact]
    public void Projecting_a_batch_counts_only_new_rows()
    {
        using var index = SqliteEventIndex.OpenInMemory();
        var events = new[]
        {
            Fixtures.Event(0, "event-1", 1),
            Fixtures.Event(1, "event-2", 2),
            Fixtures.Event(1, "event-2", 2),
        };

        Assert.Equal(2, index.ProjectAll(events));
        Assert.Equal(2, index.Count());
    }

    [Fact]
    public void Rebuild_reproduces_the_log_contents()
    {
        using var index = SqliteEventIndex.OpenInMemory();
        var events = new[]
        {
            Fixtures.Event(0, "event-1", 1),
            Fixtures.Event(1, "event-2", 2),
            Fixtures.Event(2, "event-3", 3),
        };

        index.ProjectAll(events);
        Assert.Equal(3, index.Count());

        Assert.Equal(2, index.Rebuild(events.Take(2)));
        Assert.Equal(2, index.Count());
    }

    [Fact]
    public void Status_query_returns_events_in_sequence_order()
    {
        using var index = SqliteEventIndex.OpenInMemory();

        index.ProjectAll(new[]
        {
            Fixtures.Event(0, "event-1", 1),
            Fixtures.Event(1, "event-2", 2),
            Fixtures.Event(3, "event-3", 3),
        });

        var failed = index.ByStatus(EventStatus.Timeout);

        var single = Assert.Single(failed);
        Assert.Equal("event-3", single.EventId);
        Assert.Equal(3, single.Sequence);
        Assert.Equal(EventType.ProbeResult, single.EventType);
        Assert.True(single.Synthetic);
    }

    [Fact]
    public void A_file_backed_projection_survives_reopening()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("index.db");

        using (var first = SqliteEventIndex.Open(path))
        {
            Assert.True(first.Project(Fixtures.Event(0, "event-1", 1)));
        }

        using var second = SqliteEventIndex.Open(path);

        Assert.Equal(1, second.Count());
        Assert.Single(second.ByStatus(EventStatus.Partial));
    }

    [Fact]
    public void Two_event_ids_with_the_same_sequence_are_both_kept()
    {
        using var index = SqliteEventIndex.OpenInMemory();

        Assert.True(index.Project(Fixtures.Event(0, "event-a", 1)));
        Assert.True(index.Project(Fixtures.Event(0, "event-b", 1)));
        Assert.Equal(2, index.Count());
    }
}
