using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Storage;

public enum StorageItemKind
{
    SessionLog,
    SessionIndex,
    SessionSidecar,
    Quarantine,
    ExportPackage,
    ApplicationLog,
    PinnedIncident,
    Journal,
    Other,
}

/// <summary>One stored file as retention sees it: size, age, protection flags and evidence facts.</summary>
public sealed record StorageItem
{
    public required string Path { get; init; }

    public required StorageItemKind Kind { get; init; }

    public string? SessionId { get; init; }

    public required long Bytes { get; init; }

    public required DateTimeOffset LastWriteTimeUtc { get; init; }

    public bool Pinned { get; init; }

    public bool PendingJournal { get; init; }

    public bool ActiveIncident { get; init; }

    public bool FailureBearing { get; init; }

    public int EventCount { get; init; }

    public int FailureCount { get; init; }

    public int GapEventCount { get; init; }

    public int SequenceGapCount { get; init; }

    public int ProblemLineCount { get; init; }

    public int PartialCount { get; init; }

    public DateTimeOffset? FirstFailureAt { get; init; }

    public DateTimeOffset? LastFailureAt { get; init; }

    public IReadOnlyDictionary<string, int> FailureCountsByStatus { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Aggregate/sealed/quarantine sidecars are small and are never removed by quota cleanup.</summary>
    public bool Protected { get; init; }

    public TimeSpan Age(DateTimeOffset nowUtc) =>
        LastWriteTimeUtc >= nowUtc ? TimeSpan.Zero : nowUtc - LastWriteTimeUtc;
}

/// <summary>Current usage and the threshold evaluation, with no side effects.</summary>
public sealed record QuotaInspection
{
    public required DateTimeOffset InspectedAt { get; init; }

    public required RetentionEvaluation Evaluation { get; init; }

    public required IReadOnlyList<StorageItem> Items { get; init; }

    public required long NormalBytes { get; init; }

    public required long PinnedBytes { get; init; }

    public required long DailyBytes { get; init; }

    public required bool ClockRollbackSuspected { get; init; }

    public required IReadOnlyList<string> Observations { get; init; }

    public long TotalBytes => Items.Sum(item => item.Bytes);
}

public enum QuotaActionKind
{
    SealActiveIncident,
    DeleteExpiredDetail,
    DeleteOverLimitDetail,
    WarnThreshold,
    WarnDailyBudget,
}

/// <summary>A planned retention step. Nothing is executed until <see cref="QuotaManager.Enforce"/>.</summary>
public sealed record QuotaAction
{
    public required QuotaActionKind Kind { get; init; }

    public required string Path { get; init; }

    public required long Bytes { get; init; }

    public required string Reason { get; init; }

    /// <summary>Aggregate or sealed sidecar that must be written before a detail file is removed.</summary>
    public string? OutputPath { get; init; }

    public bool PreserveAggregate { get; init; }
}

public sealed record QuotaPlan
{
    public required QuotaInspection Inspection { get; init; }

    public required IReadOnlyList<QuotaAction> Actions { get; init; }

    public required RetentionDirective Directive { get; init; }

    public required bool Blocked { get; init; }

    public required IReadOnlyList<string> BlockedReasons { get; init; }

    public required long PlannedReclaimBytes { get; init; }

    public required long ProtectedBytes { get; init; }
}

public sealed record QuotaActionResult
{
    public required QuotaAction Action { get; init; }

    public required bool Succeeded { get; init; }

    public string? FailureReason { get; init; }

    public string? OutputPath { get; init; }
}

public sealed record QuotaEnforcementResult
{
    public required QuotaPlan Plan { get; init; }

    public required QuotaInspection After { get; init; }

    public required IReadOnlyList<QuotaActionResult> Results { get; init; }

    public required bool Degraded { get; init; }

    public required IReadOnlyList<string> NotPersisted { get; init; }

    public required bool ClockRollbackSuspected { get; init; }

    public required string Summary { get; init; }
}

/// <summary>Admission answer for a new pinned incident. Reaching the pinned cap never unpins old cases.</summary>
public sealed record PinAdmission
{
    public required bool Accepted { get; init; }

