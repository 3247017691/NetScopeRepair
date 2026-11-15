using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

/// <summary>
/// Reproduces the shape of the reviewed capture without using it: 102 synthetic snapshots, every
/// interfaces domain Partial, the WinHTTP proxy subdomain Unsupported, zero probes and zero
/// diagnoses. The package must report that shape instead of treating gaps=0 as completeness.
/// </summary>
public sealed class T08R2CompletenessTests
{
    private const int SnapshotCount = 102;

    [Fact]
    public async Task One_hundred_two_partial_snapshots_are_reported_separately_from_gaps_and_probes()
    {
        using var workspace = new TempWorkspace();
        var session = await BuildSessionAsync(workspace, "t08r2-102.ndjson");

        var preview = new IncidentExporter().Preview(session);
        Assert.True(preview.CanExport, string.Join("; ", preview.BlockedReasons));
        Assert.Equal(SnapshotCount, preview.EventCount);
        Assert.Equal(0, preview.GapCount); // historical preview field: observation-gap events, not completeness
        Assert.Equal(0, preview.DiagnosisCount);
        Assert.Equal(108, preview.Files.Count); // manifest + events + 102 snapshots + 4 documents + checksums
        Assert.Contains(preview.Warnings, warning => warning.Contains("no probe results", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, warning => warning.Contains("incomplete data domains", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, warning => warning.Contains("no diagnoses", StringComparison.Ordinal));

        var details = new IncidentExporter().PreviewDetails(session);
        var statistics = details.Statistics;

        Assert.Equal(0, statistics.Gaps.SequenceGapCount);
        Assert.Equal(0, statistics.Gaps.GapEventCount);
        Assert.Equal(0, statistics.Gaps.ObservationGapEventCount);
        Assert.Equal(0, statistics.Gaps.FailedEventCount);

        Assert.Equal(SnapshotCount, statistics.Completeness.SnapshotCount);
        Assert.Equal(SnapshotCount, statistics.Completeness.IncompleteSnapshotCount);
        Assert.False(statistics.Completeness.CollectionComplete);
        Assert.Contains(statistics.Completeness.Domains,
            item => item.Path == "interfaces" && item.Status == "partial" && item.Count == SnapshotCount);
        Assert.Contains(statistics.Completeness.Domains,
            item => item.Path == "routes" && item.Status == "ok" && item.Count == SnapshotCount);
        Assert.Contains(statistics.Completeness.Domains,
            item => item.Path == "dns" && item.Status == "ok" && item.Count == SnapshotCount);
        Assert.Contains(statistics.Completeness.Domains,
            item => item.Path == "proxy" && item.Status == "partial" && item.Count == SnapshotCount);
        Assert.Contains(statistics.Completeness.ProxySubdomains,
            item => item.Path == "proxy.winhttp" && item.Status == "unsupported" && item.Count == SnapshotCount);
        Assert.Contains(statistics.Completeness.ProxySubdomains,
            item => item.Path == "proxy.wininet" && item.Status == "ok" && item.Count == SnapshotCount);

        Assert.Equal(0, statistics.ProbeCoverage.ProbeEvents);
        Assert.False(statistics.ProbeCoverage.HasProbeResults);
        Assert.Equal(0, statistics.ProbeCoverage.ProbeGroups);
        Assert.Equal(0, statistics.ProbeCoverage.TargetCount);

        Assert.Matches("^[0-9a-f]{7,40}$", statistics.Versions.AppBuildCommit);
        Assert.Equal("assembly_informational_version", statistics.Versions.AppBuildCommitSource);
        Assert.False(string.IsNullOrWhiteSpace(statistics.Versions.AppVersion));
        Assert.Equal("m1-alias-v3", statistics.Versions.RedactionPolicyVersion);
        Assert.Equal("1", statistics.Versions.PackageFormatVersion);
        Assert.Contains("m0-evidence-1", statistics.Versions.EvidencePayloadVersions);
        Assert.Contains("1.1", statistics.Versions.SourceVersions);
        Assert.Empty(statistics.Versions.RuleVersions);
        Assert.Empty(statistics.Versions.ParserVersions);

        Assert.Equal(0, statistics.Diagnoses.RecordedInPackage);
        Assert.Equal(0, statistics.Diagnoses.RecomputedByExporter);

        // The reviewed change shape: interface 44 metric 0 -> 4225 with both flags, then the
        // interface disappears for two snapshots and comes back, all with synthetic identities.
        Assert.Equal(SnapshotCount - 1, statistics.Changes.ComparablePairs);
        Assert.Equal(2, statistics.Changes.InterfacesCompared);
        Assert.False(statistics.Changes.Truncated);
        Assert.Equal(5, statistics.Changes.Changes.Count);
        Assert.Contains(statistics.Changes.FieldCounts, item => item.Field == "metric" && item.Count == 1);
        Assert.Contains(statistics.Changes.FieldCounts, item => item.Field == "automatic_metric" && item.Count == 1);
        Assert.Contains(statistics.Changes.FieldCounts, item => item.Field == "disable_default_routes" && item.Count == 1);
        Assert.Contains(statistics.Changes.FieldCounts, item => item.Field == "observed" && item.Count == 2);
        var metricChange = Assert.Single(statistics.Changes.Changes, change => change.Field == "metric");
        Assert.Equal(22, metricChange.FromSequence);
        Assert.Equal(23, metricChange.ToSequence);
        Assert.Equal("0", metricChange.Before);
        Assert.Equal("4225", metricChange.After);
    }

    [Fact]
    public async Task The_102_snapshot_package_has_108_entries_verified_checksums_and_replays_per_cycle()
    {
        using var workspace = new TempWorkspace();
        var session = await BuildSessionAsync(workspace, "t08r2-102-package.ndjson");
        var exporter = new IncidentExporter();
        var result = await exporter.ExportAsync(session, workspace.Subdirectory("packages"), CancellationToken.None);
        var entries = PackageReader.Read(result.PackagePath);

        Assert.Equal(108, entries.Count);
        Assert.Equal(SnapshotCount, entries.Keys.Count(name => name.StartsWith("snapshots/", StringComparison.Ordinal)));
        Assert.Contains("snapshots/snapshot-001.json", entries.Keys);
        Assert.Contains("snapshots/snapshot-102.json", entries.Keys);

        var manifest = PackageReader.Json(PackageReader.Text(entries["manifest.json"])).AsObject();
        Assert.Equal("1.0", manifest["schema_version"]!.GetValue<string>());
        Assert.Equal("netscope-incident-package", manifest["package_format"]!.GetValue<string>());
        Assert.Equal("1", manifest["package_format_version"]!.GetValue<string>());
        Assert.True(manifest["synthetic"]!.GetValue<bool>());

        var counts = manifest["counts"]!.AsObject();
        Assert.Equal(SnapshotCount, counts["events"]!.GetValue<int>());
        Assert.Equal(SnapshotCount, counts["snapshots"]!.GetValue<int>());
        Assert.Equal(0, counts["diagnoses"]!.GetValue<int>());
        Assert.Equal(0, counts["gaps"]!.GetValue<int>());
        Assert.Equal(0, counts["gap_events"]!.GetValue<int>());
        Assert.Equal(0, counts["observation_gap_events"]!.GetValue<int>());
        Assert.Equal(SnapshotCount, counts["partial_events"]!.GetValue<int>());
        Assert.Equal(0, counts["probe_events"]!.GetValue<int>());
        Assert.Equal(0, counts["failed_events"]!.GetValue<int>());

        var completeness = manifest["completeness"]!.AsObject();
        Assert.Equal(SnapshotCount, completeness["snapshots"]!.GetValue<int>());
        Assert.Equal(SnapshotCount, completeness["incomplete_snapshots"]!.GetValue<int>());
        Assert.False(completeness["collection_complete"]!.GetValue<bool>());
        Assert.Equal(SnapshotCount, completeness["domain_status_counts"]!["interfaces"]!["partial"]!.GetValue<int>());
        Assert.Equal(SnapshotCount, completeness["domain_status_counts"]!["routes"]!["ok"]!.GetValue<int>());
        Assert.Equal(SnapshotCount, completeness["proxy_subdomain_status_counts"]!["proxy.winhttp"]!["unsupported"]!.GetValue<int>());
        Assert.Equal(SnapshotCount, completeness["proxy_subdomain_status_counts"]!["proxy.wininet"]!["ok"]!.GetValue<int>());
        Assert.Contains("gaps=0 only means no independent gap event", completeness["note"]!.GetValue<string>(),
            StringComparison.Ordinal);

        var probeCoverage = manifest["probe_coverage"]!.AsObject();
        Assert.False(probeCoverage["has_probe_results"]!.GetValue<bool>());
        Assert.Equal(0, probeCoverage["probe_events"]!.GetValue<int>());

        var changeSummary = manifest["change_summary"]!.AsObject();
        Assert.Equal(SnapshotCount - 1, changeSummary["comparable_pairs"]!.GetValue<int>());
        Assert.Equal(5, changeSummary["changes"]!.AsArray().Count);
        Assert.False(changeSummary["truncated"]!.GetValue<bool>());

        var provenance = manifest["diagnosis_provenance"]!.AsObject();
        Assert.Equal(0, provenance["recorded_in_package"]!.GetValue<int>());
        Assert.Equal(0, provenance["recomputed_by_exporter"]!.GetValue<int>());

        var versions = manifest["versions"]!.AsObject();
        Assert.Matches("^[0-9a-f]{7,40}$", versions["app_build_commit"]!.GetValue<string>());
        Assert.Equal("assembly_informational_version", versions["app_build_commit_source"]!.GetValue<string>());
        Assert.Equal("m1-alias-v3", versions["redaction_policy_version"]!.GetValue<string>());

        // Old count field semantics are untouched; new separated fields are additive.
        Assert.Equal(manifest["counts"]!["gaps"]!.GetValue<int>(), manifest["gap_statistics"]!["sequence_gaps"]!.GetValue<int>());
        Assert.Equal(manifest["counts"]!["observation_gap_events"]!.GetValue<int>(),
            manifest["gap_statistics"]!["observation_gap_events"]!.GetValue<int>());

        Assert.Contains(manifest["warnings"]!.AsArray().Select(item => item!.GetValue<string>()),
            warning => warning.Contains("no probe results", StringComparison.Ordinal));
        Assert.Contains(manifest["warnings"]!.AsArray().Select(item => item!.GetValue<string>()),
            warning => warning.Contains("incomplete data domains", StringComparison.Ordinal));

        // Every declared file digest matches; checksums cover manifest plus the 106 payload files.
        var declared = manifest["files"]!.AsArray()
            .Select(item => item!.AsObject())
            .ToDictionary(item => item["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal(106, declared.Count);
        Assert.DoesNotContain("manifest.json", declared.Keys);
        Assert.DoesNotContain("checksums.sha256", declared.Keys);
        foreach (var (name, file) in declared)
        {
            Assert.True(entries.ContainsKey(name), name);
            Assert.Equal(entries[name].LongLength, file["bytes"]!.GetValue<long>());
            Assert.Equal(Sha256(entries[name]), file["sha256"]!.GetValue<string>());
        }

        var checksumLines = PackageReader.Text(entries["checksums.sha256"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToArray();
        Assert.Equal(107, checksumLines.Length);
        foreach (var pair in checksumLines)
        {
            Assert.Equal(2, pair.Length);
            Assert.True(entries.ContainsKey(pair[1]), pair[1]);
            Assert.Equal(Sha256(entries[pair[1]]), pair[0]);
        }

        // Export must stay replayable per cycle: 102 snapshot scopes, no probes borrowed between them.
        var exportedSession = workspace.File("t08r2-102-exported.ndjson");
        await File.WriteAllBytesAsync(exportedSession, entries["events.ndjson"]);
        var replay = new ReplayService().Replay(exportedSession);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(SnapshotCount, replay.SnapshotScopes.Count);
        Assert.All(replay.SnapshotScopes, scope => Assert.Empty(scope.Evidence.Probes));
        Assert.All(replay.SnapshotScopes, scope => Assert.Empty(scope.Evidence.Records));
        Assert.Empty(replay.Diagnoses);
        Assert.Empty(replay.EvidenceProblems);
        Assert.NotNull(replay.Evidence);
        Assert.Equal("snapshot-t08r2-102-102", replay.Evidence!.Snapshot!.SnapshotId);
        Assert.Null(replay.UnscopedEvidence);
        Assert.Equal(SnapshotCount, replay.Log.Events.Count);

        Assert.True(File.Exists(result.PackagePath));
        Assert.Matches(@"^incident-[0-9a-f]{24}\.zip$", Path.GetFileName(result.PackagePath));
    }

    [Fact]
    public async Task Two_exports_of_the_102_snapshot_session_report_the_same_shape()
    {
        using var workspace = new TempWorkspace();
        var session = await BuildSessionAsync(workspace, "t08r2-102-deterministic.ndjson");
        var exporter = new IncidentExporter();
        var first = await exporter.ExportAsync(session, workspace.Subdirectory("first"), CancellationToken.None);
        var second = await exporter.ExportAsync(session, workspace.Subdirectory("second"), CancellationToken.None);

        var firstEntries = PackageReader.Read(first.PackagePath);
        var secondEntries = PackageReader.Read(second.PackagePath);
        Assert.Equal(firstEntries.Keys.Order(StringComparer.Ordinal), secondEntries.Keys.Order(StringComparer.Ordinal));

        // The change summary and completeness sections carry no salt-dependent values, so only the
        // incident id and file digests differ between two exports of the same session.
        var firstManifest = PackageReader.Json(PackageReader.Text(firstEntries["manifest.json"])).AsObject();
        var secondManifest = PackageReader.Json(PackageReader.Text(secondEntries["manifest.json"])).AsObject();
        Assert.NotEqual(firstManifest["incident_id"]!.GetValue<string>(), secondManifest["incident_id"]!.GetValue<string>());
        firstManifest.Remove("incident_id");
        secondManifest.Remove("incident_id");
        firstManifest.Remove("files");
        secondManifest.Remove("files");
        Assert.Equal(firstManifest.ToJsonString(), secondManifest.ToJsonString());
    }

    [Fact]
    public void PreviewDetails_is_safe_for_a_blocked_session_and_reports_unknown_instead_of_zero_completeness()
    {
        using var workspace = new TempWorkspace();
        var details = new IncidentExporter().PreviewDetails(workspace.File("absent.ndjson"));

        Assert.False(details.Preview.CanExport);
        Assert.Contains(details.Preview.BlockedReasons, reason =>
            reason.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, details.Statistics.Completeness.SnapshotCount);
        Assert.False(details.Statistics.Completeness.CollectionComplete);
        Assert.False(details.Statistics.ProbeCoverage.HasProbeResults);
        Assert.Equal(0, details.Statistics.Diagnoses.RecordedInPackage);
        Assert.Equal(0, details.Statistics.Diagnoses.RecomputedByExporter);
        Assert.Contains("unknown", details.Statistics.Completeness.Note, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> BuildSessionAsync(TempWorkspace workspace, string name)
    {
        var cycles = new List<CycleEvidence>(SnapshotCount);
        for (var index = 1; index <= SnapshotCount; index++)
        {
            var at = T08R2Fixture.Origin.AddSeconds((index - 1) * 10);
            var monotonic = (index - 1) * 10_000d;
            var interfaces = new List<InterfaceState>
            {
                T08R2Fixture.Interface("interface-t08r2-102-a", "Synthetic-Uplink-A", 12,
                    addresses: new[] { "192.168.7.10/24" }, metric: 25),
            };

            // Interface 44 exists for a while, changes three parameters at snapshot 23, disappears
            // for snapshots 24-25, then reappears with different flags. Synthetic shape only; it is
            // modeled on the reviewed change but uses invented identities and values.
            if (index is <= 23 or >= 26)
            {
                interfaces.Add(index switch
                {
                    23 => T08R2Fixture.Interface("interface-t08r2-102-44", "Synthetic-Uplink-44", 44,
                        addresses: new[] { "10.77.0.5/24" }, metric: 4225, automaticMetric: false,
                        disableDefaultRoutes: true),
                    >= 26 => T08R2Fixture.Interface("interface-t08r2-102-44", "Synthetic-Uplink-44", 44,
                        addresses: new[] { "10.77.0.5/24" }, metric: 0, automaticMetric: false,
                        disableDefaultRoutes: false),
                    _ => T08R2Fixture.Interface("interface-t08r2-102-44", "Synthetic-Uplink-44", 44,
                        addresses: new[] { "10.77.0.5/24" }, metric: 0, automaticMetric: true,
                        disableDefaultRoutes: false),
                });
            }

            cycles.Add(new CycleEvidence
            {
                Snapshot = T08R2Fixture.Snapshot(
                    "snapshot-t08r2-102-" + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                    at,
                    monotonic,
                    interfaces,
                    interfacesStatus: EventStatus.Partial,
                    interfacesReason: "Synthetic fixture: the interfaces domain is only partially readable.",
                    interfacesErrors: new[]
                    {
                        "GetIfEntry2 failed for adapter \"vEthernet (Default Switch)\": synthetic partial read",
                    }),
            });
        }

        return await T08R2Fixture.WriteAsync(workspace.File(name), cycles);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
