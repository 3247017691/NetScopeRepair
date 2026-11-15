using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>One candidate interface with its attribution, used to detect multiple plausible TUNs.</summary>
public sealed record RepairTargetCandidate
{
    public required SourcedFact<InterfaceIdentity> Interface { get; init; }

    public required SourcedFact<TunAttribution> Attribution { get; init; }
}

/// <summary>
/// Everything the preflight is based on. Every fact carries its own source, status, UTC time,
/// clock domain, boot id and TTL; a single snapshot cannot make different domains atomic.
/// </summary>
public sealed record RepairPreflightRequest
{
    public required string PreflightId { get; init; }

    public required string SnapshotId { get; init; }

    public string? DiagnosisId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required string CurrentBootId { get; init; }

    public required RepairClockDomain EvaluatorClock { get; init; }

    public required SourcedFact<InterfaceIdentity> Interface { get; init; }

    public required IReadOnlyList<SourcedFact<RepairFieldState>> Fields { get; init; }

    public required SourcedFact<TunAttribution> Attribution { get; init; }

    public required SourcedFact<UplinkAssessment> Uplink { get; init; }

    public IReadOnlyList<RepairTargetCandidate> Candidates { get; init; } = Array.Empty<RepairTargetCandidate>();

    public TimeSpan ClockSkewTolerance { get; init; } = RepairLimits.ClockSkewTolerance;

    public bool Synthetic { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(
    typeof(NetScope.Domain.WireEnumJsonConverter<RepairPreflightStatus>))]
public enum RepairPreflightStatus
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("complete")]
    Complete,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("incomplete")]
    Incomplete,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("ambiguous")]
    Ambiguous,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("refused")]
    Refused,
}

/// <summary>
/// The platform facts a plan is allowed to rely on. A preflight is only usable when it is
/// complete: one critical unknown, one stale fact, one ambiguous candidate or one protected
/// uplink makes the whole target unusable.
/// </summary>
public sealed record RepairPreflight
{
    public required string PreflightId { get; init; }

    public required string SnapshotId { get; init; }

    public string? DiagnosisId { get; init; }

    public required DateTimeOffset EvaluatedAtUtc { get; init; }

    public required string CurrentBootId { get; init; }

    public required RepairClockDomain EvaluatorClock { get; init; }

    public required SourcedFact<InterfaceIdentity> Interface { get; init; }

    public required IReadOnlyList<SourcedFact<RepairFieldState>> Fields { get; init; }

    public required SourcedFact<TunAttribution> Attribution { get; init; }

    public required SourcedFact<UplinkAssessment> Uplink { get; init; }

    public required RepairPreflightStatus Status { get; init; }

    public required IReadOnlyList<RepairRefusal> Refusals { get; init; }

    public IReadOnlyList<string> MissingEvidence { get; init; } = Array.Empty<string>();

    public bool Synthetic { get; init; }

    public bool IsComplete => Status == RepairPreflightStatus.Complete;

    public string TargetFingerprint => Interface.Value?.TargetFingerprint ?? string.Empty;

    public string ClockNotice => RepairClockNotice.Text;

    public SourcedFact<RepairFieldState>? FieldFact(RepairField field, RepairStore store) =>
        Fields.FirstOrDefault(fact => fact.Value is { } state && state.Field == field && state.Store == store);

    public RepairFieldValue? FieldValue(RepairField field, RepairStore store) =>
        FieldFact(field, store)?.Value?.Value;

    /// <summary>All evidence ids cited by the facts this preflight used.</summary>
    public IReadOnlyList<string> EvidenceIds =>
        Interface.EvidenceIds
            .Concat(Attribution.EvidenceIds)
            .Concat(Uplink.EvidenceIds)
            .Concat(Fields.SelectMany(fact => fact.EvidenceIds))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<(RepairField Field, RepairStore Store)> RequiredFieldFacts { get; } = new[]
    {
        (RepairField.InterfaceMetric, RepairStore.ActiveStore),
        (RepairField.AutomaticMetric, RepairStore.ActiveStore),
        (RepairField.IgnoreDefaultRoutes, RepairStore.ActiveStore),
    };
}

