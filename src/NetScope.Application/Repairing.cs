using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Application;

public enum RepairPreviewAvailability
{
    Unavailable,
    Ready,
    NoOp,
    Blocked,
}

/// <summary>Whether the input package/facts behind a preview may drive a privileged execution.</summary>
public enum RepairInputTrust
{
    Verified,
    Unverified,
    Untrusted,
}

public sealed record RepairGateView
{
    public required bool Enabled { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

public sealed record RepairActionView
{
    public required string ActionId { get; init; }

    public required string KindText { get; init; }

    public required string FieldText { get; init; }

    public required string IdentityText { get; init; }

    public required string Ipv4Text { get; init; }

    public required string StoreText { get; init; }

    public required string BeforeText { get; init; }

    public required string DesiredText { get; init; }

    public required string DependencyText { get; init; }

    public required string EvidenceText { get; init; }

    public required string TtlText { get; init; }

    public required string RiskText { get; init; }

    public required string EligibilityText { get; init; }

    public required string BlockingText { get; init; }

    public required bool IsChanged { get; init; }

    public required bool IsNoOp { get; init; }

    /// <summary>True only when this action may be selected; selection itself stays off by default.</summary>
    public required bool CanSelect { get; init; }

    public required IReadOnlyList<string> DependsOnActionIds { get; init; }
}

public sealed record RepairNoOpView
{
    public required string FieldText { get; init; }

    public required string StoreText { get; init; }

    public required string ValueText { get; init; }

    public required string ReasonText { get; init; }
}

public sealed record RepairPlanView
{
    public required RepairPreviewAvailability Availability { get; init; }

    public required string PlanId { get; init; }

    public required string SnapshotId { get; init; }

    public string? DiagnosisId { get; init; }

    public required string TargetText { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required string TtlText { get; init; }

    public required string DecisionText { get; init; }

    public required string Digest { get; init; }

    public required IReadOnlyList<RepairActionView> Actions { get; init; }

    public required IReadOnlyList<RepairNoOpView> NoOpFields { get; init; }

    public required IReadOnlyList<string> Refusals { get; init; }

    public required IReadOnlyList<string> MissingEvidence { get; init; }

    public required RepairGateView Gate { get; init; }

    public required bool Synthetic { get; init; }

    public required RepairInputTrust InputTrust { get; init; }

    public required bool TargetDrifted { get; init; }

    public required bool Expired { get; init; }

    public required string SummaryText { get; init; }

    /// <summary>No-op plans never count as a repair; changed actions are the only selectable work.</summary>
    public bool CountsAsRepair => Availability == RepairPreviewAvailability.Ready
        && Actions.Any(action => action.IsChanged);
}

public sealed record RepairPreviewRequest
{
    public required string SnapshotId { get; init; }

    public string? DiagnosisId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required string CurrentBootId { get; init; }

    public RepairInputTrust InputTrust { get; init; } = RepairInputTrust.Unverified;
}

/// <summary>Application-level seam in front of the repair planner; Desktop never writes by itself.</summary>
public interface IRepairPreviewSource
{
    Task<RepairPlanView> PreviewAsync(RepairPreviewRequest request, CancellationToken cancellationToken);
}

public sealed record RepairExecutionRequest
{
    public required string TransactionId { get; init; }

    public required string PlanId { get; init; }

    public required string PlanDigest { get; init; }

    public required IReadOnlyList<string> ConfirmedActionIds { get; init; }

    public required string UserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required DateTimeOffset NowUtc { get; init; }
}

public enum RepairLayerStatus
{
    Passed,
    Failed,
    NotTested,
    NotApplicable,
    Pending,
}

public sealed record RepairLayerView
{
    public required string Name { get; init; }

    public required RepairLayerStatus Status { get; init; }

    public required string Detail { get; init; }
}

/// <summary>
/// Four independent result layers. A committed transaction only means the planned technical
/// acceptance passed; the user's business result is reported separately and never inferred.
/// </summary>
public sealed record RepairExecutionOutcomeView
{
    public required string TransactionId { get; init; }

    public required string StateText { get; init; }

    public required bool IsTerminal { get; init; }

    public required bool NeedsRecovery { get; init; }

    public required bool WroteAnyField { get; init; }

    public required RepairLayerView ApiCall { get; init; }

    public required RepairLayerView Readback { get; init; }

    public required RepairLayerView TechnicalProbe { get; init; }

    public required RepairLayerView BusinessObservation { get; init; }

    public required IReadOnlyList<string> Refusals { get; init; }

    public required string RecoveryNotice { get; init; }

    public required bool Synthetic { get; init; }

    public IReadOnlyList<RepairLayerView> Layers => new[] { ApiCall, Readback, TechnicalProbe, BusinessObservation };
}

public sealed record RepairTransactionView
{
    public required string TransactionId { get; init; }

    public required string StateText { get; init; }

    public required bool IsTerminal { get; init; }

    public required bool NeedsRecovery { get; init; }

    public required bool WroteAnyField { get; init; }

    public required string Detail { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record RepairRecoveryItemView
{
    public required string TransactionId { get; init; }

    public required string StateText { get; init; }

    public required int RestorableFieldCount { get; init; }

    public required int UncertainActionCount { get; init; }

    public required bool RequiresNewAuthorization { get; init; }

    public required string RecoveryPlanId { get; init; }
}

public sealed record RepairRecoveryView
{
    public required bool Available { get; init; }

    public required int PendingCount { get; init; }

    public required IReadOnlyList<RepairRecoveryItemView> Items { get; init; }

    public required string Notice { get; init; }

    public required IReadOnlyList<string> BlockedReasons { get; init; }

    public static RepairRecoveryView Unavailable(IEnumerable<string> reasons) => new()
    {
        Available = false,
        PendingCount = 0,
        Items = Array.Empty<RepairRecoveryItemView>(),
        Notice = "没有可读的权威事务 journal；恢复入口保持关闭。",
        BlockedReasons = reasons.ToArray(),
    };
}

/// <summary>The only bridge the UI may use for repair; Desktop never opens a field writer.</summary>
public interface IRepairGateway
{
    RepairGateView Gate { get; }

    Task<RepairPlanView> PreviewAsync(RepairPreviewRequest request, CancellationToken cancellationToken);

    Task<RepairExecutionOutcomeView> ExecuteAsync(
        RepairExecutionRequest request, CancellationToken cancellationToken);

    Task<RepairRecoveryView> ScanRecoveryAsync(CancellationToken cancellationToken);

    RepairTransactionView? CurrentTransaction { get; }

    /// <summary>Requests cancellation of the current transaction; it never starts a new write.</summary>
    Task<RepairTransactionView?> CancelAsync(CancellationToken cancellationToken);

    /// <summary>Called on close/exit so the UI can display the final transaction state honestly.</summary>
    Task<RepairTransactionView?> FinalizeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The delivery default. It can show the reasons but never previews a real plan or executes:
/// there is no signed deployment, no platform experiment and no broker in this delivery.
/// </summary>
public sealed class UnavailableRepairGateway : IRepairGateway
{
    public static IReadOnlyList<string> DeliveryReasons { get; } = new[]
    {
        "无可信签名部署：BrokerTrustPolicy.DeliveryDefault 保持关闭，安装目录未满足签名/ACL 要求。",
        "无平台实验：三个字段的 Windows×驱动×API×store 组合尚未验证，PlatformCapabilityMatrix.UnverifiedDefault 全部不可用。",
        "无 broker 进程：本轮不启动真实提权，UI 不直接写网络。",
        "输入事实未验证：本页不把未经验证的快照或回放包当作执行依据。",
    };

    public UnavailableRepairGateway(string? detail = null)
    {
        Gate = new RepairGateView
        {
            Enabled = false,
            Text = "执行入口已禁用（Disabled）",
            Reasons = detail is null ? DeliveryReasons : new[] { detail }.Concat(DeliveryReasons).ToArray(),
        };
    }

    public RepairGateView Gate { get; }

    public RepairTransactionView? CurrentTransaction => null;

    public Task<RepairPlanView> PreviewAsync(RepairPreviewRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(RepairPlanViewExtensions.Unavailable(request, Gate));

    public Task<RepairExecutionOutcomeView> ExecuteAsync(
        RepairExecutionRequest request, CancellationToken cancellationToken) => Task.FromResult(
        new RepairExecutionOutcomeView
        {
            TransactionId = request.TransactionId,
            StateText = "aborted",
            IsTerminal = true,
            NeedsRecovery = false,
            WroteAnyField = false,
            ApiCall = new RepairLayerView
            {
                Name = "命令/系统 API 调用",
                Status = RepairLayerStatus.NotTested,
                Detail = "执行入口关闭，没有发起任何原生调用。",
            },
            Readback = new RepairLayerView
            {
                Name = "目标配置读回",
                Status = RepairLayerStatus.NotTested,
                Detail = "没有写入，因此没有读回。",
            },
            TechnicalProbe = new RepairLayerView
            {
                Name = "技术探测",
                Status = RepairLayerStatus.NotTested,
                Detail = "没有提交事务，不进行技术验收。",
            },
            BusinessObservation = new RepairLayerView
            {
                Name = "用户业务复测",
                Status = RepairLayerStatus.NotTested,
                Detail = "未测试；未测试不等于通过。",
            },
            Refusals = new[] { "execution_disabled: " + Gate.Text },
            RecoveryNotice = "没有写入，无需恢复。",
            Synthetic = false,
        });

    public Task<RepairRecoveryView> ScanRecoveryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RepairRecoveryView.Unavailable(Gate.Reasons));

    public Task<RepairTransactionView?> CancelAsync(CancellationToken cancellationToken) =>
        Task.FromResult<RepairTransactionView?>(null);

    public Task<RepairTransactionView?> FinalizeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<RepairTransactionView?>(null);
}

/// <summary>
/// Preview-only composition for the synthetic demo: it can show a synthetic plan but never
/// executes. Real execution stays behind the unavailable/trusted broker path.
/// </summary>
public sealed class PreviewOnlyRepairGateway : IRepairGateway
{
    private readonly IRepairPreviewSource _source;
    private readonly UnavailableRepairGateway _execution = new("合成演示只提供预览；执行入口保持 Disabled。");

    public PreviewOnlyRepairGateway(IRepairPreviewSource source, RepairGateView gate)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public RepairGateView Gate { get; }

    public RepairTransactionView? CurrentTransaction => null;

    public Task<RepairPlanView> PreviewAsync(RepairPreviewRequest request, CancellationToken cancellationToken) =>
        _source.PreviewAsync(request, cancellationToken);

    public Task<RepairExecutionOutcomeView> ExecuteAsync(
        RepairExecutionRequest request, CancellationToken cancellationToken) =>
        _execution.ExecuteAsync(request, cancellationToken);

    public Task<RepairRecoveryView> ScanRecoveryAsync(CancellationToken cancellationToken) =>
        _execution.ScanRecoveryAsync(cancellationToken);

    public Task<RepairTransactionView?> CancelAsync(CancellationToken cancellationToken) =>
        _execution.CancelAsync(cancellationToken);

    public Task<RepairTransactionView?> FinalizeAsync(CancellationToken cancellationToken) =>
        _execution.FinalizeAsync(cancellationToken);
}

internal static class RepairPlanViewExtensions
{
    public static RepairPlanView Unavailable(RepairPreviewRequest request, RepairGateView gate) => new()
    {
        Availability = RepairPreviewAvailability.Unavailable,
        PlanId = "",
        SnapshotId = request.SnapshotId,
        DiagnosisId = request.DiagnosisId,
        TargetText = "没有可信的目标身份",
        CreatedAtUtc = request.NowUtc,
        ExpiresAtUtc = request.NowUtc,
        TtlText = "无预览",
        DecisionText = "不可用",
        Digest = "",
        Actions = Array.Empty<RepairActionView>(),
        NoOpFields = Array.Empty<RepairNoOpView>(),
        Refusals = gate.Reasons.ToArray(),
        MissingEvidence = Array.Empty<string>(),
        Gate = gate,
        Synthetic = false,
        InputTrust = request.InputTrust,
        TargetDrifted = false,
        Expired = false,
        SummaryText = "无法生成修复计划：" + gate.Text,
    };
}

/// <summary>Maps a typed T13 plan into display rows. It never upgrades eligibility or success.</summary>
public static class RepairPreviewMapper
{
    public static RepairPlanView ToView(
        RepairPlan plan,
        RepairGateView environmentGate,
        RepairPreviewRequest request,
        bool targetDrifted,
        RepairInputTrust inputTrust)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var now = request.NowUtc;
        var expired = now > plan.ExpiresAtUtc;
        var changed = plan.ChangedActions;
        var gateReasons = new List<string>();
        var envEnabled = environmentGate.Enabled;
        if (!envEnabled) gateReasons.AddRange(environmentGate.Reasons);
        if (plan.Synthetic) gateReasons.Add("计划标记为 synthetic：只能用于演示/测试，不能成为特权执行依据。");
        if (!plan.ExecutionAllowed) gateReasons.Add("计划未通过平台能力/执行前置检查（ExecutionAllowed=false）。");
        if (!plan.CanAuthorize) gateReasons.Add("计划不可授权（CanAuthorize=false）。");
        if (inputTrust != RepairInputTrust.Verified) gateReasons.Add("输入事实/回放包未验证，禁止作为执行依据。");
        if (targetDrifted) gateReasons.Add("目标身份已漂移：当前接口与计划目标不一致，需重新预览。");
        if (expired) gateReasons.Add("计划已过期，必须重新预览。");
        if (changed.Count == 0) gateReasons.Add("没有实际变更字段（no_op），不计修复。");

        var enabled = envEnabled
            && plan.CanAuthorize
            && plan.ExecutionAllowed
            && !plan.Synthetic
            && inputTrust == RepairInputTrust.Verified
            && !targetDrifted
            && !expired
            && changed.Count > 0;
        var gate = new RepairGateView
        {
            Enabled = enabled,
            Text = enabled ? "执行入口可用（仍需逐动作确认并走 UAC）" : "执行入口已禁用（Disabled）",
            Reasons = gateReasons.Distinct(StringComparer.Ordinal).ToArray(),
        };

        var actions = plan.Actions.Select(action => ToActionView(action, plan, now)).ToArray();
        var availability = changed.Count == 0
            ? RepairPreviewAvailability.NoOp
            : plan.Decision == RepairPlanDecision.Planned && actions.Any(action => action.CanSelect)
                ? RepairPreviewAvailability.Ready
                : RepairPreviewAvailability.Blocked;

        return new RepairPlanView
        {
            Availability = availability,
            PlanId = plan.PlanId,
            SnapshotId = plan.SnapshotId,
            DiagnosisId = plan.DiagnosisId,
            TargetText = TargetText(plan),
            CreatedAtUtc = plan.CreatedAtUtc,
            ExpiresAtUtc = plan.ExpiresAtUtc,
            TtlText = "计划有效至 " + plan.ExpiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + "（剩余 " + Math.Max(0, (int)(plan.ExpiresAtUtc - now).TotalMinutes) + " 分钟）",
            DecisionText = plan.Decision switch
            {
                RepairPlanDecision.Planned => "已生成计划（预览）",
                RepairPlanDecision.Refused => "拒绝生成计划",
                _ => plan.Decision.ToString(),
            },
            Digest = plan.Digest,
            Actions = actions,
            NoOpFields = plan.NoOpFields.Select(noOp => new RepairNoOpView
            {
                FieldText = FieldText(noOp.Field),
                StoreText = StoreText(noOp.Store),
                ValueText = noOp.Value,
                ReasonText = noOp.Reason,
            }).ToArray(),
            Refusals = plan.Refusals.Select(refusal => refusal.Code + ": " + refusal.Detail)
                .Concat(gateReasons).Distinct(StringComparer.Ordinal).ToArray(),
            MissingEvidence = plan.MissingEvidence.ToArray(),
            Gate = gate,
            Synthetic = plan.Synthetic,
            InputTrust = inputTrust,
            TargetDrifted = targetDrifted,
            Expired = expired,
            SummaryText = changed.Count == 0
                ? "计划没有实际变更（no_op），不计修复次数。"
                : "计划包含 " + changed.Count + " 个变更动作、"
                  + plan.NoOpFields.Count + " 个跳过字段；逐动作默认未选中，确认后才请求 UAC。",
        };
    }

    private static RepairActionView ToActionView(RepairAction action, RepairPlan plan, DateTimeOffset now)
    {
        var blocking = action.BlockingReasons.Select(reason => reason.Code + ": " + reason.Detail).ToArray();
        var canSelect = action.IsChanged
            && action.Eligibility == RepairActionEligibility.Allowed
            && blocking.Length == 0;
        return new RepairActionView
        {
            ActionId = action.ActionId,
            KindText = ActionKindText(action.Kind),
            FieldText = FieldText(action.Field),
            IdentityText = IdentityText(action.Identity),
            Ipv4Text = action.Family == RepairAddressFamily.Ipv4
                ? "IPv4"
                : "地址族 " + WireNames.Of(action.Family),
            StoreText = StoreText(action.Store),
            BeforeText = ValueText(action.Before),
            DesiredText = ValueText(action.Desired),
            DependencyText = action.DependsOnActionIds.Count == 0
                ? "无依赖"
                : "依赖：" + string.Join("、", action.DependsOnActionIds),
            EvidenceText = action.EvidenceIds.Count == 0
                ? "无证据引用"
                : "证据：" + string.Join("、", action.EvidenceIds),
            TtlText = "before 观察于 " + action.BeforeObservedAtUtc.ToLocalTime().ToString("HH:mm:ss")
                + "（TTL " + (int)action.BeforeTtl.TotalSeconds + " 秒，来源 " + action.BeforeSource + "）",
            RiskText = "风险：" + RiskText(action.Risk) + "；回滚条件：" + action.RollbackCondition,
            EligibilityText = action.Eligibility == RepairActionEligibility.Allowed
                ? "资格：允许（仍需确认）"
                : "资格：" + action.Eligibility,
            BlockingText = blocking.Length == 0 ? "" : string.Join("；", blocking),
            IsChanged = action.IsChanged,
            IsNoOp = action.IsNoOp,
            CanSelect = canSelect,
            DependsOnActionIds = action.DependsOnActionIds.ToArray(),
        };
    }

    private static string TargetText(RepairPlan plan)
    {
        var first = plan.Actions.FirstOrDefault();
        return first is null
            ? plan.TargetFingerprint
            : IdentityText(first.Identity) + " · 目标指纹 " + plan.TargetFingerprint;
    }

    public static string IdentityText(InterfaceIdentity identity) =>
        "GUID " + identity.InterfaceGuid.ToString("D")
        + " · LUID " + (identity.Luid is { } luid ? "0x" + luid.ToString("x16") : "未记录")
        + " · ifIndex " + (identity.IfIndex?.ToString() ?? "未记录")
        + " · " + (string.IsNullOrWhiteSpace(identity.Name) ? "名称未记录" : identity.Name);

    public static string StoreText(RepairStore store) => store switch
    {
        RepairStore.ActiveStore => "ActiveStore（当前生效）",
        RepairStore.PersistentStore => "PersistentStore（持久）",
        _ => store.ToString(),
    };

    public static string FieldText(RepairField field) => field switch
    {
        RepairField.InterfaceMetric => "InterfaceMetric（接口度量）",
        RepairField.AutomaticMetric => "AutomaticMetric（自动度量）",
        RepairField.IgnoreDefaultRoutes => "IgnoreDefaultRoutes（忽略默认路由）",
        _ => field.ToString(),
    };

    public static string ActionKindText(RepairActionKind kind) => kind switch
    {
        RepairActionKind.SetTunIpv4InterfaceMetric => "设置 TUN IPv4 InterfaceMetric",
        RepairActionKind.SetTunIpv4AutomaticMetric => "设置 TUN IPv4 AutomaticMetric",
        RepairActionKind.SetTunIpv4IgnoreDefaultRoutes => "设置 TUN IPv4 IgnoreDefaultRoutes",
        _ => kind.ToString(),
    };

    public static string RiskText(RepairRiskLevel risk) => risk switch
    {
        RepairRiskLevel.Low => "低",
        RepairRiskLevel.Medium => "中",
        RepairRiskLevel.High => "高",
        _ => risk.ToString(),
    };

    public static string ValueText(RepairFieldValue value) => value switch
    {
        InterfaceMetricValue metric => "原始 uint " + metric.RawValue
            + "（" + (metric.Kind == MetricValueKind.ManualConfigured ? "手动配置" : "有效派生/未知") + "）",
        AutomaticMetricValue automatic => automatic.Enabled ? "已启用" : "已禁用",
        IgnoreDefaultRoutesValue ignore => ignore.Ignore ? "忽略默认路由" : "不忽略默认路由",
        _ => value.CanonicalText,
    };
}



