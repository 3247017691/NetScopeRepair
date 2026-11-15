using System.Globalization;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>
/// Persists incident transitions as untyped <c>incident_marker</c> events. The payload deliberately
/// has no <c>payload_version</c>: older readers already skip such events, so adding incident markers
/// cannot turn an existing session into an unsupported-typed-payload problem. The envelope keeps the
/// strict v1 contract and the incident reference, so the markers stay traceable in replay and export.
/// </summary>
public static class IncidentSessionEvents
{
    public const string Source = "incident.recorder";

    public const string SourceVersion = "1.0";

    public static IReadOnlyList<EventEnvelope> Create(IReadOnlyList<IncidentTransition> transitions,
        string sessionId, string bootId, bool synthetic, DateTimeOffset at, double monotonicMs,
        string? batchId = null)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        var events = new List<EventEnvelope>(transitions.Count);
        for (var index = 0; index < transitions.Count; index++)
        {
            var transition = transitions[index];
            if (transition.Synthetic != synthetic)
                throw new InvalidOperationException("Incident transition synthetic marker disagrees with the session.");
            if (!string.Equals(transition.SessionId, sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("Incident transition belongs to another session; it is not restamped.");
            if (!string.Equals(transition.BootId, bootId, StringComparison.Ordinal))
                throw new InvalidOperationException("Incident transition belongs to another boot; it is not restamped.");

            var eventId = synthetic
                ? $"event-{sessionId}{(batchId is null ? "" : "-" + batchId)}-incident-{index + 1:000}"
                : Guid.NewGuid().ToString("D");
            var envelope = new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion,
                EventId = eventId,
                SessionId = sessionId,
                BootId = bootId,
                Sequence = index + 1,
                ObservedAt = transition.At,
                MonotonicMs = double.IsFinite(transition.MonotonicMs) ? transition.MonotonicMs : monotonicMs,
                EventType = EventType.IncidentMarker,
                Status = EventStatus.Ok,
                Source = Source,
                SourceVersion = SourceVersion,
                Synthetic = synthetic,
                Refs = new EventRefs
                {
                    IncidentId = transition.Incident?.IncidentId,
                    SnapshotId = transition.Incident?.LastSnapshotId,
                },
                Payload = BuildPayload(transition),
            };
            _ = EnvelopeJson.Serialize(envelope);
            EnvelopeIntegrity.EnsureComplete(envelope);
            events.Add(envelope);
        }

        return events;
    }

    private static JsonObject BuildPayload(IncidentTransition transition)
    {
        var payload = new JsonObject
        {
            ["kind"] = transition.Kind == IncidentTransitionKind.BusinessCheck ? "business_check" : "state_change",
            ["previous_state"] = Wire(transition.PreviousState),
            ["state"] = Wire(transition.State),
            ["reason"] = transition.Reason,
            ["recorded_at"] = transition.At.ToString("O", CultureInfo.InvariantCulture),
        };
        if (transition.Incident is { } incident)
        {
            payload["incident"] = Describe(incident);
        }

        if (transition.BusinessCheck is { } check)
        {
            payload["business_check"] = Describe(check);
        }

        return payload;
    }

