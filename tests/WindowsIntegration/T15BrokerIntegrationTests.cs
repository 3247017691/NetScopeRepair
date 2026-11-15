using System.Text.Json;
using NetScope.Broker;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Broker → journal → coordinator integration, all with a fake field adapter and a temporary
/// journal file under the test's own directory. The delivery trust gate stays closed unless the
/// test explicitly runs in development isolation; no native write and no elevation happens.
/// </summary>
public sealed class T15BrokerIntegrationTests
{
    private const string JournalFileName = "transactions.journal";

    private static BrokerTrustDecision DevelopmentTrust() => BrokerPlanFixtures.DevelopmentTrust();

    private static BrokerTrustDecision ClosedTrust() => new()
    {
        RealElevationEnabled = false,
        DevelopmentIsolationAllowed = false,
        IsSynthetic = false,
        Refusals = new[] { BrokerRefusal.Of(BrokerRefusalCode.RealElevationDisabled, "delivery policy closed") },
    };

    private static RepairJournalDirectoryPolicy DevelopmentPolicy(string root) => new()
    {
        ProductionRoot = Path.Combine(root, "production"),
        DevelopmentRoot = root,
        AllowDevelopmentIsolation = true,
    };

    private sealed class FakePathProbe : IRepairJournalPathProbe
    {
        public RepairJournalDirectoryFacts Inspect(string rootPath, bool developmentIsolation) => new()
        {
            RootPath = rootPath,
            Exists = Directory.Exists(rootPath),
            IsReparsePoint = false,
            IsUserWritable = true,
            IsSignedDeployment = false,
            IsElevated = false,
            DevelopmentIsolation = developmentIsolation,
        };
    }

    private static RepairBrokerTransactionHandler Handler(
        string root,
        BrokerTrustDecision? trust = null,
        IRepairFieldAdapter? adapter = null,
        IRepairObservationProbe? observation = null,
        PlatformCapabilityMatrix? capabilities = null) => new(
        trust ?? DevelopmentTrust(),
        DevelopmentPolicy(root),
        new FakePathProbe(),
        () => adapter ?? throw new InvalidOperationException("adapter factory must not be used"),
        () => observation ?? new FakeObservationProbe(),
        capabilities ?? BrokerPlanFixtures.SyntheticMatrix(),
        new FakeBrokerTimeProvider(),
        JournalFileName,
        developmentIsolation: true);

    private static BrokerTransactionExecutionRequest ExecutionRequest(
        RepairPlan plan, RepairAuthorizationBinding authorization, string transactionId = "tx-t15") => new()
    {
        TransactionId = transactionId,
        Plan = plan,
        Authorization = authorization,
        UserSid = T15Fixtures.UserSid,
        LogonSessionId = T15Fixtures.LogonSession,
        NowUtc = T15Fixtures.Now,
    };

    [Fact]
    public async Task The_default_handler_keeps_execution_not_implemented()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var (plan, authorization) = T15Fixtures.Plan();
            var outcome = await new UnavailableBrokerTransactionHandler().ExecuteAsync(
                ExecutionRequest(plan, authorization), CancellationToken.None);

