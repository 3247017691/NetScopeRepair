using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>Per-step facts reconstructed from the journal. Nothing here implies a live current value.</summary>
public sealed record RepairRecoveryStep
{
    public required int Index { get; init; }

    public required string ActionId { get; init; }

    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required RepairAddressFamily Family { get; init; }

    public RepairJournalValue? Before { get; init; }

    public RepairJournalValue? After { get; init; }

    public bool IntentPresent { get; init; }

    public bool AppliedPresent { get; init; }

    public RepairJournalValue? AppliedReadback { get; init; }

    public bool RollbackPresent { get; init; }

    /// <summary>Intent without applied and without an explaining terminal state: ownership unknown.</summary>
    public bool Uncertain { get; init; }
}

public sealed record RepairJournalTransactionSummary
{
    public required string TransactionId { get; init; }

    public required RepairTransactionState LatestState { get; init; }

    public required bool IsTerminal { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    public RepairJournalTarget? Target { get; init; }

    public string? PlanDigest { get; init; }

    public string? AuthorizationDigest { get; init; }

    public IReadOnlyList<RepairRecoveryStep> Steps { get; init; } = Array.Empty<RepairRecoveryStep>();

    /// <summary>True when the journal stops mid-execution and needs a user-confirmed recovery plan.</summary>
    public required bool RequiresUserConfirmation { get; init; }

    public required bool HasUncertainSteps { get; init; }

    public required bool HasProvenAppliedSteps { get; init; }

    public required bool IsCommitted { get; init; }
}

public sealed record RepairJournalScanResult
{
    public required bool JournalUsable { get; init; }

    public required bool HasCorruptTail { get; init; }

    public required IReadOnlyList<RepairTransactionRefusal> Problems { get; init; }

    public required IReadOnlyList<RepairJournalTransactionSummary> Transactions { get; init; }

    public required IReadOnlyList<RepairJournalTransactionSummary> Unresolved { get; init; }

