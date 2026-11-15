using NetScope.Application;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? now = null) => UtcNow = now ?? new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public double MonotonicMs { get; set; } = 1_000;

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        MonotonicMs += by.TotalMilliseconds;
    }
}

internal sealed class FakeRepairGateway : IRepairGateway
{
    public FakeRepairGateway(RepairPlanView? preview = null, RepairGateView? gate = null)
    {
        Preview = preview;
        Gate = gate ?? new RepairGateView
        {
            Enabled = false,
            Text = "执行入口已禁用（Disabled）",
            Reasons = UnavailableRepairGateway.DeliveryReasons,
        };
    }

    public RepairPlanView? Preview { get; set; }

    public RepairGateView Gate { get; }

    public RepairExecutionOutcomeView? Outcome { get; set; }

    public Func<RepairExecutionRequest, RepairExecutionOutcomeView>? OutcomeFactory { get; set; }

    public RepairRecoveryView Recovery { get; set; } = RepairRecoveryView.Unavailable(new[] { "没有可读的 journal。" });

    public RepairTransactionView? CurrentTransaction { get; set; }

    public RepairTransactionView? CancelResult { get; set; }

    public RepairTransactionView? FinalizeResult { get; set; }

    public int PreviewCalls { get; private set; }

    public int ExecuteCalls { get; private set; }

    public int CancelCalls { get; private set; }

    public int FinalizeCalls { get; private set; }

    public int RecoveryCalls { get; private set; }

    public RepairExecutionRequest? LastRequest { get; private set; }

    public Task<RepairPlanView> PreviewAsync(RepairPreviewRequest request, CancellationToken cancellationToken)
    {
        PreviewCalls++;
        if (Preview is null) throw new InvalidOperationException("no preview configured");
        return Task.FromResult(Preview);
    }

    public Task<RepairExecutionOutcomeView> ExecuteAsync(
        RepairExecutionRequest request, CancellationToken cancellationToken)
    {
        ExecuteCalls++;
        LastRequest = request;
        if (OutcomeFactory is not null) return Task.FromResult(OutcomeFactory(request));
        if (Outcome is null) throw new InvalidOperationException("no outcome configured");
        return Task.FromResult(Outcome);
    }

    public Task<RepairRecoveryView> ScanRecoveryAsync(CancellationToken cancellationToken)
    {
        RecoveryCalls++;
        return Task.FromResult(Recovery);
    }

    public Task<RepairTransactionView?> CancelAsync(CancellationToken cancellationToken)
    {
        CancelCalls++;
        return Task.FromResult(CancelResult);
    }

    public Task<RepairTransactionView?> FinalizeAsync(CancellationToken cancellationToken)
    {
        FinalizeCalls++;
        return Task.FromResult(FinalizeResult ?? CurrentTransaction);
    }
}

internal static class T16Fixtures
{
    public static DateTimeOffset Now => new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static RepairActionView Action(
        string id,
        string kind = "设置 TUN IPv4 InterfaceMetric",
        string field = "InterfaceMetric（接口度量）",
        string before = "原始 uint 35（手动配置）",
        string desired = "原始 uint 20（手动配置）",
        string dependencies = "无依赖",
        bool changed = true,
        bool canSelect = true,
        bool isNoOp = false) => new()
    {
        ActionId = id,
        KindText = kind,
        FieldText = field,
        IdentityText = "GUID 7d4a2f0e-3b5c-4d6e-8f90-123456789abc · LUID 0x0000000100000001 · ifIndex 42 · Meta Tunnel（合成）",
        Ipv4Text = "IPv4",
        StoreText = "ActiveStore（当前生效）",
        BeforeText = before,
        DesiredText = desired,
        DependencyText = dependencies,
        EvidenceText = "证据：ev:metric-35",
        TtlText = "before 观察于 11:59:55（TTL 30 秒，来源 synthetic）",
        RiskText = "风险：低；回滚条件：失败时恢复 before",
        EligibilityText = "资格：允许（仍需确认）",
        BlockingText = "",
        IsChanged = changed,
        IsNoOp = isNoOp,
        CanSelect = canSelect,
        DependsOnActionIds = dependencies == "无依赖"
            ? Array.Empty<string>()
            : dependencies.Replace("依赖：", "", StringComparison.Ordinal)
                .Split('、', StringSplitOptions.RemoveEmptyEntries),
    };

