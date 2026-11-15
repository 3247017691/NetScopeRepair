using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Transaction coordinator over the authoritative journal with a recording fake adapter. Every
/// write is in memory; no real network or registry value is touched. Tests prove the write-ahead
/// order, exact rollback, uncertain-intent handling and budget/cancel behaviour.
/// </summary>
public sealed class T15TransactionCoordinatorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Committed_transaction_follows_the_write_ahead_order()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        IReadOnlyList<RepairJournalKind>? kindsAtWrite = null;
        adapter.BeforeSet = (_, _) =>
        {
            kindsAtWrite = device.AppendedKinds().ToArray();
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var coordinator = new RepairTransactionCoordinator(journal);
        var result = await coordinator.ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Committed, result.State);
        Assert.True(result.WroteAnyField);
        Assert.Equal(1, adapter.SetCalls);
        Assert.Contains("interface_metric:uint:20:manual_configured", adapter.Writes[0].Value);

        // The intent must be durable before the native write happens.
        Assert.NotNull(kindsAtWrite);
        Assert.Contains(RepairJournalKind.StepIntent, kindsAtWrite);
        Assert.DoesNotContain(RepairJournalKind.StepApplied, kindsAtWrite);
        Assert.DoesNotContain(RepairJournalKind.ObservationStarted, kindsAtWrite);

        Assert.Equal(
            new[]
            {
                RepairJournalKind.TransactionPlanned,
                RepairJournalKind.TransactionAuthorized,
                RepairJournalKind.TransactionPrepared,
                RepairJournalKind.StepIntent,
                RepairJournalKind.StepApplied,
                RepairJournalKind.ObservationStarted,
                RepairJournalKind.TransactionCommitted,
            },
            journal.Records.Select(record => record.Entry.Kind).ToArray());
        Assert.All(journal.Records, record => Assert.Equal("tx-t15", record.Entry.TransactionId));
        Assert.Equal(Enumerable.Range(1, journal.Records.Count).Select(value => (long)value),
            journal.Records.Select(record => record.Sequence));
        Assert.Equal(RepairTransactionState.Committed, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task A_no_op_plan_writes_nothing_and_records_no_op()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var noOp = plan with
        {
            Actions = new[]
            {
                plan.Actions[0] with { Desired = plan.Actions[0].Before, IsNoOp = false },
            },
        };
        var rebuilt = BrokerPlanFixtures.Rebuild(noOp, noOp.Actions.ToArray());
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(rebuilt.Plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, rebuilt.Plan, rebuilt.Authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.NoOp, result.State);
        Assert.False(result.WroteAnyField);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Equal(RepairTransactionState.NoOp, journal.LatestState("tx-t15"));
        Assert.Contains(journal.Records, record => record.Entry.Kind == RepairJournalKind.TransactionNoOp);
    }

    [Fact]
    public async Task Invalid_authorization_aborts_before_any_write()
    {
        var (plan, _) = T15Fixtures.Plan();
        var authorization = BrokerPlanFixtures.Authorization(plan, confirmedActionIds: Array.Empty<string>());
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.AuthorizationInvalid);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Equal(RepairTransactionState.Aborted, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task A_plan_changed_after_consent_is_refused()
    {
        var (plan, _) = T15Fixtures.Plan();
        var authorization = BrokerPlanFixtures.Authorization(plan);
        var tampered = plan with
        {
            Actions = new[]
            {
                plan.Actions[0] with
                {
                    Desired = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
                },
            },
        };
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(tampered.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, tampered, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.AuthorizationInvalid);
        Assert.Equal(0, adapter.SetCalls);
    }

    [Fact]
    public async Task Production_execution_without_a_verified_adapter_is_closed()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter, developmentIsolation: false), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal =>
            refusal.Code is RepairTransactionRefusalCode.CapabilityUnavailable
                or RepairTransactionRefusalCode.PlanNotExecutable);
        Assert.Equal(0, adapter.SetCalls);
    }

    [Fact]
    public async Task A_synthetic_plan_is_refused_outside_development_isolation()
    {
        var (plan, authorization) = T15Fixtures.Plan(synthetic: true);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter, developmentIsolation: false), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.SyntheticNotAllowed);
        Assert.Equal(0, adapter.SetCalls);
    }

    [Fact]
    public async Task An_external_change_before_the_write_stops_without_writing()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.BeforeGet = (action, _) =>
            adapter.SetCurrent(action, new InterfaceMetricValue { RawValue = 31, Kind = MetricValueKind.ManualConfigured });

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.ExternalValueChanged);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Equal(RepairTransactionState.Aborted, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task An_external_change_on_the_second_step_rolls_back_the_first()
    {
        var (plan, authorization) = T15Fixtures.Plan(actions: 2);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        var gets = 0;
        adapter.BeforeGet = (action, _) =>
        {
            gets++;
            // The second step's fresh read observes an external change before any write for it.
            if (gets == 2 && action.Field == RepairField.AutomaticMetric)
                adapter.SetCurrent(action, new AutomaticMetricValue { Enabled = !((AutomaticMetricValue)action.Before).Enabled });
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        Assert.Equal(2, adapter.SetCalls);
        Assert.Equal(2, adapter.Writes.Count); // the first forward write and its exact restore
        Assert.Contains(journal.Records, record => record.Entry.Kind == RepairJournalKind.RollbackApplied);
        Assert.Equal(RepairTransactionState.RolledBack, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task A_readback_mismatch_rolls_back_without_another_write()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.SetResponsesByCall[1] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Written,
            Readback = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
            AtUtc = T15Fixtures.Now,
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.ReadbackMismatch);
        Assert.Equal(1, adapter.SetCalls); // no second write: current was still before
        Assert.Contains(journal.Records, record =>
            record.Entry.Kind == RepairJournalKind.RollbackApplied && record.Entry.AlreadyRestored == true);
    }

    [Fact]
    public async Task A_conflicting_external_value_is_preserved_not_overwritten()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        var gets = 0;
        adapter.SetResponsesByCall[1] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Written,
            Readback = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
            AtUtc = T15Fixtures.Now,
        };
        adapter.BeforeGet = (action, _) =>
        {
            gets++;
            // The rollback re-read sees a third value written by another actor.
            if (gets == 2)
                adapter.SetCurrent(action, new InterfaceMetricValue { RawValue = 25, Kind = MetricValueKind.ManualConfigured });
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RollbackConflict, result.State);
        Assert.Equal(1, adapter.SetCalls);
        Assert.Equal(RepairTransactionState.RollbackConflict, journal.LatestState("tx-t15"));
        Assert.Contains(journal.Records, record => record.Entry.Kind == RepairJournalKind.TransactionRollbackConflict);
    }

    [Fact]
    public async Task A_rejected_second_write_rolls_back_the_first()
    {
        var (plan, authorization) = T15Fixtures.Plan(actions: 2);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.SetResponsesByCall[2] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Rejected,
            Readback = null,
            AtUtc = T15Fixtures.Now,
            Reason = "injected refusal",
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        // forward write, the rejected second attempt (no value changed), and the exact restore
        Assert.Equal(3, adapter.SetCalls);
        Assert.Equal(2, adapter.Writes.Count);
        Assert.Equal(RepairTransactionState.RolledBack, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task An_unknown_write_outcome_requires_recovery_and_never_rolls_back()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.SetResponsesByCall[1] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.OutcomeUnknown,
            Readback = null,
            AtUtc = T15Fixtures.Now,
            Reason = "native call outcome unknown",
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RecoveryRequired, result.State);
        Assert.Equal(1, adapter.SetCalls);
        Assert.Equal(RepairTransactionState.RecoveryRequired, journal.LatestState("tx-t15"));
        Assert.DoesNotContain(journal.Records, record => record.Entry.Kind == RepairJournalKind.RollbackIntent);
    }

    [Fact]
    public async Task An_applied_flush_failure_leaves_an_uncertain_step()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = 6 }; // header, planned, authorized, prepared, intent, applied
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RecoveryRequired, result.State);
        Assert.Equal(1, adapter.SetCalls);
        var scan = RepairJournalRecoveryScanner.Scan(journal, T15Fixtures.Now);
        var summary = Assert.Single(scan.Unresolved);
        var step = Assert.Single(summary.Steps);
        Assert.True(step.Uncertain);
        Assert.True(summary.HasUncertainSteps);

        // An intent without applied is never replayed: the recovery plan has no restorable fields.
        var recovery = RepairRecoveryPlanner.Plan(summary, T15Fixtures.Now);
        Assert.Empty(recovery.RestorableFields);
        Assert.Contains(step.ActionId, recovery.UncertainActionIds);
    }

    [Fact]
    public async Task An_intent_flush_failure_aborts_before_the_write()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = 5 }; // intent
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Contains(result.Refusals, refusal =>
            refusal.Code is RepairTransactionRefusalCode.JournalFlushFailed or RepairTransactionRefusalCode.JournalCorrupt);
    }

    [Fact]
    public async Task A_failed_observation_rolls_back_every_applied_write()
    {
        var (plan, authorization) = T15Fixtures.Plan(actions: 2);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter, new FakeObservationProbe { Passed = false }), Ct);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.ObservationFailed);
        Assert.Equal(4, adapter.SetCalls); // two forward writes + two exact restores
        Assert.All(plan.Actions, action =>
            Assert.Equal(action.Before.CanonicalText, adapter.Values[
                new RepairFieldKey(action.Identity.InterfaceGuid, action.Identity.Family, action.Store, action.Field)].CanonicalText));
        Assert.Equal(RepairTransactionState.RolledBack, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task An_unavailable_observation_rolls_back()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter, new UnavailableRepairObservationProbe()), Ct);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.ObservationFailed);
        Assert.Equal(2, adapter.SetCalls);
    }

    [Fact]
    public async Task Cancellation_before_the_first_write_aborts_without_writing()
    {
        var (plan, authorization) = T15Fixtures.Plan(actions: 2);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), cancellation.Token);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.Cancelled);
    }

    [Fact]
    public async Task Cancellation_during_observation_still_rolls_back_the_written_fields()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        using var cancellation = new CancellationTokenSource();
        var probe = new FakeObservationProbe { DuringObservation = () => cancellation.Cancel() };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter, probe), cancellation.Token);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        Assert.Equal(2, adapter.SetCalls);
        Assert.Contains(journal.Records, record => record.Entry.Kind == RepairJournalKind.StepApplied);
        Assert.Contains(journal.Records, record => record.Entry.Kind == RepairJournalKind.TransactionRolledBack);
    }

    [Fact]
    public async Task The_forward_window_stops_a_second_step_and_rolls_back_the_first()
    {
        var (plan, authorization) = T15Fixtures.Plan(actions: 2);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        var clock = new FakeBrokerTimeProvider();
        adapter.BeforeSet = (_, call) =>
        {
            if (call == 1) clock.Advance(TimeSpan.FromSeconds(121));
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal, clock).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter, clock: clock), Ct);

        Assert.Equal(RepairTransactionState.RolledBack, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.DeadlineExceeded);
        Assert.Equal(2, adapter.SetCalls); // step 0 write + its restore; step 1 never started
        Assert.Equal(2, adapter.Writes.Count);
    }

    [Fact]
    public async Task An_unresolved_previous_transaction_blocks_a_new_one()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var firstAdapter = new FakeFieldAdapter(plan.Actions);
        firstAdapter.SetResponses.Enqueue(new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.OutcomeUnknown,
            Readback = null,
            AtUtc = T15Fixtures.Now,
        });

        using var journal = T15Fixtures.OpenJournal(device);
        var coordinator = new RepairTransactionCoordinator(journal);
        var first = await coordinator.ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, firstAdapter, transactionId: "tx-first"), Ct);
        Assert.Equal(RepairTransactionState.RecoveryRequired, first.State);

        var secondAdapter = new FakeFieldAdapter(plan.Actions);
        var second = await coordinator.ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, secondAdapter, transactionId: "tx-second"), Ct);

        Assert.Equal(RepairTransactionState.Aborted, second.State);
        Assert.Contains(second.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.UnresolvedTransactionPresent);
        Assert.Equal(0, secondAdapter.SetCalls);
    }

    [Fact]
    public async Task A_failed_read_before_the_write_aborts()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions) { FailGet = true };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.BeforeMismatch);
        Assert.Equal(0, adapter.SetCalls);
    }

    [Fact]
    public async Task Three_actions_commit_in_order()
    {
        var (plan, authorization) = T15Fixtures.Plan(actions: 3);
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Committed, result.State);
        Assert.Equal(
            plan.Actions.Select(action => action.ActionId),
            journal.Records.Where(record => record.Entry.Kind == RepairJournalKind.StepIntent)
                .Select(record => record.Entry.ActionId).ToArray());
        Assert.Equal(3, adapter.SetCalls);
        Assert.Equal(
            3,
            journal.Records.Count(record => record.Entry.Kind == RepairJournalKind.StepIntent));
        Assert.Equal(
            3,
            journal.Records.Count(record => record.Entry.Kind == RepairJournalKind.StepApplied));
    }

    [Theory]
    [InlineData(2, RepairTransactionState.Aborted, 0)]   // planned record
    [InlineData(3, RepairTransactionState.Aborted, 0)]   // authorized record
    [InlineData(4, RepairTransactionState.Aborted, 0)]   // prepared record
    [InlineData(5, RepairTransactionState.Aborted, 0)]   // step intent
    [InlineData(6, RepairTransactionState.RecoveryRequired, 1)] // applied record after the write
    [InlineData(7, RepairTransactionState.RolledBack, 2)]       // observation record after applied writes
    [InlineData(8, RepairTransactionState.RecoveryRequired, 1)] // commit record after observation
    public async Task A_flush_failure_at_each_cut_point_is_reported_honestly(
        int failedAppendCall, RepairTransactionState expectedState, int expectedSetCalls)
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = failedAppendCall };
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedSetCalls, adapter.SetCalls);
        Assert.NotEqual(RepairTransactionState.Committed, result.State);

        // A crash cut at the same call, re-opened later, is classified without inventing ownership.
        using var reopened = T15Fixtures.OpenJournal(device);
        var scan = RepairJournalRecoveryScanner.Scan(reopened, T15Fixtures.Now);
        foreach (var summary in scan.Transactions)
        {
            if (summary.HasUncertainSteps)
                Assert.Contains(summary.Steps, step => step.Uncertain && !step.AppliedPresent);
        }
    }

    [Fact]
    public async Task A_boot_mismatch_before_the_write_stops_the_transaction()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions) { BootIdOverride = "boot-other" };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.Aborted, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.CrossBoot);
        Assert.Equal(0, adapter.SetCalls);
    }

    [Fact]
    public async Task A_rejected_rollback_write_ends_in_rollback_failed()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.BeforeSet = (action, call) =>
        {
            // The native write took effect even though the readback was wrong.
            if (call == 1) adapter.SetCurrent(action, action.Desired);
        };
        adapter.SetResponsesByCall[1] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Written,
            Readback = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
            AtUtc = T15Fixtures.Now,
        };
        adapter.SetResponsesByCall[2] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Rejected,
            Readback = null,
            AtUtc = T15Fixtures.Now,
            Reason = "rollback refused by the platform",
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RollbackFailed, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.RollbackFailed);
        Assert.Equal(RepairTransactionState.RollbackFailed, journal.LatestState("tx-t15"));
    }

    [Fact]
    public async Task A_failed_read_during_rollback_ends_in_rollback_failed()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.SetResponsesByCall[1] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Written,
            Readback = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
            AtUtc = T15Fixtures.Now,
        };
        var gets = 0;
        adapter.BeforeGet = (_, _) =>
        {
            gets++;
            if (gets == 2) adapter.FailGet = true; // the rollback re-read fails
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RollbackFailed, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.RollbackFailed);
    }

    [Fact]
    public async Task An_unknown_rollback_outcome_requires_user_confirmed_recovery()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        adapter.BeforeSet = (action, call) =>
        {
            // The native write took effect even though the readback was wrong.
            if (call == 1) adapter.SetCurrent(action, action.Desired);
        };
        adapter.SetResponsesByCall[1] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.Written,
            Readback = new InterfaceMetricValue { RawValue = 21, Kind = MetricValueKind.ManualConfigured },
            AtUtc = T15Fixtures.Now,
        };
        adapter.SetResponsesByCall[2] = new RepairFieldWrite
        {
            Status = RepairFieldWriteStatus.OutcomeUnknown,
            Readback = null,
            AtUtc = T15Fixtures.Now,
        };

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), Ct);

        Assert.Equal(RepairTransactionState.RecoveryRequired, result.State);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.RecoveryUncertain);
    }
}

internal static class FakeJournalDeviceExtensions
{
    /// <summary>Kinds of the frames already durable in the fake device (read back from bytes).</summary>
    public static IReadOnlyList<RepairJournalKind> AppendedKinds(this FakeJournalDevice device)
    {
        var data = device.Snapshot();
        var kinds = new List<RepairJournalKind>();
        var offset = RepairJournalFormat.HeaderLength;
        var expected = 1L;
        while (offset < data.Length)
        {
            if (!RepairJournalFormat.TryReadRecord(data.AsSpan(offset), expected, out var record, out _)
                || record is null) break;
            kinds.Add(record.Entry.Kind);
            offset += record.Length;
            expected++;
        }

        return kinds;
    }
}




