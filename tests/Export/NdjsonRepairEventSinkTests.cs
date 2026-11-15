using NetScope.Maintenance;
using NetScope.Storage;

namespace NetScope.Export.Tests;

public sealed class NdjsonRepairEventSinkTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "netscope-sink-test-" + Guid.NewGuid().ToString("N") + ".ndjson");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task New_sink_instance_continues_the_sequence_of_the_existing_log()
    {
        var first = new NdjsonRepairEventSink(_path);
        await first.EmitAsync(new[]
        {
            Event("repair_applied"),
            Event("action_failed"),
            Event("postcheck_failed"),
        }, CancellationToken.None);

        var second = new NdjsonRepairEventSink(_path);
        await second.EmitAsync(new[] { Event("user_unresolved") }, CancellationToken.None);

        using var log = new NdjsonEventLog(_path);
        var read = log.Read();
        Assert.Empty(read.Problems);
        Assert.Empty(read.SequenceGaps);
        Assert.Equal(4, read.Events.Count);
        Assert.Equal(1, read.Events[0].Sequence);
        Assert.Equal(4, read.Events[^1].Sequence);
    }

    private static RepairEvent Event(string kind)
        => new("batch", kind, DateTimeOffset.Now, null, null, kind, null, false, null, null, null);
}
