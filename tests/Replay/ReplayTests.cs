using System.Text;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// Offline replay. It reads a captured session, applies the same strict reader as the live store
/// and reports gaps instead of filling them in. No replay path opens a socket.
/// </summary>
public sealed class ReplayTests
{
    private static string Fingerprint(ReplaySummary summary) => string.Join(
        "|",
        summary.EventCount,
        summary.SyntheticCount,
        summary.SuccessCount,
        summary.PartialCount,
        summary.ObservationGapCount,
        string.Join(",", summary.ByType.Select(pair => $"{pair.Key}:{pair.Value}")),
        string.Join(",", summary.ByStatus.Select(pair => $"{pair.Key}:{pair.Value}")));

    [Fact]
    public void Replaying_the_synthetic_fixture_produces_the_expected_summary()
    {
        var result = new ReplayService().Replay(Fixtures.SyntheticPath);

        Assert.Equal(6, result.Log.Events.Count);
        Assert.True(result.Log.IsClean);
        Assert.Equal(4, result.Summary.SuccessCount);
        Assert.Equal(1, result.Summary.PartialCount);
        Assert.Equal(1, result.Summary.ObservationGapCount);
        Assert.True(result.Summary.AllSynthetic);
        Assert.Equal(3, result.Summary.ByType[NetScope.Domain.EventType.ProbeResult]);
    }

    [Fact]
    public void Replay_is_deterministic_for_the_same_input()
    {
        var service = new ReplayService();

        var first = service.Replay(Fixtures.SyntheticPath);
        var second = service.Replay(Fixtures.SyntheticPath);

        Assert.Equal(Fingerprint(first.Summary), Fingerprint(second.Summary));
        Assert.Equal(
            first.Log.Events.Select(item => item.EventId),
            second.Log.Events.Select(item => item.EventId));
    }

    [Fact]
    public void Replay_reports_a_sequence_gap_without_inventing_the_missing_events()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("gapped.ndjson");
        var lines = Fixtures.RawLines();

        File.WriteAllLines(path, new[] { lines[0], lines[1], lines[4] });

        var result = new ReplayService().Replay(path);

        Assert.Equal(3, result.Log.Events.Count);
        var gap = Assert.Single(result.Log.SequenceGaps);
        Assert.Equal(2, gap.Missing);
        Assert.False(result.Log.IsClean);
        Assert.Equal(3, result.Summary.EventCount);
    }

    [Fact]
    public void Replay_keeps_the_valid_prefix_when_the_tail_is_corrupted()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("truncated.ndjson");
        var lines = Fixtures.RawLines();

        File.WriteAllText(
            path,
            lines[0] + "\n" + lines[1] + "\n{\"schema_version\":\"1.0\",\"sequence\":",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = new ReplayService().Replay(path);

        Assert.Equal(2, result.Log.Events.Count);
        Assert.True(result.Log.HasTruncatedTail);
        Assert.Equal(2, result.Summary.EventCount);
        Assert.True(result.Summary.AllSynthetic);
    }

    [Fact]
    public void Replaying_a_missing_file_yields_an_empty_clean_result()
    {
        using var workspace = new TempWorkspace();

        var result = new ReplayService().Replay(workspace.File("absent.ndjson"));

        Assert.Empty(result.Log.Events);
        Assert.True(result.Log.IsClean);
        Assert.Equal(0, result.Summary.EventCount);
        Assert.False(result.Summary.AllSynthetic);
    }

    [Fact]
    public void Summary_separates_success_partial_and_gaps()
    {
        var events = new[]
        {
            Fixtures.Event(0, "event-partial", 1),
            Fixtures.Event(1, "event-ok", 2),
            Fixtures.Event(3, "event-timeout", 3),
            Fixtures.Event(2, "event-ok-2", 4),
        };

        var summary = ReplayService.Summarize(events);

        Assert.Equal(2, summary.SuccessCount);
        Assert.Equal(1, summary.PartialCount);
        Assert.Equal(1, summary.ObservationGapCount);
        Assert.Equal(4, summary.EventCount);
    }
}
