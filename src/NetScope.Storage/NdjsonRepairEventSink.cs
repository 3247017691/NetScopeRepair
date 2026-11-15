using System.Diagnostics;
using NetScope.Maintenance;

namespace NetScope.Storage;

/// <summary>CLI-side sink: repair events append to their own NDJSON log (no observation session).</summary>
public sealed class NdjsonRepairEventSink : IRepairEventSink
{
    private readonly NdjsonEventLog _log;
    private readonly string _sessionId = "maintenance-" + DateTime.Now.ToString("yyyyMMdd");
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private int _nextSequence = 1;

    public NdjsonRepairEventSink(string path) => _log = new NdjsonEventLog(path);

    public async Task EmitAsync(IReadOnlyList<RepairEvent> events, CancellationToken cancellationToken)
    {
        var envelopes = RepairSessionEvents.Create(events, _sessionId, _bootId, synthetic: false,
            Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        foreach (var envelope in envelopes)
            await _log.AppendAsync(envelope with { Sequence = _nextSequence++ }, cancellationToken).ConfigureAwait(false);
    }
}