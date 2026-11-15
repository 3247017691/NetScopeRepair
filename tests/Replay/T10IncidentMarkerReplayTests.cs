using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// T10 incident markers are additive untyped events. Replay must keep them traceable without
/// changing snapshot scopes, typed evidence or diagnosis reconstruction.
/// </summary>
public sealed class T10IncidentMarkerReplayTests
{
    private const string Boot = "boot-t10-replay";
    private const string Session = "session-t10-replay";
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T01:00:00Z");

    [Fact]
    public void Markers_between_snapshot_cycles_do_not_leak_into_evidence_scopes()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("t10-markers.ndjson");
        var first = Snapshot("snapshot-t10-1", Origin);
        var second = Snapshot("snapshot-t10-2", Origin.AddSeconds(10));
        var events = new List<EventEnvelope>();
        events.AddRange(EvidenceSessionEvents.Create(new EvidenceSet
        {
            Snapshot = first,
            Probes = Array.Empty<ProbeResult>(),
            Records = Array.Empty<EvidenceRecord>(),
        }, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true, Origin.AddSeconds(1), 1000, "batch-1"));
        events.AddRange(IncidentSessionEvents.Create(new[] { Transition("incident-t10-1", Origin.AddSeconds(5), 5000) },
            Session, Boot, synthetic: true, Origin.AddSeconds(5), 5000, "batch-2"));
        events.AddRange(EvidenceSessionEvents.Create(new EvidenceSet
        {
            Snapshot = second,
            Probes = Array.Empty<ProbeResult>(),
            Records = Array.Empty<EvidenceRecord>(),
        }, Array.Empty<Diagnosis>(), Session, Boot, synthetic: true, Origin.AddSeconds(11), 11000, "batch-3"));
        Write(path, events);

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.All(replay.SnapshotScopes, scope => Assert.Empty(scope.Evidence.Records));
        Assert.Equal(second.SnapshotId, replay.Evidence!.Snapshot!.SnapshotId);
        Assert.Single(replay.Log.Events, item => item.EventType == EventType.IncidentMarker);
        Assert.Equal(1, replay.Summary.ByType[EventType.IncidentMarker]);
        Assert.Empty(replay.Diagnoses);
        Assert.Null(replay.UnscopedEvidence);
    }

    [Fact]
    public void Incident_marker_payload_survives_replay_and_is_not_a_diagnosis()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("t10-marker-payload.ndjson");
        var transitions = new[]
        {
            Transition("incident-t10-2", Origin, 0) with
            {
                PreviousState = IncidentState.Suspected,
                State = IncidentState.IncidentOpen,
                Reason = "incident_opened",
            },
        };
        Write(path, IncidentSessionEvents.Create(transitions, Session, Boot, synthetic: true, Origin, 0, "batch-1"));

        var replay = new ReplayService().Replay(path);

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        var marker = Assert.Single(replay.Log.Events);
        Assert.Equal(EventType.IncidentMarker, marker.EventType);
        Assert.Equal("incident-t10-2", marker.Refs.IncidentId);
        Assert.Equal("incident_opened", marker.Payload["reason"]!.GetValue<string>());
        Assert.False(marker.Payload.ContainsKey("payload_version"));
        Assert.Empty(replay.Diagnoses);
        Assert.Null(replay.Evidence);
    }

    private static void Write(string path, IReadOnlyList<EventEnvelope> events)
    {
        using var log = new NdjsonEventLog(path);
        var sequence = 1;
        foreach (var item in events)
        {
            log.AppendAsync(item with { Sequence = sequence++ }, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static IncidentTransition Transition(string incidentId, DateTimeOffset at, double monotonic) => new()
    {
        Kind = IncidentTransitionKind.StateChange,
        PreviousState = IncidentState.IncidentOpen,
        State = IncidentState.Closed,
        Reason = "incident_closed:session_end_before_post_window",
        At = at,
        MonotonicMs = monotonic,
        SessionId = Session,
        BootId = Boot,
        Synthetic = true,
        Incident = new IncidentRecord
        {
            IncidentId = incidentId,
            Trigger = IncidentTrigger.ManualUserMark,
            State = IncidentState.Closed,
            SessionId = Session,
            BootId = Boot,
            Synthetic = true,
            StartedAt = Origin,
            ClosedAt = Origin.AddSeconds(10),
            Window = new IncidentWindow
            {
                StartedAt = Origin,
                EndedAt = Origin.AddSeconds(10),
                PreWindowAvailable = false,
                PostWindowComplete = false,
                MissingRanges = new[] { "pre_window_incomplete", "post_window_incomplete" },
            },
            UserDescriptions = new[] { "用户标记：刚才断网了" },
            Failures = new[]
            {
                new IncidentFailureGroup
                {
                    ClassKey = "target-t10|explicit-http-proxy|proxy-t10|ipv4|GET|options-t10",
                    TargetId = "target-t10",
                    Path = "explicit-http-proxy",
                    ProxyEndpointId = "proxy-t10",
                    AddressFamily = "ipv4",
                    Method = "GET",
                    FirstAt = Origin,
                    LastAt = Origin,
                    Count = 3,
                },
            },
            MissingRanges = new[] { "pre_window_incomplete", "post_window_incomplete" },
            ClosureReason = "session_end_before_post_window",
            LastSnapshotId = "snapshot-t10-1",
        },
    };

    private static NetworkSnapshot Snapshot(string id, DateTimeOffset at) => new()
    {
        SnapshotId = id,
        ObservedAt = at,
        MonotonicMs = (at - Origin).TotalMilliseconds,
        Synthetic = true,
        Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(Array.Empty<InterfaceState>()),
        Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(Array.Empty<RouteEntry>()),
        Dns = Collected<DnsState>.Ok(new DnsState()),
        Proxies = Collected<ProxyState>.Ok(new ProxyState
        {
            ToolProcessEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
            UserEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
            MachineEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
            WinInet = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
            WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not read"),
        }),
    };
}
