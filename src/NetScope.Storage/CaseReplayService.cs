using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Storage.Export;

namespace NetScope.Storage;

/// <summary>
/// Offline case replay workbench. Input is an untrusted incident package: admission uses the
/// frozen design-04 limits and path/link checks, content is verified against its checksums and
/// manifest before any typed event is decoded, and rule evaluation is bound to the caller-supplied
/// fixed rule set. This class only reads local bytes; it has no network client, no collector/probe/
/// broker reference and no system-write path.
/// </summary>
public sealed class CaseReplayService : ICaseReplayService
{
    public const string RequiredPackageFormat = "netscope-incident-package";
    public const string SupportedPackageFormatVersion = "1";

    private const string ManifestName = "manifest.json";
    private const string EventsName = "events.ndjson";
    private const string ChecksumsName = "checksums.sha256";
    private const int MaxIssueDetail = 12;

    private readonly ICaseRuleEvaluator _evaluator;
    private readonly CaseReplayLimits _limits;

    public CaseReplayService(ICaseRuleEvaluator evaluator, CaseReplayLimits? limits = null)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _limits = limits ?? CaseReplayLimits.Default;
    }

    public CaseReplayResult Replay(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) throw new ArgumentException("A package path is required.", nameof(packagePath));

        var admissionIssues = new List<CaseReplayIssue>();
        var admission = Admit(packagePath, admissionIssues);
        if (!admission.Accepted)
        {
            return BuildUnanalysed(CaseReplayStatus.Rejected, admission, admissionIssues, null, null,
                new[] { "准入失败：包未被解析，也没有使用当前规则做任何推断。" });
        }

        PackageContent? content = null;
        var contentIssues = new List<CaseReplayIssue>();
        try
        {
            content = ReadAndVerify(packagePath, contentIssues, out var manifestRead);
            if (content is null)
            {
                return BuildUnanalysed(CaseReplayStatus.Rejected, admission, contentIssues, manifestRead, null,
                    new[] { "包完整性校验失败：没有分析结论。" });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or JsonException or NotSupportedException)
        {
            contentIssues.Add(Blocking("unreadable_package", "The package could not be read as an incident package."));
            return BuildUnanalysed(CaseReplayStatus.Rejected, admission, contentIssues, null, null,
                new[] { "包读取失败（" + exception.GetType().Name + "）；没有分析结论。" });
        }

        var manifest = content.Manifest;
        PackageInfoDraft packageDraft = ReadPackageInfo(manifest, out var formatVersion, out var schemaVersion,
            out var packageFormat);
        if (!string.Equals(packageFormat, RequiredPackageFormat, StringComparison.Ordinal))
        {
            contentIssues.Add(Blocking("unrecognized_package", "The archive is not a NetScope incident package."));
            return BuildUnanalysed(CaseReplayStatus.Rejected, admission, contentIssues, packageDraft.Info, null,
                new[] { "这不是本产品的故障包；不导入、不推断。" });
        }

        var replay = new ReplayService().ReplayText(content.EventsText);
        var integrityIssues = new List<CaseReplayIssue>();
        AddReplayProblems(replay, integrityIssues);
        AddManifestCountMismatches(manifest, replay, integrityIssues, contentIssues);
        if (integrityIssues.Count > 0)
        {
            return BuildUnanalysed(CaseReplayStatus.Rejected, admission, contentIssues.Concat(integrityIssues).ToArray(),
                ReadPackageInfoFor(packageDraft, replay), null,
                new[] { "包内事件关联/计数不一致：拒绝分析，不静默补造。" });
        }

        var unsupported = false;
        if (!string.Equals(schemaVersion, EventEnvelope.SupportedSchemaVersion, StringComparison.Ordinal))
        {
            unsupported = true;
            contentIssues.Add(Blocking("unsupported_schema_version",
                "The package schema_version is not supported by this build; only recorded facts are shown."));
        }

        if (!string.Equals(formatVersion, SupportedPackageFormatVersion, StringComparison.Ordinal))
        {
            unsupported = true;
            contentIssues.Add(Blocking("unsupported_package_format_version",
                "The package format version is not supported by this build; the current rules are not presented as the original."));
        }

        var notices = new List<string>();
        var packageInfo = BuildPackageInfo(packageDraft, replay);
        var recorded = BuildRecordedDiagnoses(replay, contentIssues, notices);
        var snapshotScopes = BuildSnapshotScopes(replay, recorded);
        var incidents = BuildIncidents(replay, contentIssues);
        var recomputed = new List<CaseRecomputedDiagnosis>();
        var differences = new List<CaseDiagnosisDifference>();
        if (unsupported)
        {
            notices.Add("unsupported_package_version: 该包的历史格式/规则版本不受当前构建支持，"
                + "只显示包内记录与准入事实；没有用当前规则冒充原版。");
        }
        else
        {
            EvaluateFixedRuleSet(replay, recomputed, contentIssues);
            differences.AddRange(Compare(replay, recorded, recomputed));
            AddOldPackageNotices(packageInfo, notices, recorded);
            notices.Add("recomputed_with_fixed_rule_set: 重算使用当前构建固定规则集 " + _evaluator.RuleSet.RuleSetVersion
                + "，与包内记录诊断分开显示；重算不代表原版规则行为。");
        }

        var status = unsupported ? CaseReplayStatus.Unsupported : CaseReplayStatus.Ok;
        var baseResult = new CaseReplayResult
        {
            Status = status,
            Admission = admission,
            RuleSet = _evaluator.RuleSet,
            Package = packageInfo,
            RecordedDiagnoses = recorded,
            RecomputedDiagnoses = recomputed,
            Differences = differences,
            SnapshotScopes = snapshotScopes,
            Incidents = incidents,
            Issues = contentIssues,
            Notices = notices,
            DeterministicFingerprint = string.Empty,
        };
        return baseResult with { DeterministicFingerprint = Fingerprint(baseResult) };
    }

    // ---------- admission and package integrity ----------

    private CaseAdmissionSummary Admit(string packagePath, List<CaseReplayIssue> issues)
    {
        PackageAdmissionResult result;
        try
        {
            result = UntrustedPackageValidator.Validate(packagePath, new PackageAdmissionLimits
            {
                MaxEntries = _limits.MaxEntries,
                MaxTotalUncompressedBytes = _limits.MaxTotalUncompressedBytes,
                MaxEntryUncompressedBytes = _limits.MaxEntryUncompressedBytes,
                MaxPathDepth = _limits.MaxPathDepth,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            issues.Add(Blocking("unreadable_package", "The package file could not be opened for admission."));
            return new CaseAdmissionSummary
            {
                Accepted = false,
                EntryCount = 0,
                TotalUncompressedBytes = 0,
                MaxObservedPathDepth = 0,
                Problems = issues.ToArray(),
            };
        }

        foreach (var problem in result.Problems)
        {
            issues.Add(new CaseReplayIssue
            {
                Code = problem.Code,
                Severity = CaseReplayIssueSeverity.Blocking,
                Message = problem.Message,
                EntryName = problem.EntryName,
            });
        }

        return new CaseAdmissionSummary
        {
            Accepted = result.IsAccepted,
            EntryCount = result.EntryCount,
            TotalUncompressedBytes = result.TotalUncompressedBytes,
            MaxObservedPathDepth = result.MaxObservedPathDepth,
            Problems = issues.ToArray(),
        };
    }

    private PackageContent? ReadAndVerify(string packagePath, List<CaseReplayIssue> issues,
        out CasePackageInfo? reachedPackage)
    {
        reachedPackage = null;
        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            entries[entry.FullName] = entry;
        }

        foreach (var required in new[] { ManifestName, EventsName, ChecksumsName })
        {
            if (!entries.ContainsKey(required))
            {
                issues.Add(Blocking("missing_required_file", $"The package is missing {required}.", required));
            }
        }

        if (issues.Count > 0) return null;

        var checksumsBytes = ReadEntry(entries[ChecksumsName], _limits.MaxEntryUncompressedBytes,
            ChecksumsName, issues);
        if (checksumsBytes is null) return null;
        var manifestBytes = ReadEntry(entries[ManifestName], _limits.MaxEntryUncompressedBytes, ManifestName, issues);
        if (manifestBytes is null) return null;
        var eventsBytes = ReadEntry(entries[EventsName], _limits.MaxEntryUncompressedBytes, EventsName, issues);
        if (eventsBytes is null) return null;

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalRead = 0;
        foreach (var (name, entry) in entries)
        {
            if (string.Equals(name, ChecksumsName, StringComparison.Ordinal)) continue;
            var hash = HashEntry(entry, name, issues, ref totalRead);
            if (hash is null) return null;
            hashes[name] = hash;
        }

        var checksums = ParseChecksums(checksumsBytes, issues);
        if (checksums is null || issues.Count > 0) return null;
        if (!VerifyChecksumSet(entries.Keys, checksums, issues)) return null;
        foreach (var (name, expected) in checksums)
        {
            if (!hashes.TryGetValue(name, out var actual))
            {
                issues.Add(Blocking("missing_package_file", "A checksummed package file is missing.", name));
                continue;
            }

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Blocking("checksum_mismatch", "A package file does not match checksums.sha256.", name));
            }
        }

        if (issues.Count > 0) return null;

        var manifest = ParseJson(manifestBytes, ManifestName, issues);
        if (manifest is null) return null;

        if (!VerifyManifestFiles(manifest, hashes, issues)) return null;
        if (issues.Count > 0) return null;

        var eventsText = DecodeUtf8(eventsBytes, EventsName, issues);
        if (eventsText is null) return null;

        reachedPackage = null;
        return new PackageContent(manifest, eventsText, entries.Count);
    }

    private sealed record PackageContent(JsonObject Manifest, string EventsText, int EntryCount);

    private byte[]? ReadEntry(ZipArchiveEntry entry, long maxBytes, string name, List<CaseReplayIssue> issues)
    {
        if (entry.Length < 0 || entry.Length > maxBytes)
        {
            issues.Add(Blocking("entry_too_large", "A required entry exceeds the admission limit.", name));
            return null;
        }

        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes)
            {
                issues.Add(Blocking("entry_too_large", "An actually-read entry exceeds the admission limit.", name));
                return null;
            }

            output.Write(buffer, 0, read);
        }

        if (total != entry.Length)
        {
            issues.Add(Blocking("entry_size_mismatch", "An entry's actual size differs from its declared size.", name));
            return null;
        }

        return output.ToArray();
    }

    private string? HashEntry(ZipArchiveEntry entry, string name, List<CaseReplayIssue> issues, ref long totalRead)
    {
        if (entry.Length < 0 || entry.Length > _limits.MaxEntryUncompressedBytes)
        {
            issues.Add(Blocking("entry_too_large", "An entry exceeds the admission limit.", name));
            return null;
        }

        using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            total += read;
            if (total > entry.Length)
            {
                issues.Add(Blocking("entry_size_mismatch", "An entry's actual size exceeds its declared size.", name));
                return null;
            }

            totalRead += read;
            if (totalRead > _limits.MaxTotalUncompressedBytes)
            {
                issues.Add(Blocking("total_too_large", "The package exceeds the total unpacked-size limit.", name));
                return null;
            }

            hash.AppendData(buffer, 0, read);
        }

        if (total != entry.Length)
        {
            issues.Add(Blocking("entry_size_mismatch", "An entry's actual size differs from its declared size.", name));
            return null;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Dictionary<string, string>? ParseChecksums(byte[] bytes, List<CaseReplayIssue> issues)
    {
        var text = DecodeUtf8(bytes, ChecksumsName, issues);
        if (text is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 || line.Length <= separator + 2)
            {
                issues.Add(Blocking("malformed_checksums", "checksums.sha256 has a malformed line."));
                return null;
            }

            var sha = line[..separator];
            var name = line[(separator + 2)..];
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit) || name.Length == 0)
            {
                issues.Add(Blocking("malformed_checksums", "checksums.sha256 has a malformed line."));
                return null;
            }

            if (!result.TryAdd(name, sha))
            {
                issues.Add(Blocking("duplicate_checksum_entry", "checksums.sha256 lists a file more than once.", name));
                return null;
            }
        }

        return result;
    }

    private static bool VerifyChecksumSet(IEnumerable<string> archiveNames, IReadOnlyDictionary<string, string> checksums,
        List<CaseReplayIssue> issues)
    {
        var actual = archiveNames.Where(name => !string.Equals(name, ChecksumsName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in actual)
        {
            if (!checksums.ContainsKey(name))
            {
                issues.Add(Blocking("unlisted_package_file", "A package file is not covered by checksums.sha256.", name));
            }
        }

        foreach (var name in checksums.Keys)
        {
            if (!actual.Contains(name))
            {
                issues.Add(Blocking("missing_package_file", "checksums.sha256 lists a file that is not present.", name));
            }
        }

        return issues.Count == 0;
    }

    private static JsonObject? ParseJson(byte[] bytes, string name, List<CaseReplayIssue> issues)
    {
        var text = DecodeUtf8(bytes, name, issues);
        if (text is null) return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            issues.Add(Blocking("malformed_json", "A package JSON document is not valid JSON.", name));
            return null;
        }
    }

    private static string? DecodeUtf8(byte[] bytes, string name, List<CaseReplayIssue> issues)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            issues.Add(Blocking("invalid_utf8", "A package file is not valid UTF-8.", name));
            return null;
        }
    }

    private static bool VerifyManifestFiles(JsonObject manifest, IReadOnlyDictionary<string, string> hashes,
        List<CaseReplayIssue> issues)
    {
        if (manifest["files"] is not JsonArray files)
        {
            issues.Add(Blocking("malformed_manifest", "manifest.json has no files array."));
            return false;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in files)
        {
            if (node is not JsonObject file)
            {
                issues.Add(Blocking("malformed_manifest", "manifest.json has a malformed files entry."));
                return false;
            }

            var name = Text(file["name"]);
            var sha = Text(file["sha256"]);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sha))
            {
                issues.Add(Blocking("malformed_manifest", "manifest.json has a files entry without name/sha256."));
                return false;
            }

            if (!listed.Add(name!))
            {
                issues.Add(Blocking("duplicate_manifest_file", "manifest.json lists a file more than once.", name));
                return false;
            }

            if (!hashes.TryGetValue(name!, out var actual))
            {
                issues.Add(Blocking("missing_package_file", "manifest.json lists a file that is not present.", name));
                return false;
            }

            if (!string.Equals(sha, actual, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Blocking("manifest_hash_mismatch", "manifest.json hash disagrees with the actual file.", name));
                return false;
            }
        }

        return true;
    }

    // ---------- package facts, recorded diagnostics, scopes, incidents ----------

    private PackageInfoDraft ReadPackageInfo(JsonObject manifest, out string? formatVersion,
        out string? schemaVersion, out string? packageFormat)
    {
        packageFormat = Text(manifest["package_format"]);
        formatVersion = Text(manifest["package_format_version"]);
        schemaVersion = Text(manifest["schema_version"]);
        var versions = manifest["versions"] as JsonObject;
        var ruleVersions = StringArray(versions?["rule_versions"] ?? manifest["rules_versions"]);
        var payloadVersions = StringArray(versions?["evidence_payload_versions"]);
        var redaction = Text(versions?["redaction_policy_version"]) ?? Text((manifest["redaction"] as JsonObject)?["policy_version"]);
        var info = new CasePackageInfo
        {
            PackageFormat = packageFormat,
            PackageFormatVersion = formatVersion,
            IncidentId = Text(manifest["incident_id"]),
            AppVersion = Text(manifest["app_version"]) ?? Text(versions?["app_version"]),
            AppBuildCommit = Text(manifest["app_build_commit"]) ?? Text(versions?["app_build_commit"]),
            Synthetic = manifest["synthetic"]?.GetValue<bool>(),
            RecordedRuleVersions = ruleVersions,
            EvidencePayloadVersions = payloadVersions,
            RedactionPolicyVersion = redaction,
        };
        return new PackageInfoDraft(info);
    }

    private CasePackageInfo BuildPackageInfo(PackageInfoDraft draft, ReplayResult? replay)
    {
        if (replay is null) return draft.Info;
        var hasTyped = replay.Evidence is not null || replay.SnapshotScopes.Count > 0
            || replay.UnscopedEvidence is not null;
        var hasProbes = replay.Summary.ByType.TryGetValue(EventType.ProbeResult, out var probeCount) && probeCount > 0;
        var hasClient = replay.SnapshotScopes
            .SelectMany(scope => scope.Evidence.Records)
            .Concat(replay.UnscopedEvidence?.Records ?? Array.Empty<EvidenceRecord>())
            .Any(record => record.Kind is "client_process" or "client_config_file" or "port_ownership");
        return draft.Info with
        {
            EventCount = replay.Summary.EventCount,
            SnapshotCount = replay.SnapshotScopes.Count,
            RecordedDiagnosisCount = replay.Diagnoses.Count,
            HasTypedEvidence = hasTyped,
            HasProbeResults = hasProbes,
            HasClientEvidence = hasClient,
        };
    }

    private static CasePackageInfo ReadPackageInfoFor(PackageInfoDraft draft, ReplayResult replay) =>
        draft.Info with
        {
            EventCount = replay.Summary.EventCount,
            SnapshotCount = replay.SnapshotScopes.Count,
            RecordedDiagnosisCount = replay.Diagnoses.Count,
            HasTypedEvidence = replay.Evidence is not null || replay.SnapshotScopes.Count > 0
                || replay.UnscopedEvidence is not null,
            HasProbeResults = replay.Summary.ByType.TryGetValue(EventType.ProbeResult, out var probeCount)
                && probeCount > 0,
            HasClientEvidence = replay.SnapshotScopes
                .SelectMany(scope => scope.Evidence.Records)
                .Concat(replay.UnscopedEvidence?.Records ?? Array.Empty<EvidenceRecord>())
                .Any(record => record.Kind is "client_process" or "client_config_file" or "port_ownership"),
        };

    private List<CaseRecordedDiagnosis> BuildRecordedDiagnoses(ReplayResult replay,
        List<CaseReplayIssue> issues, List<string> notices)
    {
        var scopeByDiagnosis = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scope in replay.SnapshotScopes)
        {
            foreach (var diagnosis in scope.Diagnoses)
            {
                scopeByDiagnosis[diagnosis.DiagnosisId] = scope.SnapshotEventId;
            }
        }

        var current = _evaluator.RuleSet.Rules
            .GroupBy(rule => rule.RuleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Version, StringComparer.Ordinal);
        var result = new List<CaseRecordedDiagnosis>();
        foreach (var diagnosis in replay.Diagnoses.OrderBy(item => item.DiagnosisId, StringComparer.Ordinal))
        {
            var supported = current.TryGetValue(diagnosis.RuleId, out var currentVersion);
            var matches = supported && string.Equals(currentVersion, diagnosis.RuleVersion, StringComparison.Ordinal);
            result.Add(new CaseRecordedDiagnosis
            {
                ScopeSnapshotEventId = scopeByDiagnosis.GetValueOrDefault(diagnosis.DiagnosisId),
                Diagnosis = diagnosis,
                RuleSupported = supported,
                VersionMatchesCurrent = matches,
                CurrentRuleVersion = supported ? currentVersion : null,
            });
            if (!supported)
            {
                issues.Add(Warning("recorded_rule_unsupported",
                    $"包内诊断引用的规则 {diagnosis.RuleId} 不在当前固定规则集中；不按当前规则比较其等级。"));
            }
            else if (!matches)
            {
                issues.Add(Warning("recorded_rule_version_mismatch",
                    $"包内诊断 {diagnosis.RuleId} 记录版本 {diagnosis.RuleVersion} 与当前 {currentVersion} 不同；"
                    + "只显示记录，不用当前版本冒充原版。"));
            }
        }

        var unsupportedRules = result.Where(item => !item.RuleSupported || !item.VersionMatchesCurrent)
            .Select(item => item.Diagnosis.RuleId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unsupportedRules.Length > 0)
        {
            notices.Add("recorded_rule_compatibility: 包内有 " + unsupportedRules.Length
                + " 条记录诊断的规则/版本不受当前构建支持（" + string.Join("、", unsupportedRules)
                + "）；这些条目标 Unsupported，不参与等级差异比较。");
        }

        return result;
    }

    private void EvaluateFixedRuleSet(ReplayResult replay, List<CaseRecomputedDiagnosis> recomputed,
        List<CaseReplayIssue> issues)
    {
        foreach (var scope in replay.SnapshotScopes)
        {
            EvaluateOne(scope.Evidence, scope.SnapshotEventId, recomputed, issues);
        }

        if (replay.UnscopedEvidence is { } unscoped)
        {
            EvaluateOne(unscoped, null, recomputed, issues);
        }

        if (replay.SnapshotScopes.Count == 0 && replay.UnscopedEvidence is null)
        {
            issues.Add(Info("no_typed_evidence", "包内没有可重算的 typed evidence；未运行当前规则。"));
        }
    }

    private void EvaluateOne(EvidenceSet evidence, string? scopeEventId,
        List<CaseRecomputedDiagnosis> recomputed, List<CaseReplayIssue> issues)
    {
        DiagnosticEvaluationResult evaluation;
        try
        {
            evaluation = _evaluator.Evaluate(evidence);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            issues.Add(Warning("recomputed_rule_exception",
                "固定规则集在这一 scope 执行失败（" + exception.GetType().Name + "）；没有生成该 scope 的重算结论。"));
            return;
        }

        foreach (var failure in evaluation.Failures)
        {
            issues.Add(Warning("recomputed_rule_failure",
                $"规则 {failure.RuleId} {failure.RuleVersion} 执行失败（{failure.ErrorType}）；没有为它补造结论。"));
        }

        foreach (var diagnosis in evaluation.Diagnoses
                     .OrderBy(item => item.RuleId, StringComparer.Ordinal)
                     .ThenBy(item => item.DiagnosisId, StringComparer.Ordinal))
        {
            recomputed.Add(new CaseRecomputedDiagnosis
            {
                ScopeSnapshotEventId = scopeEventId,
                Diagnosis = diagnosis,
            });
        }
    }

    private List<CaseSnapshotReplay> BuildSnapshotScopes(ReplayResult replay,
        IReadOnlyList<CaseRecordedDiagnosis> recorded)
    {
        var result = new List<CaseSnapshotReplay>(replay.SnapshotScopes.Count);
        foreach (var scope in replay.SnapshotScopes)
        {
            var windows = scope.Evidence.Records
                .Select(record => record.ObservationWindowId)
                .Where(window => !string.IsNullOrWhiteSpace(window))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            result.Add(new CaseSnapshotReplay
            {
                SnapshotEventId = scope.SnapshotEventId,
                Sequence = scope.Sequence,
                SnapshotId = scope.Snapshot.SnapshotId,
                ObservationWindowId = windows.Length == 1 ? windows[0] : null,
                RecordCount = scope.Evidence.Records.Count,
                ProbeCount = scope.Evidence.Probes.Count,
                RecordedDiagnosisCount = recorded.Count(item =>
                    string.Equals(item.ScopeSnapshotEventId, scope.SnapshotEventId, StringComparison.Ordinal)),
                Interfaces = scope.Snapshot.Interfaces.Value?
                    .Select(item => new CaseInterfaceRef
                    {
                        InterfaceGuid = item.InterfaceGuid,
                        IfIndex = item.IfIndex,
                    })
                    .OrderBy(item => item.InterfaceGuid, StringComparer.Ordinal)
                    .ThenBy(item => item.IfIndex)
                    .ToArray() ?? Array.Empty<CaseInterfaceRef>(),
            });
        }

        return result;
    }

    private List<CaseIncidentReplay> BuildIncidents(ReplayResult replay, List<CaseReplayIssue> issues)
    {
        var builders = new Dictionary<string, IncidentDraft>(StringComparer.Ordinal);
        foreach (var envelope in replay.Log.Events)
        {
            if (envelope.EventType != EventType.IncidentMarker) continue;
            if (envelope.Payload["incident"] is not JsonObject incident) continue;
            var incidentId = Text(incident["incident_id"]);
            if (string.IsNullOrWhiteSpace(incidentId)) continue;
            if (!builders.TryGetValue(incidentId!, out var draft))
            {
                draft = new IncidentDraft(incidentId!);
                builders[incidentId!] = draft;
            }

            var state = Text(envelope.Payload["state"]);
            if (!string.IsNullOrWhiteSpace(state)) draft.States.Add(state!);
            draft.Trigger ??= Text(incident["trigger"]);
            draft.StartedAt ??= ParseTime(incident["started_at"]);
            draft.ClosedAt ??= ParseTime(incident["closed_at"]);
            draft.ClosureReason ??= Text(incident["closure_reason"]);
            if (incident["window"] is JsonObject window)
            {
                draft.WindowStart ??= ParseTime(window["started_at"]);
                draft.WindowEnd ??= ParseTime(window["ended_at"]) ?? ParseTime(window["post_window_end"]);
                draft.PreWindowAvailable ??= window["pre_window_available"]?.GetValue<bool>();
                draft.PostWindowComplete ??= window["post_window_complete"]?.GetValue<bool>();
                foreach (var reason in StringArray(window["missing_ranges"]))
                {
                    if (!draft.MissingRanges.Contains(reason, StringComparer.Ordinal)) draft.MissingRanges.Add(reason);
                }
            }

            foreach (var id in StringArray(incident["event_ids"]))
            {
                if (!draft.EventIds.Contains(id, StringComparer.Ordinal)) draft.EventIds.Add(id);
            }

            foreach (var id in StringArray(incident["snapshot_ids"]))
            {
                if (!draft.SnapshotIds.Contains(id, StringComparer.Ordinal)) draft.SnapshotIds.Add(id);
            }

            foreach (var id in StringArray(incident["diagnosis_ids"]))
            {
                if (!draft.DiagnosisIds.Contains(id, StringComparer.Ordinal)) draft.DiagnosisIds.Add(id);
            }
        }

        var networkSnapshotToScope = replay.SnapshotScopes
            .ToDictionary(scope => scope.Snapshot.SnapshotId, scope => scope.SnapshotEventId, StringComparer.Ordinal);
        var eventToSnapshot = replay.Log.Events
            .Where(envelope => envelope.Refs.SnapshotId is not null)
            .GroupBy(envelope => envelope.EventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Refs.SnapshotId!, StringComparer.Ordinal);
        var scopeSequence = replay.SnapshotScopes
            .ToDictionary(scope => scope.SnapshotEventId, scope => scope.Sequence, StringComparer.Ordinal);

        var result = new List<CaseIncidentReplay>();
        foreach (var draft in builders.Values)
        {
            var scopeIds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var snapshotId in draft.SnapshotIds)
            {
                if (networkSnapshotToScope.TryGetValue(snapshotId, out var scopeId)) scopeIds.Add(scopeId);
            }

            var referenced = draft.EventIds.Concat(draft.DiagnosisIds).ToArray();
            foreach (var eventId in referenced)
            {
                if (eventToSnapshot.TryGetValue(eventId, out var networkId)
                    && networkSnapshotToScope.TryGetValue(networkId, out var scopeId))
                {
                    scopeIds.Add(scopeId);
                }
            }

            if (referenced.Length > 0 && scopeIds.Count == 0 && replay.SnapshotScopes.Count > 0)
            {
                issues.Add(Warning("incident_reference_unresolved",
                    $"incident {draft.IncidentId} 引用的跨周期事件在当前包中没有可解析的 snapshot scope；只保留原引用。"));
            }

            var orderedScopes = scopeIds
                .OrderBy(id => scopeSequence.GetValueOrDefault(id, int.MaxValue))
                .ThenBy(id => id, StringComparer.Ordinal)
                .ToArray();
            result.Add(new CaseIncidentReplay
            {
                IncidentId = draft.IncidentId,
                Trigger = draft.Trigger,
                States = DistinctAdjacent(draft.States),
                StartedAt = draft.StartedAt,
                ClosedAt = draft.ClosedAt,
                WindowStart = draft.WindowStart,
                WindowEnd = draft.WindowEnd,
                PreWindowAvailable = draft.PreWindowAvailable,
                PostWindowComplete = draft.PostWindowComplete,
                MissingRanges = draft.MissingRanges.ToArray(),
                EventIds = draft.EventIds.ToArray(),
                SnapshotIds = draft.SnapshotIds.ToArray(),
                DiagnosisIds = draft.DiagnosisIds.ToArray(),
                SnapshotScopeEventIds = orderedScopes,
                SpansMultipleSnapshotCycles = orderedScopes.Length > 1,
                ClosureReason = draft.ClosureReason,
            });
        }

        return result
            .OrderBy(item => item.StartedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.IncidentId, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> DistinctAdjacent(IReadOnlyList<string> values)
    {
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (result.Count == 0 || !string.Equals(result[^1], value, StringComparison.Ordinal)) result.Add(value);
        }

        return result;
    }

    private List<CaseDiagnosisDifference> Compare(ReplayResult replay,
        IReadOnlyList<CaseRecordedDiagnosis> recorded, IReadOnlyList<CaseRecomputedDiagnosis> recomputed)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scope in replay.SnapshotScopes)
        {
            order[scope.SnapshotEventId] = scope.Sequence;
        }

        var recordedGroups = recorded
            .GroupBy(item => (item.ScopeSnapshotEventId, item.Diagnosis.RuleId))
            .ToDictionary(group => group.Key, group => group.ToList());
        var recomputedGroups = recomputed
            .GroupBy(item => (item.ScopeSnapshotEventId, item.Diagnosis.RuleId))
            .ToDictionary(group => group.Key, group => group.ToList());
        var keys = recordedGroups.Keys.Concat(recomputedGroups.Keys).Distinct()
            .OrderBy(key => key.ScopeSnapshotEventId is null ? -1 : order.GetValueOrDefault(key.ScopeSnapshotEventId, int.MaxValue))
            .ThenBy(key => key.RuleId, StringComparer.Ordinal)
            .ToArray();

        var result = new List<CaseDiagnosisDifference>();
        foreach (var key in keys)
        {
            recordedGroups.TryGetValue(key, out var recordedItems);
            recomputedGroups.TryGetValue(key, out var recomputedItems);
            recordedItems ??= new List<CaseRecordedDiagnosis>();
            recomputedItems ??= new List<CaseRecomputedDiagnosis>();
            var pairs = Math.Min(recordedItems.Count, recomputedItems.Count);
            for (var index = 0; index < pairs; index++)
            {
                var recordedItem = recordedItems[index];
                var recomputedItem = recomputedItems[index];
                result.Add(Difference(recordedItem, recomputedItem));
            }

            for (var index = pairs; index < recordedItems.Count; index++)
            {
                result.Add(new CaseDiagnosisDifference
                {
                    RuleId = key.RuleId,
                    ScopeSnapshotEventId = key.ScopeSnapshotEventId,
                    Disposition = recordedItems[index].RuleSupported && recordedItems[index].VersionMatchesCurrent
                        ? CaseDiagnosisDisposition.OnlyRecorded
                        : CaseDiagnosisDisposition.Unsupported,
                    RecordedRuleVersion = recordedItems[index].Diagnosis.RuleVersion,
                    RecordedLevel = recordedItems[index].Diagnosis.Level,
                    RecordedEvidenceIds = recordedItems[index].Diagnosis.EvidenceIds,
                    Notes = recordedItems[index].RuleSupported && recordedItems[index].VersionMatchesCurrent
                        ? new[] { "包内记录了这个结论，当前固定规则集没有重复产生；不代表原版行为。" }
                        : new[] { "记录诊断的规则/版本不受当前构建支持；标 Unsupported，不比较等级。" },
                });
            }

            for (var index = pairs; index < recomputedItems.Count; index++)
            {
                result.Add(new CaseDiagnosisDifference
                {
                    RuleId = key.RuleId,
                    ScopeSnapshotEventId = key.ScopeSnapshotEventId,
                    Disposition = CaseDiagnosisDisposition.OnlyRecomputed,
                    RecomputedRuleVersion = recomputedItems[index].Diagnosis.RuleVersion,
                    RecomputedLevel = recomputedItems[index].Diagnosis.Level,
                    RecomputedEvidenceIds = recomputedItems[index].Diagnosis.EvidenceIds,
                    Notes = new[] { "当前固定规则集产生了该结论，包内没有记录；这是现在的结果，不是原版结论。" },
                });
            }
        }

        return result;
    }

    private static CaseDiagnosisDifference Difference(CaseRecordedDiagnosis recorded, CaseRecomputedDiagnosis recomputed)
    {
        var recordedDiagnosis = recorded.Diagnosis;
        var recomputedDiagnosis = recomputed.Diagnosis;
        if (!recorded.RuleSupported || !recorded.VersionMatchesCurrent)
        {
            return new CaseDiagnosisDifference
            {
                RuleId = recordedDiagnosis.RuleId,
                ScopeSnapshotEventId = recorded.ScopeSnapshotEventId ?? recomputed.ScopeSnapshotEventId,
                Disposition = CaseDiagnosisDisposition.Unsupported,
                RecordedRuleVersion = recordedDiagnosis.RuleVersion,
                RecomputedRuleVersion = recomputedDiagnosis.RuleVersion,
                RecordedLevel = recordedDiagnosis.Level,
                RecordedEvidenceIds = recordedDiagnosis.EvidenceIds,
                RecomputedEvidenceIds = recomputedDiagnosis.EvidenceIds,
                Notes = new[] { "记录诊断的规则/版本不受当前构建支持；只标 Unsupported，不用当前规则冒充原版。" },
            };
        }

        var notes = new List<string>();
        var disposition = CaseDiagnosisDisposition.Match;
        if (recordedDiagnosis.Level != recomputedDiagnosis.Level)
        {
            disposition = CaseDiagnosisDisposition.LevelChanged;
            notes.Add("等级不同：记录 " + recordedDiagnosis.Level + "，重算 " + recomputedDiagnosis.Level + "。");
        }
        else if (!SetEquals(recordedDiagnosis.EvidenceIds, recomputedDiagnosis.EvidenceIds))
        {
            disposition = CaseDiagnosisDisposition.EvidenceChanged;
            notes.Add("证据引用集合不同；比较的是引用 ID，不是证据正文。");
        }
        else if (!SetEquals(recordedDiagnosis.MissingEvidence, recomputedDiagnosis.MissingEvidence))
        {
            disposition = CaseDiagnosisDisposition.MissingEvidenceChanged;
            notes.Add("缺失证据集合不同；未知项没有被折算成正常。");
        }

        return new CaseDiagnosisDifference
        {
            RuleId = recordedDiagnosis.RuleId,
            ScopeSnapshotEventId = recorded.ScopeSnapshotEventId ?? recomputed.ScopeSnapshotEventId,
            Disposition = disposition,
            RecordedRuleVersion = recordedDiagnosis.RuleVersion,
            RecomputedRuleVersion = recomputedDiagnosis.RuleVersion,
            RecordedLevel = recordedDiagnosis.Level,
            RecomputedLevel = recomputedDiagnosis.Level,
            RecordedEvidenceIds = recordedDiagnosis.EvidenceIds,
            RecomputedEvidenceIds = recomputedDiagnosis.EvidenceIds,
            Notes = notes,
        };
    }

    private static bool SetEquals(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static void AddOldPackageNotices(CasePackageInfo package, List<string> notices,
        IReadOnlyList<CaseRecordedDiagnosis> recorded)
    {
        if (!package.HasTypedEvidence)
        {
            notices.Add("legacy_no_typed_evidence: 包内没有 typed evidence，只有事件摘要；无法重算，也不补造诊断。");
        }

        if (!package.HasProbeResults)
        {
            notices.Add("no_probe_results: 包内没有探测结果，不能据此证明外部可达、恢复或根因；只陈述已有事实与缺口。");
        }

        if (!package.HasClientEvidence)
        {
            notices.Add("no_client_evidence: 包内没有客户端进程/磁盘配置证据；不推断客户端归属、配置陈旧或端口冲突。");
        }

        if (recorded.Count == 0)
        {
            notices.Add("no_recorded_diagnoses: 包内没有记录诊断；重算结论只是当前规则对已保存事实的解释，不是原版诊断。");
        }
    }

    private static void AddReplayProblems(ReplayResult replay, List<CaseReplayIssue> issues)
    {
        foreach (var problem in replay.Log.Problems.Take(MaxIssueDetail))
        {
            issues.Add(Blocking("damaged_event_log",
                $"第 {problem.LineNumber} 行事件损坏：{problem.Message}" + (problem.IsTruncatedTail ? "（截断尾行）" : "")));
        }

        if (replay.Log.Problems.Count > MaxIssueDetail)
        {
            issues.Add(Blocking("damaged_event_log_truncated",
                "还有更多事件损坏；只显示前 " + MaxIssueDetail + " 条。"));
        }

        if (replay.Log.DuplicateCount > 0)
        {
            issues.Add(Blocking("duplicate_event_ids",
                $"包内有 {replay.Log.DuplicateCount} 个重复 event_id；拒绝把歧义事件当成可重放证据。"));
        }

        foreach (var gap in replay.Log.SequenceGaps.Take(MaxIssueDetail))
        {
            issues.Add(Blocking("sequence_gap",
                $"事件序号缺口：期望 {gap.Expected}，实际 {gap.Found}（缺 {gap.Missing}）；包不完整。"));
        }

        foreach (var problem in replay.EvidenceProblems.Take(MaxIssueDetail))
        {
            issues.Add(Blocking("damaged_evidence_reference", problem));
        }

        if (replay.EvidenceProblems.Count > MaxIssueDetail)
        {
            issues.Add(Blocking("damaged_evidence_reference_truncated",
                "还有更多证据引用问题；只显示前 " + MaxIssueDetail + " 条。"));
        }
    }

    private static void AddManifestCountMismatches(JsonObject manifest, ReplayResult replay,
        List<CaseReplayIssue> blocking, List<CaseReplayIssue> warnings)
    {
        if (manifest["counts"] is not JsonObject counts) return;
        CheckCount(counts, "events", replay.Summary.EventCount, blocking);
        CheckCount(counts, "snapshots", replay.SnapshotScopes.Count, blocking);
        CheckCount(counts, "diagnoses", replay.Diagnoses.Count, blocking);
        if (manifest["synthetic"]?.GetValue<bool>() is { } synthetic
            && synthetic != replay.Summary.AllSynthetic && replay.Summary.EventCount > 0)
        {
            warnings.Add(Warning("manifest_synthetic_mismatch",
                "manifest.synthetic 与包内事件的 synthetic 标记不一致；以事件标记为准。"));
        }
    }

    private static void CheckCount(JsonObject counts, string name, int actual, List<CaseReplayIssue> issues)
    {
        if (!counts.TryGetPropertyValue(name, out var node) || node is null) return;
        if (!int.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared)) return;
        if (declared != actual)
        {
            issues.Add(Blocking("manifest_count_mismatch",
                $"manifest.counts.{name} 声明 {declared}，实际重放 {actual}。"));
        }
    }

    // ---------- result/fingerprint ----------

    private CaseReplayResult BuildUnanalysed(CaseReplayStatus status, CaseAdmissionSummary admission,
        IReadOnlyList<CaseReplayIssue> issues, CasePackageInfo? package, ReplayResult? replay, IReadOnlyList<string> notices)
    {
        var result = new CaseReplayResult
        {
            Status = status,
            Admission = admission,
            RuleSet = _evaluator.RuleSet,
            Package = package,
            Issues = issues,
            Notices = notices,
            DeterministicFingerprint = string.Empty,
        };
        return result with { DeterministicFingerprint = Fingerprint(result) };
    }

    /// <summary>
    /// Canonical hash over the public result fields. It contains no file path, wall-clock read time
    /// or random value, so replaying the same package with the same build produces the same value.
    /// </summary>
    public static string Fingerprint(CaseReplayResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        void Line(string value) => builder.Append(value).Append('\n');
        Line("status=" + result.Status);
        Line("admission=" + result.Admission.Accepted + "|" + result.Admission.EntryCount + "|"
            + result.Admission.TotalUncompressedBytes + "|" + result.Admission.MaxObservedPathDepth);
        foreach (var problem in result.Admission.Problems)
        {
            Line("admission_problem=" + problem.Code + "|" + problem.EntryName + "|" + problem.Message);
        }

        Line("rule_set=" + result.RuleSet.RuleSetVersion + "|" + result.RuleSet.AppVersion);
        foreach (var rule in result.RuleSet.Rules.OrderBy(item => item.RuleId, StringComparer.Ordinal))
        {
            Line("rule=" + rule.RuleId + "@" + rule.Version);
        }

        if (result.Package is { } package)
        {
            Line("package=" + package.PackageFormat + "|" + package.PackageFormatVersion + "|" + package.IncidentId
                + "|" + package.AppVersion + "|" + package.AppBuildCommit + "|" + package.Synthetic
                + "|" + package.EventCount + "|" + package.SnapshotCount + "|" + package.RecordedDiagnosisCount
                + "|" + package.HasTypedEvidence + "|" + package.HasProbeResults + "|" + package.HasClientEvidence);
            Line("package_rule_versions=" + string.Join(",", package.RecordedRuleVersions));
            Line("package_payload_versions=" + string.Join(",", package.EvidencePayloadVersions));
            Line("package_redaction=" + package.RedactionPolicyVersion);
        }

        foreach (var scope in result.SnapshotScopes.OrderBy(item => item.Sequence).ThenBy(item => item.SnapshotEventId, StringComparer.Ordinal))
        {
            Line("scope=" + scope.Sequence + "|" + scope.SnapshotEventId + "|" + scope.SnapshotId + "|"
                + scope.ObservationWindowId + "|" + scope.RecordCount + "|" + scope.ProbeCount + "|"
                + scope.RecordedDiagnosisCount + "|" + scope.RecomputedDiagnosisCount);
            foreach (var @interface in scope.Interfaces)
            {
                Line("scope_interface=" + scope.Sequence + "|" + @interface.InterfaceGuid + "|" + @interface.IfIndex);
            }
        }

        foreach (var item in result.RecordedDiagnoses
                     .OrderBy(entry => entry.ScopeSnapshotEventId ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Diagnosis.RuleId, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Diagnosis.DiagnosisId, StringComparer.Ordinal))
        {
            Line("recorded=" + DescribeDiagnosis(item.ScopeSnapshotEventId, item.Diagnosis)
                + "|supported=" + item.RuleSupported + "|version_match=" + item.VersionMatchesCurrent
                + "|current=" + item.CurrentRuleVersion);
        }

        foreach (var item in result.RecomputedDiagnoses
                     .OrderBy(entry => entry.ScopeSnapshotEventId ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Diagnosis.RuleId, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Diagnosis.DiagnosisId, StringComparer.Ordinal))
        {
            Line("recomputed=" + DescribeDiagnosis(item.ScopeSnapshotEventId, item.Diagnosis));
        }

        foreach (var difference in result.Differences
                     .OrderBy(item => item.ScopeSnapshotEventId ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(item => item.RuleId, StringComparer.Ordinal)
                     .ThenBy(item => item.Disposition))
        {
            Line("difference=" + difference.ScopeSnapshotEventId + "|" + difference.RuleId + "|" + difference.Disposition
                + "|" + difference.RecordedLevel + "|" + difference.RecomputedLevel
                + "|" + string.Join(",", difference.RecordedEvidenceIds.Order(StringComparer.Ordinal))
                + "|" + string.Join(",", difference.RecomputedEvidenceIds.Order(StringComparer.Ordinal)));
        }

        foreach (var incident in result.Incidents.OrderBy(item => item.IncidentId, StringComparer.Ordinal))
        {
            Line("incident=" + incident.IncidentId + "|" + incident.Trigger + "|" + string.Join(",", incident.States)
                + "|" + incident.StartedAt?.ToString("O", CultureInfo.InvariantCulture)
                + "|" + incident.ClosedAt?.ToString("O", CultureInfo.InvariantCulture)
                + "|" + incident.PreWindowAvailable + "|" + incident.PostWindowComplete
                + "|" + string.Join(",", incident.SnapshotScopeEventIds)
                + "|" + incident.SpansMultipleSnapshotCycles + "|" + incident.ClosureReason
                + "|" + string.Join(",", incident.MissingRanges));
        }

        foreach (var issue in result.Issues)
        {
            Line("issue=" + issue.Severity + "|" + issue.Code + "|" + issue.EntryName + "|" + issue.Message);
        }

        foreach (var notice in result.Notices)
        {
            Line("notice=" + notice);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string DescribeDiagnosis(string? scope, Diagnosis diagnosis) =>
        (scope ?? "") + "|" + diagnosis.RuleId + "@" + diagnosis.RuleVersion + "|" + diagnosis.Level
        + "|" + diagnosis.DiagnosisId
        + "|" + string.Join(",", diagnosis.EvidenceIds.Order(StringComparer.Ordinal))
        + "|" + string.Join(",", diagnosis.CounterEvidenceIds.Order(StringComparer.Ordinal))
        + "|" + string.Join(",", diagnosis.MissingEvidence.Order(StringComparer.Ordinal))
        + "|" + diagnosis.Summary + "|" + diagnosis.SuggestedNextStep;

    private static CaseReplayIssue Blocking(string code, string message, string? entryName = null) => new()
    {
        Code = code,
        Severity = CaseReplayIssueSeverity.Blocking,
        Message = message,
        EntryName = entryName,
    };

    private static CaseReplayIssue Warning(string code, string message) => new()
    {
        Code = code,
        Severity = CaseReplayIssueSeverity.Warning,
        Message = message,
    };

    private static CaseReplayIssue Info(string code, string message) => new()
    {
        Code = code,
        Severity = CaseReplayIssueSeverity.Info,
        Message = message,
    };

    private static string? Text(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    private static IReadOnlyList<string> StringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return Array.Empty<string>();
        return array.Select(Text).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
    }

    private static DateTimeOffset? ParseTime(JsonNode? node)
    {
        var text = Text(node);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private sealed record PackageInfoDraft(CasePackageInfo Info);

    private sealed class IncidentDraft(string incidentId)
    {
        public string IncidentId { get; } = incidentId;

        public string? Trigger { get; set; }

        public List<string> States { get; } = new();

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? ClosedAt { get; set; }

        public DateTimeOffset? WindowStart { get; set; }

        public DateTimeOffset? WindowEnd { get; set; }

        public bool? PreWindowAvailable { get; set; }

        public bool? PostWindowComplete { get; set; }

        public List<string> MissingRanges { get; } = new();

        public List<string> EventIds { get; } = new();

        public List<string> SnapshotIds { get; } = new();

        public List<string> DiagnosisIds { get; } = new();

        public string? ClosureReason { get; set; }
    }
}
