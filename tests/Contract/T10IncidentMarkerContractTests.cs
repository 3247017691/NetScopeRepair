using System.Text.Json;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Contract.Tests;

/// <summary>
/// T10 incident markers keep the published envelope contract. They are intentionally untyped
/// (no payload_version) so an older reader skips them instead of rejecting the session.
/// </summary>
public sealed class T10IncidentMarkerContractTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-05T01:00:00Z");

    private static readonly string SchemaPath =
        Path.Combine(AppContext.BaseDirectory, "contracts", "event-envelope.schema.json");

    [Fact]
    public void Incident_marker_conforms_to_the_published_envelope_schema_and_has_no_typed_version()
    {
        var events = IncidentSessionEvents.Create(new[] { Transition() }, "session-t10", "boot-t10",
            synthetic: true, At, 0, "batch-1");

        var marker = Assert.Single(events);
        Assert.Equal(EventType.IncidentMarker, marker.EventType);
        Assert.Equal(EventStatus.Ok, marker.Status);
        Assert.Equal("incident.recorder", marker.Source);
        Assert.True(marker.Synthetic);
        Assert.False(marker.Payload.ContainsKey("payload_version"));
        Assert.Equal("state_change", marker.Payload["kind"]!.GetValue<string>());
        Assert.Equal("closed", marker.Payload["state"]!.GetValue<string>());
        Assert.Equal("incident-contract-1", marker.Refs.IncidentId);
        Assert.Equal("snapshot-1", marker.Refs.SnapshotId);
        Assert.Equal("incident-contract-1", marker.Payload["incident"]!["incident_id"]!.GetValue<string>());

        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var line = JsonDocument.Parse(EnvelopeJson.Serialize(marker));
        var problems = SchemaConformance.Validate(line.RootElement, schemaDocument.RootElement);
        Assert.Empty(problems);
    }

    [Fact]
    public void Synthetic_marker_ids_and_payloads_are_deterministic_for_the_same_batch()
    {
        var first = IncidentSessionEvents.Create(new[] { Transition(), BusinessTransition() }, "session-t10",
            "boot-t10", synthetic: true, At, 0, "batch-1");
        var second = IncidentSessionEvents.Create(new[] { Transition(), BusinessTransition() }, "session-t10",
            "boot-t10", synthetic: true, At, 0, "batch-1");

        Assert.Equal(first.Select(item => item.EventId), second.Select(item => item.EventId));
        Assert.Equal(first.Select(item => item.Payload.ToJsonString()), second.Select(item => item.Payload.ToJsonString()));
        Assert.Contains(first, item => item.Payload["kind"]!.GetValue<string>() == "business_check");
        Assert.Equal("not_tested", first[1].Payload["business_check"]!["result"]!.GetValue<string>());
    }

    [Fact]
    public void Cross_session_and_cross_boot_markers_are_rejected_instead_of_restamped()
    {
        var wrongSession = Transition() with { SessionId = "session-other" };
        Assert.Throws<InvalidOperationException>(() => IncidentSessionEvents.Create(new[] { wrongSession },
            "session-t10", "boot-t10", synthetic: true, At, 0, "batch-1"));

        var wrongBoot = Transition() with { BootId = "boot-other" };
        Assert.Throws<InvalidOperationException>(() => IncidentSessionEvents.Create(new[] { wrongBoot },
            "session-t10", "boot-t10", synthetic: true, At, 0, "batch-1"));

        var wrongSynthetic = Transition() with { Synthetic = false };
        Assert.Throws<InvalidOperationException>(() => IncidentSessionEvents.Create(new[] { wrongSynthetic },
            "session-t10", "boot-t10", synthetic: true, At, 0, "batch-1"));
    }

    private static IncidentTransition Transition() => new()
    {
        Kind = IncidentTransitionKind.StateChange,
        PreviousState = IncidentState.IncidentOpen,
        State = IncidentState.Closed,
        Reason = "incident_closed:session_end_before_post_window",
        At = At,
        MonotonicMs = 0,
        SessionId = "session-t10",
        BootId = "boot-t10",
        Synthetic = true,
        Incident = Record(),
    };

    private static IncidentTransition BusinessTransition() => new()
    {
        Kind = IncidentTransitionKind.BusinessCheck,
        PreviousState = IncidentState.Observing,
        State = IncidentState.Observing,
        Reason = "business_check",
        At = At,
        MonotonicMs = 0,
        SessionId = "session-t10",
        BootId = "boot-t10",
        Synthetic = true,
        BusinessCheck = new BusinessCheckEntry
        {
            Category = IncidentBusinessCategory.Dsh,
            Result = IncidentBusinessResult.NotTested,
            Source = "user",
            ObservedAt = At,
        },
    };

    private static IncidentRecord Record() => new()
    {
        IncidentId = "incident-contract-1",
        Trigger = IncidentTrigger.ManualUserMark,
        State = IncidentState.Closed,
        SessionId = "session-t10",
        BootId = "boot-t10",
        Synthetic = true,
        StartedAt = At,
        ClosedAt = At.AddSeconds(10),
        Window = new IncidentWindow
        {
            StartedAt = At,
            EndedAt = At.AddSeconds(10),
            PreWindowAvailable = false,
            PostWindowComplete = false,
            MissingRanges = new[] { "pre_window_incomplete", "post_window_incomplete" },
        },
        UserDescriptions = new[] { "用户标记：刚才断网了" },
        EventIds = new[] { "event-1" },
        SnapshotIds = new[] { "snapshot-1" },
        ProbeIds = new[] { "probe-1" },
        DiagnosisIds = Array.Empty<string>(),
        Failures = new[]
        {
            new IncidentFailureGroup
            {
                ClassKey = "target-1|explicit-http-proxy|proxy-1|ipv4|GET|options-1",
                TargetId = "target-1",
                Path = "explicit-http-proxy",
                ProxyEndpointId = "proxy-1",
                AddressFamily = "ipv4",
                Method = "GET",
                FirstAt = At,
                LastAt = At,
                Count = 3,
            },
        },
        MissingRanges = new[] { "pre_window_incomplete", "post_window_incomplete" },
        ClosureReason = "session_end_before_post_window",
        PostWindowComplete = false,
        EffectiveFailureCount = 3,
        EffectiveSuccessCount = 0,
        LastSnapshotId = "snapshot-1",
        LastObservationWindowId = "window-1",
    };
}
