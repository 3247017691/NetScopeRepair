using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage.Export;

/// <summary>One file inside the package, already materialized so preview and export agree.</summary>
internal sealed record PackageEntry(string Name, byte[] Bytes, string Purpose);

/// <summary>A complete package held in memory. Nothing is written until the caller accepts it.</summary>
internal sealed record BuiltIncidentPackage
{
    public required IReadOnlyList<PackageEntry> Entries { get; init; }

    public required IReadOnlyList<ExportRedactionSummary> Redactions { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    public required PackageStatistics Statistics { get; init; }
}

/// <summary>
/// Builds the documented incident package. The document set and file order are fixed and every
/// file is hashed before the manifest is written. The incident id, alias labels and address
/// mappings are derived from a random per-package salt, so two exports of the same session differ
/// while the structure and semantic classes stay the same.
/// </summary>
internal static class IncidentPackageBuilder
{
    public const string PackageFormat = "netscope-incident-package";
    public const string PackageFormatVersion = "1";

    /// <summary>
    /// Bumped when the alias strategy or the identity pre-collection changes. Package format
    /// version stays 1 because every change is additive and old readers keep their fields.
    /// </summary>
    public const string RedactionPolicyVersion = "m1-alias-v3";

    private const string ManifestName = "manifest.json";
    private const string EventsName = "events.ndjson";
    private const string DiagnosesName = "diagnoses.json";
    private const string TimelineName = "timeline.json";
    private const string RedactionReportName = "redaction-report.json";
    private const string ChecksumsName = "checksums.sha256";

    public static BuiltIncidentPackage Build(ReplayResult replay, string incidentId, byte[] salt,
        IReadOnlyList<string> warnings, PackageStatistics? statistics = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(incidentId);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(warnings);
        statistics ??= PackageStatisticsBuilder.Build(replay);

        var ledger = new RedactionLedger();
        var anonymizer = new PackageAnonymizer(ledger, salt);

        // Identity pre-collection: register every structured or structurally recognisable name
        // before rewriting the first value. A name that only exists in an error message, or an
        // event whose traversal order differs from the interface list, must not decide whether the
        // alias exists at all.
        foreach (var envelope in replay.Log.Events)
        {
            anonymizer.Precollect(envelope.Payload);
            anonymizer.PrecollectText(envelope.Source);
        }

        var events = replay.Log.Events
            .Select(envelope => envelope with
            {
                Payload = anonymizer.RedactObject(envelope.Payload),
                Source = anonymizer.RedactText(envelope.Source),
            })
            .ToArray();

        var eventsText = string.Join("\n", events.Select(EnvelopeJson.Serialize)) + "\n";
        var eventsEntry = new PackageEntry(EventsName, ExportJson.Bytes(eventsText),
            "Anonymized typed evidence events in NDJSON order.");

        var snapshots = CollectSnapshots(events);
        var diagnoses = CollectDiagnoses(events, replay, anonymizer);

        var payloadEntries = new List<PackageEntry> { eventsEntry };

        for (var index = 0; index < snapshots.Count; index++)
        {
            var name = "snapshots/snapshot-" + (index + 1).ToString("000", CultureInfo.InvariantCulture) + ".json";
            payloadEntries.Add(new PackageEntry(name, ExportJson.Serialize(snapshots[index]),
                "Anonymized network snapshot with identity links preserved."));
        }

        var diagnosesDocument = new JsonObject
        {
            ["schema_version"] = EventEnvelope.SupportedSchemaVersion,
            ["diagnoses"] = diagnoses,
        };
        payloadEntries.Add(new PackageEntry(DiagnosesName, ExportJson.Serialize(diagnosesDocument),
            "Anonymized diagnoses with evidence references."));

        var startedAt = events.Length == 0 ? DateTimeOffset.UnixEpoch : events.Min(item => item.ObservedAt);
        var endedAt = events.Length == 0 ? startedAt : events.Max(item => item.ObservedAt);

        var timeline = new JsonObject
        {
            ["schema_version"] = EventEnvelope.SupportedSchemaVersion,
            ["started_at"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            ["ended_at"] = endedAt.ToString("O", CultureInfo.InvariantCulture),
            ["events"] = BuildTimeline(events),
        };
        payloadEntries.Add(new PackageEntry(TimelineName, ExportJson.Serialize(timeline),
            "Event order, status and reference timeline without payload bodies."));

        var report = BuildRedactionReport(ledger, replay, snapshots.Count, warnings);
        payloadEntries.Add(new PackageEntry(RedactionReportName, ExportJson.Serialize(report),
            "Redaction policy, categories and counts; contains no original values."));

        var manifest = BuildManifest(replay, incidentId, payloadEntries, snapshots.Count, startedAt, endedAt,
            ledger.Snapshot(), warnings, statistics);
        var manifestEntry = new PackageEntry(ManifestName, ExportJson.Serialize(manifest),
            "Package metadata, versions, counts, policy and per-file SHA-256.");

        var checksumsEntry = new PackageEntry(ChecksumsName, BuildChecksums(manifestEntry, payloadEntries),
            "SHA-256 of every other package file for integrity checking (not a signature).");

        var entries = new List<PackageEntry>(payloadEntries.Count + 2) { manifestEntry };
        entries.Add(eventsEntry);
        entries.AddRange(payloadEntries.Where(entry => entry.Name.StartsWith("snapshots/", StringComparison.Ordinal)));
        entries.AddRange(payloadEntries.Where(entry => entry.Name == DiagnosesName));
        entries.AddRange(payloadEntries.Where(entry => entry.Name == TimelineName));
        entries.AddRange(payloadEntries.Where(entry => entry.Name == RedactionReportName));
        entries.Add(checksumsEntry);

        return new BuiltIncidentPackage
        {
            Entries = entries,
            Redactions = ledger.Snapshot(),
            Warnings = warnings,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Statistics = statistics,
        };
    }

    private static List<JsonNode> CollectSnapshots(IReadOnlyList<EventEnvelope> events)
    {
        var result = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in events)
        {
            if (envelope.Payload["snapshot"] is not JsonObject snapshot) continue;
            var id = snapshot["snapshot_id"]?.GetValue<string>() ?? string.Empty;
            if (!seen.Add(id)) continue;
            result.Add(snapshot.DeepClone());
        }

        return result;
    }

