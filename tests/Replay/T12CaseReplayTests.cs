using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage;
using NetScope.Storage.Export;

namespace NetScope.Replay.Tests;

/// <summary>
/// T12 case replay: untrusted-package admission, checksum/manifest integrity, per-scope replay,
/// fixed-rule-set recomputation kept separate from recorded diagnoses, incident windows and old
/// package gaps. Every test uses local bytes only; no collector, probe, broker or network is used.
/// </summary>
public sealed class T12CaseReplayTests
{
    private static CaseReplayService Service(CaseReplayLimits? limits = null) =>
        new(new T12RuleSetEvaluator(), limits);

    [Fact]
    public void Exported_package_is_admitted_and_recorded_is_kept_separate_from_recomputation()
    {
        using var workspace = new TempWorkspace();
        var scenario = T12CaseReplayFixture.TwoCycleScenario();
        var package = T12CaseReplayFixture.WritePackage(workspace, scenario);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.True(result.Admission.Accepted);
        Assert.NotNull(result.Package);
        Assert.Equal("netscope-incident-package", result.Package!.PackageFormat);
        Assert.Equal("1", result.Package.PackageFormatVersion);
        Assert.Equal(2, result.SnapshotScopes.Count);
        Assert.Equal(scenario.Replay.Diagnoses.Count, result.RecordedDiagnoses.Count);
        Assert.NotEmpty(result.RecordedDiagnoses);
        Assert.NotEmpty(result.RecomputedDiagnoses);
        Assert.NotEmpty(result.Differences);
        Assert.All(result.RecordedDiagnoses, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.ScopeSnapshotEventId));
            Assert.False(string.IsNullOrWhiteSpace(item.Diagnosis.RuleId));
        });
        Assert.All(result.RecomputedDiagnoses, item =>
        {
            Assert.Equal(item.Diagnosis.RuleId, item.Diagnosis.RuleId);
            Assert.False(string.IsNullOrWhiteSpace(item.ScopeSnapshotEventId));
        });
        Assert.Contains(result.RecordedDiagnoses, item => item.RuleSupported && item.VersionMatchesCurrent);
        Assert.Contains(result.Differences, item => item.Disposition == CaseDiagnosisDisposition.Match);
        Assert.All(result.RecordedDiagnoses, item => Assert.Contains(result.SnapshotScopes,
            scope => scope.SnapshotEventId == item.ScopeSnapshotEventId));
    }

    [Fact]
    public async Task Real_exporter_package_is_accepted_through_the_same_admission_path()
    {
        using var workspace = new TempWorkspace();
        var scenario = T12CaseReplayFixture.TwoCycleScenario();
        var sessionPath = workspace.File("session-t12.ndjson");
        await File.WriteAllTextAsync(sessionPath, scenario.EventsText, new UTF8Encoding(false));
        var exporter = new IncidentExporter();
        var exported = await exporter.ExportAsync(sessionPath, workspace.File("packages"), CancellationToken.None);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(exported.PackagePath));

        var result = Service().Replay(exported.PackagePath);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.True(result.Admission.Accepted);
        Assert.Equal(2, result.SnapshotScopes.Count);
        Assert.NotEmpty(result.RecordedDiagnoses);
        Assert.NotEmpty(result.RecomputedDiagnoses);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "manifest_count_mismatch");
        var after = SHA256.HashData(await File.ReadAllBytesAsync(exported.PackagePath));
        Assert.Equal(before, after);
    }

    [Fact]
    public void Replaying_the_same_package_twice_is_deterministic_and_does_not_write_files()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario());
        var beforeHash = SHA256.HashData(File.ReadAllBytes(package));
        var beforeFiles = Directory.GetFiles(workspace.Root).Order(StringComparer.Ordinal).ToArray();

        var first = Service().Replay(package);
        var second = Service().Replay(package);

        Assert.Equal(first.DeterministicFingerprint, second.DeterministicFingerprint);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.SnapshotScopes.Select(scope => scope.SnapshotId), second.SnapshotScopes.Select(scope => scope.SnapshotId));
        Assert.Equal(first.RecordedDiagnoses.Select(item => item.Diagnosis.DiagnosisId),
            second.RecordedDiagnoses.Select(item => item.Diagnosis.DiagnosisId));
        Assert.Equal(first.RecomputedDiagnoses.Select(item => item.Diagnosis.RuleId),
            second.RecomputedDiagnoses.Select(item => item.Diagnosis.RuleId));
        Assert.Equal(beforeHash, SHA256.HashData(File.ReadAllBytes(package)));
        Assert.Equal(beforeFiles, Directory.GetFiles(workspace.Root).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Incident_window_reports_cross_cycle_snapshot_evidence()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario());

        var result = Service().Replay(package);

        var incident = Assert.Single(result.Incidents);
        Assert.Equal("incident-t12-1", incident.IncidentId);
        Assert.Contains("closed", incident.States);
        Assert.True(incident.SpansMultipleSnapshotCycles);
        Assert.Equal(2, incident.SnapshotScopeEventIds.Count);
        Assert.True(incident.PreWindowAvailable);
        Assert.True(incident.PostWindowComplete);
        Assert.Empty(incident.MissingRanges);
        Assert.Contains("recovery_verified_no_new_failure", incident.ClosureReason);
    }

    [Fact]
    public void Interface_drift_is_replayed_per_scope_without_rewriting_the_old_index()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario());

        var result = Service().Replay(package);

        Assert.Equal(2, result.SnapshotScopes.Count);
        var first = Assert.Single(result.SnapshotScopes[0].Interfaces);
        var second = Assert.Single(result.SnapshotScopes[1].Interfaces);
        Assert.Equal("guid-t12", first.InterfaceGuid);
        Assert.Equal("guid-t12", second.InterfaceGuid);
        Assert.Equal(12, first.IfIndex);
        Assert.Equal(44, second.IfIndex);
    }

    [Fact]
    public void Local_only_package_explains_missing_probes_and_client_evidence_without_a_root_cause()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.LocalOnlyScenario());

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.False(result.Package!.HasProbeResults);
        Assert.False(result.Package.HasClientEvidence);
        Assert.Contains(result.Notices, notice => notice.StartsWith("no_probe_results", StringComparison.Ordinal));
        Assert.Contains(result.Notices, notice => notice.StartsWith("no_client_evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(result.RecomputedDiagnoses, item =>
            item.Diagnosis.Level is EvidenceGrade.Supported or EvidenceGrade.Verified);
        Assert.Contains(result.Notices, notice => notice.StartsWith("recomputed_with_fixed_rule_set", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_untyped_package_does_not_invent_diagnoses()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.UntypedScenario());

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.False(result.Package!.HasTypedEvidence);
        Assert.Empty(result.RecomputedDiagnoses);
        Assert.Empty(result.RecordedDiagnoses);
        Assert.Empty(result.SnapshotScopes);
        Assert.Contains(result.Notices, notice => notice.StartsWith("legacy_no_typed_evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Recorded_rule_version_mismatch_is_unsupported_and_never_compared_as_current()
    {
        using var workspace = new TempWorkspace();
        var baseScenario = T12CaseReplayFixture.TwoCycleScenario();
        var legacyText = baseScenario.EventsText.Replace("\"rule_version\":\"1.1.0\"",
            "\"rule_version\":\"0.0.0-legacy\"", StringComparison.Ordinal);
        var scenario = WithEvents(baseScenario, legacyText);
        var package = T12CaseReplayFixture.WritePackage(workspace, scenario);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.All(result.RecordedDiagnoses, item => Assert.False(item.VersionMatchesCurrent));
        Assert.Contains(result.Issues, issue => issue.Code == "recorded_rule_version_mismatch");
        Assert.All(result.Differences, difference => Assert.Equal(CaseDiagnosisDisposition.Unsupported, difference.Disposition));
        Assert.NotEmpty(result.RecomputedDiagnoses);
    }

    [Fact]
    public void Unsupported_package_format_version_is_marked_unsupported_and_not_recomputed()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario(),
            manifestMutate: manifest => manifest["package_format_version"] = "2");

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Unsupported, result.Status);
        Assert.True(result.Admission.Accepted);
        Assert.Empty(result.RecomputedDiagnoses);
        Assert.Contains(result.Issues, issue => issue.Code == "unsupported_package_format_version");
        Assert.NotEmpty(result.RecordedDiagnoses);
        Assert.Contains(result.Notices, notice => notice.StartsWith("unsupported_package_version", StringComparison.Ordinal));
    }

    [Fact]
    public void Deleted_event_is_rejected_as_a_damaged_reference_instead_of_silently_filling_it()
    {
        using var workspace = new TempWorkspace();
        var baseScenario = T12CaseReplayFixture.TwoCycleScenario();
        var lines = baseScenario.EventsText.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.RemoveAt(lines.Count / 2);
        var corrupted = WithEvents(baseScenario, string.Join("\n", lines) + "\n");
        var package = T12CaseReplayFixture.WritePackage(workspace, corrupted);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code is "sequence_gap" or "damaged_evidence_reference"
            or "manifest_count_mismatch");
        Assert.Empty(result.RecomputedDiagnoses);
        Assert.Empty(result.RecordedDiagnoses);
    }

    [Fact]
    public void Duplicate_event_and_sequence_gap_are_rejected_instead_of_silently_accepted()
    {
        using var workspace = new TempWorkspace();
        var baseScenario = T12CaseReplayFixture.TwoCycleScenario();
        var firstLine = baseScenario.EventsText.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        var duplicated = WithEvents(baseScenario, baseScenario.EventsText + firstLine + "\n");
        var package = T12CaseReplayFixture.WritePackage(workspace, duplicated);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code is "duplicate_event_ids" or "sequence_gap");
        Assert.Empty(result.RecomputedDiagnoses);
    }

    [Fact]
    public void Manifest_count_mismatch_is_rejected()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario(),
            manifestMutate: manifest => ((JsonObject)manifest["counts"]!)["events"] =
                ((JsonObject)manifest["counts"]!)["events"]!.GetValue<int>() + 1);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "manifest_count_mismatch");
        Assert.Empty(result.RecomputedDiagnoses);
    }

    [Fact]
    public void Checksum_tampering_is_rejected()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario(),
            tamperAfterChecksums: files =>
                files["events.ndjson"] = files["events.ndjson"].Concat(new byte[] { 0x20 }).ToArray());

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "checksum_mismatch");
        Assert.Empty(result.RecomputedDiagnoses);
    }

    [Fact]
    public void Missing_events_entry_is_rejected_before_analysis()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("missing-events.zip");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            writer.Write("{}");
        }

        var result = Service().Replay(path);

        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "missing_required_file");
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("parent")]
    [InlineData("drive")]
    [InlineData("symlink")]
    public void Malicious_entry_names_and_links_are_rejected(string attack)
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario(),
            zipMutate: archive =>
            {
                switch (attack)
                {
                    case "absolute":
                        archive.CreateEntry("/absolute/evil.txt");
                        break;
                    case "parent":
                        archive.CreateEntry("../evil.txt");
                        break;
                    case "drive":
                        archive.CreateEntry("C:\\evil.txt");
                        break;
                    case "symlink":
                        var link = archive.CreateEntry("link-to-host");
                        link.ExternalAttributes = unchecked((int)(0xA000u << 16));
                        break;
                }
            });

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Rejected, result.Status);
        var expected = attack switch
        {
            "absolute" => "absolute_path",
            "parent" => "parent_traversal",
            "drive" => "drive_letter_path",
            _ => "symbolic_link_entry",
        };
        Assert.Contains(result.Issues, issue => issue.Code == expected);
        Assert.Empty(result.RecomputedDiagnoses);
    }

    [Fact]
    public void Admission_limits_are_enforced_for_entries_total_size_depth_and_entry_size()
    {
        using var workspace = new TempWorkspace();
        var package = T12CaseReplayFixture.WritePackage(workspace, T12CaseReplayFixture.TwoCycleScenario());

        var tooMany = Service(new CaseReplayLimits { MaxEntries = 2 }).Replay(package);
        Assert.Equal(CaseReplayStatus.Rejected, tooMany.Status);
        Assert.Contains(tooMany.Issues, issue => issue.Code == "too_many_entries");

        var tooLarge = Service(new CaseReplayLimits { MaxTotalUncompressedBytes = 4 }).Replay(package);
        Assert.Equal(CaseReplayStatus.Rejected, tooLarge.Status);
        Assert.Contains(tooLarge.Issues, issue => issue.Code is "total_too_large" or "entry_too_large");

        var tooDeepPackage = T12CaseReplayFixture.WritePackage(workspace,
            T12CaseReplayFixture.TwoCycleScenario(), "deep.zip",
            zipMutate: archive => archive.CreateEntry("a/b.txt"));
        var tooDeep = Service(new CaseReplayLimits { MaxPathDepth = 1 }).Replay(tooDeepPackage);
        Assert.Equal(CaseReplayStatus.Rejected, tooDeep.Status);
        Assert.Contains(tooDeep.Issues, issue => issue.Code == "path_too_deep");

        var entryTooLarge = Service(new CaseReplayLimits { MaxEntryUncompressedBytes = 8 }).Replay(package);
        Assert.Equal(CaseReplayStatus.Rejected, entryTooLarge.Status);
        Assert.Contains(entryTooLarge.Issues, issue => issue.Code == "entry_too_large");
    }

    [Fact]
    public void Recorded_level_difference_is_reported_as_level_changed()
    {
        using var workspace = new TempWorkspace();
        var baseScenario = T12CaseReplayFixture.TwoCycleScenario();
        var changed = WithEvents(baseScenario,
            baseScenario.EventsText.Replace("\"level\":\"candidate\"", "\"level\":\"supported\"", StringComparison.Ordinal));
        var package = T12CaseReplayFixture.WritePackage(workspace, changed);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.Contains(result.Differences, difference => difference.Disposition == CaseDiagnosisDisposition.LevelChanged
            && difference.RecordedLevel == EvidenceGrade.Supported
            && difference.RecomputedLevel == EvidenceGrade.Candidate);
    }

    [Fact]
    public void Recorded_missing_evidence_difference_is_reported_separately()
    {
        using var workspace = new TempWorkspace();
        var baseScenario = T12CaseReplayFixture.TwoCycleScenario();
        var matches = System.Text.RegularExpressions.Regex.Matches(baseScenario.EventsText,
            "\"missing_evidence\":\\[(?<values>[^\\]]*)\\]");
        var match = matches.Cast<System.Text.RegularExpressions.Match>()
            .First(item => item.Groups["values"].Value.Length > 2);
        var text = baseScenario.EventsText[..match.Index] + "\"missing_evidence\":[]"
            + baseScenario.EventsText[(match.Index + match.Length)..];
        var changed = WithEvents(baseScenario, text);
        var package = T12CaseReplayFixture.WritePackage(workspace, changed);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.Contains(result.Differences, difference =>
            difference.Disposition == CaseDiagnosisDisposition.MissingEvidenceChanged);
    }

    [Fact]
    public void Recorded_evidence_reference_difference_is_reported_as_evidence_changed()
    {
        using var workspace = new TempWorkspace();
        var baseScenario = T12CaseReplayFixture.TwoCycleScenario();
        var firstDiagnosis = baseScenario.Replay.Diagnoses[0];
        var scope = baseScenario.Replay.SnapshotScopes[0];
        var candidate = scope.Evidence.Probes.Select(probe => probe.ProbeId)
            .Concat(scope.Evidence.Records.Select(record => record.EvidenceId))
            .First(id => !firstDiagnosis.EvidenceIds.Contains(id, StringComparer.Ordinal));
        var match = System.Text.RegularExpressions.Regex.Match(baseScenario.EventsText,
            "\"evidence_ids\":\\[[^\\]]*\\]");
        Assert.True(match.Success);
        var replacement = "\"evidence_ids\":[\"" + candidate + "\"]";
        var text = baseScenario.EventsText[..match.Index] + replacement
            + baseScenario.EventsText[(match.Index + match.Length)..];
        var changed = WithEvents(baseScenario, text);
        var package = T12CaseReplayFixture.WritePackage(workspace, changed);

        var result = Service().Replay(package);

        Assert.Equal(CaseReplayStatus.Ok, result.Status);
        Assert.Contains(result.Differences, difference =>
            difference.Disposition == CaseDiagnosisDisposition.EvidenceChanged);
    }

    [Fact]
    public void Case_replay_code_has_no_collector_probe_broker_or_adapter_dependency()
    {
        var forbidden = new[] { "NetScope.Probes", "NetScope.Collectors.Windows", "NetScope.Adapters", "NetScope.Broker" };
        var application = typeof(ICaseReplayService).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        var storage = typeof(CaseReplayService).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, application);
            Assert.DoesNotContain(name, storage);
        }

        // The shared Application project now also carries pure repair-planning types (T13), so the
        // assembly graph may cite NetScope.Repair. The replay read chain itself must not: assert
        // that no replay signature/field/return type belongs to a real collector, probe, broker,
        // adapter or repair namespace.
        var forbiddenNamespaces = forbidden.Append("NetScope.Repair").ToArray();
        foreach (var type in new[]
                 {
                     typeof(CaseReplayService), typeof(ICaseReplayService), typeof(ICaseRuleEvaluator),
                     typeof(CaseReplayResult), typeof(CaseReplayLimits),
                 })
        {
            foreach (var signatureType in SignatureTypes(type))
            {
                Assert.DoesNotContain(forbiddenNamespaces,
                    ns => (signatureType.Namespace ?? "").StartsWith(ns, StringComparison.Ordinal));
            }
        }

        var replayMethod = typeof(ICaseReplayService).GetMethod(nameof(ICaseReplayService.Replay));
        Assert.NotNull(replayMethod);
        Assert.Equal(typeof(string), replayMethod!.GetParameters().Single().ParameterType);
        Assert.Equal(typeof(CaseReplayResult), replayMethod.ReturnType);
    }

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(Flags)) yield return Unwrap(field.FieldType);
        foreach (var property in type.GetProperties(Flags)) yield return Unwrap(property.PropertyType);
        foreach (var method in type.GetMethods(Flags))
        {
            yield return Unwrap(method.ReturnType);
            foreach (var parameter in method.GetParameters()) yield return Unwrap(parameter.ParameterType);
        }

        foreach (var constructor in type.GetConstructors(Flags))
        {
            foreach (var parameter in constructor.GetParameters()) yield return Unwrap(parameter.ParameterType);
        }
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsArray) return Unwrap(type.GetElementType()!);
        if (!type.IsGenericType) return type;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Nullable<>) ? Unwrap(type.GetGenericArguments()[0]) : definition;
    }

    private static T12Scenario WithEvents(T12Scenario scenario, string eventsText)
    {
        var replay = new ReplayService().ReplayText(eventsText);
        return scenario with { EventsText = eventsText, Replay = replay };
    }
}