    public required string Reason { get; init; }

    public required long RequestedBytes { get; init; }

    public required long PinnedBytes { get; init; }

    public required long PinnedLimit { get; init; }
}

public sealed record PinnedRemovalResult
{
    public required string IncidentId { get; init; }

    public required bool Removed { get; init; }

    public required long RemovedBytes { get; init; }

    public required int RemovedFiles { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Broad local-storage write status across the documented folders.</summary>
public sealed record StoragePersistenceStatus
{
    public required bool SessionsWritable { get; init; }

    public required bool IndexWritable { get; init; }

    public required bool ExportsWritable { get; init; }

    public required bool LogsWritable { get; init; }

    public required bool PinnedWritable { get; init; }

    public required IReadOnlyList<UnpersistedRange> Unpersisted { get; init; }

    public required string Message { get; init; }

    public bool CanRetainFailureCounts => SessionsWritable;

    public bool IsLossless => SessionsWritable && IndexWritable && ExportsWritable && LogsWritable
        && PinnedWritable && Unpersisted.Count == 0;
}

/// <summary>
/// The minimal quota service for design 04 §4. It only plans and applies local retention:
/// ordinary logs 7 days / 500 MB, pinned incidents a separate 1 GB, 80/90/100 % levels, 100 MB/day
/// as a warning only. Expired un-pinned success detail is removed first; failure-bearing detail is
/// replaced by a small aggregate before removal; active incidents are sealed and protected; pinned
/// incidents and pending journals are never deleted by normal cleanup. Nothing here fills a disk,
/// changes the system clock or deletes an exported package unless the policy explicitly includes it.
/// </summary>
public sealed class QuotaManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly IStorageFileSystem _files;
    private readonly IClock _clock;
    private readonly RetentionPolicy _policy;
    private readonly StorageLayout _layout;

    public QuotaManager(StorageLayout layout, RetentionPolicy? policy = null,
        IStorageFileSystem? files = null, IClock? clock = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _policy = policy ?? RetentionPolicy.Default;
        _policy.Validate();
        _files = files ?? StorageFileSystem.Instance;
        _clock = clock ?? new SystemClock();
    }

    public RetentionPolicy Policy => _policy;

    public QuotaInspection Inspect()
    {
        var now = _clock.UtcNow;
        var observations = new List<string>();
        var clockRollback = false;
        var items = new List<StorageItem>();

        ScanSessions(now, items, observations, ref clockRollback);
        ScanExports(now, items, ref clockRollback);
        ScanLogs(now, items, ref clockRollback);
        ScanPinned(now, items, observations, ref clockRollback);
        ScanJournal(items, observations);

        var normalBytes = items.Where(item => !item.Pinned && !item.PendingJournal
                && (item.Kind != StorageItemKind.ExportPackage || _policy.IncludeExportsInNormalQuota))
            .Sum(item => item.Bytes);
        var pinnedBytes = items.Where(item => item.Pinned).Sum(item => item.Bytes);
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var dailyBytes = items.Where(item => !item.PendingJournal
                && (item.Kind != StorageItemKind.ExportPackage || _policy.IncludeExportsInNormalQuota))
            .Where(item => item.LastWriteTimeUtc >= dayStart && item.LastWriteTimeUtc <= now)
            .Sum(item => item.Bytes);

        var evaluation = _policy.Evaluate(normalBytes, pinnedBytes, dailyBytes, now, clockRollback);
        foreach (var warning in evaluation.Warnings) observations.Add(warning);

        return new QuotaInspection
        {
            InspectedAt = now,
            Evaluation = evaluation,
            Items = items,
            NormalBytes = normalBytes,
            PinnedBytes = pinnedBytes,
            DailyBytes = dailyBytes,
            ClockRollbackSuspected = clockRollback,
            Observations = observations,
        };
    }

    /// <summary>Plans retention without changing a file. Deletion order is success-detail first, then failure aggregate.</summary>
    public QuotaPlan Plan(QuotaInspection? inspection = null)
    {
        inspection ??= Inspect();
        var now = inspection.InspectedAt;
        var actions = new List<QuotaAction>();
        var blocked = new List<string>();
        long protectedBytes = 0;
        long plannedReclaim = 0;
        var remainingNormal = inspection.NormalBytes;

        var protectedItems = new List<StorageItem>();
        foreach (var item in inspection.Items)
        {
            var isProtected = item.Pinned || item.PendingJournal || item.Protected || item.ActiveIncident;
            if (isProtected) protectedItems.Add(item);
        }

        // Active incidents are sealed before any cleanup so their failure totals survive even if
        // their detail later has to be removed. The active detail itself is never deleted.
        if (inspection.Evaluation.Level >= QuotaLevel.Warning)
        {
            foreach (var item in inspection.Items.Where(item => item.ActiveIncident && !item.Pinned))
            {
                var output = SealedPathFor(item);
                if (_files.FileExists(output)) continue;
                actions.Add(new QuotaAction
                {
                    Kind = QuotaActionKind.SealActiveIncident,
                    Path = item.Path,
                    Bytes = item.Bytes,
                    Reason = "Active incident is sealed before quota cleanup so failure totals, first/last times and gaps survive.",
                    OutputPath = output,
                });
                protectedBytes += item.Bytes;
            }
        }

        foreach (var item in protectedItems)
        {
            if (item.ActiveIncident || item.Pinned || item.PendingJournal || item.Protected)
                protectedBytes += item.Bytes;
        }

        // Expired un-pinned detail, success detail first and oldest first.
        var expired = inspection.Items
            .Where(item => IsDeletable(item) && item.Age(now) >= _policy.NormalRetention)
            .OrderBy(item => item.FailureBearing)
            .ThenBy(item => item.LastWriteTimeUtc)
            .ToArray();
        foreach (var item in expired)
        {
            actions.Add(DeleteAction(item, QuotaActionKind.DeleteExpiredDetail,
                "Expired un-pinned detail past the retention period (success detail before failure evidence)."));
            plannedReclaim += item.Bytes;
            remainingNormal -= item.Bytes;
        }

        // Still above the hard normal limit: remove additional un-pinned detail, oldest first.
        if (remainingNormal >= _policy.NormalMaxBytes)
        {
            var overLimit = inspection.Items
                .Where(IsDeletable)
                .Where(item => actions.All(action => action.Path != item.Path))
                .OrderBy(item => item.FailureBearing)
                .ThenBy(item => item.LastWriteTimeUtc)
                .ToArray();
            foreach (var item in overLimit)
            {
                if (remainingNormal < _policy.NormalMaxBytes) break;
                actions.Add(DeleteAction(item, QuotaActionKind.DeleteOverLimitDetail,
                    "Removed to bring the ordinary log pool back under the 500 MB hard limit; failure counts are preserved in an aggregate first."));
                plannedReclaim += item.Bytes;
                remainingNormal -= item.Bytes;
            }
        }

        if (remainingNormal >= _policy.NormalMaxBytes)
        {
            blocked.Add($"Ordinary log pool is still {remainingNormal} bytes (hard limit {_policy.NormalMaxBytes}) after removing all deletable detail. Active incidents, pinned cases, sidecars and pending journals are protected; user cleanup is required.");
        }

        if (inspection.Evaluation.OverPinnedLimit)
        {
            blocked.Add($"Pinned incident pool is at or above its {_policy.PinnedMaxBytes}-byte limit. New pins are refused; existing pinned cases are never unpinned automatically.");
        }

        if (inspection.Evaluation.DailyBudgetExceeded)
        {
            actions.Add(new QuotaAction
            {
                Kind = QuotaActionKind.WarnDailyBudget,
                Path = _layout.Root,
                Bytes = inspection.DailyBytes,
                Reason = "Daily write volume is above the 100 MB performance target. This is a warning only and never deletes data by itself.",
            });
        }

        if (inspection.Evaluation.Level != QuotaLevel.Normal)
        {
            actions.Add(new QuotaAction
            {
                Kind = QuotaActionKind.WarnThreshold,
                Path = _layout.Root,
                Bytes = inspection.NormalBytes,
                Reason = inspection.Evaluation.Directive.Description,
            });
        }

        return new QuotaPlan
        {
            Inspection = inspection,
            Actions = actions,
            Directive = inspection.Evaluation.Directive,
            Blocked = blocked.Count > 0,
            BlockedReasons = blocked,
            PlannedReclaimBytes = plannedReclaim,
            ProtectedBytes = protectedBytes,
        };
    }

    /// <summary>Applies a plan. Every partial failure is reported; the source of truth is never silently rewritten.</summary>
    public QuotaEnforcementResult Enforce(QuotaInspection? inspection = null)
    {
        var plan = Plan(inspection);
        var results = new List<QuotaActionResult>();
        var notPersisted = new List<string>();

        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case QuotaActionKind.WarnThreshold:
                case QuotaActionKind.WarnDailyBudget:
                    results.Add(new QuotaActionResult { Action = action, Succeeded = true });
                    break;
                case QuotaActionKind.SealActiveIncident:
                case QuotaActionKind.DeleteExpiredDetail:
                case QuotaActionKind.DeleteOverLimitDetail:
                    results.Add(ApplyDeleteAction(plan, action, notPersisted));
                    break;
                default:
                    results.Add(new QuotaActionResult
                    {
                        Action = action,
                        Succeeded = false,
                        FailureReason = "unknown quota action",
                    });
                    break;
            }
        }