    public static RepairPlanView Preview(
        RepairPreviewAvailability availability = RepairPreviewAvailability.Ready,
        bool gateEnabled = true,
        bool expired = false,
        bool drifted = false,
        RepairInputTrust trust = RepairInputTrust.Verified,
        bool synthetic = false,
        IReadOnlyList<RepairActionView>? actions = null,
        IReadOnlyList<RepairNoOpView>? noOps = null,
        IReadOnlyList<string>? gateReasons = null) => new()
    {
        Availability = availability,
        PlanId = "plan-t16",
        SnapshotId = "snapshot-t16",
        DiagnosisId = "diagnosis-t16",
        TargetText = "GUID 7d4a2f0e-3b5c-4d6e-8f90-123456789abc · LUID 0x0000000100000001 · ifIndex 42",
        CreatedAtUtc = Now,
        ExpiresAtUtc = Now.AddMinutes(10),
        TtlText = "计划有效至 " + Now.AddMinutes(10).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        DecisionText = "已生成计划（预览）",
        Digest = "sha256:plan-t16",
        Actions = actions ?? new[] { Action("act:set_tun_ipv4_interface_metric") },
        NoOpFields = noOps ?? Array.Empty<RepairNoOpView>(),
        Refusals = gateReasons ?? Array.Empty<string>(),
        MissingEvidence = Array.Empty<string>(),
        Gate = new RepairGateView
        {
            Enabled = gateEnabled,
            Text = gateEnabled ? "执行入口可用（仍需逐动作确认并走 UAC）" : "执行入口已禁用（Disabled）",
            Reasons = gateReasons ?? (gateEnabled
                ? Array.Empty<string>()
                : new[] { "无可信签名部署", "无平台实验" }),
        },
        Synthetic = synthetic,
        InputTrust = trust,
        TargetDrifted = drifted,
        Expired = expired,
        SummaryText = "计划包含 1 个变更动作、0 个跳过字段；逐动作默认未选中。",
    };

    public static RepairExecutionOutcomeView Outcome(
        string state = "committed",
        bool needsRecovery = false,
        bool wroteAnyField = true,
        bool apiPassed = true,
        bool readbackPassed = true,
        bool technicalPassed = true,
        string technicalDetail = "技术探测通过",
        string businessDetail = "业务观察尚未开始") => new()
    {
        TransactionId = "tx-ui-test",
        StateText = state,
        IsTerminal = state != "recovery_required",
        NeedsRecovery = needsRecovery,
        WroteAnyField = wroteAnyField,
        ApiCall = Layer("命令/系统 API 调用", apiPassed, apiPassed ? "Set 调用返回" : "Set 调用失败"),
        Readback = Layer("目标配置读回", readbackPassed, readbackPassed ? "读回 20" : "读回 35"),
        TechnicalProbe = Layer("技术探测", technicalPassed, technicalDetail),
        BusinessObservation = new RepairLayerView
        {
            Name = "用户业务复测",
            Status = RepairLayerStatus.NotTested,
            Detail = businessDetail,
        },
        Refusals = technicalPassed ? Array.Empty<string>() : new[] { "observation_failed" },
        RecoveryNotice = state switch
        {
            "committed" => "技术提交完成；业务结果独立。",
            "rolled_back" => "已按 before 精确回滚。",
            "rollback_conflict" => "外部值被保留，未覆盖。",
            "rollback_failed" => "回滚失败，残留值见 journal，需离线恢复。",
            "recovery_required" => "需要用户确认恢复。",
            "aborted" => "写前中止，无变更。",
            "no_op" => "无实际变更。",
            _ => "事务结束。",
        },
        Synthetic = false,
    };

    private static RepairLayerView Layer(string name, bool passed, string detail) => new()
    {
        Name = name,
        Status = passed ? RepairLayerStatus.Passed : RepairLayerStatus.Failed,
        Detail = detail,
    };

    public static RepairRecoveryView Recovery() => new()
    {
        Available = true,
        PendingCount = 1,
        Items = new[]
        {
            new RepairRecoveryItemView
            {
                TransactionId = "tx-unfinished",
                StateText = "recovery_required",
                RestorableFieldCount = 1,
                UncertainActionCount = 1,
                RequiresNewAuthorization = true,
                RecoveryPlanId = "recovery-tx-unfinished",
            },
        },
        Notice = "检测到 1 个未终结事务。",
        BlockedReasons = Array.Empty<string>(),
    };

    public static RepairTransactionView Transaction(string state = "recovery_required", bool terminal = false) => new()
    {
        TransactionId = "tx-open",
        StateText = state,
        IsTerminal = terminal,
        NeedsRecovery = !terminal,
        WroteAnyField = true,
        Detail = "事务记录保留在权威 journal。",
        UpdatedAtUtc = Now,
    };
}

