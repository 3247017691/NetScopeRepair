using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Broker;

public enum BrokerPreflightAvailability
{
    Unavailable,
    Available,
}

/// <summary>
/// Fresh facts the broker collected itself before any write. T14 has no production fact source
/// (that arrives with the transaction work), so the default source is explicitly unavailable
/// instead of letting the client's plan be treated as a preflight.
/// </summary>
public sealed record BrokerPreflightSnapshot
{
    public required BrokerPreflightAvailability Availability { get; init; }

    public required string? TargetFingerprint { get; init; }

    public required string? BootId { get; init; }

    public required DateTimeOffset? ObservedAtUtc { get; init; }

    public required IReadOnlyDictionary<RepairFieldKey, RepairFieldValue> FieldValues { get; init; }

    public string? Reason { get; init; }

    public static BrokerPreflightSnapshot Unavailable(string reason) => new()
    {
        Availability = BrokerPreflightAvailability.Unavailable,
        TargetFingerprint = null,
        BootId = null,
        ObservedAtUtc = null,
        FieldValues = new Dictionary<RepairFieldKey, RepairFieldValue>(),
        Reason = reason,
    };
}

public interface IBrokerPreflightSource
{
    Task<BrokerPreflightSnapshot> CollectAsync(RepairPlan plan, CancellationToken cancellationToken);
}

/// <summary>The only production registration in this delivery: fresh facts are not available.</summary>
public sealed class UnavailableBrokerPreflightSource : IBrokerPreflightSource
{
    public Task<BrokerPreflightSnapshot> CollectAsync(RepairPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BrokerPreflightSnapshot.Unavailable(
            "broker_preflight_not_implemented"));
    }
}

/// <summary>
/// The closed action whitelist. No generic property setter, no path and no free-form value can be
/// described: only the three T13 forward actions with their typed fields, ActiveStore and IPv4.
/// </summary>
public static class BrokerActionWhitelist
{
    public static IReadOnlyList<RepairActionKind> AllowedKinds { get; } = RepairActionRules.ForwardActions;

    /// <summary>Every action this protocol version may ever request. RecoverTransaction is journal-only.</summary>
    public static bool IsAllowed(RepairActionKind kind) => !RepairActionRules.IsForward(kind)
        ? false
        : AllowedKinds.Contains(kind);

    public static IReadOnlyList<BrokerRefusal> ValidateShape(RepairAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var refusals = new List<BrokerRefusal>();

        if (action.Kind == RepairActionKind.RecoverTransaction)
        {
            refusals.Add(BrokerRefusal.Of(
                BrokerRefusalCode.RecoverRequiresJournal,
                "RecoverTransaction is only valid from the broker's authoritative journal, which this delivery does not have."));
            return refusals;
        }

        if (!IsAllowed(action.Kind))
        {
            refusals.Add(BrokerRefusal.Of(BrokerRefusalCode.ActionNotAllowed, "The action is not in the broker whitelist."));
            return refusals;
        }

        if (action.Field != RepairActionRules.FieldOf(action.Kind))
            refusals.Add(BrokerRefusal.Of(BrokerRefusalCode.ActionFieldMismatch, "The action field does not match the action kind."));

        if (action.Store != RepairStore.ActiveStore)
            refusals.Add(BrokerRefusal.Of(BrokerRefusalCode.StoreNotSupported, "Only ActiveStore is opened by V1."));

        if (action.Family != RepairAddressFamily.Ipv4)
            refusals.Add(BrokerRefusal.Of(BrokerRefusalCode.FamilyNotSupported, "Only IPv4 is opened by V1."));

        var valueProblems = ValidateValues(action);
        refusals.AddRange(valueProblems);
        return refusals;
    }