    /// <summary>Plain-language startup notice; it never executes anything.</summary>
    public required string StartupNotice { get; init; }
}

/// <summary>
/// Read-only journal interpretation. It classifies each transaction honestly:
/// an intent without an applied record and without an explaining terminal state is uncertain and
/// is never replayed; a committed transaction needs a new authorization to be revoked.
/// </summary>
public static class RepairJournalRecoveryScanner
{
    public static RepairJournalScanResult Scan(RepairJournal journal, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _ = nowUtc;
        var summaries = new List<RepairJournalTransactionSummary>();

        foreach (var transactionId in journal.Records
                     .Select(record => record.Entry.TransactionId)
                     .Distinct(StringComparer.Ordinal))
        {
            var records = journal.EntriesOf(transactionId);
            var latest = records[^1];
            var latestState = latest.Entry.State;
            var terminal = RepairTransactionStateRules.IsTerminal(latestState);

            var prepared = records.FirstOrDefault(record => record.Entry.Kind == RepairJournalKind.TransactionPrepared);
            var planned = records.FirstOrDefault(record => record.Entry.Kind == RepairJournalKind.TransactionPlanned);
            var plannedSteps = prepared?.Entry.PlannedSteps ?? Array.Empty<RepairJournalPlannedStep>();
            var steps = new List<RepairRecoveryStep>();

            foreach (var step in plannedSteps)
            {
                var intent = records.LastOrDefault(record =>
                    record.Entry.Kind == RepairJournalKind.StepIntent && record.Entry.StepIndex == step.Index);
                var applied = records.LastOrDefault(record =>
                    record.Entry.Kind == RepairJournalKind.StepApplied && record.Entry.StepIndex == step.Index);
                var rollback = records.LastOrDefault(record =>
                    record.Entry.Kind == RepairJournalKind.RollbackApplied && record.Entry.StepIndex == step.Index);

                // An explicit abort/terminal record explains an intent that never reached applied;
                // only a silence between intent and applied is an uncertain execution window.
                var explainedByState = records.SkipWhile(record => !ReferenceEquals(record, intent))
                    .Skip(1)
                    .Any(record => record.Entry.Kind is RepairJournalKind.TransactionAborted
                        or RepairJournalKind.TransactionRolledBack
                        or RepairJournalKind.TransactionCommitted
                        or RepairJournalKind.TransactionNoOp
                        or RepairJournalKind.TransactionRollbackConflict
                        or RepairJournalKind.TransactionRollbackFailed
                        or RepairJournalKind.TransactionRecoveryRequired);

                steps.Add(new RepairRecoveryStep
                {
                    Index = step.Index,
                    ActionId = step.ActionId,
                    Field = step.Field,
                    Store = step.Store,
                    Family = step.Family,
                    Before = applied?.Entry.BeforeValue ?? intent?.Entry.BeforeValue ?? step.Before,
                    After = applied?.Entry.AfterValue ?? intent?.Entry.AfterValue ?? step.After,
                    IntentPresent = intent is not null,
                    AppliedPresent = applied is not null,
                    AppliedReadback = applied?.Entry.ReadbackValue,
                    RollbackPresent = rollback is not null,
                    Uncertain = intent is not null && applied is null && rollback is null && !explainedByState,
                });
            }

            var hasUncertain = steps.Any(step => step.Uncertain);
            var hasApplied = steps.Any(step => step.AppliedPresent && !step.RollbackPresent);
            var committed = latestState == RepairTransactionState.Committed;
            var requiresConfirmation = !terminal
                || hasUncertain
                || latestState == RepairTransactionState.RecoveryRequired;

            summaries.Add(new RepairJournalTransactionSummary
            {
                TransactionId = transactionId,
                LatestState = latestState,
                IsTerminal = terminal,
                FirstSeenUtc = planned?.Entry.AtUtc ?? records[0].Entry.AtUtc,
                LastSeenUtc = latest.Entry.AtUtc,
                Target = prepared?.Entry.Target,
                PlanDigest = prepared?.Entry.PlanDigest ?? planned?.Entry.PlanDigest,
                AuthorizationDigest = prepared?.Entry.AuthorizationDigest,
                Steps = steps,
                RequiresUserConfirmation = requiresConfirmation,
                HasUncertainSteps = hasUncertain,
                HasProvenAppliedSteps = hasApplied,
                IsCommitted = committed,
            });
        }

        var unresolved = summaries
            .Where(summary => summary.RequiresUserConfirmation)
            .OrderBy(summary => summary.FirstSeenUtc)
            .ToArray();

        var notice = !journal.IsUsable
            ? "事务 journal 不可用：" + string.Join("；", journal.Problems.Select(problem => problem.Code.ToString()))
            : unresolved.Length == 0
                ? "没有未终结的事务记录。"
                : "检测到 " + unresolved.Length
                    + " 个未终结事务；尚未自动恢复。请查看恢复计划，再次授权后由新的 broker 执行。";

        return new RepairJournalScanResult
        {
            JournalUsable = journal.IsUsable,
            HasCorruptTail = journal.HasCorruptTail,
            Problems = journal.Problems.ToArray(),
            Transactions = summaries,
            Unresolved = unresolved,
            StartupNotice = notice,
        };
    }
}

/// <summary>Fields a user-confirmed recovery may restore. Uncertain steps are excluded.</summary>
public sealed record RepairRecoveryPlan
{
    public required string RecoveryPlanId { get; init; }

    public required string SourceTransactionId { get; init; }

    public required RepairTransactionState SourceState { get; init; }

    /// <summary>Restorable fields proven to have an applied record that this transaction wrote.</summary>
    public required IReadOnlyList<RepairRecoveryField> RestorableFields { get; init; }

    /// <summary>Intent-only steps: listed for the user, never written automatically.</summary>
    public required IReadOnlyList<string> UncertainActionIds { get; init; }

