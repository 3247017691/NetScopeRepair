using System.Text;
using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// T11 replay side: a recovered truncated tail replays as a clean log with one explicit gap
/// event, while middle damage is reported without pretending the earlier lines are the whole log.
/// All sessions are synthetic.
/// </summary>
public sealed class T11RecoveryReplayTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_quarantined_tail_replays_cleanly_with_one_explicit_gap_event()
    {
        using var temp = new TempWorkspace();
        var path = temp.File("crash-session.ndjson");
        var text = string.Join("\n", new[]
        {
            EnvelopeJson.Serialize(Event(1, EventStatus.Ok)),
            EnvelopeJson.Serialize(Event(2, EventStatus.Partial)),
        }) + "\n{\"schema_version\":";
        File.WriteAllText(path, text);

        var recovery = new SessionRecovery();
        var inspection = recovery.Inspect(path);
        Assert.True(inspection.IsRecoverableTruncatedTail);

        var result = recovery.QuarantineTruncatedTail(path);
        Assert.True(result.Modified);
        Assert.True(result.GapEventAppended);

        var replay = new ReplayService().Replay(path);
        Assert.True(replay.Log.IsClean, string.Join("; ", replay.Log.Problems.Select(problem => problem.Message)));
        Assert.Equal(3, replay.Log.Events.Count);
        Assert.Empty(replay.Log.SequenceGaps);
        Assert.Equal(1, replay.Summary.ByType[EventType.Gap]);
        Assert.Equal(EventStatus.Failed, replay.Log.Events[^1].Status);
        Assert.Equal("truncated_tail_quarantined",
            replay.Log.Events[^1].Payload["kind"]!.GetValue<string>());
        Assert.Null(replay.Evidence); // envelope-level session: no typed evidence is invented by recovery
        Assert.True(replay.IsClean);
    }

    [Fact]
    public void Middle_damage_keeps_later_events_and_never_becomes_a_valid_prefix()
    {
        using var temp = new TempWorkspace();
        var path = temp.File("middle-damage.ndjson");
        var text = EnvelopeJson.Serialize(Event(1, EventStatus.Ok)) + "\n"
            + "not a json line\n"
            + EnvelopeJson.Serialize(Event(3, EventStatus.Ok)) + "\n";
        File.WriteAllText(path, text);

        var recovery = new SessionRecovery();
        var inspection = recovery.Inspect(path);
        Assert.True(inspection.HasMiddleCorruption);
        Assert.Equal(2, inspection.EventCount);
        var result = recovery.QuarantineTruncatedTail(path);
        Assert.False(result.Modified);
        Assert.Equal(text, File.ReadAllText(path));

        var replay = new ReplayService().Replay(path);
        Assert.False(replay.Log.IsClean); // damage is reported, not hidden
        Assert.Single(replay.Log.Problems);
        Assert.False(replay.Log.Problems[0].IsTruncatedTail);
        Assert.Equal(2, replay.Log.Events.Count);
        Assert.Equal(3, replay.Log.Events[^1].Sequence); // the line after the damage is still read
        Assert.Null(replay.Evidence);
    }

    private static EventEnvelope Event(int sequence, EventStatus status) => new()
    {
        SchemaVersion = EventEnvelope.SupportedSchemaVersion,
        EventId = $"event-t11-replay-{sequence:000}",
        SessionId = "session-t11-replay",
        BootId = "boot-t11-replay",
        Sequence = sequence,
        ObservedAt = Origin.AddSeconds(sequence),
        MonotonicMs = sequence * 1000d,
        EventType = EventType.Snapshot,
        Status = status,
        Source = "synthetic.t11",
        SourceVersion = "1.0",
        Synthetic = true,
        Refs = new EventRefs(),
        Payload = new JsonObject { ["note"] = "synthetic T11 replay fixture" },
    };
}
