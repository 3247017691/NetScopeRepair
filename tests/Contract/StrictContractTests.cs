using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Contract.Tests;

/// <summary>
/// Regression tests for the T01-R1 review findings. Every test here failed before the fix:
/// optional references were serialized as null, explicit nulls slipped past required members,
/// numeric enum values became valid states, impossible timestamps passed the validator and
/// schema-invalid lines were still counted as successes.
/// </summary>
public sealed class StrictContractTests
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

    private static JsonDocument LoadSchema() => JsonDocument.Parse(File.ReadAllText(SchemaPath));

    private static string Mutated(Action<JsonObject> mutate, int index = 0)
    {
        var node = JsonNode.Parse(RawEvents()[index])!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    private static IReadOnlyList<string> SchemaProblems(string json)
    {
        using var schema = LoadSchema();
        using var value = JsonDocument.Parse(json);
        return SchemaConformance.Validate(value.RootElement, schema.RootElement);
    }

    [Fact]
    public void Every_serialized_fixture_envelope_still_satisfies_the_schema()
    {
        foreach (var raw in RawEvents())
        {
            var envelope = EnvelopeJson.Deserialize(raw);
            var problems = SchemaProblems(EnvelopeJson.Serialize(envelope));

            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
    }

    [Fact]
    public void Absent_optional_references_are_omitted_instead_of_written_as_null()
    {
        var envelope = EnvelopeJson.Deserialize(RawEvents()[0]) with
        {
            Refs = new EventRefs { SnapshotId = "snapshot-regression" },
        };

        var json = EnvelopeJson.Serialize(envelope);

        Assert.Contains("snapshot-regression", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"incident_id\"", json, StringComparison.Ordinal);
        Assert.Empty(SchemaProblems(json));
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("event_id")]
    [InlineData("session_id")]
    [InlineData("boot_id")]
    [InlineData("source")]
    [InlineData("source_version")]
    public void Explicit_null_required_text_member_is_rejected(string member)
    {
        var json = Mutated(node => node[member] = null);

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData("refs")]
    [InlineData("payload")]
    public void Explicit_null_required_object_member_is_rejected(string member)
    {
        var json = Mutated(node => node[member] = null);

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_identifier_is_rejected_by_the_reader(string value)
    {
        var json = Mutated(node => node["event_id"] = value);

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public void Numeric_status_value_is_rejected(int value)
    {
        var json = Mutated(node => node["status"] = value);

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void Numeric_string_status_value_is_rejected(string value)
    {
        var json = Mutated(node => node["status"] = value);

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Numeric_event_type_value_is_rejected(int value)
    {
        var json = Mutated(node => node["event_type"] = value);

        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Fact]
    public void An_undefined_enum_member_cannot_be_serialized()
    {
        var envelope = EnvelopeJson.Deserialize(RawEvents()[0]) with { Status = (EventStatus)99 };

        Assert.Throws<JsonException>(() => EnvelopeJson.Serialize(envelope));
    }

    [Theory]
    [InlineData("2026-02-30T11:00:00+08:00")]
    [InlineData("2026-13-01T11:00:00Z")]
    [InlineData("2026-11-04T25:00:00+08:00")]
    [InlineData("2026-11-04T11:61:00+08:00")]
    [InlineData("2026-11-04T11:00:61+08:00")]
    [InlineData("2026-11-04T11:00:00+99:99")]
    [InlineData("2026-11-04T11:00:00+08:60")]
    [InlineData("2026-11-04 11:00:00")]
    public void Schema_rejects_impossible_timestamps(string timestamp)
    {
        var json = Mutated(node => node["observed_at"] = timestamp);

        Assert.NotEmpty(SchemaProblems(json));
    }

    [Theory]
    [InlineData("2024-02-29T00:00:00Z")]
    [InlineData("2026-11-04T11:00:00.123456+08:00")]
    [InlineData("2026-11-04T11:00:00-14:00")]
    [InlineData("2026-11-04T11:00:00+14:00")]
    [InlineData("2016-12-31T23:59:60Z")]
    public void Schema_accepts_real_timestamps(string timestamp)
    {
        var json = Mutated(node => node["observed_at"] = timestamp);

        Assert.Empty(SchemaProblems(json));
    }

    [Fact]
    public void Inspection_counts_only_events_that_pass_both_reader_and_schema()
    {
        // sequence 0 is readable but violates minimum 1, so it must not inflate the totals.
        var bad = Mutated(node => node["sequence"] = 0, index: 1);
        var path = WriteTemporaryFixture(RawEvents()[2], bad);

        try
        {
            var result = ContractInspection.Inspect(path, SchemaPath);

            Assert.Single(result.Accepted);
            Assert.Equal(1, result.RejectedCount);
            Assert.Equal(1, result.SuccessCount);
            Assert.False(result.Passed);
            Assert.Contains(result.Problems, problem => problem.Contains("sequence", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fixture_only_inspection_rejects_a_non_synthetic_event()
    {
        var nonSynthetic = Mutated(node => node["synthetic"] = false, index: 1);
        var path = WriteTemporaryFixture(nonSynthetic);

        try
        {
            var result = ContractInspection.Inspect(path, SchemaPath, requireSynthetic: true);

            Assert.Empty(result.Accepted);
            Assert.Equal(1, result.RejectedCount);
            Assert.Equal(0, result.SuccessCount);
            Assert.False(result.Passed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void General_inspection_accepts_real_and_synthetic_events_without_relabelling()
    {
        var real = Mutated(node => node["synthetic"] = false, index: 1);
        var path = WriteTemporaryFixture(real, RawEvents()[2]);
        try
        {
            var result = ContractInspection.Inspect(path, SchemaPath);
            Assert.True(result.Passed);
            Assert.Equal(2, result.Accepted.Count);
            Assert.False(result.Accepted[0].Synthetic);
            Assert.True(result.Accepted[1].Synthetic);
            Assert.Equal(2, result.SuccessCount);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("negative_monotonic")]
    [InlineData("sequence_zero")]
    [InlineData("empty_ref")]
    [InlineData("null_ref")]
    [InlineData("long_id")]
    public void The_core_reader_enforces_the_published_constraints_without_the_CLI(string fault)
    {
        var json = Mutated(node =>
        {
            switch (fault)
            {
                case "version": node["schema_version"] = "99.0"; break;
                case "negative_monotonic": node["monotonic_ms"] = -1; break;
                case "sequence_zero": node["sequence"] = 0; break;
                case "empty_ref": node["refs"]!["snapshot_id"] = ""; break;
                case "null_ref": node["refs"]!["snapshot_id"] = null; break;
                case "long_id": node["event_id"] = new string('x', 129); break;
            }
        });
        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(json));
    }

    [Fact]
    public void Duplicate_core_fields_are_rejected_instead_of_silently_changing_meaning()
    {
        var raw = RawEvents()[0];
        var duplicate = raw.Insert(1, "\"synthetic\":false,");
        Assert.Throws<JsonException>(() => EnvelopeJson.Deserialize(duplicate));
    }

    [Fact]
    public void Inspection_accepts_every_line_of_the_shipped_fixture()
    {
        var result = ContractInspection.Inspect(FixturePath, SchemaPath);

        Assert.Equal(6, result.Accepted.Count);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(4, result.SuccessCount);
        Assert.Equal(1, result.PartialCount);
        Assert.Equal(1, result.ObservationGapCount);
        Assert.True(result.Passed);
    }

    private static string WriteTemporaryFixture(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netscope-contract-{Guid.NewGuid():N}.ndjson");
        File.WriteAllLines(path, lines);
        return path;
    }
}
