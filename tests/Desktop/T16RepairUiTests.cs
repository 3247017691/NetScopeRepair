using NetScope.Application;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

/// <summary>
/// The repair page over fake gateways: default-unselected actions, explicit confirmation,
/// disabled execution reasons, layered results, recovery preview and close/disconnect status.
/// No test opens a network socket, writes a registry value or launches an elevated process.
/// </summary>
public sealed class T16RepairUiTests
{
    private static RepairViewModel Vm(FakeRepairGateway gateway, FakeClock? clock = null) =>
        new(gateway, clock ?? new FakeClock(), () => "S-1-5-21-test", () => "luid:test");

    [Fact]
    public async Task Preview_shows_identity_store_before_after_dependencies_evidence_and_ttl_unselected()
    {
        var action = T16Fixtures.Action(
            "act:set_tun_ipv4_interface_metric",
            dependencies: "依赖：act:set_tun_ipv4_automatic_metric");
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(actions: new[] { action }));
        var vm = Vm(gateway);

        await vm.PreviewAsync();

        Assert.True(vm.HasPreview);
        var row = Assert.Single(vm.Actions);
        Assert.False(row.IsSelected);
        Assert.Contains("GUID", row.IdentityText);
        Assert.Equal("IPv4", row.Ipv4Text);
        Assert.Equal("ActiveStore（当前生效）", row.StoreText);
        Assert.Contains("35", row.BeforeText);
        Assert.Contains("20", row.DesiredText);
        Assert.Contains("automatic_metric", row.DependencyText);
        Assert.Contains("ev:metric-35", row.EvidenceText);
        Assert.Contains("TTL", row.TtlText);
        Assert.False(vm.CanExecute);
    }

    [Fact]
    public async Task Default_delivery_gateway_keeps_execution_disabled_and_explains_why()
    {
        var gateway = new UnavailableRepairGateway();
        var vm = new RepairViewModel(gateway, new FakeClock());

        await vm.PreviewAsync();
        await vm.ExecuteSelectedAsync(userConfirmed: true);

        Assert.True(vm.IsUnavailable);
        Assert.Contains("Disabled", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Disabled", vm.GateText, StringComparison.Ordinal);
        Assert.Contains("无可信签名部署", vm.GateReasonsText, StringComparison.Ordinal);
        Assert.False(vm.CanExecute);
    }

    [Fact]
    public async Task Explicit_confirmation_is_required_before_any_execution_request()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate);
        gateway.Outcome = T16Fixtures.Outcome();
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;

        await vm.ExecuteSelectedAsync(userConfirmed: false);
        Assert.Equal(0, gateway.ExecuteCalls);
        Assert.Contains("显式确认", vm.StatusMessage, StringComparison.Ordinal);

        await vm.ExecuteSelectedAsync(userConfirmed: true);
        Assert.Equal(1, gateway.ExecuteCalls);
        Assert.NotNull(gateway.LastRequest);
        Assert.Equal(new[] { "act:set_tun_ipv4_interface_metric" }, gateway.LastRequest!.ConfirmedActionIds);
        Assert.Equal("sha256:plan-t16", gateway.LastRequest.PlanDigest);
    }

    [Fact]
    public async Task Dependencies_must_be_selected_together_as_a_combined_authorization()
    {
        var dependent = T16Fixtures.Action(
            "act:set_tun_ipv4_interface_metric",
            dependencies: "依赖：act:set_tun_ipv4_automatic_metric");
        var prerequisite = T16Fixtures.Action(
            "act:set_tun_ipv4_automatic_metric",
            kind: "设置 TUN IPv4 AutomaticMetric",
            field: "AutomaticMetric（自动度量）",
            before: "已启用",
            desired: "已禁用");
        var preview = T16Fixtures.Preview(actions: new[] { prerequisite, dependent });
        var gateway = new FakeRepairGateway(preview, preview.Gate);
        gateway.Outcome = T16Fixtures.Outcome();
        var vm = Vm(gateway);
        await vm.PreviewAsync();

        vm.Actions[1].IsSelected = true; // only the dependent action
        Assert.False(vm.CanExecute);
        Assert.Contains("缺少依赖", vm.SelectionSummary, StringComparison.Ordinal);
        await vm.ExecuteSelectedAsync(userConfirmed: true);
        Assert.Equal(0, gateway.ExecuteCalls);

        vm.Actions[0].IsSelected = true;
        Assert.True(vm.CanExecute);
        Assert.Contains("依赖已作为组合授权", vm.SelectionSummary, StringComparison.Ordinal);
        await vm.ExecuteSelectedAsync(userConfirmed: true);
        Assert.Equal(1, gateway.ExecuteCalls);
        Assert.Equal(2, gateway.LastRequest!.ConfirmedActionIds.Count);
    }

    [Fact]
    public async Task A_no_op_preview_never_counts_as_a_repair_and_never_executes()
    {
        var preview = T16Fixtures.Preview(
            availability: RepairPreviewAvailability.NoOp,
            actions: Array.Empty<RepairActionView>(),
            noOps: new[]
            {
                new RepairNoOpView
                {
                    FieldText = "IgnoreDefaultRoutes（忽略默认路由）",
                    StoreText = "ActiveStore（当前生效）",
                    ValueText = "false",
                    ReasonText = "现值已等于目标值",
                },
            });
        var gateway = new FakeRepairGateway(preview, preview.Gate);
        var vm = Vm(gateway);

        await vm.PreviewAsync();
        await vm.ExecuteSelectedAsync(userConfirmed: true);

        Assert.False(preview.CountsAsRepair);
        Assert.Empty(vm.Actions);
        Assert.Single(vm.NoOpFields);
        Assert.True(vm.ShowNoOp);
        Assert.False(vm.CanExecute);
        Assert.Equal(0, gateway.ExecuteCalls);
        Assert.Contains("no_op", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, RepairInputTrust.Verified, "过期")]
    [InlineData(false, true, RepairInputTrust.Verified, "漂移")]
    [InlineData(false, false, RepairInputTrust.Untrusted, "未验证")]
    public async Task Expired_drifted_or_untrusted_input_disables_execution(
        bool expired, bool drifted, RepairInputTrust trust, string expectedText)
    {
        var preview = T16Fixtures.Preview(expired: expired, drifted: drifted, trust: trust);
        var gateway = new FakeRepairGateway(preview, preview.Gate);
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;

        Assert.False(vm.CanExecute);
        await vm.ExecuteSelectedAsync(userConfirmed: true);
        Assert.Equal(0, gateway.ExecuteCalls);
        Assert.Contains(expectedText, vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_gate_reports_reasons_and_records_a_denied_attempt()
    {
        var preview = T16Fixtures.Preview(
            gateEnabled: false,
            gateReasons: new[] { "无可信签名部署", "无平台实验" });
        var gateway = new FakeRepairGateway(preview, preview.Gate);
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;

        await vm.ExecuteSelectedAsync(userConfirmed: true);

        Assert.Equal(0, gateway.ExecuteCalls);
        Assert.Contains("Disabled", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("拒绝", vm.FunnelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_committed_outcome_shows_four_separate_layers_and_starts_business_observation()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            Outcome = T16Fixtures.Outcome(),
        };
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;
        await vm.ExecuteSelectedAsync(userConfirmed: true);

        Assert.Equal(4, vm.Layers.Count);
        Assert.Contains(vm.Layers, layer => layer.Name.Contains("API", StringComparison.Ordinal));
        Assert.Contains(vm.Layers, layer => layer.Name.Contains("读回", StringComparison.Ordinal));
        Assert.Contains(vm.Layers, layer => layer.Name.Contains("技术探测", StringComparison.Ordinal));
        Assert.Contains(vm.Layers, layer => layer.Name.Contains("业务", StringComparison.Ordinal));
        Assert.Contains("十分钟", vm.BusinessText, StringComparison.Ordinal);
        Assert.Contains("committed", vm.TransactionStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_technical_failure_is_rolled_back_and_never_reported_as_committed()
    {
        var outcome = T16Fixtures.Outcome(
            state: "rolled_back",
            technicalPassed: false,
            technicalDetail: "观察失败：目标探测仍失败");
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate) { Outcome = outcome };
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;
        await vm.ExecuteSelectedAsync(userConfirmed: true);

        Assert.Contains("rolled_back", vm.TransactionStateText, StringComparison.Ordinal);
        Assert.Contains(vm.Layers, layer => layer.StatusText == "失败");
        Assert.Contains("已回滚", vm.FunnelText, StringComparison.Ordinal);
        Assert.DoesNotContain("已提交", vm.FunnelText, StringComparison.Ordinal);
        Assert.DoesNotContain("十分钟", vm.BusinessText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aborted", "写前中止")]
    [InlineData("rollback_conflict", "回滚冲突")]
    [InlineData("rollback_failed", "回滚失败")]
    [InlineData("recovery_required", "需恢复")]
    public async Task Non_committed_states_are_reported_by_their_own_names(string state, string funnelLabel)
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            Outcome = T16Fixtures.Outcome(state: state, needsRecovery: state == "recovery_required"),
        };
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;
        await vm.ExecuteSelectedAsync(userConfirmed: true);

        Assert.Contains(state, vm.TransactionStateText, StringComparison.Ordinal);
        Assert.Contains(funnelLabel, vm.FunnelText, StringComparison.Ordinal);
        Assert.DoesNotContain("已提交", vm.FunnelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_and_disconnected_attempts_stay_in_the_funnel_denominator()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            Outcome = T16Fixtures.Outcome(),
            CurrentTransaction = T16Fixtures.Transaction("applying"),
            CancelResult = T16Fixtures.Transaction("aborted", terminal: true),
        };
        var vm = Vm(gateway);
        await vm.PreviewAsync();
        vm.Actions[0].IsSelected = true;
        await vm.ExecuteSelectedAsync(userConfirmed: true);
        await vm.CancelAsync();
        vm.ReportDisconnect();

        Assert.Equal(1, gateway.CancelCalls);
        Assert.Contains("尝试 3 次", vm.FunnelText, StringComparison.Ordinal);
        Assert.Contains("断连", vm.FunnelText, StringComparison.Ordinal);
        Assert.Contains("已取消", vm.FunnelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_displays_an_unfinished_transaction_and_never_starts_a_write()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            CurrentTransaction = T16Fixtures.Transaction("recovery_required"),
        };
        var vm = Vm(gateway);

        var text = await vm.PrepareExitAsync();

        Assert.Contains("尚未终结", text, StringComparison.Ordinal);
        Assert.Contains("恢复不依赖互联网", text, StringComparison.Ordinal);
        Assert.Equal(1, gateway.FinalizeCalls);
        Assert.Equal(0, gateway.ExecuteCalls);
    }

    [Fact]
    public async Task Disconnect_reports_the_current_transaction_state()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            CurrentTransaction = T16Fixtures.Transaction("applying"),
        };
        var vm = Vm(gateway);

        vm.ReportDisconnect();

        Assert.Contains("连接已断开", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("applying", vm.TransactionStateText, StringComparison.Ordinal);
        Assert.Contains("断连", vm.FunnelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_scan_lists_pending_transactions_without_auto_execution()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            Recovery = T16Fixtures.Recovery(),
        };
        var vm = Vm(gateway);

        await vm.ScanRecoveryAsync();

        Assert.Single(vm.RecoveryItems);
        Assert.Contains("不依赖互联网", vm.RecoveryNotice, StringComparison.Ordinal);
        Assert.Contains("需要新的恢复授权", vm.RecoveryItems[0].AuthorizationText, StringComparison.Ordinal);
        Assert.Equal(1, gateway.RecoveryCalls);
        Assert.Equal(0, gateway.ExecuteCalls);
    }

    [Fact]
    public async Task Recovery_unavailable_is_stated_instead_of_pretending_it_exists()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate)
        {
            Recovery = RepairRecoveryView.Unavailable(new[] { "没有可读的权威 journal" }),
        };
        var vm = Vm(gateway);

        await vm.ScanRecoveryAsync();

        Assert.Empty(vm.RecoveryItems);
        Assert.Contains("恢复不可用", vm.RecoveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_plan_ttl_and_digest_are_visible_before_confirmation()
    {
        var gateway = new FakeRepairGateway(T16Fixtures.Preview(), T16Fixtures.Preview().Gate);
        var vm = Vm(gateway);

        await vm.PreviewAsync();

        Assert.Contains("计划有效至", vm.PlanTtlText, StringComparison.Ordinal);
        Assert.Contains("sha256:plan-t16", vm.DigestText, StringComparison.Ordinal);
        Assert.Contains("默认未选中", vm.PlanSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void Funnel_denominator_keeps_cancelled_denied_disconnected_and_incomplete_attempts()
    {
        var funnel = new RepairFunnelCounter();
        funnel.Record(RepairAttemptOutcome.Committed);
        funnel.Record(RepairAttemptOutcome.Cancelled);
        funnel.Record(RepairAttemptOutcome.Denied);
        funnel.Record(RepairAttemptOutcome.Disconnected);
        funnel.Record(RepairAttemptOutcome.IncompleteObservation);

        Assert.Equal(5, funnel.Total);
        Assert.Contains("尝试 5 次", funnel.DenominatorText, StringComparison.Ordinal);
        Assert.Contains("已取消", funnel.DenominatorText, StringComparison.Ordinal);
        Assert.Contains("观察未完成", funnel.DenominatorText, StringComparison.Ordinal);
    }
}