        var after = Inspect();
        var failed = results.Count(result => !result.Succeeded);
        var degraded = after.Evaluation.OverNormalLimit || failed > 0 || notPersisted.Count > 0;
        var summary = BuildSummary(plan, after, results, notPersisted);
        return new QuotaEnforcementResult
        {
            Plan = plan,
            After = after,
            Results = results,
            Degraded = degraded,
            NotPersisted = notPersisted,
            ClockRollbackSuspected = after.ClockRollbackSuspected,
            Summary = summary,
        };
    }

    /// <summary>Decides whether a new incident may be pinned. At the pinned cap the new pin is refused, never an old one unpinned.</summary>
    public PinAdmission EvaluatePinRequest(long requestedBytes)
    {
        var pinnedBytes = Inspect().PinnedBytes;
        if (requestedBytes <= 0)
        {
            return Refuse("Pinned incident size must be positive; nothing was pinned.", requestedBytes, pinnedBytes);
        }

        if (pinnedBytes >= _policy.PinnedMaxBytes)
        {
            return Refuse("Pinned incident pool is already at its 1 GB limit. The new pin is refused; existing pinned cases stay pinned.",
                requestedBytes, pinnedBytes);
        }

        if (requestedBytes > _policy.PinnedMaxBytes - pinnedBytes)
        {
            return Refuse("Pinned incident would exceed the independent 1 GB limit. The new pin is refused; existing pinned cases stay pinned.",
                requestedBytes, pinnedBytes);
        }

        return new PinAdmission
        {
            Accepted = true,
            Reason = "Pinned incident fits in the independent pinned pool.",
            RequestedBytes = requestedBytes,
            PinnedBytes = pinnedBytes,
            PinnedLimit = _policy.PinnedMaxBytes,
        };
    }

