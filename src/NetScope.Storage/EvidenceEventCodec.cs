using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>Lossless M0 evidence payloads. This is a local typed codec, not a general incident-package importer.</summary>
public static class EvidenceEventCodec
{
    private static readonly JsonSerializerOptions Options = new(EnvelopeJson.Options)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IgnoreReadOnlyProperties = true,
    };

    public static JsonObject Snapshot(NetworkSnapshot value, string sourceEventId,
        IReadOnlyList<ProposedChange>? proposedChanges = null) => new()
    {
        ["payload_version"] = "m0-evidence-1",
        ["snapshot"] = JsonSerializer.SerializeToNode(value, Options),
        ["proposed_changes"] = JsonSerializer.SerializeToNode(proposedChanges ?? Array.Empty<ProposedChange>(), Options),
        ["evidence_metadata"] = Metadata(value.SnapshotId, sourceEventId),
    };

    public static JsonObject Probe(ProbeResult value, string sourceEventId) => new()
    {
        ["payload_version"] = "m0-evidence-1",
        ["probe_result"] = JsonSerializer.SerializeToNode(value, Options),
        ["evidence_metadata"] = Metadata(value.ProbeId, sourceEventId),
    };

    public static JsonObject Record(EvidenceRecord value, string sourceEventId) => new()
    {
        ["payload_version"] = "m0-evidence-1",
        ["evidence_record"] = JsonSerializer.SerializeToNode(value with { SourceEventIds = new[] { sourceEventId } }, Options),
        ["evidence_metadata"] = Metadata(value.EvidenceId, sourceEventId),
    };

    public static JsonObject Diagnosis(Diagnosis value) => new()
    {
        ["payload_version"] = "m0-evidence-1",
        ["diagnosis"] = JsonSerializer.SerializeToNode(value, Options),
    };

    private static JsonObject Metadata(string id, string sourceEventId) => new()
    {
        ["evidence_id"] = id,
        ["source_event_ids"] = new JsonArray(JsonValue.Create(sourceEventId)),
        ["transformation_version"] = "1",
    };

    /// <summary>
    /// Validates every typed event and keeps one evidence set per snapshot. Probes and records are
    /// scoped by their explicit snapshot reference when they have one, otherwise by the most
    /// recent preceding snapshot. They are never added to every snapshot. A diagnosis may cite
    /// evidence from more than one snapshot (cross-cycle evidence), but its own snapshot scope is
    /// still resolved explicitly and a citation to an undefined evidence id is still a problem.
    /// </summary>
    internal static DecodedEvidence Decode(IReadOnlyList<EventEnvelope> events)
    {
        var problems = new List<string>();
        var eventIds = events.Select(x => x.EventId).ToHashSet(StringComparer.Ordinal);
        var definitions = new HashSet<string>(StringComparer.Ordinal);
        var pendingSnapshots = new List<PendingSnapshot>();
        var pendingProbes = new List<PendingProbe>();
        var pendingRecords = new List<PendingRecord>();
        var pendingDiagnoses = new List<PendingDiagnosis>();
        var diagnoses = new List<Diagnosis>();
        var typed = false;

        void Define(EventEnvelope envelope, string id)
        {
            Require(!string.IsNullOrWhiteSpace(id) && envelope.Refs.EvidenceId == id, "canonical evidence id mismatch");
            var metadata = envelope.Payload["evidence_metadata"]?.AsObject() ?? throw new JsonException("evidence metadata missing");
            Require(metadata["evidence_id"]?.GetValue<string>() == id &&
                    metadata["transformation_version"]?.GetValue<string>() == "1", "evidence transformation mismatch");
            var sources = metadata["source_event_ids"]?.AsArray() ?? throw new JsonException("source events missing");
            Require(sources.Count > 0 && sources.All(x => x is JsonValue && eventIds.Contains(x.GetValue<string>())),
                "source event reference missing");
            Require(definitions.Add(id), "duplicate evidence definition");
        }

        for (var index = 0; index < events.Count; index++)
        {
            var envelope = events[index];
            if (!envelope.Payload.ContainsKey("payload_version")) continue;
            typed = true;
            try
            {
                if (envelope.Payload["payload_version"]?.GetValue<string>() != "m0-evidence-1")
                    throw new JsonException("unsupported typed evidence payload version");
                if (envelope.Payload.ContainsKey("snapshot"))
                {
                    Require(envelope.EventType == EventType.Snapshot, "snapshot event type mismatch");
                    var value = Read<NetworkSnapshot>(envelope.Payload, "snapshot");
                    ValidateDomain(value.Interfaces);
                    ValidateDomain(value.Routes);
                    ValidateDomain(value.Dns);
                    ValidateDomain(value.Proxies);
                    if (value.Proxies.Value is { } proxies)
                    {
                        ValidateDomain(proxies.ToolProcessEnvironment);
                        ValidateDomain(proxies.UserEnvironment);
                        ValidateDomain(proxies.MachineEnvironment);
                        ValidateDomain(proxies.WinInet);
                        ValidateDomain(proxies.WinHttp);
                    }
                    Require(value.SnapshotId == envelope.Refs.SnapshotId && value.Synthetic == envelope.Synthetic,
                        "snapshot identity or synthetic marker mismatch");
                    Require(value.ObservedAt == envelope.ObservedAt && value.MonotonicMs == envelope.MonotonicMs,
                        "snapshot observation time mismatch");
                    Define(envelope, value.SnapshotId);
                    var changes = Read<List<ProposedChange>>(envelope.Payload, "proposed_changes");
                    pendingSnapshots.Add(new PendingSnapshot(index, envelope, value, changes));
                }
                else if (envelope.Payload.ContainsKey("probe_result"))
                {
                    Require(envelope.EventType == EventType.ProbeResult, "probe event type mismatch");
                    var value = Read<ProbeResult>(envelope.Payload, "probe_result");
                    Require(value.ProbeId == envelope.Refs.ProbeId && value.ProbeGroupId == envelope.Refs.ProbeGroupId,
                        "probe identity mismatch");
                    Require(value.Status == envelope.Status && value.StartedAt == envelope.ObservedAt &&
                            value.StartedMonotonicMs == envelope.MonotonicMs && value.BootId == envelope.BootId,
                        "probe status or observation time mismatch");
                    Require(double.IsFinite(value.DurationMs) && value.DurationMs >= 0 && value.ResolvedAddresses is not null,
                        "invalid probe duration or addresses");
                    Define(envelope, value.ProbeId);
                    pendingProbes.Add(new PendingProbe(index, envelope, value));
                }
                else if (envelope.Payload.ContainsKey("evidence_record"))
                {
                    var value = Read<EvidenceRecord>(envelope.Payload, "evidence_record");
                    Require(value.ObservedAt == envelope.ObservedAt && value.MonotonicMs == envelope.MonotonicMs &&
                            value.BootId == envelope.BootId && value.Status == envelope.Status,
                        "evidence record status or observation time mismatch");
                    Require(!string.IsNullOrWhiteSpace(value.Kind) && value.Facts is not null && value.SourceEventIds is not null,
                        "incomplete evidence record");
                    Require(value.SourceEventIds!.Count > 0 && value.SourceEventIds.All(eventIds.Contains), "record source event missing");
                    Define(envelope, value.EvidenceId);
                    pendingRecords.Add(new PendingRecord(index, envelope, value));
                }
                else if (envelope.Payload.ContainsKey("diagnosis"))
                {
                    Require(envelope.EventType == EventType.Diagnosis, "diagnosis event type mismatch");
                    var value = Read<Diagnosis>(envelope.Payload, "diagnosis");
                    Require(value.DiagnosisId == envelope.Refs.DiagnosisId && value.Scope is not null,
                        "diagnosis identity or scope mismatch");
                    Require(value.AllowedRepairActions is not null && value.AllowedRepairActions.Count == 0,
                        "M0 evidence cannot authorize repair actions");
                    Require(value.EvidenceIds is not null && value.EvidenceIds.Count > 0 && value.CounterEvidenceIds is not null,
                        "diagnosis evidence references are absent");
                    diagnoses.Add(value);
                    pendingDiagnoses.Add(new PendingDiagnosis(index, envelope, value));
                }
                else throw new JsonException("unknown typed evidence payload");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
            {
                problems.Add($"event {envelope.EventId}: {exception.Message}");
            }
        }

        foreach (var diagnosis in diagnoses)
        {
            foreach (var id in diagnosis.EvidenceIds.Concat(diagnosis.CounterEvidenceIds))
                if (string.IsNullOrWhiteSpace(id) || !definitions.Contains(id))
                    problems.Add($"diagnosis {diagnosis.DiagnosisId}: missing evidence reference {id}");
        }

        var scopes = pendingSnapshots
            .Select(snapshot => new ScopeBuilder(snapshot))
            .ToList();
        var scopeBySnapshotId = new Dictionary<string, ScopeBuilder>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (!scopeBySnapshotId.TryAdd(scope.Snapshot.Snapshot.SnapshotId, scope))
                problems.Add($"snapshot {scope.Snapshot.Snapshot.SnapshotId}: duplicate snapshot scope");
        }

        var unscopedProbes = new List<ProbeResult>();
        var unscopedRecords = new List<EvidenceRecord>();
        foreach (var probe in pendingProbes)
        {
            var snapshotId = probe.Envelope.Refs.SnapshotId;
            if (snapshotId is not null)
            {
                if (!scopeBySnapshotId.TryGetValue(snapshotId, out var scope))
                {
                    problems.Add($"probe {probe.Probe.ProbeId}: references unknown snapshot {snapshotId}");
                    continue;
                }

                scope.Probes.Add(probe.Probe);
                continue;
            }

            var preceding = PrecedingSnapshot(pendingSnapshots, probe.Position);
            if (preceding is not null) scopeBySnapshotId[preceding.Snapshot.SnapshotId].Probes.Add(probe.Probe);
            else unscopedProbes.Add(probe.Probe);
        }

        foreach (var record in pendingRecords)
        {
            var snapshotId = record.Envelope.Refs.SnapshotId;
            if (snapshotId is null && record.Record.Facts.TryGetValue("snapshot_id", out var factSnapshotId))
            {
                if (string.IsNullOrWhiteSpace(factSnapshotId))
                {
                    problems.Add($"record {record.Record.EvidenceId}: empty snapshot reference");
                    continue;
                }

                snapshotId = factSnapshotId;
            }

            if (snapshotId is not null)
            {
                if (!scopeBySnapshotId.TryGetValue(snapshotId, out var scope))
                {
                    problems.Add($"record {record.Record.EvidenceId}: references unknown snapshot {snapshotId}");
                    continue;
                }

                scope.Records.Add(record.Record);
                continue;
            }

            var preceding = PrecedingSnapshot(pendingSnapshots, record.Position);
            if (preceding is not null) scopeBySnapshotId[preceding.Snapshot.SnapshotId].Records.Add(record.Record);
            else unscopedRecords.Add(record.Record);
        }

        foreach (var diagnosis in pendingDiagnoses)
        {
            var snapshotId = diagnosis.Envelope.Refs.SnapshotId;
            if (snapshotId is not null)
            {
                if (!scopeBySnapshotId.TryGetValue(snapshotId, out var scope))
                {
                    problems.Add($"diagnosis {diagnosis.Diagnosis.DiagnosisId}: references unknown snapshot {snapshotId}");
                    continue;
                }

                scope.Diagnoses.Add(diagnosis.Diagnosis);
                continue;
            }

            var preceding = PrecedingSnapshot(pendingSnapshots, diagnosis.Position);
            if (preceding is not null)
                scopeBySnapshotId[preceding.Snapshot.SnapshotId].Diagnoses.Add(diagnosis.Diagnosis);
        }

        var snapshotScopes = scopes.Select(scope => new SnapshotEvidenceScope
        {
            SnapshotEventId = scope.Snapshot.Envelope.EventId,
            Sequence = scope.Snapshot.Envelope.Sequence,
            Snapshot = scope.Snapshot.Snapshot,
            Evidence = new EvidenceSet
            {
                Snapshot = scope.Snapshot.Snapshot,
                Probes = scope.Probes.ToArray(),
                Records = scope.Records.ToArray(),
                ProposedChanges = scope.Snapshot.ProposedChanges,
            },
            Diagnoses = scope.Diagnoses.ToArray(),
        }).ToArray();

        EvidenceSet? unscoped = null;
        if (unscopedProbes.Count > 0 || unscopedRecords.Count > 0 || (typed && snapshotScopes.Length == 0))
        {
            unscoped = new EvidenceSet
            {
                Snapshot = null,
                Probes = unscopedProbes.ToArray(),
                Records = unscopedRecords.ToArray(),
            };
        }

        var evidence = typed
            ? snapshotScopes.Length > 0 ? snapshotScopes[^1].Evidence : unscoped
            : null;

        return new DecodedEvidence(evidence, diagnoses, problems, snapshotScopes, unscoped);
    }

    private static PendingSnapshot? PrecedingSnapshot(IReadOnlyList<PendingSnapshot> snapshots, int position)
    {
        PendingSnapshot? preceding = null;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Position >= position) break;
            preceding = snapshot;
        }

        return preceding;
    }

    private static T Read<T>(JsonObject payload, string key) where T : class =>
        payload[key]?.Deserialize<T>(Options) ?? throw new JsonException("missing typed payload member: " + key);

    private static void ValidateDomain<T>(Collected<T>? value) where T : class
    {
        var domain = value ?? throw new JsonException("snapshot domain is absent");
        Require(domain.Status.ProducedData() ? domain.Value is not null : domain.Value is null,
            "snapshot domain status and available data disagree");
        if (domain.Status != EventStatus.Ok)
            Require(domain.Reason is { } reason && reason.Trim().Length > 0, "incomplete snapshot domain has no reason");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new JsonException(message);
    }

    private sealed record PendingSnapshot(int Position, EventEnvelope Envelope, NetworkSnapshot Snapshot,
        IReadOnlyList<ProposedChange> ProposedChanges);

    private sealed record PendingProbe(int Position, EventEnvelope Envelope, ProbeResult Probe);

    private sealed record PendingRecord(int Position, EventEnvelope Envelope, EvidenceRecord Record);

    private sealed record PendingDiagnosis(int Position, EventEnvelope Envelope, Diagnosis Diagnosis);

    private sealed class ScopeBuilder
    {
        public ScopeBuilder(PendingSnapshot snapshot) => Snapshot = snapshot;

        public PendingSnapshot Snapshot { get; }

        public List<ProbeResult> Probes { get; } = new();

        public List<EvidenceRecord> Records { get; } = new();

        public List<Diagnosis> Diagnoses { get; } = new();
    }
}

internal sealed record DecodedEvidence(EvidenceSet? Evidence, IReadOnlyList<Diagnosis> Diagnoses,
    IReadOnlyList<string> Problems, IReadOnlyList<SnapshotEvidenceScope> SnapshotScopes,
    EvidenceSet? UnscopedEvidence);