    private static IEnumerable<BrokerRefusal> ValidateValues(RepairAction action)
    {
        if (action.Before is not { } before || action.Desired is not { } desired)
        {
            yield return BrokerRefusal.Of(BrokerRefusalCode.ActionValueInvalid, "The action is missing a typed before/desired value.");
            yield break;
        }

        switch (action.Kind)
        {
            case RepairActionKind.SetTunIpv4InterfaceMetric:
                if (before is not InterfaceMetricValue { Kind: MetricValueKind.ManualConfigured }
                    || desired is not InterfaceMetricValue { Kind: MetricValueKind.ManualConfigured })
                {
                    yield return BrokerRefusal.Of(
                        BrokerRefusalCode.ActionValueInvalid,
                        "InterfaceMetric requires manual configured uint values; a derived or unknown metric cannot be written or restored.");
                }

                break;
            case RepairActionKind.SetTunIpv4AutomaticMetric:
                if (before is not AutomaticMetricValue || desired is not AutomaticMetricValue)
                    yield return BrokerRefusal.Of(BrokerRefusalCode.ActionValueInvalid, "AutomaticMetric requires typed enabled/disabled values.");
                break;
            case RepairActionKind.SetTunIpv4IgnoreDefaultRoutes:
                if (before is not IgnoreDefaultRoutesValue || desired is not IgnoreDefaultRoutesValue)
                    yield return BrokerRefusal.Of(BrokerRefusalCode.ActionValueInvalid, "IgnoreDefaultRoutes requires typed true/false values.");
                break;
            default:
                yield return BrokerRefusal.Of(BrokerRefusalCode.ActionNotAllowed, "The action is not in the broker whitelist.");
                break;
        }
    }
}

public sealed record BrokerTransactionValidationRequest
{
    public required BrokerSubmitTransactionMessage Submit { get; init; }

    public required string InitiatingUserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required PlatformCapabilityMatrix Capabilities { get; init; }

    public required BrokerPreflightSnapshot Preflight { get; init; }
}

public sealed record BrokerTransactionDecision
{
    public required string TransactionId { get; init; }

    public required string PlanDigest { get; init; }

    /// <summary>True only when every independent check passed. Execution is still refused in T14.</summary>
    public required bool Accepted { get; init; }

    /// <summary>True when the caller must obtain a new preview (plan, target or values no longer match).</summary>
    public required bool RequiresNewPreview { get; init; }

    public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }
}

