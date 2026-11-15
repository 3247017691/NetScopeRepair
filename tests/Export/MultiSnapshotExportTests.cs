using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Application.Mock;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>
/// A real observation session appends one snapshot per cycle. Export must accept that normal
/// multi-snapshot session, keep every snapshot in the package, and replay it per cycle.
/// </summary>
public sealed class MultiSnapshotExportTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-05T04:00:00Z");
    private const string Session = "session-export-multi";
    private const string Boot = "boot-export-multi";

    [Fact]
    public async Task A_two_cycle_session_is_previewed_exported_and_replayed_per_snapshot()
    {
        using var workspace = new TempWorkspace();
        var session = await BuildSessionAsync(workspace, "multi.ndjson");
        var exporter = new IncidentExporter();

        var preview = exporter.Preview(session);
        Assert.True(preview.CanExport, string.Join("; ", preview.BlockedReasons));
        Assert.Equal(8, preview.EventCount); // two cycles x (snapshot + probe + record + diagnosis)
        Assert.Equal(2, preview.DiagnosisCount);
        Assert.Equal(2, preview.Files.Count(file => file.Name.StartsWith("snapshots/", StringComparison.Ordinal)));
        // The synthetic collector cannot read WinHTTP, so the separated completeness section must
        // say so instead of pretending the capture is complete just because gaps is zero.
        Assert.All(preview.Warnings, warning => Assert.Contains("incomplete data domains", warning));
        Assert.DoesNotContain(preview.Warnings, warning =>
            warning.Contains("probe results", StringComparison.Ordinal) ||
            warning.Contains("mixes synthetic", StringComparison.Ordinal));

        var result = await exporter.ExportAsync(session, workspace.Subdirectory("packages"), CancellationToken.None);
        var entries = PackageReader.Read(result.PackagePath);
        Assert.Contains("snapshots/snapshot-001.json", entries.Keys);
        Assert.Contains("snapshots/snapshot-002.json", entries.Keys);
        Assert.Equal(8, entries.Count);

        var manifest = PackageReader.Json(PackageReader.Text(entries["manifest.json"])).AsObject();
        Assert.Equal(8, manifest["counts"]!["events"]!.GetValue<int>());
        Assert.Equal(2, manifest["counts"]!["snapshots"]!.GetValue<int>());
        Assert.Equal(2, manifest["counts"]!["diagnoses"]!.GetValue<int>());
        Assert.False(manifest["synthetic"]!.GetValue<bool>());
        Assert.Equal(2, manifest["files"]!.AsArray().Count(file =>
            file!["name"]!.GetValue<string>().StartsWith("snapshots/", StringComparison.Ordinal)));

        var diagnoses = PackageReader.Json(PackageReader.Text(entries["diagnoses.json"])).AsObject();
        Assert.Equal(2, diagnoses["diagnoses"]!.AsArray().Count);

        // The exported events must still be a clean typed session, and replay must preserve both
        // snapshot scopes rather than mixing both cycles' probes into one current view.
        var exportedEvents = workspace.File("exported-multi.ndjson");
        await File.WriteAllBytesAsync(exportedEvents, entries["events.ndjson"]);
        var replay = new ReplayService().Replay(exportedEvents);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(2, replay.SnapshotScopes.Count);
        Assert.Equal(new[] { "probe-cycle-1" }, replay.SnapshotScopes[0].Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.Equal(new[] { "probe-cycle-2" }, replay.SnapshotScopes[1].Evidence.Probes.Select(probe => probe.ProbeId));
        Assert.Equal(new[] { "record-cycle-1" }, replay.SnapshotScopes[0].Evidence.Records.Select(record => record.EvidenceId));
        Assert.Equal(new[] { "record-cycle-2" }, replay.SnapshotScopes[1].Evidence.Records.Select(record => record.EvidenceId));
        Assert.Equal("snapshot-cycle-2", replay.Evidence!.Snapshot!.SnapshotId);
        Assert.DoesNotContain(replay.Evidence.Probes, probe => probe.ProbeId == "probe-cycle-1");

        var crossCycle = Assert.Single(replay.Diagnoses, diagnosis => diagnosis.DiagnosisId == "diagnosis-cycle-2");
        Assert.Contains("probe-cycle-2", crossCycle.EvidenceIds);
        Assert.Contains("record-cycle-1", crossCycle.EvidenceIds);
    }

    private static async Task<string> BuildSessionAsync(TempWorkspace workspace, string name)
    {
        var path = workspace.File(name);
        using var log = new NdjsonEventLog(path);
        var sequence = 1;
        for (var index = 1; index <= 2; index++)
        {
            var at = Origin.AddSeconds((index - 1) * 10);
            var monotonic = (index - 1) * 10_000d;
            var snapshotId = "snapshot-cycle-" + index;
            var probeId = "probe-cycle-" + index;
            var recordId = "record-cycle-" + index;
            var diagnosisId = "diagnosis-cycle-" + index;
            var snapshot = await new SyntheticSnapshotCollector().CollectAsync(new CollectionContext
            {
                SessionId = Session,
                BootId = Boot,
                SnapshotId = snapshotId,
                ObservedAt = at,
                MonotonicMs = monotonic,
                Synthetic = false,
            }, CancellationToken.None);

            var probe = new ProbeResult
            {
                ProbeId = probeId,
                ProbeGroupId = "group-cycle-" + index,
                TargetId = "target-multi",
                Path = ProbePath.NoExplicitProxy,
                Status = EventStatus.Ok,
                StartedAt = at.AddSeconds(1),
                DurationMs = 20,
                HttpStatus = 204,
                Classification = ProbeClassification.ReachableExpected,
                AddressFamily = "ipv4",
                Method = "GET",
                RequestOptionsKey = "options-" + index,
                ObservationWindowId = "window-" + index,
                BootId = Boot,
                StartedMonotonicMs = monotonic + 1000,
                TimingComparable = true,
                ResolvedAddresses = new[] { "198.18.0.11" },
            };
            var record = new EvidenceRecord
            {
                EvidenceId = recordId,
                Kind = "client_runtime_config",
                Summary = "Synthetic multi-cycle export evidence.",
                ObservedAt = at,
                MonotonicMs = monotonic,
                BootId = Boot,
                Source = "synthetic.runtime",
                Provenance = "runtime_api",
                Status = EventStatus.Ok,
                TargetId = "target-multi",
                AddressFamily = "ipv4",
                ObservationWindowId = "window-" + index,
                Facts = new Dictionary<string, string>
                {
                    ["snapshot_id"] = snapshotId,
                    ["client"] = "synthetic",
                    ["running"] = "true",
                    ["dns_mode"] = "fake-ip",
                },
            };
            var diagnosis = new Diagnosis
            {
                DiagnosisId = diagnosisId,
                RuleId = "DX-MULTI",
                RuleVersion = "1.0.0",
                Level = EvidenceGrade.Supported,
                Summary = "Synthetic multi-cycle diagnosis fixture.",
                EvidenceIds = new[] { probeId, recordId },
                CounterEvidenceIds = Array.Empty<string>(),
                AllowedRepairActions = Array.Empty<string>(),
                SuggestedNextStep = "Read-only fixture; no repair.",
                Scope = new DiagnosisScope
                {
                    TargetId = "target-multi",
                    AddressFamily = "ipv4",
                    StartedAt = at,
                    EndedAt = at.AddSeconds(30),
                    ValidUntil = at.AddSeconds(30),
                    BootId = Boot,
                    ObservationWindowId = "window-" + index,
                },
            };

            var events = EvidenceSessionEvents.Create(new EvidenceSet
            {
                Snapshot = snapshot,
                Probes = new[] { probe },
                Records = new[] { record },
            }, new[] { diagnosis }, Session, Boot, synthetic: false, at.AddSeconds(30), monotonic + 30_000);

            if (index == 2)
            {
                events = events.Select(envelope =>
                {
                    if (envelope.Refs.DiagnosisId != diagnosisId) return envelope;
                    var payload = envelope.Payload.DeepClone().AsObject();
                    ((JsonArray)payload["diagnosis"]!["evidence_ids"]!).Add("record-cycle-1");
                    return envelope with { Payload = payload };
                }).ToArray();
            }

            foreach (var envelope in events)
                await log.AppendAsync(envelope with { Sequence = sequence++ }, CancellationToken.None);
        }

        return path;
    }
}