    /// <summary>Explicit user cleanup entry for pinned cases. There is no automatic unpin path.</summary>
    public PinnedRemovalResult RemovePinned(string incidentId)
    {
        if (string.IsNullOrWhiteSpace(incidentId)) throw new ArgumentException("An incident id is required.", nameof(incidentId));
        var entries = _files.EnumerateFiles(_layout.PinnedDirectory, "*")
            .Where(entry => Path.GetFileName(entry.Path).StartsWith(incidentId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0)
        {
            return new PinnedRemovalResult
            {
                IncidentId = incidentId,
                Removed = false,
                RemovedBytes = 0,
                RemovedFiles = 0,
                Reason = "No pinned file matched this incident id; nothing was removed.",
            };
        }

        long bytes = 0;
        var removed = 0;
        foreach (var entry in entries)
        {
            try
            {
                _files.Delete(entry.Path);
                bytes += entry.Bytes;
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or NotSupportedException)
            {
                // Keep removing the others; report honestly below.
            }
        }

        return new PinnedRemovalResult
        {
            IncidentId = incidentId,
            Removed = removed > 0,
            RemovedBytes = bytes,
            RemovedFiles = removed,
            Reason = removed == entries.Length
                ? "Pinned incident files removed by explicit user cleanup."
                : $"Only {removed} of {entries.Length} pinned file(s) could be removed; the rest remain pinned.",
        };
    }

    /// <summary>Reports which folders are currently writable and therefore which scopes are not persisted.</summary>
    public StoragePersistenceStatus CheckPersistence()
    {
        var sessions = _files.ProbeWritable(_layout.SessionsDirectory);
        var index = _files.ProbeWritable(_layout.SessionsDirectory);
        var exports = _files.ProbeWritable(_layout.ExportsDirectory);
        var logs = _files.ProbeWritable(_layout.LogsDirectory);
        var pinned = _files.ProbeWritable(_layout.PinnedDirectory);
        var unpersisted = new List<UnpersistedRange>();

        if (!sessions)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "session events, failure counts and sequence-gap markers",
                Path = _layout.SessionsDirectory,
                FirstSequence = null,
                LastSequence = null,
                Reason = "the session directory is not writable",
                FailureCountsRetained = false,
            });
        }

        if (!index)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "SQLite query projection and cursor",
                Path = _layout.SessionsDirectory,
                FirstSequence = null,
                LastSequence = null,
                Reason = "the index directory is not writable",
                FailureCountsRetained = sessions,
            });
        }

        if (!exports)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "incident export package",
                Path = _layout.ExportsDirectory,
                FirstSequence = null,
                LastSequence = null,
                Reason = "the export directory is not writable",
                FailureCountsRetained = sessions,
            });
        }

        if (!logs)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "application run log",
                Path = _layout.LogsDirectory,
                FirstSequence = null,
                LastSequence = null,
                Reason = "the run-log directory is not writable",
                FailureCountsRetained = sessions,
            });
        }

        if (!pinned)
        {
            unpersisted.Add(new UnpersistedRange
            {
                Scope = "new pinned incident copy",
                Path = _layout.PinnedDirectory,
                FirstSequence = null,
                LastSequence = null,
                Reason = "the pinned directory is not writable",
                FailureCountsRetained = sessions,
            });
        }

        var message = unpersisted.Count == 0
            ? "Session log, projection, exports, run log and pinned storage are writable."
            : sessions
                ? "Session events are writable, but one or more secondary scopes are not. Events are retained; the listed scopes are explicitly not persisted."
                : "Log write failed. This cycle's session events, failure counts and gap markers were NOT persisted. Do not claim storage is lossless.";

        return new StoragePersistenceStatus
        {
            SessionsWritable = sessions,
            IndexWritable = index,
            ExportsWritable = exports,
            LogsWritable = logs,
            PinnedWritable = pinned,
            Unpersisted = unpersisted,
            Message = message,
        };
    }

    private void ScanSessions(DateTimeOffset now, List<StorageItem> items, List<string> observations,
        ref bool clockRollback)
    {
        var factsBySession = new Dictionary<string, SessionEvidenceFacts?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _files.EnumerateFiles(_layout.SessionsDirectory, "*.ndjson"))
        {
            var sessionId = StorageLayout.SessionIdOf(entry.Path);
            var facts = TryReadFacts(entry.Path, observations, sessionId);
            factsBySession[sessionId] = facts;
            RegisterTimestamp(now, entry, ref clockRollback);
            items.Add(Item(entry, StorageItemKind.SessionLog, sessionId, facts,
                active: facts?.HasActiveIncident ?? true, protectedItem: false));
        }

        foreach (var pattern in new[] { "*.db", "*.db-wal", "*.db-shm" })
        {
            foreach (var entry in _files.EnumerateFiles(_layout.SessionsDirectory, pattern))
            {
                var sessionId = Path.GetFileName(entry.Path).Split('.')[0];
                factsBySession.TryGetValue(sessionId, out var facts);
                RegisterTimestamp(now, entry, ref clockRollback);
                items.Add(Item(entry, StorageItemKind.SessionIndex, sessionId, facts,
                    active: facts?.HasActiveIncident ?? false, protectedItem: false));
            }
        }

        foreach (var entry in _files.EnumerateFiles(_layout.SessionsDirectory, "*.json"))
        {
            var sessionId = StorageLayout.SessionIdOf(entry.Path);
            RegisterTimestamp(now, entry, ref clockRollback);
            items.Add(Item(entry, StorageItemKind.SessionSidecar, sessionId, null,
                active: false, protectedItem: true));
        }

        foreach (var entry in _files.EnumerateFiles(_layout.SessionsDirectory, "*.quarantine"))
        {
            var sessionId = StorageLayout.SessionIdOf(entry.Path);
            RegisterTimestamp(now, entry, ref clockRollback);
            items.Add(Item(entry, StorageItemKind.Quarantine, sessionId, null,
                active: false, protectedItem: true));
        }
    }

    private void ScanExports(DateTimeOffset now, List<StorageItem> items, ref bool clockRollback)
    {
        foreach (var entry in _files.EnumerateFiles(_layout.ExportsDirectory, "*.zip"))
        {
            RegisterTimestamp(now, entry, ref clockRollback);
            items.Add(Item(entry, StorageItemKind.ExportPackage, null, null, active: false,
                protectedItem: !_policy.IncludeExportsInNormalQuota));
        }
    }

    private void ScanLogs(DateTimeOffset now, List<StorageItem> items, ref bool clockRollback)
    {
        foreach (var entry in _files.EnumerateFiles(_layout.LogsDirectory, "*.log"))
        {
            RegisterTimestamp(now, entry, ref clockRollback);
            items.Add(Item(entry, StorageItemKind.ApplicationLog, null, null, active: false, protectedItem: false));
        }
    }

    private void ScanPinned(DateTimeOffset now, List<StorageItem> items, List<string> observations,
        ref bool clockRollback)
    {
        foreach (var entry in _files.EnumerateFiles(_layout.PinnedDirectory, "*"))
        {
            RegisterTimestamp(now, entry, ref clockRollback);
            items.Add(Item(entry, StorageItemKind.PinnedIncident, null, null, active: false,
                protectedItem: true, pinned: true));
        }
    }

    private void ScanJournal(List<StorageItem> items, List<string> observations)
    {
        var entries = _files.EnumerateFiles(_layout.JournalDirectory, "*");
        if (entries.Count > 0)
        {
            observations.Add($"{entries.Count} pending repair-journal file(s) are excluded from normal quota cleanup; resolve recovery before any explicit journal cleanup.");
        }

        foreach (var entry in entries)
        {
            items.Add(Item(entry, StorageItemKind.Journal, null, null, active: false,
                protectedItem: true, pendingJournal: true));
        }
    }

    private SessionEvidenceFacts? TryReadFacts(string path, List<string> observations, string sessionId)
    {
        try
        {
            using var stream = _files.OpenRead(path);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(memory.ToArray());
            return SessionLogFacts.Read(EventLogParser.Parse(text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or System.Security.SecurityException
                                              or ArgumentException or DecoderFallbackException)
        {
            observations.Add($"Session '{sessionId}' could not be inspected ({exception.GetType().Name}); its detail is treated as evidence-bearing and will not be deleted silently.");
            return null;
        }
    }

    private static void RegisterTimestamp(DateTimeOffset now, StorageFileEntry entry, ref bool clockRollback)
    {
        if (entry.LastWriteTimeUtc > now + TimeSpan.FromMinutes(1)) clockRollback = true;
    }

    private static StorageItem Item(StorageFileEntry entry, StorageItemKind kind, string? sessionId,
        SessionEvidenceFacts? facts, bool active, bool protectedItem, bool pinned = false,
        bool pendingJournal = false) => new()
    {
        Path = entry.Path,
        Kind = kind,
        SessionId = sessionId,
        Bytes = entry.Bytes,
        LastWriteTimeUtc = entry.LastWriteTimeUtc,
        Pinned = pinned,
        PendingJournal = pendingJournal,
        ActiveIncident = active || facts?.HasActiveIncident == true,
        FailureBearing = facts?.HasEvidence ?? !protectedItem,
        EventCount = facts?.EventCount ?? 0,
        FailureCount = facts?.FailureCount ?? 0,
        GapEventCount = facts?.GapEventCount ?? 0,
        SequenceGapCount = facts?.SequenceGapCount ?? 0,
        ProblemLineCount = facts?.ProblemLineCount ?? 0,
        PartialCount = facts?.PartialCount ?? 0,
        FirstFailureAt = facts?.FirstFailureAt,
        LastFailureAt = facts?.LastFailureAt,
        FailureCountsByStatus = facts?.FailureCountsByStatus ?? new Dictionary<string, int>(),
        Protected = protectedItem,
    };

    private bool IsDeletable(StorageItem item)
    {
        if (item.Pinned || item.PendingJournal || item.Protected || item.ActiveIncident) return false;
        if (item.Kind is StorageItemKind.SessionLog or StorageItemKind.SessionIndex or StorageItemKind.ApplicationLog)
            return true;
        return _policy.IncludeExportsInNormalQuota && item.Kind == StorageItemKind.ExportPackage;
    }

    private QuotaAction DeleteAction(StorageItem item, QuotaActionKind kind, string reason)
    {
        var preserveAggregate = item.FailureBearing
            && item.Kind is StorageItemKind.SessionLog or StorageItemKind.SessionIndex;
        return new QuotaAction
        {
            Kind = kind,
            Path = item.Path,
            Bytes = item.Bytes,
            Reason = reason,
            OutputPath = preserveAggregate ? AggregatePathFor(item) : null,
            PreserveAggregate = preserveAggregate,
        };
    }

    private string AggregatePathFor(StorageItem item)
    {
        var sessionPath = item.Path;
        if (item.Kind == StorageItemKind.SessionIndex)
            sessionPath = Path.ChangeExtension(item.Path, ".ndjson");
        return StorageLayout.AggregatePathFor(sessionPath);
    }

    private string SealedPathFor(StorageItem item)
    {
        var sessionPath = item.Path;
        if (item.Kind == StorageItemKind.SessionIndex)
            sessionPath = Path.ChangeExtension(item.Path, ".ndjson");
        return StorageLayout.SealedPathFor(sessionPath);
    }

    private QuotaActionResult ApplyDeleteAction(QuotaPlan plan, QuotaAction action, List<string> notPersisted)
    {
        var source = plan.Inspection.Items.FirstOrDefault(item => item.Path == action.Path) ?? PlanItem(action);
        if (action.PreserveAggregate && action.OutputPath is { } aggregatePath)
        {
            if (!TryWriteAggregate(source, aggregatePath, "failure_aggregate", action.Reason, @sealed: false,
                    notPersisted))
            {
                return new QuotaActionResult
                {
                    Action = action,
                    Succeeded = false,
                    FailureReason = "Aggregate could not be written; detail was kept rather than deleted without its failure counts.",
                };
            }
        }

        if (action.Kind == QuotaActionKind.SealActiveIncident && action.OutputPath is { } sealedPath)
        {
            if (!TryWriteAggregate(source, sealedPath, "sealed_incident", action.Reason, @sealed: true, notPersisted))
            {
                return new QuotaActionResult
                {
                    Action = action,
                    Succeeded = false,
                    FailureReason = "Sealed incident summary could not be written.",
                };
            }
        }

        if (action.Kind is QuotaActionKind.DeleteExpiredDetail or QuotaActionKind.DeleteOverLimitDetail)
        {
            try
            {
                _files.Delete(action.Path);
                return new QuotaActionResult
                {
                    Action = action,
                    Succeeded = true,
                    OutputPath = action.OutputPath,
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or NotSupportedException or System.Security.SecurityException)
            {
                notPersisted.Add("retention delete of " + Path.GetFileName(action.Path) + " (" + exception.GetType().Name + ")");
                return new QuotaActionResult
                {
                    Action = action,
                    Succeeded = false,
                    FailureReason = "Delete failed: " + exception.GetType().Name + ". The file was kept.",
                };
            }
        }

        return new QuotaActionResult { Action = action, Succeeded = true, OutputPath = action.OutputPath };
    }

    private StorageItem PlanItem(QuotaAction action) => new()
    {
        Path = action.Path,
        Kind = StorageItemKind.Other,
        Bytes = action.Bytes,
        LastWriteTimeUtc = _clock.UtcNow,
        FailureBearing = action.PreserveAggregate,
        ActiveIncident = action.Kind == QuotaActionKind.SealActiveIncident,
    };

    private bool TryWriteAggregate(StorageItem item, string outputPath, string kind, string reason,
        bool @sealed, List<string> notPersisted)
    {
        try
        {
            var failureCounts = new JsonObject();
            foreach (var pair in item.FailureCountsByStatus)
                failureCounts[pair.Key] = pair.Value;
            var document = new JsonObject
            {
                ["schema_version"] = "1",
                ["kind"] = kind,
                ["session"] = Path.GetFileName(item.Path),
                ["recorded_at"] = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["reason"] = reason,
                ["event_count"] = item.EventCount,
                ["failure_count"] = item.FailureCount,
                ["partial_count"] = item.PartialCount,
                ["failure_counts_by_status"] = failureCounts,
                ["gap_event_count"] = item.GapEventCount,
                ["sequence_gap_count"] = item.SequenceGapCount,
                ["problem_line_count"] = item.ProblemLineCount,
                ["first_failure_at"] = item.FirstFailureAt?.ToString("O", CultureInfo.InvariantCulture),
                ["last_failure_at"] = item.LastFailureAt?.ToString("O", CultureInfo.InvariantCulture),
                ["active"] = @sealed,
                ["note"] = @sealed
                    ? "Active incident sealed before quota cleanup; failure totals, first/last times and gaps are preserved. Detail is not a root-cause proof."
                    : "Detail was removed by retention; aggregated failure/gap counts are preserved. This is not evidence of a healthy network.",
            };
            var text = document.ToJsonString(JsonOptions) + "\n";
            var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = _files.OpenWrite(temporary))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush();
            }

            _files.Move(temporary, outputPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or System.Security.SecurityException)
        {
            notPersisted.Add(Path.GetFileName(outputPath) + " (" + exception.GetType().Name + ")");
            return false;
        }
    }

    private PinAdmission Refuse(string reason, long requestedBytes, long pinnedBytes) => new()
    {
        Accepted = false,
        Reason = reason,
        RequestedBytes = requestedBytes,
        PinnedBytes = pinnedBytes,
        PinnedLimit = _policy.PinnedMaxBytes,
    };

    private static string BuildSummary(QuotaPlan plan, QuotaInspection after, IReadOnlyList<QuotaActionResult> results,
        IReadOnlyList<string> notPersisted)
    {
        var succeeded = results.Count(result => result.Succeeded);
        var failed = results.Count - succeeded;
        var blocked = plan.BlockedReasons.Count == 0 ? string.Empty : " " + string.Join(" ", plan.BlockedReasons);
        return $"Quota enforcement: {succeeded} action(s) succeeded, {failed} failed; "
            + $"ordinary log pool {after.NormalBytes}/{after.Evaluation.NormalLimit} bytes "
            + $"({after.Evaluation.NormalPercent:0.0}%), pinned {after.PinnedBytes}/{after.Evaluation.PinnedLimit} bytes; "
            + $"level {after.Evaluation.Level}.{blocked}"
            + (notPersisted.Count == 0 ? string.Empty : " Not persisted: " + string.Join("; ", notPersisted) + ".");
    }
}