/// <summary>
/// The broker's independent validation of a submitted transaction. It recomputes the plan digest
/// from the received typed plan (so a changed value/target/store invalidates the consent), checks
/// the closed action whitelist, the capability matrix, its own preflight and the T13 authorization
/// binding. It never trusts a digest, a before value or an authorization supplied by the caller.
/// </summary>
public static class BrokerTransactionValidator
{
    public static BrokerTransactionDecision Validate(BrokerTransactionValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var submit = request.Submit;
        var plan = submit.Plan;
        var refusals = new List<BrokerRefusal>();
        var requiresNewPreview = false;

        if (!BrokerIdentifiers.IsSafe(submit.TransactionId)
            || !BrokerIdentifiers.IsSafe(submit.PlanDigest)
            || !BrokerIdentifiers.IsSafe(submit.TargetFingerprint))
        {
            refusals.Add(Refuse(BrokerRefusalCode.InvalidIdentifier, "The transaction identifiers are not bounded identifiers."));
        }

        var recomputedDigest = RepairPlanDigest.Compute(plan);
        if (!string.Equals(recomputedDigest, submit.PlanDigest, StringComparison.Ordinal)
            || !string.Equals(recomputedDigest, plan.Digest, StringComparison.Ordinal)
            || !string.Equals(recomputedDigest, submit.Authorization.PlanDigest, StringComparison.Ordinal))
        {
            refusals.Add(Refuse(BrokerRefusalCode.PlanDigestMismatch, "The received plan does not match the authorized plan digest."));
            requiresNewPreview = true;
        }

        if (!string.Equals(submit.Authorization.PlanId, plan.PlanId, StringComparison.Ordinal))
        {
            refusals.Add(Refuse(BrokerRefusalCode.PlanDigestMismatch, "The authorization references a different plan id."));
            requiresNewPreview = true;
        }

        if (!string.Equals(submit.TargetFingerprint, plan.TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(submit.TargetFingerprint, submit.Authorization.TargetFingerprint, StringComparison.Ordinal))
        {
            refusals.Add(Refuse(BrokerRefusalCode.TargetMismatch, "The transaction target differs from the planned and authorized target."));
            requiresNewPreview = true;
        }

        if (plan.Decision != RepairPlanDecision.Planned || plan.ChangedActions.Count == 0)
        {
            refusals.Add(Refuse(BrokerRefusalCode.PlanNotExecutable, "The plan is not a planned repair with changed actions."));
            requiresNewPreview = true;
        }

        if (plan.Synthetic)
        {
            refusals.Add(Refuse(BrokerRefusalCode.SyntheticPlanNotExecutable, "A synthetic plan can never be executed as a privileged repair."));
        }

        if (!plan.ExecutionAllowed || !plan.CanAuthorize)
        {
            refusals.Add(Refuse(BrokerRefusalCode.PlanNotExecutable, "The plan is not executable: capability or preflight checks did not pass."));
            requiresNewPreview = true;
        }

        if (plan.Actions.Count > BrokerProtocol.MaxActionsPerTransaction)
        {
            refusals.Add(Refuse(BrokerRefusalCode.ActionLimitExceeded, "The plan exceeds the 16-action transaction limit."));
        }
        else if (plan.Actions.Count == 0)
        {
            refusals.Add(Refuse(BrokerRefusalCode.NoActions, "The plan contains no actions."));
        }

        var seenActions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (!string.Equals(action.TargetFingerprint, plan.TargetFingerprint, StringComparison.Ordinal))
            {
                refusals.Add(Refuse(BrokerRefusalCode.TargetMismatch, "An action targets a different fingerprint than the plan."));
                requiresNewPreview = true;
            }

            if (!string.Equals(action.Identity.TargetFingerprint, action.TargetFingerprint, StringComparison.Ordinal))
            {
                refusals.Add(Refuse(BrokerRefusalCode.TargetMismatch, "An action identity no longer matches its target fingerprint."));
                requiresNewPreview = true;
            }

            var shape = BrokerActionWhitelist.ValidateShape(action);
            refusals.AddRange(shape);

            // An action outside the closed set has no wire name, target key or capability; the
            // whitelist refusal is final and nothing further may be computed for it.
            if (!Enum.IsDefined(action.Kind) || !BrokerActionWhitelist.IsAllowed(action.Kind))
                continue;

            var key = WireNames.Of(action.Kind) + "|" + action.TargetFingerprint + "|" + WireNames.Of(action.Store);
            if (!seenActions.Add(key))
                refusals.Add(Refuse(BrokerRefusalCode.DuplicateAction, "The plan contains a duplicate action for the same field/target/store."));

            var capability = request.Capabilities.Resolve(action.Kind, action.Store, action.Family);
            if (!capability.IsAvailable)
            {
                refusals.Add(Refuse(
                    capability.Status == CapabilityStatus.Disallowed
                        ? BrokerRefusalCode.CapabilityDisallowed
                        : BrokerRefusalCode.CapabilityUnavailable,
                    capability.Reason));
            }

            if (!action.IsNoOp && string.Equals(action.Before.CanonicalText, action.Desired.CanonicalText, StringComparison.Ordinal))
                refusals.Add(Refuse(BrokerRefusalCode.ActionValueInvalid, "The action has no change but is not marked as a no-op."));
        }

        if (request.NowUtc >= plan.ExpiresAtUtc)
        {
            refusals.Add(Refuse(BrokerRefusalCode.PlanExpired, "The plan has expired; a new preview is required."));
            requiresNewPreview = true;
        }

        foreach (var action in plan.Actions.Where(action => request.NowUtc >= action.ValidUntilUtc))
        {
            refusals.Add(Refuse(BrokerRefusalCode.PlanExpired, "An action preview has expired; a new preview is required."));
            requiresNewPreview = true;
        }

        var authorizationRefusals = RepairAuthorizationBindingValidator.Validate(
            submit.Authorization,
            plan,
            request.NowUtc,
            request.InitiatingUserSid,
            request.LogonSessionId);
        foreach (var refusal in authorizationRefusals)
        {
            refusals.Add(Refuse(Map(refusal.Code), refusal.Detail));
            if (refusal.Code is RepairRefusalCode.PlanExpired
                or RepairRefusalCode.PlanDigestMismatch
                or RepairRefusalCode.AuthorizationMismatch
                or RepairRefusalCode.ConsentMissing)
            {
                requiresNewPreview = true;
            }
        }

        var preflight = ValidatePreflight(request, plan);
        refusals.AddRange(preflight.Refusals);
        requiresNewPreview |= preflight.RequiresNewPreview;

        var distinct = refusals
            .GroupBy(refusal => refusal.Code)
            .Select(group => group.First())
            .ToArray();

        return new BrokerTransactionDecision
        {
            TransactionId = submit.TransactionId,
            PlanDigest = recomputedDigest,
            Accepted = distinct.Length == 0,
            RequiresNewPreview = requiresNewPreview,
            Refusals = distinct,
        };
    }

