using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;

namespace NetScope.Contract.Tests;

/// <summary>
/// T01 contract tests. They run entirely against the checked-in schema and the synthetic
/// fixture: no network access, no system configuration, no repair capability.
/// </summary>
public sealed class EnvelopeContractTests
{
    private static readonly string SchemaPath =
        Path.Combine(AppContext.BaseDirectory, "contracts", "event-envelope.schema.json");

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-incident.ndjson");

    private static string[] RawEvents() => File
        .ReadAllLines(FixturePath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.Trim())
        .ToArray();

    private static List<EventEnvelope> Envelopes() => RawEvents().Select(EnvelopeJson.Deserialize).ToList();

    private static JsonDocument LoadSchema() => JsonDocument.Parse(File.ReadAllText(SchemaPath));

    private static string MutatedFirstEvent(Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(RawEvents()[0])!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    [Fact]
    public void Contract_files_are_present_next_to_the_tests()
    {
        Assert.True(File.Exists(SchemaPath), $"missing contract at {SchemaPath}");
        Assert.True(File.Exists(FixturePath), $"missing fixture at {FixturePath}");
    }

    [Fact]
    public void Fixture_events_deserialize_with_the_supported_schema_version()
    {
        var envelopes = Envelopes();

        Assert.NotEmpty(envelopes);
        Assert.All(envelopes, envelope =>
            Assert.Equal(EventEnvelope.SupportedSchemaVersion, envelope.SchemaVersion));
    }

    [Fact]
    public void Fixture_events_are_all_marked_synthetic()
    {
        Assert.All(Envelopes(), envelope =>
            Assert.True(envelope.Synthetic, $"{envelope.EventId} must be marked synthetic"));
    }

    [Fact]
    public void Fixture_sequence_and_identifiers_are_well_formed()
    {
        var envelopes = Envelopes();

        Assert.Equal(Enumerable.Range(1, envelopes.Count), envelopes.Select(envelope => envelope.Sequence));
        Assert.Equal(envelopes.Count, envelopes.Select(envelope => envelope.EventId).Distinct().Count());

        var monotonic = envelopes.Select(envelope => envelope.MonotonicMs).ToList();
        Assert.Equal(monotonic.OrderBy(value => value), monotonic);
    }

    [Fact]
    public void Missing_required_envelope_field_is_rejected()
    {
        var json = MutatedFirstEvent(node =>
        {
            node.Remove("event_id");
        });

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Fact]
    public void Missing_payload_is_rejected()
    {
        var json = MutatedFirstEvent(node =>
        {
            node.Remove("payload");
        });

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Fact]
    public void Unknown_envelope_property_is_rejected()
    {
        var json = MutatedFirstEvent(node =>
        {
            node["unexpected_envelope_field"] = "must not be ignored";
        });

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Fact]
    public void Unknown_reference_property_is_rejected()
    {
        var json = MutatedFirstEvent(node =>
        {
            node["refs"]!.AsObject()["unexpected_ref"] = "must not be ignored";
        });

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData("not-a-status")]
    [InlineData("OK")]
    [InlineData("")]
    public void Unknown_status_value_is_rejected(string status)
    {
        var json = MutatedFirstEvent(node =>
        {
            node["status"] = status;
        });

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData("not-an-event-type")]
    [InlineData("ProbeResult")]
    public void Unknown_event_type_value_is_rejected(string eventType)
    {
        var json = MutatedFirstEvent(node =>
        {
            node["event_type"] = eventType;
        });

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Fact]
    public void Schema_only_uses_keywords_the_validator_implements()
    {
        using var schema = LoadSchema();

        var unsupported = SchemaConformance.FindUnsupportedKeywords(schema.RootElement);

        Assert.True(unsupported.Count == 0, "Unsupported schema keywords: " + string.Join(", ", unsupported));
    }

    [Fact]
    public void Every_fixture_event_conforms_to_the_published_schema()
    {
        using var schema = LoadSchema();

        foreach (var raw in RawEvents())
        {
            using var value = JsonDocument.Parse(raw);
            var problems = SchemaConformance.Validate(value.RootElement, schema.RootElement);

            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
    }

    [Fact]
    public void Schema_check_rejects_missing_required_property()
    {
        using var schema = LoadSchema();
        var json = MutatedFirstEvent(node =>
        {
            node.Remove("payload");
        });

        using var value = JsonDocument.Parse(json);

        Assert.NotEmpty(SchemaConformance.Validate(value.RootElement, schema.RootElement));
    }

    [Fact]
    public void Schema_check_rejects_unexpected_schema_version()
    {
        using var schema = LoadSchema();
        var json = MutatedFirstEvent(node =>
        {
            node["schema_version"] = "2.0";
        });

        using var value = JsonDocument.Parse(json);

        Assert.NotEmpty(SchemaConformance.Validate(value.RootElement, schema.RootElement));
    }

    [Fact]
    public void Schema_check_rejects_unknown_property()
    {
        using var schema = LoadSchema();
        var json = MutatedFirstEvent(node =>
        {
            node["extra"] = 1;
        });

        using var value = JsonDocument.Parse(json);

        Assert.NotEmpty(SchemaConformance.Validate(value.RootElement, schema.RootElement));
    }

    [Fact]
    public void Schema_check_rejects_non_rfc3339_timestamp()
    {
        using var schema = LoadSchema();
        var json = MutatedFirstEvent(node =>
        {
            node["observed_at"] = "2026-11-04 11:00:00";
        });

        using var value = JsonDocument.Parse(json);

        Assert.NotEmpty(SchemaConformance.Validate(value.RootElement, schema.RootElement));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("unsupported")]
    [InlineData("access_denied")]
    [InlineData("unknown")]
    public void Only_ok_counts_as_success(string status)
    {
        var json = MutatedFirstEvent(node =>
        {
            node["status"] = status;
        });

        var envelope = EnvelopeJson.Deserialize(json);

        Assert.Equal(status == "ok", envelope.Status.IsSuccess());
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("timeout", true)]
    [InlineData("cancelled", true)]
    [InlineData("unsupported", true)]
    [InlineData("access_denied", true)]
    [InlineData("unknown", true)]
    [InlineData("partial", false)]
    [InlineData("ok", false)]
    public void Observation_gap_is_classified_explicitly(string status, bool isGap)
    {
        var json = MutatedFirstEvent(node =>
        {
            node["status"] = status;
        });

        var envelope = EnvelopeJson.Deserialize(json);

        Assert.Equal(isGap, envelope.Status.IsObservationGap());
    }

    [Fact]
    public void Envelope_round_trips_through_the_contract_serializer()
    {
        var original = Envelopes()[1];

        var roundTripped = EnvelopeJson.Deserialize(EnvelopeJson.Serialize(original));

        Assert.Equal(original.EventId, roundTripped.EventId);
        Assert.Equal(original.Sequence, roundTripped.Sequence);
        Assert.Equal(original.EventType, roundTripped.EventType);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.Synthetic, roundTripped.Synthetic);
        Assert.Equal(original.Payload.ToJsonString(), roundTripped.Payload.ToJsonString());
    }
}