    public required bool RequiresUserConfirmation { get; init; }

    public required bool RequiresNewAuthorization { get; init; }

    public required string TargetFingerprint { get; init; }

    public required RepairJournalTarget? Target { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required IReadOnlyList<RepairTransactionRefusal> Refusals { get; init; }

    /// <summary>Stable digest over the recovery scope; a new authorization binds it.</summary>
    public required string Digest { get; init; }
}

public sealed record RepairRecoveryField
{
    public required int Index { get; init; }

    public required string ActionId { get; init; }

    public required RepairField Field { get; init; }

    public required RepairStore Store { get; init; }

    public required RepairAddressFamily Family { get; init; }

    public required RepairJournalValue Before { get; init; }

    public required RepairJournalValue After { get; init; }
}

public static class RepairRecoveryPlanner
{
    public static RepairRecoveryPlan Plan(
        RepairJournalTransactionSummary summary, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var restorable = new List<RepairRecoveryField>();
        var uncertain = new List<string>();
        var refusals = new List<RepairTransactionRefusal>();

        foreach (var step in summary.Steps)
        {
            if (step.Uncertain)
            {
                uncertain.Add(step.ActionId);
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "步骤 " + step.ActionId + " 只有 intent 没有 applied，无法证明归属；不得自动重放 setter。"));
                continue;
            }

            if (!step.AppliedPresent || step.RollbackPresent) continue;

            var before = step.Before ?? (step.AppliedReadback is null ? null : step.Before);
            if (before is null)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "步骤 " + step.ActionId + " 缺少可重建的 before 值。"));
                continue;
            }

            restorable.Add(new RepairRecoveryField
            {
                Index = step.Index,
                ActionId = step.ActionId,
                Field = step.Field,
                Store = step.Store,
                Family = step.Family,
                Before = before,
                After = step.After ?? before,
            });
        }

        var requiresNewAuthorization = summary.IsCommitted;
        if (requiresNewAuthorization)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.AuthorizationInvalid,
                "已提交事务的主动撤销必须建立新的恢复计划并再次授权；旧授权/旧 nonce 不可复用。"));
        }

        var planId = "recovery-" + summary.TransactionId;
        var targetFingerprint = summary.Target?.TargetFingerprint ?? string.Empty;
        var digestSource = planId + "|" + summary.TransactionId + "|" + summary.LatestState
            + "|" + targetFingerprint
            + "|" + string.Join(",", restorable.Select(field => field.Index + ":" + field.ActionId + ":" + field.Before.Canonical))
            + "|" + string.Join(",", uncertain.Order(StringComparer.Ordinal));
        var digest = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(digestSource)));

        return new RepairRecoveryPlan
        {
            RecoveryPlanId = planId,
            SourceTransactionId = summary.TransactionId,
            SourceState = summary.LatestState,
            RestorableFields = restorable,
            UncertainActionIds = uncertain,
            RequiresUserConfirmation = true,
            RequiresNewAuthorization = true,
            TargetFingerprint = targetFingerprint,
            Target = summary.Target,
            CreatedAtUtc = nowUtc,
            Refusals = refusals,
            Digest = digest,
        };
    }
}

/// <summary>The new authorization a recovery execution must present; it replaces the old nonce.</summary>
public sealed record RepairRecoveryAuthorization
{
    public required string RecoveryPlanId { get; init; }

    public required string RecoveryPlanDigest { get; init; }

    public required string UserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required string Nonce { get; init; }

    public required DateTimeOffset IssuedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public bool PerActionConsent { get; init; } = true;
}

public sealed record RepairRecoveryRequest
{
    public required RepairRecoveryPlan Plan { get; init; }

    public required RepairRecoveryAuthorization Authorization { get; init; }

    public required IRepairFieldAdapter Adapter { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required string CurrentBootId { get; init; }

    public required string UserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public bool DevelopmentIsolation { get; init; }
}

public sealed record RepairRecoveryResult
{
    public required string SourceTransactionId { get; init; }