    private static JsonArray CollectDiagnoses(IReadOnlyList<EventEnvelope> events, ReplayResult replay,
        PackageAnonymizer anonymizer)
    {
        var byId = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var envelope in events)
        {
            if (envelope.Payload["diagnosis"] is not JsonObject diagnosis) continue;
            var id = diagnosis["diagnosis_id"]?.GetValue<string>() ?? string.Empty;
            byId[id] = diagnosis.DeepClone();
        }

        var result = new JsonArray();
        foreach (var diagnosis in replay.Diagnoses)
        {
            if (byId.TryGetValue(diagnosis.DiagnosisId, out var transformed))
            {
                result.Add(transformed.DeepClone());
                continue;
            }

            // Defensive fallback: a diagnosis that was decoded from a typed payload but not
            // represented as an event would otherwise silently disappear from diagnoses.json.
            if (JsonSerializer.SerializeToNode(diagnosis, ExportJson.Options) is JsonObject serialized)
            {
                result.Add(anonymizer.RedactObject(serialized));
            }
        }

        return result;
    }

    private static JsonArray BuildTimeline(IReadOnlyList<EventEnvelope> events)
    {
        var result = new JsonArray();
        foreach (var envelope in events)
        {
            result.Add(new JsonObject
            {
                ["sequence"] = envelope.Sequence,
                ["event_id"] = envelope.EventId,
                ["observed_at"] = envelope.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                ["monotonic_ms"] = envelope.MonotonicMs,
                ["event_type"] = WireNames.Of(envelope.EventType),
                ["status"] = WireNames.Of(envelope.Status),
                ["synthetic"] = envelope.Synthetic,
                ["source"] = envelope.Source,
                ["refs"] = JsonSerializer.SerializeToNode(envelope.Refs, ExportJson.Options),
            });
        }

        return result;
    }