    /// <summary>Broker preflight: fresh facts must exist and match the plan's before values and target.</summary>
    private static BrokerPreflightValidation ValidatePreflight(
        BrokerTransactionValidationRequest request, RepairPlan plan)
    {
        var refusals = new List<BrokerRefusal>();
        var requiresNewPreview = false;
        var preflight = request.Preflight;
        if (preflight.Availability != BrokerPreflightAvailability.Available)
        {
            return new BrokerPreflightValidation
            {
                Refusals = new[]
                {
                    Refuse(
                        BrokerRefusalCode.PreflightUnavailable,
                        "The broker has no independent preflight fact source; the client's plan is not a preflight."),
                },
                RequiresNewPreview = false,
            };
        }

        if (!string.Equals(preflight.TargetFingerprint, plan.TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(preflight.BootId, plan.BootId, StringComparison.Ordinal))
        {
            refusals.Add(Refuse(BrokerRefusalCode.PreflightMismatch, "The broker preflight observed a different target or boot."));
            return new BrokerPreflightValidation { Refusals = refusals, RequiresNewPreview = true };
        }

        if (preflight.ObservedAtUtc is not { } observed
            || request.NowUtc - observed > RepairLimits.DefaultFactTtl + RepairLimits.ClockSkewTolerance
            || observed - request.NowUtc > RepairLimits.ClockSkewTolerance)
        {
            refusals.Add(Refuse(BrokerRefusalCode.PreflightMismatch, "The broker preflight facts are stale or from a different clock window."));
            return new BrokerPreflightValidation { Refusals = refusals, RequiresNewPreview = true };
        }

        foreach (var action in plan.Actions)
        {
            var key = RepairFieldKeys.For(action.Identity, action.Store, action.Field);
            if (!preflight.FieldValues.TryGetValue(key, out var current))
            {
                refusals.Add(Refuse(BrokerRefusalCode.PreflightMismatch, "The broker preflight is missing a planned field."));
                return new BrokerPreflightValidation { Refusals = refusals, RequiresNewPreview = true };
            }

            if (!string.Equals(current.CanonicalText, action.Before.CanonicalText, StringComparison.Ordinal))
            {
                refusals.Add(Refuse(
                    BrokerRefusalCode.RequiresNewPreview,
                    "The current value differs from the authorized before value; preview again instead of writing a new baseline."));
                return new BrokerPreflightValidation { Refusals = refusals, RequiresNewPreview = true };
            }
        }

        return new BrokerPreflightValidation { Refusals = refusals, RequiresNewPreview = requiresNewPreview };
    }

    private sealed record BrokerPreflightValidation
    {
        public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }

        public required bool RequiresNewPreview { get; init; }
    }

    private static BrokerRefusalCode Map(RepairRefusalCode code) => code switch
    {
        RepairRefusalCode.PlanNotExecutable => BrokerRefusalCode.PlanNotExecutable,
        RepairRefusalCode.PlanExpired => BrokerRefusalCode.PlanExpired,
        RepairRefusalCode.PlanDigestMismatch => BrokerRefusalCode.PlanDigestMismatch,
        RepairRefusalCode.AuthorizationMismatch => BrokerRefusalCode.AuthorizationMismatch,
        RepairRefusalCode.ConsentMissing => BrokerRefusalCode.ConsentMissing,
        RepairRefusalCode.CapabilityUnavailable => BrokerRefusalCode.CapabilityUnavailable,
        RepairRefusalCode.CapabilityDisallowed => BrokerRefusalCode.CapabilityDisallowed,
        RepairRefusalCode.RecoverRequiresJournal => BrokerRefusalCode.RecoverRequiresJournal,
        RepairRefusalCode.ActionNotAllowed => BrokerRefusalCode.ActionNotAllowed,
        RepairRefusalCode.ActionLimitExceeded => BrokerRefusalCode.ActionLimitExceeded,
        RepairRefusalCode.DuplicateAction => BrokerRefusalCode.DuplicateAction,
        RepairRefusalCode.NoActions => BrokerRefusalCode.NoActions,
        RepairRefusalCode.ActionValueInvalid => BrokerRefusalCode.ActionValueInvalid,
        _ => BrokerRefusalCode.PlanNotExecutable,
    };

    private static BrokerRefusal Refuse(BrokerRefusalCode code, string detail) => BrokerRefusal.Of(code, detail);
}