/// <summary>
/// Builds the preflight from sourced facts. It is deliberately conservative: a partial source,
/// an expired fact, a future timestamp, an untrusted attribution or an unknown uplink is a gap
/// or a refusal, never a quiet default.
/// </summary>
public static class RepairPreflightEvaluator
{
    public static RepairPreflight Evaluate(RepairPreflightRequest request)
    {
        var refusals = new List<RepairRefusal>();
        var missing = new List<string>();

        if (!string.Equals(request.EvaluatorClock.BootId, request.CurrentBootId, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.ClockSkew,
                "评估进程时钟的 boot id 与当前 boot id 不一致，不能把该 preflight 当作当前事实。"));
        }

        EvaluateInterface(request, refusals, missing);
        EvaluateAmbiguity(request, refusals, missing);
        EvaluateAttribution(request, refusals, missing);
        EvaluateUplink(request, refusals, missing);
        EvaluateFields(request, refusals, missing);

        var status = refusals.Count == 0
            ? RepairPreflightStatus.Complete
            : refusals.Any(refusal => refusal.Code == RepairRefusalCode.InterfaceAmbiguous)
                ? RepairPreflightStatus.Ambiguous
                : missing.Count > 0 || refusals.Any(refusal => refusal.Code is RepairRefusalCode.CriticalUnknown
                    or RepairRefusalCode.IdentityUnknown
                    or RepairRefusalCode.IdentityIncomplete
                    or RepairRefusalCode.AttributionUnknown
                    or RepairRefusalCode.FactStale
                    or RepairRefusalCode.ClockSkew
                    or RepairRefusalCode.UplinkUnknown)
                    ? RepairPreflightStatus.Incomplete
                    : RepairPreflightStatus.Refused;