    private static JsonObject BuildRedactionReport(RedactionLedger ledger, ReplayResult replay,
        int snapshotCount, IReadOnlyList<string> warnings)
    {
        var categories = new JsonArray();
        foreach (var summary in ledger.Snapshot())
        {
            categories.Add(new JsonObject
            {
                ["category"] = summary.Category,
                ["replaced_fields"] = summary.ReplacedFields,
                ["strategy"] = summary.Strategy,
            });
        }

        return new JsonObject
        {
            ["schema_version"] = EventEnvelope.SupportedSchemaVersion,
            ["policy_version"] = RedactionPolicyVersion,
            ["salt_included"] = false,
            ["salt_handling"] = "A random per-package salt keys user/device/path/private-host aliases, class-preserving address aliases, proxy endpoint aliases and the incident id. It is held in memory only and is never serialized.",
            ["alias_scheme"] = "Public targets use target-A, target-B, ...; user, device, path, private-host and proxy endpoint aliases are keyed by the per-package salt and stay stable inside one package. Interface name/description/alias/SSID values use device aliases. Device identities are pre-collected over the whole package before any value is rewritten, so an adapter, virtual switch or filter name that exists only in an error message, or whose event order differs from the interface list, still maps to the same device alias. Proxy server and NO_PROXY lists retain protocol names, ports, wildcards, CIDR prefixes and list order while host identity is aliased.",
            ["anonymous_claims"] = "Pseudonymous aliases, not mathematical anonymity.",
            ["address_alias_limits"] = "Addresses remain class-preserving and parseable, but are pseudonymous placeholders rather than an injective permutation: different private or public addresses can share an alias when a 100-value octet map collides. Private IPv4 interface/route/gateway aliases keep their /16 and /24 relation (10/8, 192.168/16 and 172.16/12; 172.16/12 aliases also share the /12 class). 100.64/10 aliases map into 10/8 and keep /16 and /24, but the original 100.64/10 boundary is not preserved. IPv4 public aliases use a bounded 203.100-199.100-199.100-199 placeholder space: it offers far more than 100 values but carries no public-prefix or topology guarantee. Prefixes finer than /24 and non-octet boundaries are not preserved. IPv6 host bits use a keyed prefix-preserving mapping inside the class range, and IPv4-mapped IPv6 keeps the same embedded IPv4 alias as the bare form.",
            ["counts"] = new JsonObject
            {
                ["events"] = replay.Log.Events.Count,
                ["snapshots"] = snapshotCount,
                ["diagnoses"] = replay.Diagnoses.Count,
                ["gaps"] = replay.Log.SequenceGaps.Count,
                ["observation_gap_events"] = replay.Summary.ObservationGapCount,
            },
            ["categories"] = categories,
            ["retained"] = new JsonArray(
                "schema and contract versions",
                "event ids, sequence numbers and envelope references",
                "observed_at and monotonic_ms",
                "event_type and status",
                "synthetic flag",
                "evidence, probe, snapshot and diagnosis identifiers",
                "address family, port and semantic range class",
                "interface kind and operational state",
                "rule ids, rule versions and evidence grades"),
            ["removed"] = new JsonArray(
                "user names",
                "computer names",
                "interface names, descriptions, aliases and SSIDs",
                "adapter, virtual switch, filter and tunnel names quoted or introduced by an entity keyword in error text",
                "full local paths, including extended-length and UNC paths",
                "subscription URLs",
                "API keys, tokens and passwords",
                "Authorization and Cookie header values",
                "proxy credentials and structured proxy server hosts",
                "NO_PROXY host entries",
                "URL query secrets and business URL paths",
                "private host addresses (replaced by class-preserving aliases)"),
            ["warnings"] = ToJsonArray(warnings),
        };
    }