            Assert.False(outcome.Executed);
            Assert.False(outcome.WroteAnyField);
            Assert.Contains(outcome.Refusals, refusal => refusal.Code == BrokerRefusalCode.ExecutionNotImplemented);
            Assert.False(File.Exists(Path.Combine(root, JournalFileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task A_closed_trust_decision_refuses_before_creating_a_journal()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var (plan, authorization) = T15Fixtures.Plan();
            var outcome = await Handler(root, trust: ClosedTrust()).ExecuteAsync(
                ExecutionRequest(plan, authorization), CancellationToken.None);

            Assert.False(outcome.Executed);
            Assert.Contains(outcome.Refusals, refusal => refusal.Code == BrokerRefusalCode.RealElevationDisabled);
            Assert.False(File.Exists(Path.Combine(root, JournalFileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Development_isolation_runs_the_coordinator_through_the_broker_handler()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var (plan, authorization) = T15Fixtures.Plan();
            var adapter = new FakeFieldAdapter(plan.Actions);
            var handler = Handler(root, adapter: adapter);

            var outcome = await handler.ExecuteAsync(
                ExecutionRequest(plan, authorization), CancellationToken.None);

            Assert.True(outcome.Executed);
            Assert.True(outcome.WroteAnyField);
            Assert.Equal(RepairTransactionState.Committed, outcome.State);
            Assert.Equal(1, adapter.SetCalls);
            Assert.Empty(outcome.Refusals);

            var journalPath = Path.Combine(root, JournalFileName);
            Assert.True(File.Exists(journalPath));
            using var device = RepairFileJournalDevice.Open(journalPath, developmentIsolation: true);
            using var journal = RepairJournal.Open(device);
            Assert.True(journal.IsUsable);
            Assert.Equal(RepairTransactionState.Committed, journal.LatestState("tx-t15"));
            Assert.Contains(journal.Records, record => record.Entry.Kind == RepairJournalKind.StepApplied);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task A_validated_transaction_runs_through_the_broker_session_when_a_handler_is_registered()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var (plan, authorization) = T15Fixtures.Plan();
            var adapter = new FakeFieldAdapter(plan.Actions);
            var handler = Handler(root, adapter: adapter);
            var options = BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                preflight: BrokerPlanFixtures.MatchingPreflight(plan),
                audit: new BrokerAuditTrail(new FakeBrokerTimeProvider()),
                clock: new FakeBrokerTimeProvider(),
                trust: DevelopmentTrust()) with
            {
                TransactionHandler = handler,
            };

            var (result, client) = await RunSessionAsync(options, plan, authorization, "tx-t15");

            Assert.Equal(BrokerStopReason.Completed, result.StopReason);
            Assert.NotNull(result.Execution);
            Assert.True(result.Execution!.Executed);
            Assert.Equal(RepairTransactionState.Committed, result.Execution.State);

            var resultFrame = Assert.Single(client.Received, frame => frame.Kind == BrokerMessageKind.TransactionResult);
            var body = resultFrame.Payload!.Value.Deserialize<BrokerTransactionResultMessage>(BrokerJson.Options)!;
            Assert.True(body.Accepted);
            Assert.True(body.Executed);
            Assert.Equal(1, adapter.SetCalls);

            var journalPath = Path.Combine(root, JournalFileName);
            using var device = RepairFileJournalDevice.Open(journalPath, developmentIsolation: true);
            using var journal = RepairJournal.Open(device);
            Assert.Equal(RepairTransactionState.Committed, journal.LatestState("tx-t15"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task A_failed_observation_is_reported_as_a_rollback_not_a_success()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var (plan, authorization) = T15Fixtures.Plan();
            var adapter = new FakeFieldAdapter(plan.Actions);
            var handler = Handler(root, adapter: adapter, observation: new FakeObservationProbe { Passed = false });
            var options = BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                preflight: BrokerPlanFixtures.MatchingPreflight(plan),
                audit: new BrokerAuditTrail(new FakeBrokerTimeProvider()),
                clock: new FakeBrokerTimeProvider(),
                trust: DevelopmentTrust()) with
            {
                TransactionHandler = handler,
            };

            var (result, client) = await RunSessionAsync(options, plan, authorization, "tx-t15");

            Assert.Equal(BrokerStopReason.Refused, result.StopReason);
            Assert.NotNull(result.Execution);
            Assert.False(result.Execution!.Executed);
            Assert.True(result.Execution.WroteAnyField);
            Assert.Equal(RepairTransactionState.RolledBack, result.Execution.State);

            var resultFrame = Assert.Single(client.Received, frame => frame.Kind == BrokerMessageKind.TransactionResult);
            var body = resultFrame.Payload!.Value.Deserialize<BrokerTransactionResultMessage>(BrokerJson.Options)!;
            Assert.True(body.Accepted);
            Assert.False(body.Executed);
            Assert.Equal(2, adapter.SetCalls); // forward write + exact restore

            var journalPath = Path.Combine(root, JournalFileName);
            using var device = RepairFileJournalDevice.Open(journalPath, developmentIsolation: true);
            using var journal = RepairJournal.Open(device);
            Assert.Equal(RepairTransactionState.RolledBack, journal.LatestState("tx-t15"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task A_corrupt_authoritative_journal_refuses_execution()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var journalPath = Path.Combine(root, JournalFileName);
            await File.WriteAllTextAsync(journalPath, "not-a-journal");
            var (plan, authorization) = T15Fixtures.Plan();
            var adapter = new FakeFieldAdapter(plan.Actions);

            var outcome = await Handler(root, adapter: adapter).ExecuteAsync(
                ExecutionRequest(plan, authorization), CancellationToken.None);

            Assert.False(outcome.Executed);
            Assert.Equal(0, adapter.SetCalls);
            Assert.Contains(outcome.Refusals, refusal => refusal.Code == BrokerRefusalCode.JournalUnavailable);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task An_unresolved_transaction_blocks_the_next_one_and_is_only_reported_at_startup()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var journalPath = Path.Combine(root, JournalFileName);
            using (var device = RepairFileJournalDevice.Open(journalPath, developmentIsolation: true))
            {
                using var journal = RepairJournal.Open(device, T15Fixtures.Now);
                journal.Append(new RepairJournalEntry
                {
                    TransactionId = "tx-unfinished",
                    Kind = RepairJournalKind.TransactionPrepared,
                    State = RepairTransactionState.Prepared,
                    AtUtc = T15Fixtures.Now,
                });
            }

            var (plan, authorization) = T15Fixtures.Plan();
            var adapter = new FakeFieldAdapter(plan.Actions);
            var outcome = await Handler(root, adapter: adapter).ExecuteAsync(
                ExecutionRequest(plan, authorization, "tx-next"), CancellationToken.None);

            Assert.False(outcome.Executed);
            Assert.Equal(0, adapter.SetCalls);
            Assert.Contains(outcome.Refusals, refusal => refusal.Code == BrokerRefusalCode.JournalUnavailable);

            var scan = new RepairBrokerRecoveryStartup(
                DevelopmentPolicy(root), new FakePathProbe(), new FakeBrokerTimeProvider(), JournalFileName,
                developmentIsolation: true).Scan(DevelopmentTrust());

            Assert.True(scan.JournalAvailable);
            Assert.Equal(1, scan.UnresolvedCount);
            Assert.Contains("未终结", scan.Notice, StringComparison.Ordinal);
            var preview = Assert.Single(scan.Previews);
            Assert.Equal("tx-unfinished", preview.TransactionId);
            Assert.Equal(0, adapter.SetCalls); // a startup scan never writes
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void The_startup_scan_is_unavailable_without_a_trusted_or_development_deployment()
    {
        var root = T15Fixtures.CreateJournalDirectory();
        try
        {
            var scan = new RepairBrokerRecoveryStartup(
                DevelopmentPolicy(root), new FakePathProbe(), new FakeBrokerTimeProvider(), JournalFileName,
                developmentIsolation: false).Scan(ClosedTrust());

            Assert.False(scan.JournalAvailable);
            Assert.Empty(scan.Previews);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<(BrokerSessionResult Result, ScriptedBrokerClient Client)> RunSessionAsync(
        BrokerSessionOptions options, RepairPlan plan, RepairAuthorizationBinding authorization, string transactionId)
    {
        var (clientStream, serverStream) = BrokerDuplex.Create();
        var evidence = BrokerPlanFixtures.TrustedPeer();
        var connection = new FakeBrokerConnection(serverStream, evidence);
        var session = new BrokerSession(options);
        var sessionTask = session.RunAsync(connection, CancellationToken.None);
        var client = new ScriptedBrokerClient(clientStream) { TransactionId = transactionId, PlanDigest = plan.Digest };
        var clientTask = Task.Run(async () =>
        {
            try
            {
                await client.SendHelloAsync(evidence.PeerProcessId, evidence.PeerProcessCreationUtc!.Value);
                var challenge = await client.ReadChallengeAsync();
                await client.SendAuthenticateAsync(challenge);
                await client.SendSubmitAsync(BrokerPlanFixtures.Submit(transactionId, plan, authorization));
                await client.ReadFrameAsync();
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or EndOfStreamException)
            {
                // The broker may close the pipe first.
            }
            finally
            {
                await clientStream.DisposeAsync();
            }
        });

        var result = await sessionTask.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // The scripted peer can outlive a refused session.
        }

        return (result, client);
    }

    private static void TryDelete(string path)
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



