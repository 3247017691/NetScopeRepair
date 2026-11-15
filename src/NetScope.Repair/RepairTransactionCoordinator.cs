using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>One fresh read of a target field, through the injectable platform seam.</summary>
public sealed record RepairFieldRead
{
    public required EventStatus Status { get; init; }

    public required RepairFieldValue? Value { get; init; }

    public required string Source { get; init; }

    public required DateTimeOffset ObservedAtUtc { get; init; }

    public required string BootId { get; init; }

    public string? Reason { get; init; }

    public bool IsUsable => Status == EventStatus.Ok && Value is not null;

    public static RepairFieldRead Failed(EventStatus status, string reason) => new()
    {
        Status = status,
        Value = null,
        Source = "unavailable",
        ObservedAtUtc = DateTimeOffset.UtcNow,
        BootId = string.Empty,
        Reason = reason,
    };
}

public enum RepairFieldWriteStatus
{
    /// <summary>The adapter performed the write and read the value back.</summary>
    Written,

    /// <summary>The adapter refused the write and proved nothing changed.</summary>
    Rejected,

    /// <summary>The adapter could not establish whether the write happened.</summary>
    OutcomeUnknown,

    /// <summary>No adapter is available.</summary>
    Unavailable,
}

public sealed record RepairFieldWrite
{
    public required RepairFieldWriteStatus Status { get; init; }

    public required RepairFieldValue? Readback { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The injectable native seam for the three V1 fields. Production registration does not exist in
/// this delivery (no verified platform experiment); tests provide a recording fake.
/// </summary>
public interface IRepairFieldAdapter
{
    string AdapterId { get; }

    /// <summary>True only for an adapter that passed the T13 platform experiment for these fields.</summary>
    bool IsPlatformVerified { get; }

    Task<RepairFieldRead> GetAsync(
        InterfaceIdentity identity, RepairStore store, RepairField field, CancellationToken cancellationToken);

    Task<RepairFieldWrite> SetAsync(
        InterfaceIdentity identity, RepairStore store, RepairField field, RepairFieldValue value,
        CancellationToken cancellationToken);
}

/// <summary>The only production registration until a platform experiment exists: it never writes.</summary>
public sealed class UnavailableRepairFieldAdapter : IRepairFieldAdapter
{
    public string AdapterId => "unavailable";

    public bool IsPlatformVerified => false;

    public Task<RepairFieldRead> GetAsync(
        InterfaceIdentity identity, RepairStore store, RepairField field, CancellationToken cancellationToken) =>
        Task.FromResult(RepairFieldRead.Failed(EventStatus.Unsupported, "no verified platform adapter"));

    public Task<RepairFieldWrite> SetAsync(
        InterfaceIdentity identity, RepairStore store, RepairField field, RepairFieldValue value,
        CancellationToken cancellationToken) => Task.FromResult(new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Unavailable,
            Readback = null,
            AtUtc = DateTimeOffset.UtcNow,
            Reason = "no verified platform adapter",
        });
}

public sealed record RepairObservationResult
{
    public required bool Passed { get; init; }

    public required string Detail { get; init; }
}

public interface IRepairObservationProbe
{
    Task<RepairObservationResult> ObserveAsync(
        RepairPlan plan, IReadOnlyList<RepairAction> actions, TimeSpan window, CancellationToken cancellationToken);
}

public sealed class UnavailableRepairObservationProbe : IRepairObservationProbe
{
    public Task<RepairObservationResult> ObserveAsync(
        RepairPlan plan, IReadOnlyList<RepairAction> actions, TimeSpan window, CancellationToken cancellationToken) =>
        Task.FromResult(new RepairObservationResult
        {
            Passed = false,
            Detail = "observation_unavailable",
        });
}

public sealed record RepairStepOutcome
{
    public required int Index { get; init; }

    public required string ActionId { get; init; }

    public required string Phase { get; init; }

    public RepairFieldValue? Readback { get; init; }

    public string? Detail { get; init; }
}

public sealed record RepairTransactionResult
{
    public required string TransactionId { get; init; }

    public required RepairTransactionState State { get; init; }

    public required IReadOnlyList<RepairTransactionRefusal> Refusals { get; init; }

    public required IReadOnlyList<RepairStepOutcome> Steps { get; init; }

