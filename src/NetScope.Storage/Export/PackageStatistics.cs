using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage.Export;

/// <summary>One domain path (for example <c>interfaces</c> or <c>proxy.winhttp</c>) and how often a status occurred.</summary>
public sealed record PackageStatusCount
{
    public required string Path { get; init; }

    public required string Status { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// Per-snapshot completeness of the recorded data domains. It exists so a reader cannot mistake
/// <c>gaps = 0</c> (no independent gap event) for "every domain was read completely".
/// </summary>
public sealed record PackageDomainCompleteness
{
    public required int SnapshotCount { get; init; }

    public required int IncompleteSnapshotCount { get; init; }

    /// <summary>True only when there is at least one snapshot, every domain is ok and no gap event exists.</summary>
    public required bool CollectionComplete { get; init; }

    /// <summary>One entry per domain path and status, e.g. <c>interfaces/partial=102</c>.</summary>
    public required IReadOnlyList<PackageStatusCount> Domains { get; init; }

    /// <summary>Proxy layers are reported separately so an unsupported subdomain is not averaged away.</summary>
    public required IReadOnlyList<PackageStatusCount> ProxySubdomains { get; init; }

    public required string Note { get; init; }
}

/// <summary>External probe coverage. Zero probes mean no reachability test exists, not that the network is healthy.</summary>
public sealed record PackageProbeCoverage
{
    public required int ProbeEvents { get; init; }

    public required int ProbeGroups { get; init; }

    public required int TargetCount { get; init; }

    public required IReadOnlyList<string> AddressFamilies { get; init; }

    public required IReadOnlyList<string> Paths { get; init; }

    public required int Successful { get; init; }

    public required int Partial { get; init; }

    public required int Failed { get; init; }

    public required string Note { get; init; }

    public bool HasProbeResults => ProbeEvents > 0;
}

/// <summary>Gap counters kept apart on purpose.</summary>
public sealed record PackageGapStatistics
{
    /// <summary>Sequence discontinuities in the event log (the historical <c>counts.gaps</c>).</summary>
    public required int SequenceGapCount { get; init; }

    /// <summary>Independent <c>event_type = gap</c> records.</summary>
    public required int GapEventCount { get; init; }

    /// <summary>Events whose status means "no usable observation was obtained".</summary>
    public required int ObservationGapEventCount { get; init; }

    public required int FailedEventCount { get; init; }

    public required string Note { get; init; }
}

/// <summary>Build and rule provenance. The commit is the one the assembly was built from, not the current checkout.</summary>
public sealed record PackageVersionInfo
{
    public required string AppVersion { get; init; }

    public required string AppInformationalVersion { get; init; }

    public required string AppBuildCommit { get; init; }

    /// <summary>Where the commit value came from; <c>not_embedded</c> means the build carries no commit.</summary>
    public required string AppBuildCommitSource { get; init; }

    public required string PackageFormatVersion { get; init; }

    public required string RedactionPolicyVersion { get; init; }

    public required IReadOnlyList<string> EvidencePayloadVersions { get; init; }

    public required IReadOnlyList<string> ParserVersions { get; init; }

    public required IReadOnlyList<string> RuleVersions { get; init; }

    public required IReadOnlyList<string> SourceVersions { get; init; }
}

public sealed record PackageFieldChangeCount
{
    public required string Field { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// One identity-safe parameter delta between two adjacent snapshots. Values are numbers, booleans,
/// enums or address counts only; names and addresses themselves are never copied here.
/// </summary>
public sealed record PackageChange
{
    public required int FromSequence { get; init; }

    public required int ToSequence { get; init; }

    public required string InterfaceGuid { get; init; }

    public required string Family { get; init; }

    public required int IfIndex { get; init; }

    public required string Field { get; init; }

    public required string Before { get; init; }

    public required string After { get; init; }
}

/// <summary>A short change summary derived from adjacent snapshots; it never infers a cause or operator.</summary>
public sealed record PackageChangeSummary
{
    public required int ComparablePairs { get; init; }

    public required int InterfacesCompared { get; init; }

    public required IReadOnlyList<PackageFieldChangeCount> FieldCounts { get; init; }

    public required IReadOnlyList<PackageChange> Changes { get; init; }

    public required bool Truncated { get; init; }

    public required string Note { get; init; }
}

/// <summary>Separates recorded diagnoses from any offline re-evaluation. The exporter performs none.</summary>
public sealed record PackageDiagnosisProvenance
{
    public required int RecordedInPackage { get; init; }

    public required int RecomputedByExporter { get; init; }

    public required string Note { get; init; }
}

/// <summary>Everything the manifest and the local preview report about the package shape.</summary>
public sealed record PackageStatistics
{
    public required PackageGapStatistics Gaps { get; init; }

    public required PackageDomainCompleteness Completeness { get; init; }

    public required PackageProbeCoverage ProbeCoverage { get; init; }

    public required PackageVersionInfo Versions { get; init; }

    public required PackageChangeSummary Changes { get; init; }

    public required PackageDiagnosisProvenance Diagnoses { get; init; }

    public static PackageStatistics Empty { get; } = new()
    {
        Gaps = new PackageGapStatistics
        {
            SequenceGapCount = 0,
            GapEventCount = 0,
            ObservationGapEventCount = 0,
            FailedEventCount = 0,
            Note = "No readable event log was available; every gap count is unknown rather than zero evidence of completeness.",
        },
        Completeness = new PackageDomainCompleteness
        {
            SnapshotCount = 0,
            IncompleteSnapshotCount = 0,
            CollectionComplete = false,
            Domains = Array.Empty<PackageStatusCount>(),
            ProxySubdomains = Array.Empty<PackageStatusCount>(),
            Note = "Completeness is unknown without a readable snapshot.",
        },
        ProbeCoverage = new PackageProbeCoverage
        {
            ProbeEvents = 0,
            ProbeGroups = 0,
            TargetCount = 0,
            AddressFamilies = Array.Empty<string>(),
            Paths = Array.Empty<string>(),
            Successful = 0,
            Partial = 0,
            Failed = 0,
            Note = "No probe event was readable; reachability was not tested.",
        },
        Versions = new PackageVersionInfo
        {
            AppVersion = "0.0.0",
            AppInformationalVersion = string.Empty,
            AppBuildCommit = "unknown",
            AppBuildCommitSource = "not_embedded",
            PackageFormatVersion = IncidentPackageBuilder.PackageFormatVersion,
            RedactionPolicyVersion = IncidentPackageBuilder.RedactionPolicyVersion,
            EvidencePayloadVersions = Array.Empty<string>(),
            ParserVersions = Array.Empty<string>(),
            RuleVersions = Array.Empty<string>(),
            SourceVersions = Array.Empty<string>(),
        },
        Changes = new PackageChangeSummary
        {
            ComparablePairs = 0,
            InterfacesCompared = 0,
            FieldCounts = Array.Empty<PackageFieldChangeCount>(),
            Changes = Array.Empty<PackageChange>(),
            Truncated = false,
            Note = "No snapshot pair could be compared.",
        },
        Diagnoses = new PackageDiagnosisProvenance
        {
            RecordedInPackage = 0,
            RecomputedByExporter = 0,
            Note = "No diagnosis was readable.",
        },
    };
}

/// <summary>The preview plus the package-shape statistics the manifest reports.</summary>
public sealed record ExportPackageDetails
{
    public required ExportPreview Preview { get; init; }

    public required PackageStatistics Statistics { get; init; }
}

/// <summary>
/// Optional extra surface for the local preview. <see cref="IIncidentExporter"/> predates the
/// separated gap/completeness/probe/version sections, so callers detect this interface instead of
/// re-reading the package.
/// </summary>
public interface IExportPackageDetailsProvider
{
    ExportPackageDetails PreviewDetails(string sessionPath);
}

/// <summary>
/// Derives every manifest/preview statistic from the replayed session. It is read-only and needs
/// no network access. Statistics intentionally contain no redaction-dependent values (no aliases,
/// names or addresses) so two exports of one session report the same shape.
/// </summary>
internal static class PackageStatisticsBuilder
{
    private const int MaxChangesInSummary = 20;

    private static readonly string[] DomainPaths = { "interfaces", "routes", "dns", "proxy" };

    private static readonly (string Layer, string Path)[] ProxySubdomainPaths =
    {
        // Layer names are the snake_case wire names produced by the evidence codec
        // (WinInet -> win_inet, WinHttp -> win_http), not the C# property spelling.
        ("tool_process_environment", "proxy.tool_process_environment"),
        ("user_environment", "proxy.user_environment"),
        ("machine_environment", "proxy.machine_environment"),
        ("win_inet", "proxy.wininet"),
        ("win_http", "proxy.winhttp"),
    };

    private static readonly Regex CommitPattern = new("^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static PackageStatistics Build(ReplayResult replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var events = replay.Log.Events;
        var snapshotEvents = events
            .Where(envelope => envelope.Payload["snapshot"] is JsonObject)
            .ToArray();

        var gaps = BuildGaps(replay);
        var completeness = BuildCompleteness(snapshotEvents, gaps);
        var probeCoverage = BuildProbeCoverage(events);
        var versions = BuildVersions(replay, events);
        var changes = BuildChangeSummary(snapshotEvents);
        var diagnoses = new PackageDiagnosisProvenance
        {
            RecordedInPackage = replay.Diagnoses.Count,
            RecomputedByExporter = 0,
            Note = "diagnoses.json holds only diagnoses recorded by the diagnostic engine at capture time. The exporter never re-evaluates rules; any offline recomputation must be done by a separate read-only tool and is not part of this package.",
        };

        return new PackageStatistics
        {
            Gaps = gaps,
            Completeness = completeness,
            ProbeCoverage = probeCoverage,
            Versions = versions,
            Changes = changes,
            Diagnoses = diagnoses,
        };
    }

    private static PackageGapStatistics BuildGaps(ReplayResult replay) => new()
    {
        SequenceGapCount = replay.Log.SequenceGaps.Count,
        GapEventCount = replay.Log.Events.Count(envelope => envelope.EventType == EventType.Gap),
        ObservationGapEventCount = replay.Summary.ObservationGapCount,
        FailedEventCount = replay.Log.Events.Count(envelope => envelope.Status is EventStatus.Failed
            or EventStatus.Timeout or EventStatus.Cancelled or EventStatus.Unsupported
            or EventStatus.AccessDenied),
        Note = "The three counters are not interchangeable: sequence gaps are missing sequence numbers, gap events are independent event_type=gap records, and observation-gap events are non-ok statuses. None of them measures whether each snapshot domain was read completely; use completeness.domain_status_counts for that.",
    };

    private static PackageDomainCompleteness BuildCompleteness(IReadOnlyList<EventEnvelope> snapshotEvents,
        PackageGapStatistics gaps)
    {
        var domainCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var subdomainCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var incomplete = 0;

        foreach (var envelope in snapshotEvents)
        {
            var snapshot = envelope.Payload["snapshot"]!.AsObject();
            var statuses = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["interfaces"] = DomainStatus(snapshot, "interfaces"),
                ["routes"] = DomainStatus(snapshot, "routes"),
                ["dns"] = DomainStatus(snapshot, "dns"),
                ["proxy"] = ProxyDomainStatus(snapshot),
            };

            foreach (var path in DomainPaths)
            {
                Increment(domainCounts, path, statuses[path]);
            }

            foreach (var (layer, path) in ProxySubdomainPaths)
            {
                Increment(subdomainCounts, path, ProxyLayerStatus(snapshot, layer));
            }

            if (statuses.Values.Any(status => !string.Equals(status, WireNames.Of(EventStatus.Ok), StringComparison.Ordinal)))
            {
                incomplete++;
            }
        }

        return new PackageDomainCompleteness
        {
            SnapshotCount = snapshotEvents.Count,
            IncompleteSnapshotCount = incomplete,
            CollectionComplete = snapshotEvents.Count > 0 && incomplete == 0 && gaps.GapEventCount == 0,
            Domains = Flatten(domainCounts),
            ProxySubdomains = Flatten(subdomainCounts),
            Note = "Each snapshot domain and proxy subdomain is counted by status. gaps=0 only means no independent gap event; it does not mean the domains were read completely and it says nothing about network health.",
        };
    }

    private static PackageProbeCoverage BuildProbeCoverage(IReadOnlyList<EventEnvelope> events)
    {
        var probeEvents = events
            .Where(envelope => envelope.Payload["probe_result"] is JsonObject)
            .ToArray();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var successful = 0;
        var partial = 0;
        var failed = 0;

        foreach (var envelope in probeEvents)
        {
            var probe = envelope.Payload["probe_result"]!.AsObject();
            if (String(probe, "probe_group_id") is { Length: > 0 } group) groups.Add(group);
            if (String(probe, "target_id") is { Length: > 0 } target) targets.Add(target);
            if (String(probe, "address_family") is { Length: > 0 } family) families.Add(family);
            if (String(probe, "path") is { Length: > 0 } path) paths.Add(path);

            if (envelope.Status == EventStatus.Ok) successful++;
            else if (envelope.Status == EventStatus.Partial) partial++;
            else failed++;
        }

        return new PackageProbeCoverage
        {
            ProbeEvents = probeEvents.Length,
            ProbeGroups = groups.Count,
            TargetCount = targets.Count,
            AddressFamilies = families.Order(StringComparer.Ordinal).ToArray(),
            Paths = paths.Order(StringComparer.Ordinal).ToArray(),
            Successful = successful,
            Partial = partial,
            Failed = failed,
            Note = "Probe coverage describes how much external verification exists. Zero probe events means nothing was tested against a target; snapshot status is a local observation, not a reachability result.",
        };
    }

    private static PackageVersionInfo BuildVersions(ReplayResult replay, IReadOnlyList<EventEnvelope> events)
    {
        var assembly = typeof(PackageStatisticsBuilder).Assembly;
        var appVersion = assembly.GetName().Version?.ToString() ?? "0.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? string.Empty;
        var (commit, commitSource) = ResolveCommit(assembly, informational);

        var payloadVersions = events
            .Select(envelope => String(envelope.Payload, "payload_version"))
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var parserVersions = events
            .SelectMany(envelope => ParserVersionsIn(envelope.Payload))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var ruleVersions = replay.Diagnoses
            .Select(diagnosis => diagnosis.RuleVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var sourceVersions = events
            .Select(envelope => envelope.SourceVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new PackageVersionInfo
        {
            AppVersion = appVersion,
            AppInformationalVersion = informational,
            AppBuildCommit = commit,
            AppBuildCommitSource = commitSource,
            PackageFormatVersion = IncidentPackageBuilder.PackageFormatVersion,
            RedactionPolicyVersion = IncidentPackageBuilder.RedactionPolicyVersion,
            EvidencePayloadVersions = payloadVersions,
            ParserVersions = parserVersions,
            RuleVersions = ruleVersions,
            SourceVersions = sourceVersions,
        };
    }

    private static (string Commit, string Source) ResolveCommit(Assembly assembly, string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus > 0 && plus < informationalVersion.Length - 1)
        {
            var candidate = informationalVersion[(plus + 1)..];
            var dot = candidate.IndexOf('.');
            if (dot > 0) candidate = candidate[..dot];
            if (CommitPattern.IsMatch(candidate))
            {
                return (candidate.ToLowerInvariant(), "assembly_informational_version");
            }
        }

        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key is "SourceRevisionId" or "Commit" or "GitCommit")
            ?.Value;
        if (!string.IsNullOrWhiteSpace(metadata) && CommitPattern.IsMatch(metadata))
        {
            return (metadata.ToLowerInvariant(), "assembly_metadata");
        }

        return ("unknown", "not_embedded");
    }

    private static IEnumerable<string> ParserVersionsIn(JsonObject payload)
    {
        if (payload["evidence_record"]?["facts"] is not JsonObject facts) yield break;
        foreach (var property in facts)
        {
            if (!property.Key.Contains("parser", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value is JsonValue value && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static PackageChangeSummary BuildChangeSummary(IReadOnlyList<EventEnvelope> snapshotEvents)
    {
        var changes = new List<PackageChange>();
        var fieldCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var comparedInterfaces = new HashSet<string>(StringComparer.Ordinal);
        var previous = (IReadOnlyList<InterfaceObservation>?)null;
        var previousSequence = 0;
        var comparablePairs = 0;
        var totalChanges = 0;

        foreach (var envelope in snapshotEvents)
        {
            var snapshot = envelope.Payload["snapshot"]!.AsObject();
            var current = ReadInterfaces(snapshot);
            if (current is not null)
            {
                foreach (var item in current) comparedInterfaces.Add(item.Key);
            }

            if (previous is not null && current is not null)
            {
                comparablePairs++;
                foreach (var change in Compare(previous, current, previousSequence, envelope.Sequence))
                {
                    totalChanges++;
                    fieldCounts[change.Field] = fieldCounts.GetValueOrDefault(change.Field) + 1;
                    if (changes.Count < MaxChangesInSummary) changes.Add(change);
                }
            }

            previous = current;
            previousSequence = envelope.Sequence;
        }

        return new PackageChangeSummary
        {
            ComparablePairs = comparablePairs,
            InterfacesCompared = comparedInterfaces.Count,
            FieldCounts = fieldCounts
                .Select(item => new PackageFieldChangeCount { Field = item.Key, Count = item.Value })
                .ToArray(),
            Changes = changes,
            Truncated = totalChanges > changes.Count,
            Note = "Short parameter diff between adjacent snapshots whose interfaces domain produced data. Values are numbers, booleans, enums or address counts; names and addresses are not copied. A delta is an observation, not a cause or an operator action.",
        };
    }

    private static IReadOnlyList<InterfaceObservation>? ReadInterfaces(JsonObject snapshot)
    {
        if (snapshot["interfaces"] is not JsonObject domain) return null;
        var status = String(domain, "status");
        if (status is not ("ok" or "partial")) return null;
        if (domain["value"] is not JsonArray values) return null;

        var result = new List<InterfaceObservation>(values.Count);
        foreach (var item in values)
        {
            if (item is not JsonObject networkInterface) continue;
            var guid = Scalar(networkInterface, "interface_guid");
            if (string.IsNullOrWhiteSpace(guid)) guid = Scalar(networkInterface, "luid");
            if (string.IsNullOrWhiteSpace(guid)) continue;
            var family = Scalar(networkInterface, "family") ?? "unknown";
            result.Add(new InterfaceObservation(
                guid + "|" + family,
                guid,
                family,
                Int(networkInterface, "if_index") ?? 0,
                Scalar(networkInterface, "metric"),
                Scalar(networkInterface, "automatic_metric"),
                Scalar(networkInterface, "disable_default_routes"),
                Scalar(networkInterface, "operational_status"),
                Scalar(networkInterface, "kind"),
                networkInterface["addresses"] is JsonArray addresses ? addresses.Count : 0));
        }

        return result;
    }

    private static IEnumerable<PackageChange> Compare(IReadOnlyList<InterfaceObservation> previous,
        IReadOnlyList<InterfaceObservation> current, int fromSequence, int toSequence)
    {
        var previousByKey = IndexByKey(previous);
        var currentByKey = IndexByKey(current);

        foreach (var (key, before) in previousByKey)
        {
            if (currentByKey.TryGetValue(key, out var after))
            {
                foreach (var change in CompareFields(before, after, fromSequence, toSequence)) yield return change;
                continue;
            }

            yield return NewChange(fromSequence, toSequence, before, "observed", "present", "absent");
        }

        foreach (var (key, after) in currentByKey)
        {
            if (previousByKey.ContainsKey(key)) continue;
            yield return NewChange(fromSequence, toSequence, after, "observed", "absent", "present");
        }
    }

    private static Dictionary<string, InterfaceObservation> IndexByKey(IReadOnlyList<InterfaceObservation> items)
    {
        var result = new Dictionary<string, InterfaceObservation>(StringComparer.Ordinal);
        foreach (var item in items) result.TryAdd(item.Key, item);
        return result;
    }

    private static IEnumerable<PackageChange> CompareFields(InterfaceObservation before,
        InterfaceObservation after, int fromSequence, int toSequence)
    {
        foreach (var (field, previousValue, currentValue) in new[]
                 {
                     ("metric", before.Metric, after.Metric),
                     ("automatic_metric", before.AutomaticMetric, after.AutomaticMetric),
                     ("disable_default_routes", before.DisableDefaultRoutes, after.DisableDefaultRoutes),
                     ("operational_status", before.OperationalStatus, after.OperationalStatus),
                     ("kind", before.Kind, after.Kind),
                     ("address_count", before.AddressCount.ToString(CultureInfo.InvariantCulture),
                         after.AddressCount.ToString(CultureInfo.InvariantCulture)),
                 })
        {
            if (string.Equals(previousValue ?? "<unknown>", currentValue ?? "<unknown>", StringComparison.Ordinal))
                continue;
            yield return NewChange(fromSequence, toSequence, after, field, previousValue ?? "<unknown>",
                currentValue ?? "<unknown>");
        }
    }

    private static PackageChange NewChange(int fromSequence, int toSequence, InterfaceObservation item,
        string field, string before, string after) => new()
    {
        FromSequence = fromSequence,
        ToSequence = toSequence,
        InterfaceGuid = item.InterfaceGuid,
        Family = item.Family,
        IfIndex = item.IfIndex,
        Field = field,
        Before = before,
        After = after,
    };

    private static void Increment(Dictionary<string, Dictionary<string, int>> counts, string path, string status)
    {
        if (!counts.TryGetValue(path, out var statuses))
        {
            statuses = new Dictionary<string, int>(StringComparer.Ordinal);
            counts[path] = statuses;
        }

        statuses[status] = statuses.GetValueOrDefault(status) + 1;
    }

    private static IReadOnlyList<PackageStatusCount> Flatten(Dictionary<string, Dictionary<string, int>> counts) =>
        counts
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .SelectMany(item => item.Value
                .OrderBy(status => status.Key, StringComparer.Ordinal)
                .Select(status => new PackageStatusCount
                {
                    Path = item.Key,
                    Status = status.Key,
                    Count = status.Value,
                }))
            .ToArray();

    private static string DomainStatus(JsonObject snapshot, string domain) =>
        snapshot[domain] is JsonObject value ? String(value, "status") ?? "unknown" : "absent";

    private static string ProxyDomainStatus(JsonObject snapshot)
    {
        if (snapshot["proxies"] is not JsonObject proxies) return "absent";
        var own = String(proxies, "status") ?? "unknown";
        if (!string.Equals(own, WireNames.Of(EventStatus.Ok), StringComparison.Ordinal)) return own;
        var layerStatuses = ProxySubdomainPaths.Select(item => ProxyLayerStatus(snapshot, item.Layer)).ToArray();
        return layerStatuses.All(status => string.Equals(status, WireNames.Of(EventStatus.Ok), StringComparison.Ordinal))
            ? WireNames.Of(EventStatus.Ok)
            : WireNames.Of(EventStatus.Partial);
    }

    private static string ProxyLayerStatus(JsonObject snapshot, string layer)
    {
        if (snapshot["proxies"]?["value"] is not JsonObject proxies) return "unknown";
        return proxies[layer] is JsonObject value ? String(value, "status") ?? "unknown" : "unknown";
    }

    private static string? String(JsonObject node, string name)
    {
        var value = node[name];
        return value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>Textual form of a scalar for diffing: numbers and booleans are not strings in JSON.</summary>
    private static string? Scalar(JsonObject node, string name)
    {
        var value = node[name];
        return value is JsonValue json ? json.ToJsonString().Trim('"') : null;
    }

    private static int? Int(JsonObject node, string name)
    {
        var value = node[name];
        return value is JsonValue json && json.TryGetValue<int>(out var number) ? number : null;
    }

    private sealed record InterfaceObservation(
        string Key,
        string InterfaceGuid,
        string Family,
        int IfIndex,
        string? Metric,
        string? AutomaticMetric,
        string? DisableDefaultRoutes,
        string? OperationalStatus,
        string? Kind,
        int AddressCount);
}