    private static JsonObject Describe(IncidentRecord incident)
    {
        var window = new JsonObject
        {
            ["pre_window_ms"] = incident.Window.PreWindow.TotalMilliseconds,
            ["post_window_ms"] = incident.Window.PostWindow.TotalMilliseconds,
            ["started_at"] = incident.Window.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["pre_window_start"] = incident.Window.PreWindowStart.ToString("O", CultureInfo.InvariantCulture),
            ["pre_window_available"] = incident.Window.PreWindowAvailable,
            ["post_window_complete"] = incident.Window.PostWindowComplete,
            ["missing_ranges"] = ToArray(incident.Window.MissingRanges),
        };
        if (incident.Window.EndedAt is { } endedAt)
        {
            window["ended_at"] = endedAt.ToString("O", CultureInfo.InvariantCulture);
        }

        if (incident.Window.PostWindowEnd is { } postEnd)
        {
            window["post_window_end"] = postEnd.ToString("O", CultureInfo.InvariantCulture);
        }

        var failures = new JsonArray();
        foreach (var failure in incident.Failures)
        {
            failures.Add(new JsonObject
            {
                ["class_key"] = failure.ClassKey,
                ["target_id"] = failure.TargetId,
                ["path"] = failure.Path,
                ["proxy_endpoint_id"] = failure.ProxyEndpointId,
                ["address_family"] = failure.AddressFamily,
                ["method"] = failure.Method,
                ["count"] = failure.Count,
                ["first_at"] = failure.FirstAt.ToString("O", CultureInfo.InvariantCulture),
                ["last_at"] = failure.LastAt.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        var incidentJson = new JsonObject
        {
            ["incident_id"] = incident.IncidentId,
            ["trigger"] = Wire(incident.Trigger),
            ["state"] = Wire(incident.State),
            ["started_at"] = incident.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["pre_window_available"] = incident.Window.PreWindowAvailable,
            ["post_window_complete"] = incident.PostWindowComplete,
            ["effective_failure_count"] = incident.EffectiveFailureCount,
            ["effective_success_count"] = incident.EffectiveSuccessCount,
            ["window"] = window,
            ["user_descriptions"] = ToArray(incident.UserDescriptions),
            ["event_ids"] = ToArray(incident.EventIds),
            ["snapshot_ids"] = ToArray(incident.SnapshotIds),
            ["probe_ids"] = ToArray(incident.ProbeIds),
            ["diagnosis_ids"] = ToArray(incident.DiagnosisIds),
            ["failures"] = failures,
            ["missing_ranges"] = ToArray(incident.MissingRanges),
        };
        if (incident.ClosedAt is { } closedAt)
        {
            incidentJson["closed_at"] = closedAt.ToString("O", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(incident.ClosureReason))
        {
            incidentJson["closure_reason"] = incident.ClosureReason;
        }

        if (incident.LastSnapshotId is not null)
        {
            incidentJson["last_snapshot_id"] = incident.LastSnapshotId;
        }

        if (incident.LastObservationWindowId is not null)
        {
            incidentJson["last_observation_window_id"] = incident.LastObservationWindowId;
        }

        return incidentJson;
    }

    private static JsonObject Describe(BusinessCheckEntry entry) => new()
    {
        ["category"] = Wire(entry.Category),
        ["result"] = Wire(entry.Result),
        ["source"] = entry.Source,
        ["observed_at"] = entry.ObservedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["user_note"] = entry.UserNote,
    };

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    private static string Wire(IncidentState state) => state switch
    {
        IncidentState.Idle => "idle",
        IncidentState.Observing => "observing",
        IncidentState.Suspected => "suspected",
        IncidentState.IncidentOpen => "incident_open",
        IncidentState.Recovering => "recovering",
        IncidentState.Closed => "closed",
        IncidentState.Paused => "paused",
        IncidentState.Degraded => "degraded",
        _ => "unknown",
    };

    private static string Wire(IncidentTrigger trigger) => trigger switch
    {
        IncidentTrigger.ConsecutiveFailures => "consecutive_failures",
        IncidentTrigger.ManualUserMark => "manual_user_mark",
        _ => "unknown",
    };

    private static string Wire(IncidentBusinessCategory category) => category switch
    {
        IncidentBusinessCategory.Dsh => "dsh",
        IncidentBusinessCategory.Antigravity => "antigravity",
        IncidentBusinessCategory.Chrome => "chrome",
        IncidentBusinessCategory.TunApplication => "tun_application",
        _ => "unknown",
    };

    private static string Wire(IncidentBusinessResult result) => result switch
    {
        IncidentBusinessResult.Pass => "pass",
        IncidentBusinessResult.Fail => "fail",
        IncidentBusinessResult.NotTested => "not_tested",
        _ => "unknown",
    };
}