    public required bool WroteAnyField { get; init; }

    public required bool JournalUsable { get; init; }

    public bool IsTerminal => RepairTransactionStateRules.IsTerminal(State);
}

public sealed record RepairTransactionRequest
{
    public required string TransactionId { get; init; }

    public required RepairPlan Plan { get; init; }

    public required RepairAuthorizationBinding Authorization { get; init; }

    public required string UserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required IRepairFieldAdapter Adapter { get; init; }

    public required IRepairObservationProbe Observation { get; init; }

    public RepairTransactionLimits Limits { get; init; } = RepairTransactionLimits.Design05;

    public PlatformCapabilityMatrix Capabilities { get; init; } = PlatformCapabilityMatrix.UnverifiedDefault;

    /// <summary>
    /// Explicit development isolation. It permits a synthetic plan and a fake adapter for tests,
    /// marks the result synthetic, and never becomes a production execution claim.
    /// </summary>
    public bool DevelopmentIsolation { get; init; }

    public TimeProvider? Clock { get; init; }
}

/// <summary>
/// Executes one already-authorized, typed plan with the design 05 write-ahead order:
/// prepared (full before + authorization digest) → per-step intent flush → fresh GET → minimal
/// write → readback → applied flush → observation → commit or exact rollback.
///
/// The coordinator never writes outside the injected adapter, never abandons a native call, and
/// never invents ownership: an intent without an applied record stays recovery_required.
/// </summary>
public sealed class RepairTransactionCoordinator
{
    private readonly RepairJournal _journal;
    private readonly TimeProvider _clock;