    private static JsonObject BuildManifest(ReplayResult replay, string incidentId,
        IReadOnlyList<PackageEntry> payloadEntries, int snapshotCount, DateTimeOffset startedAt,
        DateTimeOffset endedAt, IReadOnlyList<ExportRedactionSummary> redactions,
        IReadOnlyList<string> warnings, PackageStatistics statistics)
    {
        var files = new JsonArray();
        foreach (var entry in payloadEntries.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            files.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["bytes"] = entry.Bytes.LongLength,
                ["sha256"] = ExportJson.Sha256(entry.Bytes),
                ["purpose"] = entry.Purpose,
            });
        }

        var statusCounts = new JsonObject();
        foreach (var item in replay.Summary.ByStatus.OrderBy(pair => WireNames.Of(pair.Key), StringComparer.Ordinal))
        {
            statusCounts[WireNames.Of(item.Key)] = item.Value;
        }

        var rulesVersions = new JsonArray();
        foreach (var version in replay.Diagnoses.Select(diagnosis => diagnosis.RuleVersion)
                     .Where(version => !string.IsNullOrWhiteSpace(version))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            rulesVersions.Add(version);
        }

        var categories = new JsonArray();
        foreach (var summary in redactions)
        {
            categories.Add(new JsonObject
            {
                ["category"] = summary.Category,
                ["replaced_fields"] = summary.ReplacedFields,
                ["strategy"] = summary.Strategy,
            });
        }

        return new JsonObject
        {
            ["schema_version"] = EventEnvelope.SupportedSchemaVersion,
            ["package_format"] = PackageFormat,
            ["package_format_version"] = PackageFormatVersion,
            ["incident_id"] = incidentId,
            ["app_version"] = statistics.Versions.AppVersion,
            ["app_build_commit"] = statistics.Versions.AppBuildCommit,
            ["rules_version"] = rulesVersions.Count == 0 ? "none" : string.Join(",", rulesVersions.Select(node => node!.GetValue<string>())),
            ["rules_versions"] = rulesVersions,
            ["versions"] = BuildVersions(statistics.Versions),
            ["synthetic"] = replay.Summary.AllSynthetic,
            ["synthetic_event_count"] = replay.Summary.SyntheticCount,
            ["time_range"] = new JsonObject
            {
                ["started_at"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
                ["ended_at"] = endedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            ["counts"] = new JsonObject
            {
                // The historical fields keep their meaning: gaps is the sequence-gap count and
                // observation_gap_events is the non-success status count. New counters are added
                // next to them instead of redefining either one.
                ["events"] = replay.Log.Events.Count,
                ["snapshots"] = snapshotCount,
                ["diagnoses"] = replay.Diagnoses.Count,
                ["gaps"] = replay.Log.SequenceGaps.Count,
                ["gap_events"] = statistics.Gaps.GapEventCount,
                ["observation_gap_events"] = replay.Summary.ObservationGapCount,
                ["partial_events"] = replay.Summary.PartialCount,
                ["probe_events"] = statistics.ProbeCoverage.ProbeEvents,
                ["failed_events"] = statistics.Gaps.FailedEventCount,
                ["duplicate_event_ids"] = replay.Log.DuplicateCount,
            },
            ["gap_statistics"] = BuildGapStatistics(statistics.Gaps),
            ["completeness"] = BuildCompleteness(statistics.Completeness),
            ["probe_coverage"] = BuildProbeCoverage(statistics.ProbeCoverage),
            ["change_summary"] = BuildChangeSummary(statistics.Changes),
            ["diagnosis_provenance"] = BuildDiagnosisProvenance(statistics.Diagnoses),
            ["status_counts"] = statusCounts,
            ["redaction"] = new JsonObject
            {
                ["policy_version"] = RedactionPolicyVersion,
                ["salt_included"] = false,
                ["anonymous_claims"] = "Pseudonymous aliases, not mathematical anonymity.",
                ["categories"] = categories,
            },
            ["files"] = files,
            ["warnings"] = ToJsonArray(warnings),
        };
    }

    private static JsonObject BuildVersions(PackageVersionInfo versions) => new()
    {
        ["app_version"] = versions.AppVersion,
        ["app_informational_version"] = versions.AppInformationalVersion,
        ["app_build_commit"] = versions.AppBuildCommit,
        ["app_build_commit_source"] = versions.AppBuildCommitSource,
        ["package_format_version"] = versions.PackageFormatVersion,
        ["redaction_policy_version"] = versions.RedactionPolicyVersion,
        ["evidence_payload_versions"] = ToJsonArray(versions.EvidencePayloadVersions),
        ["parser_versions"] = ToJsonArray(versions.ParserVersions),
        ["rule_versions"] = ToJsonArray(versions.RuleVersions),
        ["source_versions"] = ToJsonArray(versions.SourceVersions),
        ["commit_note"] = "app_build_commit is the source revision embedded in the built assembly; it is not the current checkout and a local uncommitted build can differ from it.",
    };

    private static JsonObject BuildGapStatistics(PackageGapStatistics gaps) => new()
    {
        ["sequence_gaps"] = gaps.SequenceGapCount,
        ["gap_events"] = gaps.GapEventCount,
        ["observation_gap_events"] = gaps.ObservationGapEventCount,
        ["failed_events"] = gaps.FailedEventCount,
        ["note"] = gaps.Note,
    };

    private static JsonObject BuildCompleteness(PackageDomainCompleteness completeness) => new()
    {
        ["snapshots"] = completeness.SnapshotCount,
        ["incomplete_snapshots"] = completeness.IncompleteSnapshotCount,
        ["collection_complete"] = completeness.CollectionComplete,
        ["domain_status_counts"] = BuildStatusCounts(completeness.Domains),
        ["proxy_subdomain_status_counts"] = BuildStatusCounts(completeness.ProxySubdomains),
        ["note"] = completeness.Note,
    };

    private static JsonObject BuildProbeCoverage(PackageProbeCoverage coverage) => new()
    {
        ["has_probe_results"] = coverage.HasProbeResults,
        ["probe_events"] = coverage.ProbeEvents,
        ["probe_groups"] = coverage.ProbeGroups,
        ["targets"] = coverage.TargetCount,
        ["address_families"] = ToJsonArray(coverage.AddressFamilies),
        ["paths"] = ToJsonArray(coverage.Paths),
        ["successful"] = coverage.Successful,
        ["partial"] = coverage.Partial,
        ["failed"] = coverage.Failed,
        ["note"] = coverage.Note,
    };

    private static JsonObject BuildChangeSummary(PackageChangeSummary changes) => new()
    {
        ["comparable_pairs"] = changes.ComparablePairs,
        ["interfaces_compared"] = changes.InterfacesCompared,
        ["changed_field_counts"] = new JsonObject(
            changes.FieldCounts.Select(item =>
                new KeyValuePair<string, JsonNode?>(item.Field, JsonValue.Create(item.Count)))),
        ["changes"] = new JsonArray(changes.Changes.Select(change => (JsonNode)new JsonObject
        {
            ["from_sequence"] = change.FromSequence,
            ["to_sequence"] = change.ToSequence,
            ["interface_guid"] = change.InterfaceGuid,
            ["family"] = change.Family,
            ["if_index"] = change.IfIndex,
            ["field"] = change.Field,
            ["before"] = change.Before,
            ["after"] = change.After,
        }).ToArray()),
        ["truncated"] = changes.Truncated,
        ["note"] = changes.Note,
    };

    private static JsonObject BuildDiagnosisProvenance(PackageDiagnosisProvenance provenance) => new()
    {
        ["recorded_in_package"] = provenance.RecordedInPackage,
        ["recomputed_by_exporter"] = provenance.RecomputedByExporter,
        ["note"] = provenance.Note,
    };

    private static JsonObject BuildStatusCounts(IReadOnlyList<PackageStatusCount> counts)
    {
        var result = new JsonObject();
        foreach (var item in counts)
        {
            result[item.Path] ??= new JsonObject();
            ((JsonObject)result[item.Path]!)[item.Status] = item.Count;
        }

        return result;
    }

    private static byte[] BuildChecksums(PackageEntry manifest, IReadOnlyList<PackageEntry> payloadEntries)
    {
        var all = payloadEntries.Append(manifest)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        foreach (var entry in all)
        {
            builder.Append(ExportJson.Sha256(entry.Bytes))
                .Append("  ")
                .Append(entry.Name)
                .Append('\n');
        }

        return ExportJson.Bytes(builder.ToString());
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add(value);
        return result;
    }
}
