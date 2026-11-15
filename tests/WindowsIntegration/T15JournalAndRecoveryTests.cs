using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Journal format, tail/corruption/sequence handling, directory policy, real-file durability and
/// user-confirmed recovery. Every write is either in memory or a temporary test journal file under
/// the test's own temp directory; no network or system network value is touched.
/// </summary>
public sealed class T15JournalAndRecoveryTests
{
    private static readonly DateTimeOffset Now = T15Fixtures.Now;

    private static RepairJournalEntry Entry(
        long kindSeed = 1,
        RepairJournalKind kind = RepairJournalKind.TransactionPlanned,
        RepairTransactionState state = RepairTransactionState.Planned,
        string transactionId = "tx-j") => new()
    {
        TransactionId = transactionId,
        Kind = kind,
        State = state,
        AtUtc = Now.AddSeconds(kindSeed),
    };

    [Fact]
    public void Header_and_records_round_trip_with_contiguous_sequences()
    {
        var device = new FakeJournalDevice();
        using var journal = T15Fixtures.OpenJournal(device);

        journal.Append(Entry(1));
        journal.Append(Entry(2, RepairJournalKind.TransactionAuthorized, RepairTransactionState.Authorized));

        Assert.True(journal.IsUsable);
        Assert.Equal(2, journal.Records.Count);
        Assert.Equal(new long[] { 1, 2 }, journal.Records.Select(record => record.Sequence));
        Assert.Equal(RepairTransactionState.Authorized, journal.LatestState("tx-j"));

        using var reopened = T15Fixtures.OpenJournal(device);
        Assert.True(reopened.IsUsable);
        Assert.Equal(2, reopened.Records.Count);
        Assert.Equal(RepairJournalKind.TransactionAuthorized, reopened.Records[1].Entry.Kind);
    }

    [Fact]
    public void A_corrupt_tail_is_detected_and_can_be_truncated_to_the_last_complete_record()
    {
        var device = new FakeJournalDevice();
        using var journal = T15Fixtures.OpenJournal(device);
        journal.Append(Entry(1));
        journal.Append(Entry(2, RepairJournalKind.TransactionAuthorized, RepairTransactionState.Authorized));

        var data = device.Snapshot();
        device.Restore(data.Concat(System.Text.Encoding.ASCII.GetBytes("garbage-tail")).ToArray());

        using var corrupted = T15Fixtures.OpenJournal(device);
        Assert.False(corrupted.IsUsable);
        Assert.True(corrupted.HasCorruptTail);
        Assert.Contains(corrupted.Problems, problem => problem.Code == RepairTransactionRefusalCode.JournalCorrupt);
        Assert.Throws<RepairJournalException>(() => corrupted.Append(Entry(3)));

        var removed = corrupted.TruncateCorruptTail();
        Assert.True(removed > 0);
        Assert.True(corrupted.IsUsable);
        Assert.Equal(2, corrupted.Records.Count);
        corrupted.Append(Entry(3));
        Assert.Equal(3, corrupted.Records.Count);
    }

    [Fact]
    public void An_unsupported_journal_version_is_refused()
    {
        var header = RepairJournalFormat.EncodeHeader(Now, new byte[16]);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 2);
        SHA256.HashData(header.AsSpan(0, 20)).AsSpan(0, 16).CopyTo(header.AsSpan(20));

        var device = new FakeJournalDevice();
        device.Append(header);
        using var journal = T15Fixtures.OpenJournal(device);