    public RepairTransactionCoordinator(RepairJournal journal, TimeProvider? clock = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<RepairTransactionResult> ExecuteAsync(
        RepairTransactionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _transactionId = request.TransactionId;
        var steps = new List<RepairStepOutcome>();
        var startedAt = _clock.GetTimestamp();
        var wroteAnyField = false;
        var refusals = new List<RepairTransactionRefusal>();

        RepairTransactionResult Result(RepairTransactionState finalState) => new()
        {
            TransactionId = request.TransactionId,
            State = finalState,
            Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
            Steps = steps.ToArray(),
            WroteAnyField = wroteAnyField,
            JournalUsable = _journal.IsUsable,
        };

        RepairTransactionRefusal Refuse(RepairTransactionRefusalCode code, string detail) =>
            RepairTransactionRefusal.Of(code, detail);

        // Journal durability is the precondition for every privileged action.
        if (!_journal.IsUsable)
        {
            refusals.Add(Refuse(
                RepairTransactionRefusalCode.JournalCorrupt,
                "The authoritative journal is not usable; no privileged action was attempted."));
            refusals.AddRange(_journal.Problems);
            return Result(RepairTransactionState.Aborted);
        }

        var appendFailure = Append(RepairJournalKind.TransactionPlanned, request.NowUtc, RepairTransactionState.Planned);
        if (appendFailure is not null)
        {
            refusals.Add(appendFailure);
            return Result(RepairTransactionState.Aborted);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            refusals.Add(Refuse(RepairTransactionRefusalCode.Cancelled, "The transaction was cancelled before preparation."));
            Append(RepairJournalKind.TransactionAborted, request.NowUtc, RepairTransactionState.Aborted,
                refusal: RepairTransactionRefusalCode.Cancelled);
            return Result(RepairTransactionState.Aborted);
        }

        var preflight = Preflight(request);
        if (preflight is not null)
        {
            refusals.Add(preflight);
            Append(RepairJournalKind.TransactionAborted, request.NowUtc, RepairTransactionState.Aborted,
                refusal: preflight.Code);
            return Result(RepairTransactionState.Aborted);
        }

        var unresolved = _journal.UnresolvedTransactionIds()
            .Where(id => !string.Equals(id, request.TransactionId, StringComparison.Ordinal))
            .ToArray();
        if (unresolved.Length > 0)
        {
            refusals.Add(Refuse(
                RepairTransactionRefusalCode.UnresolvedTransactionPresent,
                "An earlier transaction is unresolved; the user must confirm a new recovery plan first."));
            Append(RepairJournalKind.TransactionAborted, request.NowUtc, RepairTransactionState.Aborted,
                refusal: RepairTransactionRefusalCode.UnresolvedTransactionPresent);
            return Result(RepairTransactionState.Aborted);
        }

        var changed = request.Plan.ChangedActions;
        if (changed.Count == 0)
        {
            var authorizedNoOp = Append(RepairJournalKind.TransactionAuthorized, request.NowUtc, RepairTransactionState.Authorized);
            if (authorizedNoOp is null)
            {
                Append(RepairJournalKind.TransactionNoOp, request.NowUtc, RepairTransactionState.NoOp);
            }

            if (authorizedNoOp is not null) refusals.Add(authorizedNoOp);
            return Result(RepairTransactionState.NoOp);
        }

        var authorizedFailure = Append(RepairJournalKind.TransactionAuthorized, request.NowUtc, RepairTransactionState.Authorized);
        if (authorizedFailure is not null)
        {
            refusals.Add(authorizedFailure);
            return Result(RepairTransactionState.Aborted);
        }

        var target = RepairJournalTarget.From(changed[0].Identity);
        var plannedSteps = changed
            .Select((action, index) => new RepairJournalPlannedStep
            {
                Index = index,
                ActionId = action.ActionId,
                Kind = action.Kind,
                Field = action.Field,
                Store = action.Store,
                Family = action.Family,
                Before = RepairJournalValue.From(action.Before),
                After = RepairJournalValue.From(action.Desired),
                DependsOnActionIds = action.DependsOnActionIds.ToArray(),
            })
            .ToArray();

        var preparedFailure = Append(
            RepairJournalKind.TransactionPrepared, request.NowUtc, RepairTransactionState.Prepared,
            additional: entry => entry with
            {
                PlanId = request.Plan.PlanId,
                PlanDigest = request.Plan.Digest,
                AuthorizationDigest = request.Authorization.PlanDigest,
                UserSid = request.UserSid,
                LogonSessionId = request.LogonSessionId,
                Target = target,
                PlannedSteps = plannedSteps,
                ObservationWindowMs = (int)request.Limits.MaxTechnicalObservation.TotalMilliseconds,
            });
        if (preparedFailure is not null)
        {
            refusals.Add(preparedFailure);
            return Result(RepairTransactionState.Aborted);
        }

        for (var index = 0; index < changed.Count; index++)
        {
            var action = changed[index];
            var step = plannedSteps[index];
            var elapsed = _clock.GetElapsedTime(startedAt);
            var deadline = elapsed >= request.Limits.Total
                ? RepairTransactionRefusalCode.DeadlineExceeded
                : elapsed >= request.Limits.LatestForwardAction
                    ? RepairTransactionRefusalCode.DeadlineExceeded
                    : (RepairTransactionRefusalCode?)null;
            if (deadline is not null)
            {
                var refusal = Refuse(deadline.Value, "No new forward action may start after the bounded window.");
                refusals.Add(refusal);
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                refusals.Add(Refuse(RepairTransactionRefusalCode.Cancelled, "The transaction was cancelled."));
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            var intentFailure = Append(
                RepairJournalKind.StepIntent, request.NowUtc, RepairTransactionState.Applying,
                additional: entry => entry with
                {
                    StepIndex = index,
                    ActionId = action.ActionId,
                    Field = action.Field,
                    BeforeValue = step.Before,
                    AfterValue = step.After,
                });
            if (intentFailure is not null)
            {
                refusals.Add(intentFailure);
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            // Fresh GET before the write; the authorized baseline is never silently replaced.
            var fresh = await request.Adapter
                .GetAsync(action.Identity, action.Store, action.Field, cancellationToken)
                .ConfigureAwait(false);
            if (fresh.IsUsable
                && !string.IsNullOrEmpty(fresh.BootId)
                && !string.Equals(fresh.BootId, request.Plan.BootId, StringComparison.Ordinal))
            {
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.CrossBoot,
                    "The current read belongs to another boot; the interface identity cannot be trusted."));
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            if (!fresh.IsUsable)
            {
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.BeforeMismatch,
                    "A fresh read before the write did not return a usable current value."));
                steps.Add(Step(index, action, "fresh_get_unusable", null, fresh.Reason));
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            if (!string.Equals(fresh.Value!.CanonicalText, action.Before.CanonicalText, StringComparison.Ordinal))
            {
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.ExternalValueChanged,
                    "An external change was observed before the write; the transaction stops without writing."));
                steps.Add(Step(index, action, "fresh_get_mismatch", fresh.Value, null));
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            var write = await request.Adapter
                .SetAsync(action.Identity, action.Store, action.Field, action.Desired, cancellationToken)
                .ConfigureAwait(false);
            if (write.Status is RepairFieldWriteStatus.Rejected or RepairFieldWriteStatus.Unavailable)
            {
                refusals.Add(Refuse(
                    write.Status == RepairFieldWriteStatus.Unavailable
                        ? RepairTransactionRefusalCode.CapabilityUnavailable
                        : RepairTransactionRefusalCode.WriteFailed,
                    write.Reason ?? "The adapter refused the write before it happened."));
                return wroteAnyField
                    ? await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false)
                    : Abort(request, steps, index, action, refusals);
            }

            wroteAnyField = true;

            if (write.Status == RepairFieldWriteStatus.OutcomeUnknown)
            {
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "The native write outcome is unknown; no automatic rollback and no further write is attempted."));
                steps.Add(Step(index, action, "write_unknown", null, write.Reason));
                Append(RepairJournalKind.TransactionRecoveryRequired, request.NowUtc,
                    RepairTransactionState.RecoveryRequired,
                    refusal: RepairTransactionRefusalCode.RecoveryUncertain,
                    additional: entry => entry with { StepIndex = index, ActionId = action.ActionId, Field = action.Field });
                return Result(RepairTransactionState.RecoveryRequired);
            }