    public required RepairTransactionState FinalState { get; init; }

    public required IReadOnlyList<RepairStepOutcome> Steps { get; init; }

    public required IReadOnlyList<string> UncertainActionIds { get; init; }

    public required IReadOnlyList<RepairTransactionRefusal> Refusals { get; init; }

    public required bool WroteAnyField { get; init; }
}

/// <summary>
/// Executes a user-confirmed recovery against the authoritative journal. It re-reads the journal
/// itself, validates a brand-new authorization, and restores only fields with an applied record
/// whose current value is still the value this transaction wrote. Conflicts are preserved; intent
/// without applied is reported and never touched. Recovery never depends on the network.
/// </summary>
public sealed class RepairRecoveryExecutor
{
    private readonly RepairJournal _journal;
    private readonly TimeProvider _clock;

    public RepairRecoveryExecutor(RepairJournal journal, TimeProvider? clock = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<RepairRecoveryResult> ExecuteAsync(
        RepairRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        var steps = new List<RepairStepOutcome>();
        var refusals = new List<RepairTransactionRefusal>();
        var wrote = false;
        RepairRecoveryPlan authoritative = null!;

        RepairRecoveryResult Result(RepairTransactionState state) => new()
        {
            SourceTransactionId = request.Plan.SourceTransactionId,
            FinalState = state,
            Steps = steps.ToArray(),
            UncertainActionIds = authoritative.UncertainActionIds,
            Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
            WroteAnyField = wrote,
        };

        if (!_journal.IsUsable)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalCorrupt,
                "权威 journal 不可用，拒绝恢复。"));
            return Result(RepairTransactionState.RecoveryRequired);
        }