        return new RepairPreflight
        {
            PreflightId = request.PreflightId,
            SnapshotId = request.SnapshotId,
            DiagnosisId = request.DiagnosisId,
            EvaluatedAtUtc = request.NowUtc,
            CurrentBootId = request.CurrentBootId,
            EvaluatorClock = request.EvaluatorClock,
            Interface = request.Interface,
            Fields = request.Fields,
            Attribution = request.Attribution,
            Uplink = request.Uplink,
            Status = status,
            Refusals = refusals,
            MissingEvidence = missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Synthetic = request.Synthetic,
        };
    }

    private static void EvaluateInterface(
        RepairPreflightRequest request,
        List<RepairRefusal> refusals,
        List<string> missing)
    {
        var fact = request.Interface;
        if (!fact.IsKnown || fact.Value is null)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.IdentityUnknown,
                "接口身份不可读或来源状态不是 ok：" + (fact.Reason ?? fact.Status.ToString()) + "。"));
            missing.Add("interface:identity");
            return;
        }

        var identity = fact.Value;

        if (!string.Equals(fact.BootId, request.CurrentBootId, StringComparison.Ordinal)
            || !string.Equals(identity.BootId, request.CurrentBootId, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CrossBoot,
                "接口身份来自另一个启动会话（fact=" + fact.BootId + "，identity=" + identity.BootId
                + "，current=" + request.CurrentBootId + "）；不能跨 boot 写回 ifIndex/LUID。"));
        }

        AddFreshnessProblem(fact, request, "interface", refusals, missing);

        if (!identity.IsComplete)
        {
            var fields = identity.MissingCriticalFields;
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.IdentityIncomplete,
                "接口身份缺少关键字段：" + string.Join(", ", fields) + "。",
                missingEvidence: fields.Select(field => "identity:" + field)));
            missing.AddRange(fields.Select(field => "identity:" + field));
        }
    }

    private static void EvaluateAmbiguity(
        RepairPreflightRequest request,
        List<RepairRefusal> refusals,
        List<string> missing)
    {
        if (request.Candidates.Count == 0)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CriticalUnknown,
                "没有枚举 TUN 候选，无法排除第二个可归属目标；单个目标也要由完整候选集证明。",
                missingEvidence: new[] { "tun_candidates" }));
            missing.Add("tun_candidates");
            return;
        }

        var verifiedTargets = request.Candidates
            .Where(candidate => candidate.Interface.Value is not null && candidate.Attribution.Value is not null)
            .Where(candidate =>
                string.Equals(candidate.Interface.BootId, request.CurrentBootId, StringComparison.Ordinal)
                && string.Equals(candidate.Attribution.BootId, request.CurrentBootId, StringComparison.Ordinal)
                && RepairFactFreshnessEvaluator.Assess(candidate.Interface, request.NowUtc, request.ClockSkewTolerance)
                    == RepairFactFreshness.Fresh
                && RepairFactFreshnessEvaluator.Assess(candidate.Attribution, request.NowUtc, request.ClockSkewTolerance)
                    == RepairFactFreshness.Fresh)
            .Select(candidate => new
            {
                fingerprint = candidate.Interface.Value!.TargetFingerprint,
                attribution = TunAttributionPolicy.Normalize(candidate.Attribution.Value!),
            })
            .Where(candidate => candidate.attribution.Verdict == TunAttributionVerdict.Verified)
            .Select(candidate => candidate.fingerprint)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (verifiedTargets.Length > 1)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.InterfaceAmbiguous,
                "存在多个可归属的 TUN 候选（" + string.Join(", ", verifiedTargets)
                + "），不能自动选择或转移目标。"));
            missing.Add("single_verified_tun_target");
        }
    }

    private static void EvaluateAttribution(
        RepairPreflightRequest request,
        List<RepairRefusal> refusals,
        List<string> missing)
    {
        var fact = request.Attribution;
        if (!fact.IsKnown || fact.Value is null)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AttributionUnknown,
                "没有可读的核心→TUN 归属来源：" + (fact.Reason ?? fact.Status.ToString()) + "。"));
            missing.Add("attribution:tun_owner");
            return;
        }

        if (!string.Equals(fact.BootId, request.CurrentBootId, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CrossBoot,
                "核心→TUN 归属事实来自另一个 boot，不能用于当前计划。"));
            missing.Add("attribution:tun_owner");
            return;
        }

        AddFreshnessProblem(fact, request, "attribution:tun_owner", refusals, missing);

        var normalized = TunAttributionPolicy.Normalize(fact.Value);
        if (fact.Value.Verdict == TunAttributionVerdict.Verified
            && normalized.Verdict != TunAttributionVerdict.Verified)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AttributionUntrusted,
                normalized.Reason ?? "归属依据不可信。"));
            missing.Add("attribution:trusted_source");
            return;
        }

        if (normalized.Verdict != TunAttributionVerdict.Verified)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.AttributionUnknown,
                "核心→TUN 归属未验证（依据：" + TunAttributionPolicy.BasisText(normalized.Basis)
                + "）；名称、198.18 地址、磁盘 tun=true 与用户点选都不是强归属。"));
            missing.Add("attribution:verified");
        }
    }

    private static void EvaluateUplink(
        RepairPreflightRequest request,
        List<RepairRefusal> refusals,
        List<string> missing)
    {
        var fact = request.Uplink;
        if (!fact.IsKnown || fact.Value is null)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.UplinkUnknown,
                "受保护上行/默认路由事实不可读，无法证明目标不是真实出口。"));
            missing.Add("uplink:protection");
            return;
        }

        if (!string.Equals(fact.BootId, request.CurrentBootId, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CrossBoot,
                "受保护上行事实来自另一个 boot，不能用于当前计划。"));
            missing.Add("uplink:protection");
            return;
        }

        AddFreshnessProblem(fact, request, "uplink:protection", refusals, missing);

        var uplink = fact.Value;
        if (!uplink.ProtectedUplinkKnown || uplink.ProtectedInterfaceGuid is null)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.UplinkUnknown,
                "受保护上行身份未知（可能随时切换），拒绝在该状态下计划写入。"));
            missing.Add("uplink:protected_guid");
            return;
        }

        if (uplink.TargetOwnsDefaultRoute is null)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.UplinkUnknown,
                "目标是否拥有默认路由未知（需要 GetBestRoute2 或等价查询），不能判断它是否已成为真实出口。"));
            missing.Add("uplink:target_default_route");
            return;
        }

        if (request.Interface.Value is not { } target)
        {
            return;
        }

        if (InterfaceKinds.IsProtectedUplinkKind(target.Kind))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.ProtectedUplink,
                "目标是真实/共享上行（" + target.Kind + "），V1 禁止写入。"));
            return;
        }

        if (target.Kind != InterfaceKind.Tun)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.NotTun,
                "目标接口类型为 " + target.Kind + "，V1 只允许已验证的 TUN 接口。"));
            return;
        }

        if (target.InterfaceGuid == uplink.ProtectedInterfaceGuid.Value)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.ProtectedUplink,
                "目标接口就是当前受保护的上行接口，拒绝写入。"));
        }
    }

    private static void EvaluateFields(
        RepairPreflightRequest request,
        List<RepairRefusal> refusals,
        List<string> missing)
    {
        var duplicateFields = request.Fields
            .Where(fact => fact.Value is { Store: RepairStore.ActiveStore })
            .GroupBy(fact => fact.Value!.Field)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicate in duplicateFields)
        {
            var fieldName = NetScope.Domain.WireNames.Of(duplicate);
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CriticalUnknown,
                "同一 ActiveStore 字段 " + fieldName + " 有多个观测，无法确定 before；重复事实不是一致性证据。",
                field: fieldName,
                missingEvidence: new[] { "field:" + fieldName + ":active_store:unique" }));
            missing.Add("field:" + fieldName + ":active_store:unique");
        }

        foreach (var (field, store) in RepairPreflight.RequiredFieldFacts)
        {
            var fieldName = NetScope.Domain.WireNames.Of(field);
            var storeName = NetScope.Domain.WireNames.Of(store);
            var fact = request.Fields.FirstOrDefault(item =>
                item.Value is { } state && state.Field == field && state.Store == store);

            if (fact is null)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.CriticalUnknown,
                    "缺少 " + storeName + " 的 " + fieldName + " 观测；不能猜测 before 值。",
                    field: fieldName,
                    missingEvidence: new[] { "field:" + fieldName + ":" + storeName }));
                missing.Add("field:" + fieldName + ":" + storeName);
                continue;
            }

            if (!fact.IsKnown || fact.Value?.Value is null)
            {
                refusals.Add(RepairRefusal.Of(
                    field == RepairField.AutomaticMetric
                        ? RepairRefusalCode.AutomaticMetricUnknown
                        : RepairRefusalCode.CriticalUnknown,
                    "字段 " + fieldName + " 读取失败：" + (fact.Reason ?? fact.Status.ToString())
                    + (field == RepairField.AutomaticMetric
                        ? "；AutomaticMetric 前态无法可靠还原时不得生成写入计划。"
                        : "。"),
                    field: fieldName,
                    missingEvidence: new[] { "field:" + fieldName + ":" + storeName }));
                missing.Add("field:" + fieldName + ":" + storeName);
                continue;
            }

            if (fact.Value.Field != field || fact.Value.Store != store)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.CriticalUnknown,
                    "字段观测与请求的 " + fieldName + "/" + storeName + " 不匹配。",
                    field: fieldName));
                continue;
            }

            if (!string.Equals(fact.BootId, request.CurrentBootId, StringComparison.Ordinal))
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.CrossBoot,
                    "字段 " + fieldName + " 的值来自另一个 boot，不能作为当前 before。",
                    field: fieldName));
                continue;
            }

            AddFreshnessProblem(fact, request, "field:" + fieldName, refusals, missing);

            if (fact.Value.Value is InterfaceMetricValue metric && metric.Kind == MetricValueKind.Unknown)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ValueUnknown,
                    "InterfaceMetric 的手动/派生语义未知，不能作为写入或恢复基线。",
                    field: fieldName));
            }
        }
    }

    private static void AddFreshnessProblem<T>(
        SourcedFact<T> fact,
        RepairPreflightRequest request,
        string missingKey,
        List<RepairRefusal> refusals,
        List<string> missing)
        where T : class
    {
        switch (RepairFactFreshnessEvaluator.Assess(fact, request.NowUtc, request.ClockSkewTolerance))
        {
            case RepairFactFreshness.Stale:
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.FactStale,
                    "事实 " + fact.FactId + " 已超过 TTL（" + fact.Ttl.TotalSeconds + " 秒），必须重新采集。",
                    missingEvidence: new[] { missingKey }));
                missing.Add(missingKey);
                break;

            case RepairFactFreshness.NotYetValid:
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.ClockSkew,
                    "事实 " + fact.FactId + " 的 UTC 时间晚于当前时钟，超出容忍窗口。",
                    missingEvidence: new[] { missingKey }));
                missing.Add(missingKey);
                break;

            case RepairFactFreshness.Unknown:
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.CriticalUnknown,
                    "事实 " + fact.FactId + " 没有可用数据：" + (fact.Reason ?? fact.Status.ToString()) + "。",
                    missingEvidence: new[] { missingKey }));
                missing.Add(missingKey);
                break;
        }
    }
}

