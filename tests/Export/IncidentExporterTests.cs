using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

public sealed class IncidentExporterTests
{
    private static readonly string[] ExpectedFiles =
    {
        "manifest.json",
        "events.ndjson",
        "snapshots/snapshot-001.json",
        "diagnoses.json",
        "timeline.json",
        "redaction-report.json",
        "checksums.sha256",
    };

    [Fact]
    public async Task Preview_blocks_a_dirty_log_and_export_writes_no_package()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("dirty.ndjson"));
        await File.AppendAllTextAsync(session, "{\"schema_version\":");

        var exporter = new IncidentExporter();
        var preview = exporter.Preview(session);

        Assert.False(preview.CanExport);
        Assert.NotEmpty(preview.BlockedReasons);
        Assert.Empty(preview.Files);
        Assert.Empty(preview.Redactions);

        var destination = workspace.File("not-created");
        var exception = await Assert.ThrowsAsync<ExportBlockedException>(
            () => exporter.ExportAsync(session, destination, CancellationToken.None));
        Assert.NotEmpty(exception.BlockedReasons);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Preview_blocks_envelope_only_session_because_typed_evidence_is_unavailable()
    {
        using var workspace = new TempWorkspace();
        var session = workspace.File("legacy.ndjson");
        using (var log = new NdjsonEventLog(session))
        {
            await log.AppendAsync(ExportSession.UntypedEvent(), CancellationToken.None);
        }

        var preview = new IncidentExporter().Preview(session);

        Assert.False(preview.CanExport);
        Assert.Contains(preview.BlockedReasons, reason =>
            reason.Contains("Typed evidence is unavailable", StringComparison.Ordinal));
        Assert.Equal(1, preview.EventCount);
    }

    [Fact]
    public void Preview_blocks_a_missing_session_without_throwing()
    {
        using var workspace = new TempWorkspace();
        var preview = new IncidentExporter().Preview(workspace.File("absent.ndjson"));

        Assert.False(preview.CanExport);
        Assert.Contains(preview.BlockedReasons, reason =>
            reason.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_reports_files_counts_and_categories_without_original_values()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var exporter = new IncidentExporter();

        var preview = exporter.Preview(session);

        Assert.True(preview.CanExport, string.Join("; ", preview.BlockedReasons));
        Assert.Equal(5, preview.EventCount);
        Assert.Equal(1, preview.GapCount); // the timeout probe is an observation gap, not a success
        Assert.Equal(1, preview.DiagnosisCount);
        Assert.Equal("good.ndjson", preview.SessionPath);
        Assert.DoesNotContain(workspace.Root, preview.SessionPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExpectedFiles.Order(StringComparer.Ordinal), preview.Files.Select(file => file.Name).Order(StringComparer.Ordinal));
        Assert.All(preview.Files, file => Assert.True(file.Bytes > 0, file.Name));
        Assert.All(preview.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.Purpose)));

        var categories = preview.Redactions.Select(item => item.Category).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "user_name", "device_name", "local_path", "private_host", "public_target",
                     "fake_ip", "private_ip", "proxy_credential", "authorization", "token",
                     "password", "query_secret",
                 })
        {
            Assert.Contains(expected, categories);
        }

        Assert.All(preview.Redactions, item => Assert.True(item.ReplacedFields > 0, item.Category));
        Assert.All(preview.Redactions, item => Assert.False(string.IsNullOrWhiteSpace(item.Strategy)));

        var serialized = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("\"salt\"", serialized, StringComparison.OrdinalIgnoreCase);
        foreach (var secret in ExportSession.Secrets.Where(secret => secret.Length > 3))
        {
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Preview_blocks_semantically_inconsistent_typed_evidence_without_writing()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var corrupted = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(session))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var node = JsonNode.Parse(line)!.AsObject();
            if (node["payload"]?["diagnosis"] is JsonObject diagnosis)
            {
                diagnosis["evidence_ids"] = new JsonArray("missing-evidence");
            }

            corrupted.Add(node.ToJsonString());
        }

        var path = workspace.File("inconsistent.ndjson");
        await File.WriteAllLinesAsync(path, corrupted);

        var exporter = new IncidentExporter();
        var preview = exporter.Preview(path);
        Assert.False(preview.CanExport);
        Assert.Contains(preview.BlockedReasons, reason =>
            reason.Contains("Typed evidence is inconsistent", StringComparison.Ordinal));

        var destination = workspace.File("not-created");
        await Assert.ThrowsAsync<ExportBlockedException>(
            () => exporter.ExportAsync(path, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Preview_file_sizes_match_the_exported_package()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var exporter = new IncidentExporter();
        var preview = exporter.Preview(session);
        var result = await exporter.ExportAsync(session, workspace.Subdirectory("packages"), CancellationToken.None);
        var entries = PackageReader.Read(result.PackagePath);

        Assert.Equal(entries.Count, preview.Files.Count);
        foreach (var file in preview.Files)
        {
            Assert.True(entries.ContainsKey(file.Name), file.Name);
            Assert.Equal(entries[file.Name].LongLength, file.Bytes);
        }
    }

    [Fact]
    public async Task Export_writes_the_documented_layout_with_verifiable_checksums()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var destination = workspace.Subdirectory("packages");

        var result = await new IncidentExporter().ExportAsync(session, destination, CancellationToken.None);

        Assert.True(File.Exists(result.PackagePath));
        Assert.Equal(new FileInfo(result.PackagePath).Length, result.Bytes);
        Assert.Matches(@"^incident-[0-9a-f]{24}\.zip$", Path.GetFileName(result.PackagePath));

        var entries = PackageReader.Read(result.PackagePath);
        Assert.Equal(ExpectedFiles.Order(StringComparer.Ordinal),
            entries.Keys.Order(StringComparer.Ordinal));

        var manifest = PackageReader.Json(PackageReader.Text(entries["manifest.json"])).AsObject();
        Assert.Equal(EventEnvelope.SupportedSchemaVersion, manifest["schema_version"]!.GetValue<string>());
        Assert.Equal("netscope-incident-package", manifest["package_format"]!.GetValue<string>());
        Assert.Equal("1", manifest["package_format_version"]!.GetValue<string>());
        Assert.True(manifest["synthetic"]!.GetValue<bool>());
        Assert.Equal(5, manifest["counts"]!["events"]!.GetValue<int>());
        Assert.Equal(1, manifest["counts"]!["snapshots"]!.GetValue<int>());
        Assert.Equal(1, manifest["counts"]!["diagnoses"]!.GetValue<int>());
        Assert.Equal(0, manifest["counts"]!["gaps"]!.GetValue<int>());
        Assert.NotNull(manifest["time_range"]!["started_at"]);
        Assert.NotNull(manifest["time_range"]!["ended_at"]);
        Assert.False(manifest["redaction"]!["salt_included"]!.GetValue<bool>());
        Assert.Null(manifest["salt"]); // The salt itself is never a manifest member.
        Assert.DoesNotContain("\"salt\":", PackageReader.Text(entries["manifest.json"]), StringComparison.Ordinal);
        var report = PackageReader.Json(PackageReader.Text(entries["redaction-report.json"])).AsObject();
        Assert.False(report["salt_included"]!.GetValue<bool>());
        Assert.Null(report["salt"]);

        var incidentId = Path.GetFileNameWithoutExtension(result.PackagePath)["incident-".Length..];
        Assert.Equal(incidentId, manifest["incident_id"]!.GetValue<string>());

        var declaredFiles = manifest["files"]!.AsArray()
            .Select(file => file!.AsObject())
            .ToDictionary(file => file["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal(5, declaredFiles.Count);
        Assert.DoesNotContain("manifest.json", declaredFiles.Keys);
        Assert.DoesNotContain("checksums.sha256", declaredFiles.Keys);
        foreach (var (name, file) in declaredFiles)
        {
            Assert.True(entries.ContainsKey(name), name);
            Assert.Equal(entries[name].LongLength, file["bytes"]!.GetValue<long>());
            Assert.Equal(Sha256(entries[name]), file["sha256"]!.GetValue<string>());
        }

        var checksumLines = PackageReader.Text(entries["checksums.sha256"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToArray();
        Assert.Equal(6, checksumLines.Length); // manifest + five payload files
        foreach (var pair in checksumLines)
        {
            Assert.Equal(2, pair.Length);
            Assert.True(entries.ContainsKey(pair[1]), pair[1]);
            Assert.Equal(Sha256(entries[pair[1]]), pair[0]);
        }

        Assert.Contains(checksumLines, pair => pair[1] == "manifest.json");
    }

    [Fact]
    public async Task Export_removes_every_synthetic_secret_from_every_package_file()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var result = await new IncidentExporter().ExportAsync(session,
            workspace.Subdirectory("packages"), CancellationToken.None);

        var entries = PackageReader.Read(result.PackagePath);
        Assert.NotEmpty(entries);

        foreach (var (name, bytes) in entries)
        {
            var strings = PackageReader.StringsIn(bytes, name);
            var raw = PackageReader.Text(bytes);
            foreach (var secret in ExportSession.Secrets.Where(secret => secret.Length > 3))
            {
                Assert.All(strings, value =>
                    Assert.DoesNotContain(secret, value, StringComparison.Ordinal));
                Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            }
        }

        // The package must not retain an original private address or local path either.
        var allStrings = entries.SelectMany(item => PackageReader.StringsIn(item.Value, item.Key)).ToArray();
        foreach (var original in new[] { "192.168.1.10", "192.168.1.1", "192.168.1.5", "10.0.0.7", @"C:\Users\alice" })
        {
            Assert.DoesNotContain(allStrings, value => value.Contains(original, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Exported_events_still_replay_cleanly_and_keep_aliases_and_evidence_links()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var result = await new IncidentExporter().ExportAsync(session,
            workspace.Subdirectory("packages"), CancellationToken.None);
        var entries = PackageReader.Read(result.PackagePath);

        var exportedEvents = workspace.File("exported.ndjson");
        await File.WriteAllBytesAsync(exportedEvents, entries["events.ndjson"]);
        var replay = new ReplayService().Replay(exportedEvents);

        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.NotNull(replay.Evidence);
        Assert.Single(replay.Diagnoses);
        Assert.Equal(5, replay.Log.Events.Count);

        var eventIds = replay.Log.Events.Select(envelope => envelope.EventId).ToHashSet(StringComparer.Ordinal);
        var definedEvidenceIds = replay.Log.Events
            .Where(envelope => envelope.Refs.EvidenceId is not null)
            .Select(envelope => envelope.Refs.EvidenceId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var recordedDiagnosis in replay.Diagnoses)
        {
            Assert.All(recordedDiagnosis.EvidenceIds.Concat(recordedDiagnosis.CounterEvidenceIds),
                id => Assert.Contains(id, definedEvidenceIds));
        }

        foreach (var envelope in replay.Log.Events)
        {
            if (envelope.Payload["evidence_metadata"]?["source_event_ids"] is not JsonArray sources) continue;
            foreach (var source in sources)
            {
                Assert.Contains(source!.GetValue<string>(), eventIds);
            }
        }

        var record = replay.Evidence!.Records.Single();
        Assert.Equal(replay.Evidence.Snapshot!.SnapshotId, record.Facts["snapshot_id"]);
        Assert.Equal(record.Facts["user_name"], record.Facts["account_name"]);
        Assert.StartsWith("user-", record.Facts["user_name"], StringComparison.Ordinal);
        Assert.Equal(record.Facts["computer_name"], record.Facts["host_name"]);
        Assert.StartsWith("device-", record.Facts["computer_name"], StringComparison.Ordinal);
        Assert.DoesNotContain("alice", record.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_", record.Summary, StringComparison.Ordinal);
        var pathAlias = System.Text.RegularExpressions.Regex.Match(record.Summary, @"path-[A-Z]+").Value;
        Assert.False(string.IsNullOrEmpty(pathAlias));
        Assert.Equal("[redacted:password]", record.Facts["password"]);
        Assert.Equal("[redacted:token]", record.Facts["api_key"]);

        var diagnosis = Assert.Single(replay.Diagnoses);
        Assert.Contains("target-A", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains(pathAlias, diagnosis.Summary, StringComparison.Ordinal);
        Assert.Matches(@"private-host-[A-Z]+", diagnosis.SuggestedNextStep);
        Assert.DoesNotContain("api.example.com", diagnosis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("sub.example.invalid", diagnosis.SuggestedNextStep, StringComparison.Ordinal);

        var suffix = replay.Evidence.Snapshot.Dns.Value!.SuffixSearchList;
        Assert.Contains(suffix, value => value.StartsWith("private-host-", StringComparison.Ordinal));
        Assert.DoesNotContain("corp.internal", suffix);

        // Referential integrity for addresses: the same original value in two probes still
        // resolves to one alias, and the fake-IP class survives.
        var resolved = replay.Evidence.Probes.Select(probe => probe.ResolvedAddresses).ToArray();
        Assert.Equal(resolved[0], resolved[1]);
        Assert.Contains(resolved[0], address => address.StartsWith("198.18.", StringComparison.Ordinal));
        Assert.Contains(resolved[0], address => address.StartsWith("10.", StringComparison.Ordinal));

        var routes = replay.Evidence.Snapshot.Routes.Value!;
        Assert.Contains(routes, route => route.DestinationPrefix == "198.18.0.0/15");
        Assert.Contains(routes, route => route.DestinationPrefix.StartsWith("10.", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.DestinationPrefix.StartsWith("192.168.", StringComparison.Ordinal));
        Assert.Contains(replay.Evidence.Snapshot.Dns.Value!.ServersByInterface[12],
            server => server.StartsWith("198.18.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_exports_share_structure_and_non_alias_content_but_not_salt_or_incident_id()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var exporter = new IncidentExporter();
        var first = await exporter.ExportAsync(session, workspace.Subdirectory("first"), CancellationToken.None);
        var second = await exporter.ExportAsync(session, workspace.Subdirectory("second"), CancellationToken.None);

        Assert.NotEqual(Path.GetFileName(first.PackagePath), Path.GetFileName(second.PackagePath));

        var firstEntries = PackageReader.Read(first.PackagePath);
        var secondEntries = PackageReader.Read(second.PackagePath);
        Assert.Equal(firstEntries.Keys.Order(StringComparer.Ordinal), secondEntries.Keys.Order(StringComparer.Ordinal));

        // The salt keys aliases and class-preserving addresses, so those values deliberately
        // differ between packages. Everything else must be identical.
        foreach (var name in new[] { "events.ndjson", "snapshots/snapshot-001.json", "diagnoses.json" })
        {
            Assert.Equal(NormalizeAliases(PackageReader.Text(firstEntries[name])),
                NormalizeAliases(PackageReader.Text(secondEntries[name])));
        }

        foreach (var name in new[] { "timeline.json", "redaction-report.json" })
        {
            Assert.Equal(firstEntries[name], secondEntries[name]);
        }

        var firstManifest = PackageReader.Json(PackageReader.Text(firstEntries["manifest.json"])).AsObject();
        var secondManifest = PackageReader.Json(PackageReader.Text(secondEntries["manifest.json"])).AsObject();
        var firstId = firstManifest["incident_id"]!.GetValue<string>();
        var secondId = secondManifest["incident_id"]!.GetValue<string>();
        Assert.NotEqual(firstId, secondId);

        var firstSizes = firstManifest["files"]!.AsArray()
            .Select(file => (file!["name"]!.GetValue<string>(), file["bytes"]!.GetValue<long>()))
            .ToArray();
        var secondSizes = secondManifest["files"]!.AsArray()
            .Select(file => (file!["name"]!.GetValue<string>(), file["bytes"]!.GetValue<long>()))
            .ToArray();
        Assert.Equal(firstSizes, secondSizes); // fixed-width aliases keep the manifest stable

        firstManifest.Remove("incident_id");
        secondManifest.Remove("incident_id");
        firstManifest.Remove("files");
        secondManifest.Remove("files");
        Assert.Equal(firstManifest.ToJsonString(), secondManifest.ToJsonString());

        // Checksums necessarily differ with the manifest and salt-keyed payloads, but each
        // package still verifies internally.
        Assert.NotEqual(Sha256(firstEntries["events.ndjson"]), Sha256(secondEntries["events.ndjson"]));
        Assert.NotEqual(
            PackageReader.Text(firstEntries["checksums.sha256"]),
            PackageReader.Text(secondEntries["checksums.sha256"]));
    }

    private static string NormalizeAliases(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(user|device|path|private-host|target)-[A-Z]{2,}", "$1-<alias>");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,3})?\b", "<address>");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\b(?:[0-9a-f]{4}:){7}[0-9a-f]{4}(?:/\d{1,3})?\b", "<address>");
        return text;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
