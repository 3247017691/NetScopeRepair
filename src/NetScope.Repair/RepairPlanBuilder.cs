using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// One untrusted request to change one field. Text is parsed by
/// <see cref="RepairActionParser"/>; an unknown action, store or value never becomes a plan.
/// </summary>
public sealed record RepairActionRequest
{
    public required string Action { get; init; }

    public required string Store { get; init; }

    public required string DesiredValue { get; init; }
}

public sealed record RepairPlanRequest
{
    public required string PlanId { get; init; }

    public required string SnapshotId { get; init; }

    public string? DiagnosisId { get; init; }

    public string? IncidentId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required RepairClockDomain PlannerClock { get; init; }

    public required RepairPreflight Preflight { get; init; }

    public required PlatformCapabilityMatrix Capabilities { get; init; }

    public required IReadOnlyList<RepairActionRequest> ActionRequests { get; init; }

    public TimeSpan PlanTtl { get; init; } = RepairLimits.DefaultPlanTtl;

    public bool Synthetic { get; init; }
}

/// <summary>
/// Builds a pure preview plan. It never opens a socket, calls a native setter or touches the
/// registry; every capability in this delivery resolves to unavailable/disallowed, so the
/// resulting plan is never executable.
/// </summary>
public sealed class RepairPlanBuilder
{
    public RepairPlan Build(RepairPlanRequest request)
    {
        var refusals = new List<RepairRefusal>();
        var missing = new List<string>(request.Preflight.MissingEvidence);
        var noOps = new List<RepairNoOpField>();

        refusals.AddRange(request.Preflight.Refusals);

        if (request.ActionRequests.Count == 0)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.NoActions,
                "计划没有任何动作请求；空计划不是修复，也不计修复次数。"));
        }
        else if (request.ActionRequests.Count > RepairLimits.MaxActionsPerPlan)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.ActionLimitExceeded,
                "单个计划最多 " + RepairLimits.MaxActionsPerPlan + " 个动作。"));
        }

        var parsed = ParseRequests(request, refusals);
        var identity = request.Preflight.Interface.Value;

        if (!request.Preflight.IsComplete)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CriticalUnknown,
                "preflight 未完成（" + request.Preflight.Status + "），不能生成可执行计划。"));
        }

        if (identity is null)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.IdentityUnknown,
                "缺少接口身份，无法生成计划。"));
        }
        else
        {
            if (identity.Family != RepairAddressFamily.Ipv4)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.FamilyNotSupported,
                    "目标地址族为 " + WireNames.Of(identity.Family) + "；V1 只开放 IPv4 TUN 字段。"));
            }

            if (identity.Kind != InterfaceKind.Tun)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.NotTun,
                    "目标不是已验证的 TUN 接口（" + WireNames.Of(identity.Kind) + "）。"));
            }
        }

        var parsedByKind = parsed.ToDictionary(item => item.Kind);
        var drafts = new List<ActionDraft>();

        foreach (var item in parsed)
        {
            var fieldName = WireNames.Of(item.Field);

            if (item.Kind == RepairActionKind.RecoverTransaction)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.RecoverRequiresJournal,
                    "RecoverTransaction 只能由 broker 从权威 journal 发起，不能由诊断或计划请求创建。",
                    action: WireNames.Of(item.Kind)));
                continue;
            }

            if (item.Store != RepairStore.ActiveStore)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.StoreNotSupported,
                    "V1 只允许已验证的 ActiveStore 动作，拒绝 " + WireNames.Of(item.Store) + "。",
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
                continue;
            }

            if (identity is null)
            {
                continue;
            }

            var fieldFact = request.Preflight.FieldFact(item.Field, item.Store);
            if (fieldFact?.Value?.Value is not { } before)
            {
                missing.Add("field:" + fieldName + ":" + WireNames.Of(item.Store));
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.CriticalUnknown,
                    "动作 " + WireNames.Of(item.Kind) + " 缺少 before 值（" + WireNames.Of(item.Store) + "/" + fieldName + "）。",
                    field: fieldName,
                    action: WireNames.Of(item.Kind),
                    missingEvidence: new[] { "field:" + fieldName + ":" + WireNames.Of(item.Store) }));
                continue;
            }

            if (before.Field != item.Field)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.CriticalUnknown,
                    "before 值类型与字段 " + fieldName + " 不匹配。",
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
                continue;
            }

            if (before is InterfaceMetricValue { Kind: not MetricValueKind.ManualConfigured })
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ValueNotManual,
                    "InterfaceMetric 当前是有效派生值，不能作为写入/恢复基线；必须重新读取手动配置值。",
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
                continue;
            }

            var key = RepairFieldKeys.For(identity, item.Store, item.Field);
            var observedKey = new RepairFieldKey(
                identity.InterfaceGuid,
                identity.Family,
                fieldFact.Value.Store,
                fieldFact.Value.Field);
            var noOp = RepairNoOpEvaluator.Assess(key, observedKey, before, item.Desired);
            if (noOp.IsNoOp)
            {
                noOps.Add(new RepairNoOpField
                {
                    Field = item.Field,
                    Store = item.Store,
                    TargetFingerprint = identity.TargetFingerprint,
                    Value = item.Desired.CanonicalText,
                    Reason = noOp.Reason,
                });
                continue;
            }

            if (noOp.ReasonCode == "value_not_manual")
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ValueNotManual,
                    noOp.Reason,
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
                continue;
            }

            if (item.Kind == RepairActionKind.SetTunIpv4InterfaceMetric
                && request.Preflight.FieldValue(RepairField.AutomaticMetric, RepairStore.ActiveStore)
                    is AutomaticMetricValue { Enabled: true }
                && !(parsedByKind.TryGetValue(RepairActionKind.SetTunIpv4AutomaticMetric, out var automaticRequest)
                    && automaticRequest.Desired is AutomaticMetricValue { Enabled: false }))
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.DependencyMissing,
                    "AutomaticMetric 仍为 enabled；只改 InterfaceMetric 不会成为有效的手动值，必须把 AutomaticMetric→disabled 作为组合授权的一部分。",
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
                continue;
            }

            var capability = request.Capabilities.Resolve(item.Kind, item.Store, identity.Family);
            var blocking = new List<RepairRefusal>();

            if (!capability.IsAvailable)
            {
                blocking.Add(RepairRefusal.Of(
                    capability.Status == CapabilityStatus.Disallowed
                        ? RepairRefusalCode.CapabilityDisallowed
                        : RepairRefusalCode.CapabilityUnavailable,
                    capability.Reason,
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
            }
            else if (item.Kind == RepairActionKind.SetTunIpv4InterfaceMetric
                && request.Preflight.FieldValue(RepairField.AutomaticMetric, RepairStore.ActiveStore)
                    is AutomaticMetricValue { Enabled: true }
                && !capability.VerifiedChecks.Contains(RepairExperimentChecks.CouplingClosed, StringComparer.Ordinal))
            {
                blocking.Add(RepairRefusal.Of(
                    RepairRefusalCode.CouplingUnverified,
                    "AutomaticMetric↔InterfaceMetric 的耦合影响没有已验证的平台实验，不能假设两个字段独立。",
                    field: fieldName,
                    action: WireNames.Of(item.Kind)));
            }

            drafts.Add(new ActionDraft
            {
                Kind = item.Kind,
                Field = item.Field,
                Store = item.Store,
                Before = before,
                Desired = item.Desired,
                Capability = capability,
                Blocking = blocking,
            });
        }

        if (refusals.Count > 0)
        {
            return Finalize(new RepairPlan
            {
                PlanId = request.PlanId,
                SchemaVersion = RepairPlan.CurrentSchemaVersion,
                CreatedAtUtc = request.NowUtc,
                ExpiresAtUtc = ExpiresAt(request),
                SnapshotId = request.SnapshotId,
                PreflightId = request.Preflight.PreflightId,
                DiagnosisId = request.DiagnosisId,
                IncidentId = request.IncidentId,
                BootId = request.Preflight.CurrentBootId,
                PlannerClock = request.PlannerClock,
                ClockNotice = RepairClockNotice.Text,
                Decision = RepairPlanDecision.Refused,
                Digest = string.Empty,
                TargetFingerprint = request.Preflight.TargetFingerprint,
                Actions = Array.Empty<RepairAction>(),
                NoOpFields = noOps,
                Refusals = refusals,
                EvidenceIds = Evidence(request, Array.Empty<RepairAction>()),
                MissingEvidence = missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Authorization = Terms(request, Array.Empty<string>(), Array.Empty<RepairDependencyGroup>()),
                ExecutionAllowed = false,
                Synthetic = request.Synthetic || request.Preflight.Synthetic,
            });
        }

        if (drafts.Count == 0)
        {
            return Finalize(new RepairPlan
            {
                PlanId = request.PlanId,
                SchemaVersion = RepairPlan.CurrentSchemaVersion,
                CreatedAtUtc = request.NowUtc,
                ExpiresAtUtc = ExpiresAt(request),
                SnapshotId = request.SnapshotId,
                PreflightId = request.Preflight.PreflightId,
                DiagnosisId = request.DiagnosisId,
                IncidentId = request.IncidentId,
                BootId = request.Preflight.CurrentBootId,
                PlannerClock = request.PlannerClock,
                ClockNotice = RepairClockNotice.Text,
                Decision = RepairPlanDecision.NoOp,
                Digest = string.Empty,
                TargetFingerprint = request.Preflight.TargetFingerprint,
                Actions = Array.Empty<RepairAction>(),
                NoOpFields = noOps,
                Refusals = Array.Empty<RepairRefusal>(),
                EvidenceIds = request.Preflight.EvidenceIds,
                MissingEvidence = Array.Empty<string>(),
                Authorization = Terms(request, Array.Empty<string>(), Array.Empty<RepairDependencyGroup>()),
                ExecutionAllowed = false,
                Synthetic = request.Synthetic || request.Preflight.Synthetic,
            });
        }

        var orderedDrafts = drafts
            .OrderBy(draft => RepairActionRules.OrderOf(draft.Kind))
            .ThenBy(draft => WireNames.Of(draft.Kind), StringComparer.Ordinal)
            .ToArray();

        var automaticActionId = orderedDrafts
            .Where(draft => draft.Kind == RepairActionKind.SetTunIpv4AutomaticMetric)
            .Select(draft => RepairActionRules.ActionIdOf(draft.Kind))
            .FirstOrDefault();

        var actions = new List<RepairAction>();
        foreach (var draft in orderedDrafts)
        {
            var actionId = RepairActionRules.ActionIdOf(draft.Kind);
            var dependencies = draft.Kind == RepairActionKind.SetTunIpv4InterfaceMetric
                && automaticActionId is not null
                ? new[] { automaticActionId }
                : Array.Empty<string>();
            var fieldFact = request.Preflight.FieldFact(draft.Field, draft.Store)
                ?? throw new InvalidOperationException("A built draft must have its source field fact.");

            actions.Add(new RepairAction
            {
                ActionId = actionId,
                Kind = draft.Kind,
                Field = draft.Field,
                Store = draft.Store,
                Family = identity!.Family,
                TargetFingerprint = identity.TargetFingerprint,
                Identity = identity,
                Before = draft.Before,
                Desired = draft.Desired,
                BeforeSource = fieldFact.Source,
                BeforeSourceVersion = fieldFact.SourceVersion,
                BeforeStatus = fieldFact.Status,
                BeforeObservedAtUtc = fieldFact.ObservedAtUtc,
                BeforeTtl = fieldFact.Ttl,
                DependsOnActionIds = dependencies,
                EvidenceIds = request.Preflight.EvidenceIds
                    .Concat(draft.Capability.EvidenceIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                Capability = draft.Capability,
                Eligibility = draft.Blocking.Count == 0
                    ? RepairActionEligibility.Allowed
                    : RepairActionEligibility.Blocked,
                BlockingReasons = draft.Blocking,
                Risk = RiskOf(draft.Kind),
                RollbackCondition = RollbackOf(draft.Kind),
                ObservationPlan = ObservationOf(draft.Kind),
                ObservationWindow = TimeSpan.FromSeconds(60),
                ValidUntilUtc = ExpiresAt(request),
            });
        }

        var groups = new List<RepairDependencyGroup>();
        if (automaticActionId is not null
            && actions.Any(action => action.Kind == RepairActionKind.SetTunIpv4InterfaceMetric))
        {
            groups.Add(new RepairDependencyGroup
            {
                GroupId = "group:automatic_metric+interface_metric",
                ActionIds = new[]
                {
                    automaticActionId,
                    RepairActionRules.ActionIdOf(RepairActionKind.SetTunIpv4InterfaceMetric),
                },
                RequiredTogether = true,
            });
        }

        var actionArray = actions.ToArray();
        var executionAllowed = !request.Capabilities.IsSynthetic
            && actionArray.Length > 0
            && actionArray.All(action => action.Eligibility == RepairActionEligibility.Allowed);

        return Finalize(new RepairPlan
        {
            PlanId = request.PlanId,
            SchemaVersion = RepairPlan.CurrentSchemaVersion,
            CreatedAtUtc = request.NowUtc,
            ExpiresAtUtc = ExpiresAt(request),
            SnapshotId = request.SnapshotId,
            PreflightId = request.Preflight.PreflightId,
            DiagnosisId = request.DiagnosisId,
            IncidentId = request.IncidentId,
            BootId = request.Preflight.CurrentBootId,
            PlannerClock = request.PlannerClock,
            ClockNotice = RepairClockNotice.Text,
            Decision = RepairPlanDecision.Planned,
            Digest = string.Empty,
            TargetFingerprint = request.Preflight.TargetFingerprint,
            Actions = actionArray,
            NoOpFields = noOps,
            Refusals = Array.Empty<RepairRefusal>(),
            EvidenceIds = Evidence(request, actionArray),
            MissingEvidence = Array.Empty<string>(),
            Authorization = Terms(request, actionArray.Select(action => action.ActionId).ToArray(), groups),
            ExecutionAllowed = executionAllowed,
            Synthetic = request.Synthetic || request.Preflight.Synthetic,
        });
    }

    private static List<ParsedRequest> ParseRequests(
        RepairPlanRequest request,
        List<RepairRefusal> refusals)
    {
        var parsed = new List<ParsedRequest>();
        var seen = new HashSet<RepairActionKind>();

        foreach (var actionRequest in request.ActionRequests)
        {
            var actionName = actionRequest.Action ?? string.Empty;
            if (!RepairActionParser.TryParseKind(actionRequest.Action, out var kind))
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ActionNotAllowed,
                    "动作 '" + actionName + "' 不在闭集内；禁止通用 SetProperty、任意属性名或脚本动作。",
                    action: actionName));
                continue;
            }

            if (!RepairActionParser.TryParseStore(actionRequest.Store, out var store))
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ActionNotAllowed,
                    "store '" + actionRequest.Store + "' 不是 active_store 或 persistent_store，拒绝猜测。",
                    action: WireNames.Of(kind)));
                continue;
            }

            if (kind == RepairActionKind.RecoverTransaction)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.RecoverRequiresJournal,
                    "RecoverTransaction 不能由调用方携带期望值；只允许 broker 按权威 journal 恢复。",
                    action: WireNames.Of(kind)));
                continue;
            }

            if (!RepairActionParser.TryParseDesired(kind, actionRequest.DesiredValue, out var desired, out var error)
                || desired is null)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ActionValueInvalid,
                    error ?? "动作值无效。",
                    action: WireNames.Of(kind)));
                continue;
            }

            if (!seen.Add(kind))
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.DuplicateAction,
                    "同一计划重复请求 " + WireNames.Of(kind) + "；闭集每字段每 store 只能出现一次。",
                    field: WireNames.Of(RepairActionRules.FieldOf(kind)),
                    action: WireNames.Of(kind)));
                continue;
            }

            parsed.Add(new ParsedRequest(kind, RepairActionRules.FieldOf(kind), store, desired));
        }

        return parsed;
    }

    private static RepairPlan Finalize(RepairPlan plan)
    {
        var digest = RepairPlanDigest.Compute(plan);
        return plan with
        {
            Digest = digest,
            Authorization = plan.Authorization with { PlanDigest = digest },
        };
    }

    private static DateTimeOffset ExpiresAt(RepairPlanRequest request)
    {
        var ttl = request.PlanTtl;
        if (ttl <= TimeSpan.Zero || ttl > RepairLimits.MaxPlanTtl)
        {
            ttl = RepairLimits.MaxPlanTtl;
        }

        return request.NowUtc + ttl;
    }

    private static RepairAuthorizationTerms Terms(
        RepairPlanRequest request,
        IReadOnlyList<string> actionIds,
        IReadOnlyList<RepairDependencyGroup> groups) => new()
        {
            PlanDigest = "pending",
            ExpiresAtUtc = ExpiresAt(request),
            ActionIds = actionIds,
            DependencyGroups = groups,
            PerActionConsentRequired = true,
            DenyByDefault = true,
            UacIsNotConsent = true,
            BindingIssued = false,
        };

    private static IReadOnlyList<string> Evidence(
        RepairPlanRequest request,
        IReadOnlyList<RepairAction> actions) => request.Preflight.EvidenceIds
        .Concat(actions.SelectMany(action => action.EvidenceIds))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static RepairRiskLevel RiskOf(RepairActionKind kind) => kind switch
    {
        RepairActionKind.SetTunIpv4AutomaticMetric => RepairRiskLevel.High,
        RepairActionKind.SetTunIpv4InterfaceMetric => RepairRiskLevel.Medium,
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes => RepairRiskLevel.Medium,
        _ => RepairRiskLevel.High,
    };

    private static string RollbackOf(RepairActionKind kind) => kind switch
    {
        RepairActionKind.SetTunIpv4InterfaceMetric =>
            "仅当本事务已写且当前仍等于 after 时，把 ActiveStore InterfaceMetric 恢复为记录的 before；值已被外部改变则报告冲突，不覆盖。",
        RepairActionKind.SetTunIpv4AutomaticMetric =>
            "AutomaticMetric 前态必须可读且能精确还原；耦合的 InterfaceMetric 未获组合授权或无法独立恢复时停止并报告，不扩大写集合。",
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes =>
            "仅当本事务已写且当前仍等于 after 时恢复原 IgnoreDefaultRoutes；若接口已重建或值被外部改变，保留并报告冲突。",
        _ => "恢复只来自权威 journal 且仅限本事务实际改过、当前仍等于 after 的字段。",
    };

    private static string ObservationOf(RepairActionKind kind) => kind switch
    {
        RepairActionKind.SetTunIpv4InterfaceMetric =>
            "读回 ActiveStore InterfaceMetric；核对非目标字段、目标路由与受保护上行未变；观察预算最多 60 秒且包含在事务总预算内。",
        RepairActionKind.SetTunIpv4AutomaticMetric =>
            "读回 AutomaticMetric 及其耦合的 InterfaceMetric；核对非目标字段与 PersistentStore 未被隐式修改，耦合未建模时不得提交。",
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes =>
            "读回 IgnoreDefaultRoutes；核对默认路由、受保护上行与非目标字段未变。",
        _ => "按计划预定的后置条件观察。",
    };

    private sealed record ParsedRequest(
        RepairActionKind Kind,
        RepairField Field,
        RepairStore Store,
        RepairFieldValue Desired);

    private sealed class ActionDraft
    {
        public required RepairActionKind Kind { get; init; }

        public required RepairField Field { get; init; }

        public required RepairStore Store { get; init; }

        public required RepairFieldValue Before { get; init; }

        public required RepairFieldValue Desired { get; init; }

        public required PlatformCapability Capability { get; init; }

        public required IReadOnlyList<RepairRefusal> Blocking { get; init; }
    }
}