            var readback = write.Readback;
            if (readback is null)
            {
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "The write returned no readback; ownership cannot be proven and recovery needs user confirmation."));
                Append(RepairJournalKind.TransactionRecoveryRequired, request.NowUtc,
                    RepairTransactionState.RecoveryRequired,
                    refusal: RepairTransactionRefusalCode.RecoveryUncertain,
                    additional: entry => entry with { StepIndex = index, ActionId = action.ActionId, Field = action.Field });
                return Result(RepairTransactionState.RecoveryRequired);
            }

            var appliedFailure = Append(
                RepairJournalKind.StepApplied, request.NowUtc, RepairTransactionState.Applying,
                additional: entry => entry with
                {
                    StepIndex = index,
                    ActionId = action.ActionId,
                    Field = action.Field,
                    BeforeValue = step.Before,
                    AfterValue = step.After,
                    ReadbackValue = RepairJournalValue.From(readback),
                });
            steps.Add(Step(index, action, "applied", readback, appliedFailure?.Detail));
            if (appliedFailure is not null)
            {
                refusals.Add(appliedFailure);
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "The write happened but its applied record is not durable; recovery needs user confirmation."));
                return Result(RepairTransactionState.RecoveryRequired);
            }

            if (!string.Equals(readback.CanonicalText, action.Desired.CanonicalText, StringComparison.Ordinal))
            {
                refusals.Add(Refuse(
                    RepairTransactionRefusalCode.ReadbackMismatch,
                    "The value read back after the write does not match the planned after value."));
                return await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false);
            }
        }

        var observationWindow = request.Limits.EffectiveObservationWindow(_clock.GetElapsedTime(startedAt));
        if (observationWindow <= TimeSpan.Zero)
        {
            refusals.Add(Refuse(
                RepairTransactionRefusalCode.DeadlineExceeded,
                "No new observation may start after the forward window closed."));
            return await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false);
        }

        var observingFailure = Append(
            RepairJournalKind.ObservationStarted, request.NowUtc, RepairTransactionState.Observing,
            additional: entry => entry with { ObservationWindowMs = (int)observationWindow.TotalMilliseconds });
        if (observingFailure is not null)
        {
            refusals.Add(observingFailure);
            return await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false);
        }

        RepairObservationResult observation;
        try
        {
            observation = await request.Observation
                .ObserveAsync(request.Plan, changed, observationWindow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            refusals.Add(Refuse(RepairTransactionRefusalCode.Cancelled, "The transaction was cancelled during observation."));
            return await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false);
        }

        if (!observation.Passed)
        {
            refusals.Add(Refuse(
                RepairTransactionRefusalCode.ObservationFailed,
                "The planned technical observation did not pass: " + observation.Detail));
            return await RollbackAsync(request, steps, refusals, cancellationToken).ConfigureAwait(false);
        }

        var commitFailure = Append(RepairJournalKind.TransactionCommitted, request.NowUtc, RepairTransactionState.Committed);
        if (commitFailure is not null)
        {
            refusals.Add(commitFailure);
            refusals.Add(Refuse(
                RepairTransactionRefusalCode.RecoveryUncertain,
                "The transaction passed observation but the commit record is not durable."));
            return Result(RepairTransactionState.RecoveryRequired);
        }

        return Result(RepairTransactionState.Committed);
    }

    private RepairTransactionRefusal? Preflight(RepairTransactionRequest request)
    {
        var plan = request.Plan;
        var recomputed = RepairPlanDigest.Compute(plan);
        if (!string.Equals(recomputed, plan.Digest, StringComparison.Ordinal)
            || !string.Equals(recomputed, request.Authorization.PlanDigest, StringComparison.Ordinal))
        {
            return RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.AuthorizationInvalid,
                "The plan changed after the authorized digest; a new preview is required.");
        }

        var authorizationRefusals = RepairAuthorizationBindingValidator.Validate(
            request.Authorization, plan, request.NowUtc, request.UserSid, request.LogonSessionId);
        if (authorizationRefusals.Count > 0)
        {
            return RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.AuthorizationInvalid,
                "The authorization binding is not valid for this plan: " + authorizationRefusals[0].Detail);
        }

        if (request.DevelopmentIsolation) return null;

        if (plan.Synthetic)
        {
            return RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.SyntheticNotAllowed,
                "A synthetic plan can never be executed as a privileged repair.");
        }

        if (!plan.ExecutionAllowed || !plan.CanAuthorize)
        {
            return RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.PlanNotExecutable,
                "The plan is not executable without a verified platform capability.");
        }

        if (!request.Adapter.IsPlatformVerified)
        {
            return RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.CapabilityUnavailable,
                "No verified platform field adapter exists for this deployment.");
        }

        foreach (var action in plan.Actions)
        {
            var capability = request.Capabilities.Resolve(action.Kind, action.Store, action.Family);
            if (!capability.IsAvailable)
                return RepairTransactionRefusal.Of(RepairTransactionRefusalCode.CapabilityUnavailable, capability.Reason);
        }

        return null;
    }

    /// <summary>
    /// Reverse exact rollback. Only applied fields are considered, and only when the current value
    /// is still the value this transaction wrote. Conflicts are reported and preserved; an intent
    /// without an applied record is never replayed.
    /// </summary>
    private async Task<RepairTransactionResult> RollbackAsync(
        RepairTransactionRequest request, List<RepairStepOutcome> steps,
        List<RepairTransactionRefusal> refusals, CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Compensation is bounded by its own reserve and is not abandoned on cancel.
        var applied = _journal.Records
            .Where(record => string.Equals(record.Entry.TransactionId, request.TransactionId, StringComparison.Ordinal)
                && record.Entry.Kind == RepairJournalKind.StepApplied)
            .OrderByDescending(record => record.Entry.StepIndex ?? 0)
            .ToArray();
        var actionById = request.Plan.Actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);

        if (Append(RepairJournalKind.TransactionStateChanged, request.NowUtc, RepairTransactionState.RollingBack,
                refusal: refusals.Count > 0 ? refusals[0].Code : null) is { } rollingFailure)
        {
            refusals.Add(rollingFailure);
            return RollbackResult(RepairTransactionState.RecoveryRequired);
        }

        foreach (var record in applied)
        {
            var entry = record.Entry;
            if (entry.StepIndex is not { } stepIndex
                || entry.ActionId is null
                || !actionById.TryGetValue(entry.ActionId, out var action)
                || entry.BeforeValue is null)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.JournalCorrupt,
                    "A journal applied record is incomplete; rollback cannot prove ownership."));
                return RollbackResult(RepairTransactionState.RecoveryRequired);
            }

            var before = entry.BeforeValue;
            var after = entry.AfterValue ?? RepairJournalValue.From(action.Desired);
            var current = await request.Adapter
                .GetAsync(action.Identity, action.Store, action.Field, CancellationToken.None)
                .ConfigureAwait(false);
            if (!current.IsUsable)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RollbackFailed,
                    "The current value could not be read during rollback."));
                Append(RepairJournalKind.TransactionRollbackFailed, request.NowUtc, RepairTransactionState.RollbackFailed,
                    refusal: RepairTransactionRefusalCode.RollbackFailed);
                return RollbackResult(RepairTransactionState.RollbackFailed);
            }

            var currentText = current.Value!.CanonicalText;
            if (string.Equals(currentText, before.Canonical, StringComparison.Ordinal))
            {
                if (AppendRollback(entry, alreadyRestored: true, before, request.NowUtc) is { } alreadyFailure)
                {
                    refusals.Add(alreadyFailure);
                    return RollbackResult(RepairTransactionState.RecoveryRequired);
                }

                steps.Add(new RepairStepOutcome
                {
                    Index = stepIndex,
                    ActionId = entry.ActionId,
                    Phase = "already_restored",
                    Readback = current.Value,
                });
                continue;
            }

            if (!string.Equals(currentText, after.Canonical, StringComparison.Ordinal))
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RollbackConflict,
                    "The current value is neither the before nor the after value; it is preserved, not overwritten."));
                Append(RepairJournalKind.TransactionRollbackConflict, request.NowUtc,
                    RepairTransactionState.RollbackConflict, refusal: RepairTransactionRefusalCode.RollbackConflict);
                steps.Add(new RepairStepOutcome
                {
                    Index = stepIndex,
                    ActionId = entry.ActionId,
                    Phase = "conflict",
                    Readback = current.Value,
                    Detail = "external value preserved",
                });
                return RollbackResult(RepairTransactionState.RollbackConflict);
            }

            if (AppendRollbackIntent(entry, request.NowUtc) is { } intentFailure)
            {
                refusals.Add(intentFailure);
                return RollbackResult(RepairTransactionState.RecoveryRequired);
            }

            RepairFieldValue restore;
            try
            {
                restore = before.ToValue();
            }
            catch (ArgumentException)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "The journal before value cannot be reconstructed; recovery needs user confirmation."));
                return RollbackResult(RepairTransactionState.RecoveryRequired);
            }

            var write = await request.Adapter
                .SetAsync(action.Identity, action.Store, action.Field, restore, CancellationToken.None)
                .ConfigureAwait(false);
            if (write.Status == RepairFieldWriteStatus.OutcomeUnknown)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "A rollback write outcome is unknown; the transaction requires user-confirmed recovery."));
                return RollbackResult(RepairTransactionState.RecoveryRequired);
            }

            if (write.Status is RepairFieldWriteStatus.Unavailable or RepairFieldWriteStatus.Rejected
                || write.Readback is null
                || !string.Equals(write.Readback.CanonicalText, before.Canonical, StringComparison.Ordinal))
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RollbackFailed,
                    "The rollback write did not read back the before value."));
                Append(RepairJournalKind.TransactionRollbackFailed, request.NowUtc,
                    RepairTransactionState.RollbackFailed, refusal: RepairTransactionRefusalCode.RollbackFailed);
                return RollbackResult(RepairTransactionState.RollbackFailed);
            }

            var rollbackAppliedFailure = AppendRollbackApplied(entry, before, request.NowUtc);
            steps.Add(new RepairStepOutcome
            {
                Index = stepIndex,
                ActionId = entry.ActionId,
                Phase = "rolled_back",
                Readback = write.Readback,
            });
            if (rollbackAppliedFailure is not null)
            {
                refusals.Add(rollbackAppliedFailure);
                return RollbackResult(RepairTransactionState.RecoveryRequired);
            }
        }

        if (Append(RepairJournalKind.TransactionRolledBack, request.NowUtc, RepairTransactionState.RolledBack) is { } failure)
        {
            refusals.Add(failure);
            return RollbackResult(RepairTransactionState.RecoveryRequired);
        }

        return RollbackResult(RepairTransactionState.RolledBack);

        RepairTransactionResult RollbackResult(RepairTransactionState state) => new()
        {
            TransactionId = request.TransactionId,
            State = state,
            Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
            Steps = steps.ToArray(),
            WroteAnyField = true,
            JournalUsable = _journal.IsUsable,
        };

        RepairTransactionRefusal? AppendRollback(RepairJournalEntry applied, bool alreadyRestored, RepairJournalValue readback, DateTimeOffset at)
        {
            try
            {
                _journal.Append(Base(RepairJournalKind.RollbackApplied, at, RepairTransactionState.RollingBack,
                    applied, refusal: null) with
                {
                    ReadbackValue = readback,
                    AlreadyRestored = alreadyRestored,
                });
                return null;
            }
            catch (RepairJournalException exception)
            {
                return RepairTransactionRefusal.Of(exception.Code, "The rollback record could not be made durable.");
            }
        }

        RepairTransactionRefusal? AppendRollbackIntent(RepairJournalEntry applied, DateTimeOffset at)
        {
            try
            {
                _journal.Append(Base(RepairJournalKind.RollbackIntent, at, RepairTransactionState.RollingBack, applied, refusal: null));
                return null;
            }
            catch (RepairJournalException exception)
            {
                return RepairTransactionRefusal.Of(exception.Code, "The rollback intent could not be made durable.");
            }
        }

        RepairTransactionRefusal? AppendRollbackApplied(RepairJournalEntry applied, RepairJournalValue readback, DateTimeOffset at)
        {
            try
            {
                _journal.Append(Base(RepairJournalKind.RollbackApplied, at, RepairTransactionState.RollingBack, applied, refusal: null) with
                {
                    ReadbackValue = readback,
                    AlreadyRestored = false,
                });
                return null;
            }
            catch (RepairJournalException exception)
            {
                return RepairTransactionRefusal.Of(exception.Code, "The rollback result could not be made durable.");
            }
        }
    }

    private RepairTransactionResult Abort(
        RepairTransactionRequest request, List<RepairStepOutcome> steps, int index,
        RepairAction action, List<RepairTransactionRefusal> refusals)
    {
        if (Append(RepairJournalKind.TransactionAborted, request.NowUtc, RepairTransactionState.Aborted,
                refusal: refusals.Count > 0 ? refusals[0].Code : null,
                additional: entry => entry with { StepIndex = index, ActionId = action.ActionId, Field = action.Field })
            is { } failure)
        {
            refusals.Add(failure);
        }

        return new RepairTransactionResult
        {
            TransactionId = request.TransactionId,
            State = RepairTransactionState.Aborted,
            Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
            Steps = steps.ToArray(),
            WroteAnyField = false,
            JournalUsable = _journal.IsUsable,
        };
    }

    private RepairTransactionRefusal? Append(
        RepairJournalKind kind,
        DateTimeOffset atUtc,
        RepairTransactionState state,
        RepairTransactionRefusalCode? refusal = null,
        string? reason = null,
        Func<RepairJournalEntry, RepairJournalEntry>? additional = null)
    {
        var entry = new RepairJournalEntry
        {
            TransactionId = _transactionId!,
            Kind = kind,
            State = state,
            AtUtc = atUtc,
            RefusalCode = refusal,
            Reason = reason,
        };
        if (additional is not null) entry = additional(entry);
        try
        {
            _journal.Append(entry);
            return null;
        }
        catch (RepairJournalException exception)
        {
            return RepairTransactionRefusal.Of(
                exception.Code, "The journal append failed; no further privileged action is attempted.");
        }
    }

    private string? _transactionId;

    private RepairJournalEntry Base(
        RepairJournalKind kind, DateTimeOffset atUtc, RepairTransactionState state,
        RepairJournalEntry applied, RepairTransactionRefusalCode? refusal) => new()
    {
        TransactionId = _transactionId!,
        Kind = kind,
        State = state,
        AtUtc = atUtc,
        RefusalCode = refusal,
        StepIndex = applied.StepIndex,
        ActionId = applied.ActionId,
        Field = applied.Field,
        BeforeValue = applied.BeforeValue,
        AfterValue = applied.AfterValue,
    };

    private static RepairStepOutcome Step(
        int index, RepairAction action, string phase, RepairFieldValue? readback, string? detail) => new()
    {
        Index = index,
        ActionId = action.ActionId,
        Phase = phase,
        Readback = readback,
        Detail = detail,
    };
}


