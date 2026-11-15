using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Contract.Tests;

/// <summary>
/// T11 retention sidecars are small, stable documents that other read-only tools may inspect.
/// The contract keeps the source file name (not a full path), failure/gap counts and first/last
/// failure times, and never pretends the network was healthy.
/// </summary>
public sealed class T11QuotaContractTests
{
    [Fact]
    public void Retention_defaults_and_aggregate_document_shape_are_stable()
    {
        var policy = RetentionPolicy.Default;
        Assert.Equal(TimeSpan.FromDays(7), policy.NormalRetention);
        Assert.Equal(500L * 1024 * 1024, policy.NormalMaxBytes);
        Assert.Equal(1024L * 1024 * 1024, policy.PinnedMaxBytes);
        Assert.Equal(100L * 1024 * 1024, policy.DailyBudgetBytes);
        Assert.Equal(80, policy.WarningPercent);
        Assert.Equal(90, policy.ReducedDetailPercent);
        Assert.Equal(100, policy.StopOptionalDetailPercent);

        var root = Path.Combine(Path.GetTempPath(), "netscope-t11-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var layout = new StorageLayout(root);
            layout.EnsureCreated(StorageFileSystem.Instance);
            var sessionPath = layout.SessionPath("contract-session");
            var failed = new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion,
                EventId = "event-t11-contract-001",
                SessionId = "session-t11-contract",
                BootId = "boot-t11-contract",
                Sequence = 1,
                ObservedAt = now - TimeSpan.FromDays(9),
                MonotonicMs = 0,
                EventType = EventType.Snapshot,
                Status = EventStatus.Timeout,
                Source = "synthetic.t11",
                SourceVersion = "1.0",
                Synthetic = true,
                Refs = new EventRefs(),
                Payload = new JsonObject { ["note"] = "synthetic T11 contract fixture" },
            };
            File.WriteAllText(sessionPath, EnvelopeJson.Serialize(failed) + "\n");
            File.SetLastWriteTimeUtc(sessionPath, (now - TimeSpan.FromDays(9)).UtcDateTime);

            var manager = new QuotaManager(layout,
                new RetentionPolicy { NormalMaxBytes = 1024, NormalRetention = TimeSpan.FromDays(7) },
                StorageFileSystem.Instance, new FixedClock(now));
            var result = manager.Enforce();
            Assert.False(result.Degraded, result.Summary);

            var aggregatePath = StorageLayout.AggregatePathFor(sessionPath);
            Assert.True(File.Exists(aggregatePath));
            var text = File.ReadAllText(aggregatePath);
            var document = JsonNode.Parse(text)!.AsObject();

            foreach (var field in new[]
                     {
                         "schema_version", "kind", "session", "recorded_at", "reason", "event_count",
                         "failure_count", "partial_count", "failure_counts_by_status", "gap_event_count",
                         "sequence_gap_count", "problem_line_count", "first_failure_at", "last_failure_at",
                         "active", "note",
                     })
            {
                Assert.True(document.ContainsKey(field), "missing contract field " + field);
            }

            Assert.Equal("1", document["schema_version"]!.GetValue<string>());
            Assert.Equal("failure_aggregate", document["kind"]!.GetValue<string>());
            Assert.Equal("contract-session.ndjson", document["session"]!.GetValue<string>());
            Assert.Equal(1, document["failure_count"]!.GetValue<int>());
            Assert.Equal(1, document["failure_counts_by_status"]!["timeout"]!.GetValue<int>());
            Assert.False(document["active"]!.GetValue<bool>());
            Assert.Contains("not evidence of a healthy network", document["note"]!.GetValue<string>(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase); // no full local path
            Assert.False(File.Exists(sessionPath), "expired detail was replaced by the aggregate");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp directory must not fail the test.
            }
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }

        public double MonotonicMs => 0;
    }
}