/// <summary>Result of re-reading every critical fact immediately before a write.</summary>
public sealed record RepairPreflightRevalidation
{
    public required bool IsValid { get; init; }

    public required bool RequiresNewPreview { get; init; }

    public required IReadOnlyList<RepairRefusal> Refusals { get; init; }
}

/// <summary>
/// A preflight is a point-in-time reading, not an atomic snapshot: the broker must re-read the
/// identity, the store values and the protected uplink, then compare them with the plan. Any
/// mismatch invalidates the existing authorization and requires a new preview.
/// </summary>
public static class RepairPreflightRevalidator
{
    public static RepairPreflightRevalidation Revalidate(
        RepairPreflight planned,
        RepairPreflightRequest currentRequest)
    {
        var current = RepairPreflightEvaluator.Evaluate(currentRequest);
        var refusals = new List<RepairRefusal>(current.Refusals);

        if (planned.Interface.Value is not { } plannedIdentity
            || current.Interface.Value is not { } currentIdentity)
        {
            return Invalid(refusals);
        }

        if (!string.Equals(planned.CurrentBootId, current.CurrentBootId, StringComparison.Ordinal)
            || !string.Equals(plannedIdentity.BootId, currentIdentity.BootId, StringComparison.Ordinal))
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.CrossBoot,
                "计划与重验不在同一个 boot；旧 ifIndex/LUID 不得写回。"));
        }

        if (plannedIdentity.InterfaceGuid != currentIdentity.InterfaceGuid)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.TargetDrift,
                "目标 GUID 已变化，不能自动转移到另一个接口。"));
        }

        if (plannedIdentity.Family != currentIdentity.Family
            || plannedIdentity.CompartmentId != currentIdentity.CompartmentId)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.TargetDrift,
                "目标的地址族或 compartment 已变化，必须重新预览。"));
        }

        if (!string.Equals(plannedIdentity.DeviceInstanceId, currentIdentity.DeviceInstanceId, StringComparison.Ordinal)
            || plannedIdentity.Luid != currentIdentity.Luid)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.InterfaceRebuilt,
                "设备实例或 LUID 已变化，接口疑似被重建；拒绝沿用旧目标。"));
        }

        if (plannedIdentity.IfIndex != currentIdentity.IfIndex)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.TargetDrift,
                "ifIndex 已变化；ifIndex 只是本次重验的临时解析值，必须重新解析后再次确认。"));
        }

        if (planned.Uplink.Value is { } plannedUplink
            && current.Uplink.Value is { } currentUplink
            && plannedUplink.ProtectedInterfaceGuid != currentUplink.ProtectedInterfaceGuid)
        {
            refusals.Add(RepairRefusal.Of(
                RepairRefusalCode.UplinkChanged,
                "受保护上行在计划与重验之间发生变化，目标可能已成为真实出口。"));
        }

        foreach (var (field, store) in RepairPreflight.RequiredFieldFacts)
        {
            var plannedFact = planned.FieldFact(field, store);
            var currentFact = current.FieldFact(field, store);
            var fieldName = NetScope.Domain.WireNames.Of(field);

            if (plannedFact?.Value?.Value is not { } plannedValue)
            {
                continue;
            }

            if (currentFact?.Value?.Value is not { } currentValue)
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.TargetDrift,
                    "字段 " + fieldName + " 在重验时不可读，计划 before 无法确认。",
                    field: fieldName));
                continue;
            }

            if (!string.Equals(plannedValue.CanonicalText, currentValue.CanonicalText, StringComparison.Ordinal))
            {
                refusals.Add(RepairRefusal.Of(
                    RepairRefusalCode.TargetDrift,
                    "字段 " + fieldName + " 的当前值已不等于计划 before（"
                    + plannedValue.CanonicalText + " → " + currentValue.CanonicalText
                    + "），整项授权失效，必须重新预览。",
                    field: fieldName));
            }
        }

        return new RepairPreflightRevalidation
        {
            IsValid = refusals.Count == 0,
            RequiresNewPreview = refusals.Count > 0,
            Refusals = refusals,
        };
    }

    private static RepairPreflightRevalidation Invalid(List<RepairRefusal> refusals) => new()
    {
        IsValid = false,
        RequiresNewPreview = true,
        Refusals = refusals,
    };
}