        Assert.False(journal.IsUsable);
        Assert.Contains(journal.Problems, problem => problem.Code == RepairTransactionRefusalCode.JournalVersionUnsupported);
    }

    [Fact]
    public void A_sequence_gap_is_reported_instead_of_being_silently_ignored()
    {
        var device = new FakeJournalDevice();
        device.Append(RepairJournalFormat.EncodeHeader(Now, new byte[16]));
        device.Append(RepairJournalFormat.EncodeRecord(1, Entry(1)));
        device.Append(RepairJournalFormat.EncodeRecord(3, Entry(2, RepairJournalKind.TransactionAuthorized, RepairTransactionState.Authorized)));

        using var journal = T15Fixtures.OpenJournal(device);

        Assert.False(journal.IsUsable);
        Assert.True(journal.HasSequenceGap);
        Assert.Contains(journal.Problems, problem => problem.Code == RepairTransactionRefusalCode.JournalSequenceGap);
    }

    [Fact]
    public void Unknown_json_fields_in_a_record_are_corrupt()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"transaction_id\":\"tx\",\"kind\":\"transaction_planned\",\"state\":\"planned\","
            + "\"at_utc\":\"2026-11-05T12:00:00+00:00\",\"unexpected\":1}");
        var record = RawRecord(sequence: 1, kind: RepairJournalKind.TransactionPlanned, payload);
        var device = new FakeJournalDevice();
        device.Append(RepairJournalFormat.EncodeHeader(Now, new byte[16]));
        device.Append(record);

        using var journal = T15Fixtures.OpenJournal(device);
        Assert.False(journal.IsUsable);
        Assert.True(journal.HasCorruptTail);
    }

    [Fact]
    public void An_oversized_journal_payload_is_rejected_before_writing()
    {
        var oversized = Entry(1) with { Reason = new string('x', RepairJournalFormat.MaxPayloadBytes) };
        Assert.Throws<ArgumentException>(() => RepairJournalFormat.EncodeRecord(1, oversized));
    }

    [Fact]
    public async Task A_real_file_device_is_exclusive_and_durable()
    {
        var directory = T15Fixtures.CreateJournalDirectory();
        var path = Path.Combine(directory, "transactions.journal");
        try
        {
            using (var device = RepairFileJournalDevice.Open(path, developmentIsolation: true))
            {
                using var journal = RepairJournal.Open(device, Now);
                journal.Append(Entry(1));
                Assert.True(File.Exists(path));
                var lengthAfterAppend = new FileInfo(path).Length;

                Assert.Throws<RepairJournalDeviceException>(() => RepairFileJournalDevice.Open(path, developmentIsolation: true));
                Assert.True(lengthAfterAppend > 0);
            }

            using var reopenedDevice = RepairFileJournalDevice.Open(path, developmentIsolation: true);
            using var reopened = RepairJournal.Open(reopenedDevice);
            Assert.True(reopened.IsUsable);
            Assert.Single(reopened.Records);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task A_real_file_journal_with_a_corrupt_tail_can_be_truncated()
    {
        var directory = T15Fixtures.CreateJournalDirectory();
        var path = Path.Combine(directory, "transactions.journal");
        try
        {
            using (var device = RepairFileJournalDevice.Open(path, developmentIsolation: true))
            {
                using var journal = RepairJournal.Open(device, Now);
                journal.Append(Entry(1));
                journal.Append(Entry(2, RepairJournalKind.TransactionAuthorized, RepairTransactionState.Authorized));
            }

            await File.AppendAllTextAsync(path, "garbage-tail");
            using (var device = RepairFileJournalDevice.Open(path, developmentIsolation: true))
            {
                using var journal = RepairJournal.Open(device);
                Assert.True(journal.HasCorruptTail);
                var removed = journal.TruncateCorruptTail();
                Assert.True(removed > 0);
                Assert.True(journal.IsUsable);
                Assert.Equal(2, journal.Records.Count);
            }

            using (var device = RepairFileJournalDevice.Open(path, developmentIsolation: true))
            {
                using var journal = RepairJournal.Open(device);
                Assert.True(journal.IsUsable);
                Assert.Equal(2, journal.Records.Count);
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void The_directory_policy_requires_a_fixed_signed_non_user_writable_root()
    {
        var policy = RepairJournalDirectoryPolicy.Design05;
        var fixedFacts = new RepairJournalDirectoryFacts
        {
            RootPath = policy.ProductionRoot,
            Exists = true,
            IsReparsePoint = false,
            IsUserWritable = false,
            IsSignedDeployment = true,
            IsElevated = true,
            DevelopmentIsolation = false,
        };

        var allowed = policy.Evaluate(fixedFacts);
        Assert.True(allowed.Allowed);
        Assert.False(allowed.IsDevelopmentIsolation);

        var userWritable = policy.Evaluate(fixedFacts with { IsUserWritable = true });
        Assert.False(userWritable.Allowed);
        Assert.Contains(userWritable.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.JournalDirectoryUserWritable);

        var reparse = policy.Evaluate(fixedFacts with { IsReparsePoint = true });
        Assert.False(reparse.Allowed);
        Assert.Contains(reparse.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.JournalDirectoryReparsePoint);

        var unsigned = policy.Evaluate(fixedFacts with { IsSignedDeployment = false, IsElevated = false });
        Assert.False(unsigned.Allowed);
        Assert.Contains(unsigned.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.JournalCapabilityClosed);

        var wrongRoot = policy.Evaluate(fixedFacts with { RootPath = Path.Combine(Path.GetTempPath(), "other") });
        Assert.False(wrongRoot.Allowed);
    }

    [Fact]
    public void A_development_root_is_allowed_only_when_explicitly_requested_and_marked()
    {
        var directory = T15Fixtures.CreateJournalDirectory();
        try
        {
            var policy = new RepairJournalDirectoryPolicy
            {
                ProductionRoot = Path.Combine(Path.GetTempPath(), "never-production"),
                DevelopmentRoot = directory,
                AllowDevelopmentIsolation = true,
            };
            var facts = new RepairJournalDirectoryFacts
            {
                RootPath = directory,
                Exists = true,
                IsReparsePoint = false,
                IsUserWritable = true,
                IsSignedDeployment = false,
                IsElevated = false,
                DevelopmentIsolation = true,
            };

            var decision = policy.Evaluate(facts);
            Assert.True(decision.Allowed);
            Assert.True(decision.IsDevelopmentIsolation);
            Assert.Contains(decision.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.JournalCapabilityClosed);

            var noIsolation = policy with { AllowDevelopmentIsolation = false };
            Assert.False(noIsolation.Evaluate(facts).Allowed);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task A_crash_before_commit_leaves_a_proven_applied_field_for_user_confirmed_recovery()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = 8 }; // observation ok, commit append fails
        var adapter = new FakeFieldAdapter(plan.Actions);

        RepairRecoveryPlan recoveryPlan;
        using (var journal = T15Fixtures.OpenJournal(device))
        {
            var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
                T15Fixtures.Request(journal, plan, authorization, adapter), CancellationToken.None);
            Assert.Equal(RepairTransactionState.RecoveryRequired, result.State);
            Assert.Equal(1, adapter.SetCalls);

            var scan = RepairJournalRecoveryScanner.Scan(journal, Now);
            var summary = Assert.Single(scan.Unresolved);
            Assert.Equal(RepairTransactionState.Observing, summary.LatestState);
            Assert.True(summary.HasProvenAppliedSteps);
            Assert.False(summary.HasUncertainSteps);

            recoveryPlan = RepairRecoveryPlanner.Plan(summary, Now);
            Assert.Single(recoveryPlan.RestorableFields);
            Assert.True(recoveryPlan.RequiresNewAuthorization);
            Assert.True(recoveryPlan.RequiresUserConfirmation);
        }

        var recoveryAdapter = new FakeFieldAdapter(plan.Actions);
        recoveryAdapter.SetCurrent(plan.Actions[0], plan.Actions[0].Desired);
        using var reopened = T15Fixtures.OpenJournal(device);
        var recovery = await new RepairRecoveryExecutor(reopened, new FakeBrokerTimeProvider()).ExecuteAsync(
            new RepairRecoveryRequest
            {
                Plan = recoveryPlan,
                Authorization = T15Fixtures.RecoveryAuthorization(recoveryPlan),
                Adapter = recoveryAdapter,
                NowUtc = Now,
                CurrentBootId = plan.BootId,
                UserSid = T15Fixtures.UserSid,
                LogonSessionId = T15Fixtures.LogonSession,
                DevelopmentIsolation = true,
            }, CancellationToken.None);

        Assert.Equal(RepairTransactionState.RolledBack, recovery.FinalState);
        Assert.Equal(1, recoveryAdapter.SetCalls);
        Assert.Equal(plan.Actions[0].Before.CanonicalText, recoveryAdapter.Values[
            new RepairFieldKey(plan.Actions[0].Identity.InterfaceGuid, plan.Actions[0].Identity.Family,
                plan.Actions[0].Store, plan.Actions[0].Field)].CanonicalText);
        Assert.Equal(RepairTransactionState.RolledBack, reopened.LatestState("tx-t15"));
    }

    [Fact]
    public async Task Recovery_records_already_restored_without_writing()
    {
        var fixture = await BuildFailedRecoveryFixtureAsync();
        var device = fixture.Device;
        var plan = fixture.SourcePlan;
        var recoveryPlan = fixture.Plan;
        var adapter = new FakeFieldAdapter(plan.Actions); // current is still the before value

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairRecoveryExecutor(journal, new FakeBrokerTimeProvider()).ExecuteAsync(
            RecoveryRequest(recoveryPlan, adapter, plan), CancellationToken.None);

        Assert.Equal(RepairTransactionState.RolledBack, result.FinalState);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Contains(journal.Records, record =>
            record.Entry.Kind == RepairJournalKind.RollbackApplied && record.Entry.AlreadyRestored == true);
    }

    [Fact]
    public async Task Recovery_preserves_a_third_party_value_instead_of_overwriting_it()
    {
        var fixture = await BuildFailedRecoveryFixtureAsync();
        var device = fixture.Device;
        var plan = fixture.SourcePlan;
        var recoveryPlan = fixture.Plan;
        var adapter = new FakeFieldAdapter(plan.Actions);
        var action = plan.Actions[0];
        adapter.SetCurrent(action, new InterfaceMetricValue { RawValue = 25, Kind = MetricValueKind.ManualConfigured });

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairRecoveryExecutor(journal, new FakeBrokerTimeProvider()).ExecuteAsync(
            RecoveryRequest(recoveryPlan, adapter, plan), CancellationToken.None);

        Assert.Equal(RepairTransactionState.RollbackConflict, result.FinalState);
        Assert.Equal(0, adapter.SetCalls);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.RollbackConflict);
    }

    [Fact]
    public async Task Recovery_refuses_cross_boot_target_rebuild_or_bad_authorization()
    {
        var fixture = await BuildFailedRecoveryFixtureAsync();
        var device = fixture.Device;
        var plan = fixture.SourcePlan;
        var recoveryPlan = fixture.Plan;

        using (var journal = T15Fixtures.OpenJournal(device))
        {
            var adapter = new FakeFieldAdapter(plan.Actions);
            var crossBoot = await new RepairRecoveryExecutor(journal).ExecuteAsync(
                RecoveryRequest(recoveryPlan, adapter, plan) with { CurrentBootId = "boot-other" }, CancellationToken.None);
            Assert.Equal(RepairTransactionState.RecoveryRequired, crossBoot.FinalState);
            Assert.Contains(crossBoot.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.CrossBoot);
            Assert.Equal(0, adapter.SetCalls);
        }

        using (var journal = T15Fixtures.OpenJournal(device))
        {
            var adapter = new FakeFieldAdapter(plan.Actions);
            var rebuilt = await new RepairRecoveryExecutor(journal).ExecuteAsync(
                RecoveryRequest(recoveryPlan, adapter, plan) with
                {
                    Plan = recoveryPlan with { TargetFingerprint = "other|ipv4|0" },
                }, CancellationToken.None);
            Assert.Equal(RepairTransactionState.RecoveryRequired, rebuilt.FinalState);
            Assert.Contains(rebuilt.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.TargetRebuilt);
            Assert.Equal(0, adapter.SetCalls);
        }

        using (var journal = T15Fixtures.OpenJournal(device))
        {
            var adapter = new FakeFieldAdapter(plan.Actions);
            var request = RecoveryRequest(recoveryPlan, adapter, plan);
            var expired = await new RepairRecoveryExecutor(journal).ExecuteAsync(
                request with { Authorization = T15Fixtures.RecoveryAuthorization(recoveryPlan, expiresAt: Now.AddSeconds(-1)) },
                CancellationToken.None);
            Assert.Equal(RepairTransactionState.RecoveryRequired, expired.FinalState);
            Assert.Contains(expired.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.AuthorizationInvalid);

            var wrongUser = await new RepairRecoveryExecutor(journal).ExecuteAsync(
                request with { Authorization = T15Fixtures.RecoveryAuthorization(recoveryPlan, userSid: "S-1-5-21-9999") },
                CancellationToken.None);
            Assert.Contains(wrongUser.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.AuthorizationInvalid);

            var noConsent = await new RepairRecoveryExecutor(journal).ExecuteAsync(
                request with { Authorization = T15Fixtures.RecoveryAuthorization(recoveryPlan, perActionConsent: false) },
                CancellationToken.None);
            Assert.Contains(noConsent.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.AuthorizationInvalid);
            Assert.Equal(0, adapter.SetCalls);
        }
    }

    [Fact]
    public async Task Recovery_refuses_a_plan_the_authoritative_journal_does_not_support()
    {
        var fixture = await BuildFailedRecoveryFixtureAsync();
        var device = fixture.Device;
        var plan = fixture.SourcePlan;
        var source = fixture.Plan.RestorableFields[0];
        var tampered = fixture.Plan with
        {
            RestorableFields = new[]
            {
                source with { Before = RepairJournalValue.From(new InterfaceMetricValue { RawValue = 99, Kind = MetricValueKind.ManualConfigured }) },
            },
        };
        var adapter = new FakeFieldAdapter(plan.Actions);

        using var journal = T15Fixtures.OpenJournal(device);
        var result = await new RepairRecoveryExecutor(journal).ExecuteAsync(
            RecoveryRequest(tampered, adapter, plan) with
            {
                Authorization = T15Fixtures.RecoveryAuthorization(tampered),
            }, CancellationToken.None);

        Assert.Equal(RepairTransactionState.RecoveryRequired, result.FinalState);
        Assert.Contains(result.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.JournalCorrupt);
        Assert.Equal(0, adapter.SetCalls);
    }

    [Fact]
    public async Task Recovery_of_an_intent_only_journal_never_replays_the_setter()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = 6 }; // applied record is not durable
        var adapter = new FakeFieldAdapter(plan.Actions);
        RepairRecoveryPlan recoveryPlan;

        using (var journal = T15Fixtures.OpenJournal(device))
        {
            var result = await new RepairTransactionCoordinator(journal).ExecuteAsync(
                T15Fixtures.Request(journal, plan, authorization, adapter), CancellationToken.None);
            Assert.Equal(RepairTransactionState.RecoveryRequired, result.State);
            var summary = Assert.Single(RepairJournalRecoveryScanner.Scan(journal, Now).Unresolved);
            Assert.True(summary.HasUncertainSteps);
            recoveryPlan = RepairRecoveryPlanner.Plan(summary, Now);
            Assert.Empty(recoveryPlan.RestorableFields);
            Assert.Single(recoveryPlan.UncertainActionIds);
        }

        var recoveryAdapter = new FakeFieldAdapter(plan.Actions);
        recoveryAdapter.SetCurrent(plan.Actions[0], plan.Actions[0].Desired);
        using var reopened = T15Fixtures.OpenJournal(device);
        var recovery = await new RepairRecoveryExecutor(reopened).ExecuteAsync(
            RecoveryRequest(recoveryPlan, recoveryAdapter, plan), CancellationToken.None);

        Assert.Equal(RepairTransactionState.RecoveryRequired, recovery.FinalState);
        Assert.Equal(0, recoveryAdapter.SetCalls);
        Assert.Contains(recovery.Refusals, refusal => refusal.Code == RepairTransactionRefusalCode.RecoveryUncertain);
    }

    [Fact]
    public async Task A_committed_transaction_plans_a_new_authorization_for_revocation()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice();
        var adapter = new FakeFieldAdapter(plan.Actions);
        using var journal = T15Fixtures.OpenJournal(device);
        await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), CancellationToken.None);

        var scan = RepairJournalRecoveryScanner.Scan(journal, Now);
        var summary = Assert.Single(scan.Transactions);
        Assert.True(summary.IsCommitted);
        Assert.Empty(scan.Unresolved); // a committed transaction is not an unfinished process

        var recoveryPlan = RepairRecoveryPlanner.Plan(summary, Now);
        Assert.True(recoveryPlan.RequiresNewAuthorization);
        Assert.Single(recoveryPlan.RestorableFields);
    }

    [Fact]
    public async Task The_startup_scan_reports_unfinished_transactions_without_touching_them()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = 8 };
        var adapter = new FakeFieldAdapter(plan.Actions);
        using var journal = T15Fixtures.OpenJournal(device);
        await new RepairTransactionCoordinator(journal).ExecuteAsync(
            T15Fixtures.Request(journal, plan, authorization, adapter), CancellationToken.None);

        var scan = RepairJournalRecoveryScanner.Scan(journal, Now);

        Assert.False(string.IsNullOrWhiteSpace(scan.StartupNotice));
        Assert.Contains("未终结", scan.StartupNotice, StringComparison.Ordinal);
        Assert.Single(scan.Unresolved);
        Assert.Equal(1, adapter.SetCalls); // the scan performed no additional write
        Assert.DoesNotContain(journal.Records, record => record.Entry.Kind == RepairJournalKind.TransactionRolledBack);
    }

    private sealed record RecoveryFixture(
        RepairRecoveryPlan Plan, FakeJournalDevice Device, RepairPlan SourcePlan);

    private static async Task<RecoveryFixture> BuildFailedRecoveryFixtureAsync()
    {
        var (plan, authorization) = T15Fixtures.Plan();
        var device = new FakeJournalDevice { FailAppendCall = 8 };
        var adapter = new FakeFieldAdapter(plan.Actions);
        RepairRecoveryPlan recoveryPlan;
        using (var journal = T15Fixtures.OpenJournal(device))
        {
            await new RepairTransactionCoordinator(journal).ExecuteAsync(
                T15Fixtures.Request(journal, plan, authorization, adapter), CancellationToken.None);
            var summary = Assert.Single(RepairJournalRecoveryScanner.Scan(journal, Now).Unresolved);
            recoveryPlan = RepairRecoveryPlanner.Plan(summary, Now);
        }

        return new RecoveryFixture(recoveryPlan, device, plan);
    }

    private static RepairRecoveryRequest RecoveryRequest(
        RepairRecoveryPlan recoveryPlan, IRepairFieldAdapter adapter, RepairPlan plan) => new()
    {
        Plan = recoveryPlan,
        Authorization = T15Fixtures.RecoveryAuthorization(recoveryPlan),
        Adapter = adapter,
        NowUtc = Now,
        CurrentBootId = plan.BootId,
        UserSid = T15Fixtures.UserSid,
        LogonSessionId = T15Fixtures.LogonSession,
        DevelopmentIsolation = true,
    };

    private static byte[] RawRecord(long sequence, RepairJournalKind kind, byte[] payload)
    {
        var record = new byte[RepairJournalFormat.RecordOverhead + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x4E534A52);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), payload.Length);
        payload.CopyTo(record.AsSpan(20));
        SHA256.HashData(record.AsSpan(4, 16 + payload.Length)).AsSpan(0, 16)
            .CopyTo(record.AsSpan(20 + payload.Length));
        return record;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail a passing test.
        }
    }
}