        var scan = RepairJournalRecoveryScanner.Scan(_journal, request.NowUtc);
        var summary = scan.Transactions.FirstOrDefault(item =>
            string.Equals(item.TransactionId, request.Plan.SourceTransactionId, StringComparison.Ordinal));
        if (summary is null)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalCorrupt,
                "journal 中没有该事务记录，恢复不能凭空开始。"));
            return Result(RepairTransactionState.RecoveryRequired);
        }

        // Recovery is only ever derived from the authoritative journal, never from a client plan.
        authoritative = RepairRecoveryPlanner.Plan(summary, request.NowUtc);
        var sameScope = authoritative.RestorableFields.Count == request.Plan.RestorableFields.Count
            && authoritative.RestorableFields.Zip(request.Plan.RestorableFields).All(pair =>
                pair.First.Index == pair.Second.Index
                && string.Equals(pair.First.ActionId, pair.Second.ActionId, StringComparison.Ordinal)
                && string.Equals(pair.First.Before.Canonical, pair.Second.Before.Canonical, StringComparison.Ordinal)
                && string.Equals(pair.First.After.Canonical, pair.Second.After.Canonical, StringComparison.Ordinal))
            && authoritative.UncertainActionIds.SequenceEqual(request.Plan.UncertainActionIds, StringComparer.Ordinal);
        if (!sameScope
            || !string.Equals(authoritative.Digest, request.Plan.Digest, StringComparison.Ordinal)
            || !string.Equals(authoritative.RecoveryPlanId, request.Plan.RecoveryPlanId, StringComparison.Ordinal)
            || !string.Equals(authoritative.SourceTransactionId, request.Plan.SourceTransactionId, StringComparison.Ordinal))
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.JournalCorrupt,
                "恢复计划与权威 journal 不一致；拒绝执行客户端构造的恢复范围。"));
            return Result(RepairTransactionState.RecoveryRequired);
        }

        if (!string.Equals(request.Authorization.RecoveryPlanId, request.Plan.RecoveryPlanId, StringComparison.Ordinal)
            || !string.Equals(request.Authorization.RecoveryPlanDigest, request.Plan.Digest, StringComparison.Ordinal)
            || !string.Equals(request.Authorization.UserSid, request.UserSid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.Authorization.LogonSessionId, request.LogonSessionId, StringComparison.Ordinal)
            || request.NowUtc >= request.Authorization.ExpiresAtUtc
            || request.Authorization.IssuedAtUtc > request.NowUtc + RepairLimits.ClockSkewTolerance
            || !request.Authorization.PerActionConsent
            || string.IsNullOrWhiteSpace(request.Authorization.Nonce))
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.AuthorizationInvalid,
                "恢复授权与恢复计划、用户、会话或有效期不一致；必须重新授权。"));
            return Result(RepairTransactionState.RecoveryRequired);
        }

        if (request.Plan.Target is { } target)
        {
            if (!string.Equals(target.BootId, request.CurrentBootId, StringComparison.Ordinal))
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.CrossBoot,
                    "恢复计划属于另一个启动会话；不能跨 boot 自动恢复。"));
                return Result(RepairTransactionState.RecoveryRequired);
            }

            if (!string.Equals(target.TargetFingerprint, request.Plan.TargetFingerprint, StringComparison.Ordinal))
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.TargetRebuilt,
                    "目标身份已变化；恢复计划必须按新身份重新生成。"));
                return Result(RepairTransactionState.RecoveryRequired);
            }
        }

        if (authoritative.RestorableFields.Count == 0)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.RecoveryUncertain,
                "没有可证明归属且可恢复的字段；保持 recovery_required，需人工处理。"));
            return Result(RepairTransactionState.RecoveryRequired);
        }

        var identity = IdentityFrom(request.Plan.Target);

        foreach (var field in authoritative.RestorableFields.OrderByDescending(item => item.Index))
        {
            var current = await request.Adapter
                .GetAsync(identity, field.Store, field.Field, CancellationToken.None)
                .ConfigureAwait(false);
            if (!current.IsUsable)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RollbackFailed,
                    "恢复时无法读取当前值。"));
                Append(RepairJournalKind.TransactionRollbackFailed, RepairTransactionState.RollbackFailed, request,
                    RepairTransactionRefusalCode.RollbackFailed);
                return Result(RepairTransactionState.RollbackFailed);
            }

            var currentText = current.Value!.CanonicalText;
            if (string.Equals(currentText, field.Before.Canonical, StringComparison.Ordinal))
            {
                AppendRollback(field, alreadyRestored: true, field.Before, request);
                steps.Add(new RepairStepOutcome
                {
                    Index = field.Index,
                    ActionId = field.ActionId,
                    Phase = "already_restored",
                    Readback = current.Value,
                });
                continue;
            }

            if (!string.Equals(currentText, field.After.Canonical, StringComparison.Ordinal))
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RollbackConflict,
                    "当前值既不是 before 也不是 after；保留现值，不覆盖外部修改。"));
                Append(RepairJournalKind.TransactionRollbackConflict, RepairTransactionState.RollbackConflict, request,
                    RepairTransactionRefusalCode.RollbackConflict);
                steps.Add(new RepairStepOutcome
                {
                    Index = field.Index,
                    ActionId = field.ActionId,
                    Phase = "conflict",
                    Readback = current.Value,
                    Detail = "external value preserved",
                });
                return Result(RepairTransactionState.RollbackConflict);
            }

            AppendRollbackIntent(field, request);
            var restore = field.Before.ToValue();
            var write = await request.Adapter
                .SetAsync(identity, field.Store, field.Field, restore, CancellationToken.None)
                .ConfigureAwait(false);
            if (write.Status == RepairFieldWriteStatus.OutcomeUnknown)
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RecoveryUncertain,
                    "恢复写入结果未知，保持 recovery_required。"));
                return Result(RepairTransactionState.RecoveryRequired);
            }

            if (write.Status is RepairFieldWriteStatus.Unavailable or RepairFieldWriteStatus.Rejected
                || write.Readback is null
                || !string.Equals(write.Readback.CanonicalText, field.Before.Canonical, StringComparison.Ordinal))
            {
                refusals.Add(RepairTransactionRefusal.Of(
                    RepairTransactionRefusalCode.RollbackFailed,
                    "恢复写入没有读回 before 值。"));
                Append(RepairJournalKind.TransactionRollbackFailed, RepairTransactionState.RollbackFailed, request,
                    RepairTransactionRefusalCode.RollbackFailed);
                return Result(RepairTransactionState.RollbackFailed);
            }

            wrote = true;
            AppendRollback(field, alreadyRestored: false, field.Before, request);
            steps.Add(new RepairStepOutcome
            {
                Index = field.Index,
                ActionId = field.ActionId,
                Phase = "rolled_back",
                Readback = write.Readback,
            });
        }

        if (authoritative.UncertainActionIds.Count > 0)
        {
            refusals.Add(RepairTransactionRefusal.Of(
                RepairTransactionRefusalCode.RecoveryUncertain,
                "存在只有 intent 的步骤；它们保持 recovery_required，未重放 setter。"));
            Append(RepairJournalKind.TransactionRecoveryRequired, RepairTransactionState.RecoveryRequired, request,
                RepairTransactionRefusalCode.RecoveryUncertain);
            return Result(RepairTransactionState.RecoveryRequired);
        }

        Append(RepairJournalKind.TransactionRolledBack, RepairTransactionState.RolledBack, request, null);
        return Result(RepairTransactionState.RolledBack);
    }

    private void Append(
        RepairJournalKind kind, RepairTransactionState state, RepairRecoveryRequest request,
        RepairTransactionRefusalCode? refusal)
    {
        try
        {
            _journal.Append(new RepairJournalEntry
            {
                TransactionId = request.Plan.SourceTransactionId,
                Kind = kind,
                State = state,
                AtUtc = request.NowUtc,
                RefusalCode = refusal,
            });
        }
        catch (RepairJournalException)
        {
            // A journal that cannot record the recovery outcome is itself recovery_required; the
            // caller sees the in-memory state and must not claim a durable rollback.
        }
    }

    private void AppendRollbackIntent(RepairRecoveryField field, RepairRecoveryRequest request)
    {
        try
        {
            _journal.Append(Base(field, request, RepairJournalKind.RollbackIntent));
        }
        catch (RepairJournalException)
        {
            // Reported by the authoritative journal being unusable on the next execution.
        }
    }

    private void AppendRollback(
        RepairRecoveryField field, bool alreadyRestored, RepairJournalValue readback, RepairRecoveryRequest request)
    {
        try
        {
            _journal.Append(Base(field, request, RepairJournalKind.RollbackApplied) with
            {
                ReadbackValue = readback,
                AlreadyRestored = alreadyRestored,
            });
        }
        catch (RepairJournalException)
        {
            // Same rule: a non-durable recovery record must not be claimed as rolled_back.
        }
    }

    private static RepairJournalEntry Base(
        RepairRecoveryField field, RepairRecoveryRequest request, RepairJournalKind kind) => new()
    {
        TransactionId = request.Plan.SourceTransactionId,
        Kind = kind,
        State = RepairTransactionState.RollingBack,
        AtUtc = request.NowUtc,
        StepIndex = field.Index,
        ActionId = field.ActionId,
        Field = field.Field,
        BeforeValue = field.Before,
        AfterValue = field.After,
    };

    private static InterfaceIdentity IdentityFrom(RepairJournalTarget? target)
    {
        if (target is null) throw new InvalidOperationException("recovery needs a journal target identity");
        return new InterfaceIdentity
        {
            InterfaceGuid = target.InterfaceGuid,
            Luid = target.Luid,
            IfIndex = target.IfIndex,
            Family = target.Family,
            CompartmentId = target.CompartmentId,
            DeviceInstanceId = target.DeviceInstanceId,
            Kind = InterfaceKind.Unknown,
            BootId = target.BootId,
        };
    }
}




