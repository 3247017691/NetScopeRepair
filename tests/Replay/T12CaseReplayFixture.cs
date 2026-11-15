using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Diagnostics;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

/// <summary>
/// T12 fixtures: one deterministic two-cycle synthetic session plus a deterministic untrusted
/// package writer. The writer lets tests break one property at a time (checksum, manifest, entry
/// names, entry sizes, link flags) while keeping the rest of a valid package intact.
/// </summary>
internal static class T12CaseReplayFixture
{
    public const string Session = "session-t12";
    public const string Boot = "boot-t12";
    public static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));

    public static T12Scenario TwoCycleScenario(bool includeIncident = true, bool includeClientEvidence = true)
    {
        var events = new List<EventEnvelope>();
        var cycleOne = BuildCycle("snapshot-t12-1", T0, failing: true, ifIndex: 12, includeClientEvidence);
        var cycleTwo = BuildCycle("snapshot-t12-2", T0.AddSeconds(10), failing: false, ifIndex: 44, includeClientEvidence);
        events.AddRange(cycleOne.Events);
        events.AddRange(cycleTwo.Events);
        if (includeIncident)
        {
            events.AddRange(IncidentMarker(events, cycleOne.Diagnoses.Concat(cycleTwo.Diagnoses).ToArray()));
        }

        var rebased = Rebase(events);
        var text = string.Join("\n", rebased.Select(EnvelopeJson.Serialize)) + "\n";
        return new T12Scenario(rebased, text, new ReplayService().ReplayText(text));
    }

    public static T12Scenario LocalOnlyScenario()
    {
        var snapshot = Snapshot("snapshot-t12-local", T0, ifIndex: 12);
        var evidence = new EvidenceSet
        {
            Snapshot = snapshot,
            Probes = Array.Empty<ProbeResult>(),
            Records = Array.Empty<EvidenceRecord>(),
        };
        var events = Rebase(EvidenceSessionEvents.Create(evidence, Array.Empty<Diagnosis>(), Session, Boot,
            synthetic: true, T0.AddSeconds(1), 1000, "local"));
        var text = string.Join("\n", events.Select(EnvelopeJson.Serialize)) + "\n";
        return new T12Scenario(events, text, new ReplayService().ReplayText(text));
    }

    public static T12Scenario UntypedScenario()
    {
        var events = new[]
        {
            new EventEnvelope
            {
                SchemaVersion = EventEnvelope.SupportedSchemaVersion,
                EventId = "event-untyped-1",
                SessionId = Session,
                BootId = Boot,
                Sequence = 1,
                ObservedAt = T0,
                MonotonicMs = 0,
                EventType = EventType.Gap,
                Status = EventStatus.Partial,
                Source = "legacy.collector",
                SourceVersion = "0.1",
                Synthetic = true,
                Refs = new EventRefs(),
                Payload = new JsonObject { ["note"] = "legacy event without typed evidence" },
            },
        };
        var text = string.Join("\n", events.Select(EnvelopeJson.Serialize)) + "\n";
        return new T12Scenario(events, text, new ReplayService().ReplayText(text));
    }

    /// <summary>Builds a deterministic package whose manifest/checksums are internally consistent.</summary>
    public static string WritePackage(TempWorkspace workspace, T12Scenario scenario, string name = "case.zip",
        Action<JsonObject>? manifestMutate = null,
        Action<Dictionary<string, byte[]>>? tamperAfterChecksums = null,
        Action<ZipArchive>? zipMutate = null,
        CaseReplayLimits? limits = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["events.ndjson"] = Encoding.UTF8.GetBytes(scenario.EventsText),
        };
        var manifest = new JsonObject
        {
            ["schema_version"] = EventEnvelope.SupportedSchemaVersion,
            ["package_format"] = "netscope-incident-package",
            ["package_format_version"] = "1",
            ["incident_id"] = "incident-t12-package",
            ["app_version"] = "test",
            ["app_build_commit"] = "test-commit",
            ["synthetic"] = true,
            ["time_range"] = new JsonObject
            {
                ["started_at"] = T0.ToString("O"),
                ["ended_at"] = T0.AddSeconds(30).ToString("O"),
            },
            ["counts"] = new JsonObject
            {
                ["events"] = scenario.Replay.Summary.EventCount,
                ["snapshots"] = scenario.Replay.SnapshotScopes.Count,
                ["diagnoses"] = scenario.Replay.Diagnoses.Count,
            },
            ["versions"] = new JsonObject
            {
                ["app_version"] = "test",
                ["package_format_version"] = "1",
                ["redaction_policy_version"] = "m1-alias-v3",
                ["evidence_payload_versions"] = new JsonArray("m0-evidence-1"),
                ["rule_versions"] = new JsonArray(scenario.Replay.Diagnoses
                    .Select(item => item.RuleVersion).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select((string value) => JsonValue.Create(value)).ToArray()),
            },
        };
        manifestMutate?.Invoke(manifest);
        var manifestFiles = new JsonArray();
        foreach (var (fileName, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            manifestFiles.Add(new JsonObject
            {
                ["name"] = fileName,
                ["sha256"] = Sha256(bytes),
                ["purpose"] = "test payload",
            });
        }

        manifest["files"] = manifestFiles;
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var checksums = new StringBuilder();
        foreach (var (fileName, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            checksums.Append(Sha256(bytes)).Append("  ").Append(fileName).Append('\n');
        }

        files["checksums.sha256"] = Encoding.UTF8.GetBytes(checksums.ToString());
        tamperAfterChecksums?.Invoke(files);

        var path = workspace.File(name);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (fileName, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
                using var output = entry.Open();
                output.Write(bytes, 0, bytes.Length);
            }

            zipMutate?.Invoke(archive);
        }

        return path;
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Cycle BuildCycle(string snapshotId, DateTimeOffset at, bool failing, int ifIndex,
        bool includeClientEvidence)
    {
        var snapshot = Snapshot(snapshotId, at, ifIndex);
        var path = failing
            ? new[]
            {
                Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Failed, "connect",
                    transportError: "connection_refused", proxyEndpoint: "proxy-A", proxyIsLocal: true),
                Probe(at, ProbePath.SystemDefault, EventStatus.Ok, null, httpStatus: 204),
                Probe(at, ProbePath.NoExplicitProxy, EventStatus.Timeout, "connect", failedEndpoint: "target"),
            }
            : new[]
            {
                Probe(at, ProbePath.ExplicitHttpProxy, EventStatus.Ok, null, httpStatus: 204, proxyEndpoint: "proxy-A", proxyIsLocal: true),
                Probe(at, ProbePath.SystemDefault, EventStatus.Ok, null, httpStatus: 204),
                Probe(at, ProbePath.NoExplicitProxy, EventStatus.Ok, null, httpStatus: 204),
            };
        path = path.Select(probe => probe with { ObservationWindowId = "window-" + snapshotId }).ToArray();
        var records = new List<EvidenceRecord>();
        if (includeClientEvidence)
        {
            records.Add(new EvidenceRecord
            {
                EvidenceId = "record-" + snapshotId,
                Kind = "client_process",
                Summary = "synthetic client process",
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["snapshot_id"] = snapshotId,
                    ["client"] = "nice",
                    ["pid"] = "100",
                    ["started_at"] = T0.ToString("O"),
                },
                ObservedAt = at,
                MonotonicMs = (at - T0).TotalMilliseconds,
                BootId = Boot,
                ObservationWindowId = "window-" + snapshotId,
                Source = "synthetic.adapter",
                Provenance = "adapter_read",
                Status = EventStatus.Ok,
            });
            records.Add(new EvidenceRecord
            {
                EvidenceId = "config-" + snapshotId,
                Kind = "client_config_file",
                Summary = "synthetic client config",
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["snapshot_id"] = snapshotId,
                    ["client"] = "nice",
                    ["config_path"] = "synthetic/runtime.yaml",
                    ["config_hash"] = "synthetic-hash",
                    ["mtime"] = T0.AddDays(-1).ToString("O"),
                },
                ObservedAt = at,
                MonotonicMs = (at - T0).TotalMilliseconds,
                BootId = Boot,
                ObservationWindowId = "window-" + snapshotId,
                Source = "synthetic.adapter",
                Provenance = "disk_only",
                Status = EventStatus.Ok,
            });
        }

        var evidence = new EvidenceSet
        {
            Snapshot = snapshot,
            Probes = path,
            Records = records,
        };
        var diagnoses = DiagnosticEngine.CreateDefault(new CycleDiagnosisIds(snapshotId))
            .EvaluateDetailed(evidence).Diagnoses;
        var events = EvidenceSessionEvents.Create(evidence, diagnoses, Session, Boot, synthetic: true,
            at.AddSeconds(30), (at - T0).TotalMilliseconds + 30000, "cycle-" + snapshotId);
        return new Cycle(events, diagnoses);
    }

    private static IEnumerable<EventEnvelope> IncidentMarker(IReadOnlyList<EventEnvelope> cycleEvents,
        IReadOnlyList<Diagnosis> diagnoses)
    {
        var snapshotIds = new[] { "snapshot-t12-1", "snapshot-t12-2" };
        var record = new IncidentRecord
        {
            IncidentId = "incident-t12-1",
            Trigger = IncidentTrigger.ConsecutiveFailures,
            State = IncidentState.Closed,
            SessionId = Session,
            BootId = Boot,
            Synthetic = true,
            StartedAt = T0,
            ClosedAt = T0.AddSeconds(30),
            Window = new IncidentWindow
            {
                StartedAt = T0,
                EndedAt = T0.AddSeconds(30),
                PreWindowAvailable = true,
                PostWindowComplete = true,
            },
            EventIds = cycleEvents.Select(item => item.EventId).ToArray(),
            SnapshotIds = snapshotIds,
            DiagnosisIds = diagnoses.Select(item => item.DiagnosisId).ToArray(),
            Failures = new[]
            {
                new IncidentFailureGroup
                {
                    ClassKey = "target-t12|explicit-http-proxy|proxy-A|ipv4|GET|options",
                    TargetId = "target-t12",
                    Path = "explicit-http-proxy",
                    ProxyEndpointId = "proxy-A",
                    AddressFamily = "ipv4",
                    Method = "GET",
                    FirstAt = T0,
                    LastAt = T0,
                    Count = 3,
                },
            },
            ClosureReason = "recovery_verified_no_new_failure",
            PostWindowComplete = true,
            EffectiveFailureCount = 3,
            EffectiveSuccessCount = 3,
            LastSnapshotId = snapshotIds[^1],
        };
        var transition = new IncidentTransition
        {
            Kind = IncidentTransitionKind.StateChange,
            PreviousState = IncidentState.Recovering,
            State = IncidentState.Closed,
            Reason = "incident_closed",
            At = T0.AddSeconds(30),
            MonotonicMs = 30000,
            SessionId = Session,
            BootId = Boot,
            Synthetic = true,
            Incident = record,
        };
        return IncidentSessionEvents.Create(new[] { transition }, Session, Boot, synthetic: true,
            T0.AddSeconds(30), 30000, "incident-t12");
    }

    public static NetworkSnapshot Snapshot(string id, DateTimeOffset at, int ifIndex) => new()
    {
        SnapshotId = id,
        ObservedAt = at,
        MonotonicMs = (at - T0).TotalMilliseconds,
        Synthetic = true,
        Interfaces = Collected<IReadOnlyList<InterfaceState>>.Ok(new[]
        {
            new InterfaceState
            {
                InterfaceGuid = "guid-t12",
                Name = "Ethernet",
                IfIndex = ifIndex,
                Kind = "ethernet",
                OperationalStatus = "up",
                Metric = 25,
                AutomaticMetric = true,
                DisableDefaultRoutes = false,
            },
        }),
        Routes = Collected<IReadOnlyList<RouteEntry>>.Ok(new[]
        {
            new RouteEntry
            {
                DestinationPrefix = "0.0.0.0/0",
                Family = "ipv4",
                IfIndex = ifIndex,
                RouteMetric = 0,
                InterfaceMetric = 25,
                Protocol = "netmgmt",
            },
        }),
        Dns = Collected<DnsState>.Ok(new DnsState()),
        Proxies = Collected<ProxyState>.Ok(new ProxyState
        {
            ToolProcessEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "tool_process_environment",
                Provenance = "synthetic",
                Enabled = true,
                ProxyServer = "http://127.0.0.1:7890",
                Variables = new[] { "HTTPS_PROXY" },
            }),
            UserEnvironment = Collected<ProxyLayer>.Ok(new ProxyLayer
            {
                Layer = "user_environment",
                Provenance = "synthetic",
                Enabled = true,
                ProxyServer = "http://127.0.0.1:7890",
                Variables = new[] { "HTTPS_PROXY" },
            }),
            MachineEnvironment = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not collected"),
            WinInet = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not collected"),
            WinHttp = Collected<ProxyLayer>.Unavailable(EventStatus.Unsupported, "not collected"),
        }),
    };

    private static ProbeResult Probe(DateTimeOffset at, ProbePath path, EventStatus status, string? failedStage,
        string? transportError = null, string? failedEndpoint = null, int? httpStatus = null,
        string? proxyEndpoint = null, bool? proxyIsLocal = null)
    {
        var id = (int)(at - T0).TotalMilliseconds + "-" + path;
        return new ProbeResult
        {
            ProbeId = "probe-" + id,
            ProbeGroupId = "group-" + at.ToUnixTimeMilliseconds(),
            TargetId = "target-t12",
            Path = path,
            Status = status,
            StartedAt = at,
            StartedMonotonicMs = (at - T0).TotalMilliseconds,
            DurationMs = 50,
            HttpStatus = httpStatus,
            Classification = status == EventStatus.Ok
                ? ProbeClassification.ReachableExpected
                : ProbeClassification.TransportFailure,
            AddressFamily = "ipv4",
            Method = "GET",
            RequestOptionsKey = "options",
            ObservationWindowId = "window-" + at.ToUnixTimeMilliseconds(),
            BootId = Boot,
            ProxyEndpointId = proxyEndpoint,
            ProxyIsLocal = proxyIsLocal,
            FailedStage = failedStage,
            FailedEndpoint = failedEndpoint,
            TransportError = transportError,
            TimingComparable = true,
        };
    }

    private static IReadOnlyList<EventEnvelope> Rebase(IReadOnlyList<EventEnvelope> events)
    {
        var result = new List<EventEnvelope>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            result.Add(events[index] with { Sequence = index + 1 });
        }

        return result;
    }

    internal sealed record Cycle(IReadOnlyList<EventEnvelope> Events, IReadOnlyList<Diagnosis> Diagnoses);

    private sealed class CycleDiagnosisIds(string prefix) : IDiagnosisIdFactory
    {
        private int _next;

        public string Next(string ruleId) => prefix + "-diagnosis-" + ++_next;
    }
}

internal sealed record T12Scenario(
    IReadOnlyList<EventEnvelope> Events,
    string EventsText,
    ReplayResult Replay);

/// <summary>This build's fixed rule catalog for tests; independent of the CLI/Desktop adapter.</summary>
internal sealed class T12RuleSetEvaluator : ICaseRuleEvaluator
{
    private static readonly IReadOnlyList<IDiagnosticRule> Rules = RuleCatalog.All;

    public T12RuleSetEvaluator()
    {
        var versions = Rules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
            .Select(rule => new CaseRuleVersion(rule.RuleId, rule.Version)).ToArray();
        var canonical = string.Join(";", versions.Select(version => version.RuleId + "@" + version.Version));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        RuleSet = new CaseRuleSetInfo
        {
            RuleSetVersion = "diagnostics/" + hash[..16],
            AppVersion = "test",
            Rules = versions,
        };
    }

    public CaseRuleSetInfo RuleSet { get; }

    public DiagnosticEvaluationResult Evaluate(EvidenceSet evidence) =>
        new DiagnosticEngine(Rules).EvaluateDetailed(evidence);
}
