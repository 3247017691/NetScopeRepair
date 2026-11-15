using System.Text.Json;
using NetScope.Broker;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// End-to-end broker session over an in-memory duplex stream: handshake identity, one-time nonce,
/// typed transaction decision, bounded timeouts, disconnect and audit privacy. Nothing is
/// elevated, no named pipe is created and no write path exists.
/// </summary>
public sealed class BrokerSessionTests
{
    private sealed record SessionRun(
        BrokerSessionResult Result,
        FakeBrokerConnection Connection,
        ScriptedBrokerClient Client,
        BrokerAuditTrail Audit);

    private static async Task<SessionRun> RunAsync(
        Func<ScriptedBrokerClient, Task> script,
        BrokerPeerEvidence? evidence = null,
        Func<FakeBrokerTimeProvider, BrokerAuditTrail, BrokerSessionOptions>? optionsFactory = null)
    {
        var clock = new FakeBrokerTimeProvider();
        var audit = new BrokerAuditTrail(clock);
        var (clientStream, serverStream) = BrokerDuplex.Create();
        var connection = new FakeBrokerConnection(serverStream, evidence ?? BrokerPlanFixtures.TrustedPeer());
        var options = optionsFactory?.Invoke(clock, audit)
            ?? BrokerPlanFixtures.SessionOptions(audit: audit, clock: clock);
        var session = new BrokerSession(options);
        var sessionTask = session.RunAsync(connection, CancellationToken.None);
        var client = new ScriptedBrokerClient(clientStream);
        var clientTask = Task.Run(async () =>
        {
            try
            {
                await script(client);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or EndOfStreamException or ObjectDisposedException)
            {
                // Expected when the broker closes the pipe first.
            }
            finally
            {
                // A script that stops early simulates a disconnect, not a silent peer.
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
            // The scripted peer can outlive a refused session; the session outcome is authoritative.
        }

        return new SessionRun(result, connection, client, audit);
    }

    private static async Task<BrokerChallengeMessage> HandshakeAsync(
        ScriptedBrokerClient client, BrokerPeerEvidence evidence)
    {
        await client.SendHelloAsync(evidence.PeerProcessId, evidence.PeerProcessCreationUtc!.Value);
        return await client.ReadChallengeAsync();
    }

    [Fact]
    public async Task A_session_cannot_start_without_a_serving_trust_decision()
    {
        var closedTrust = new BrokerTrustDecision
        {
            RealElevationEnabled = false,
            DevelopmentIsolationAllowed = false,
            IsSynthetic = false,
            Refusals = new[]
            {
                BrokerRefusal.Of(BrokerRefusalCode.RealElevationDisabled, "delivery policy closed"),
            },
        };
        var run = await RunAsync(
            _ => Task.CompletedTask,
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(
                trust: closedTrust, audit: audit, clock: clock));

        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
        Assert.False(run.Result.HandshakeCompleted);
        Assert.False(run.Result.IdentityVerified);
        Assert.False(run.Result.NonceConsumed);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.RealElevationDisabled);
        Assert.Empty(run.Client.Received);
    }

    [Fact]
    public async Task A_verified_peer_gets_an_honest_execution_refusal_after_validation()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendSubmitAsync(BrokerPlanFixtures.Submit("tx-t14", plan, authorization));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                preflight: BrokerPlanFixtures.MatchingPreflight(plan),
                audit: audit,
                clock: clock));

        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
        Assert.True(run.Result.HandshakeCompleted);
        Assert.True(run.Result.IdentityVerified);
        Assert.True(run.Result.NonceConsumed);
        Assert.NotNull(run.Result.Decision);
        Assert.True(run.Result.Decision!.Accepted);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.ExecutionNotImplemented);
        Assert.DoesNotContain(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.InternalError);

        var resultFrame = Assert.Single(run.Client.Received, frame => frame.Kind == BrokerMessageKind.TransactionResult);
        var body = resultFrame.Payload!.Value.Deserialize<BrokerTransactionResultMessage>(BrokerJson.Options)!;
        Assert.True(body.Accepted);
        Assert.False(body.Executed);
        Assert.Contains(BrokerRefusalCode.ExecutionNotImplemented, body.Codes);

        Assert.True(run.Connection.FakeLease.DisposeCount >= 1);
        Assert.False(run.Connection.FakeLease.IsHeld);
        Assert.True(run.Result.PeerLeaseReleased);
    }

    [Fact]
    public async Task The_delivery_capability_state_refuses_the_transaction_inside_the_session()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendSubmitAsync(BrokerPlanFixtures.Submit("tx-t14", plan, authorization));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(
                capabilities: PlatformCapabilityMatrix.UnverifiedDefault,
                preflight: BrokerPlanFixtures.MatchingPreflight(plan),
                audit: audit,
                clock: clock));

        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
        Assert.NotNull(run.Result.Decision);
        Assert.False(run.Result.Decision!.Accepted);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.CapabilityUnavailable);

        var resultFrame = Assert.Single(run.Client.Received, frame => frame.Kind == BrokerMessageKind.TransactionResult);
        var body = resultFrame.Payload!.Value.Deserialize<BrokerTransactionResultMessage>(BrokerJson.Options)!;
        Assert.False(body.Accepted);
        Assert.False(body.Executed);
        Assert.Contains(BrokerRefusalCode.CapabilityUnavailable, body.Codes);
    }

    [Fact]
    public async Task A_changed_plan_value_after_consent_is_reported_as_requiring_a_new_preview()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
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
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendSubmitAsync(BrokerPlanFixtures.Submit("tx-t14", tampered, authorization, plan.Digest));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                preflight: BrokerPlanFixtures.MatchingPreflight(tampered),
                audit: audit,
                clock: clock));

        var resultFrame = Assert.Single(run.Client.Received, frame => frame.Kind == BrokerMessageKind.TransactionResult);
        var body = resultFrame.Payload!.Value.Deserialize<BrokerTransactionResultMessage>(BrokerJson.Options)!;
        Assert.False(body.Accepted);
        Assert.True(body.RequiresNewPreview);
        Assert.Contains(BrokerRefusalCode.PlanDigestMismatch, body.Codes);
        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
    }

    [Fact]
    public async Task An_untrusted_peer_is_rejected_before_a_challenge_is_issued()
    {
        var peer = BrokerPlanFixtures.TrustedPeer() with { PeerUserSid = "S-1-5-21-9999" };
        var run = await RunAsync(
            async client =>
            {
                await client.SendHelloAsync(peer.PeerProcessId, peer.PeerProcessCreationUtc!.Value);
                await client.ReadFrameAsync();
            },
            evidence: peer);

        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
        Assert.False(run.Result.HandshakeCompleted);
        Assert.False(run.Result.IdentityVerified);
        Assert.False(run.Result.NonceConsumed);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.PeerUserMismatch);
        var error = Assert.Single(run.Client.Received);
        Assert.Equal(BrokerMessageKind.Error, error.Kind);
        Assert.False(run.Connection.FakeLease.IsHeld);
    }

    [Fact]
    public async Task A_hello_claiming_a_reused_pid_creation_time_is_rejected()
    {
        var peer = BrokerPlanFixtures.TrustedPeer();
        var run = await RunAsync(
            async client =>
            {
                await client.SendHelloAsync(peer.PeerProcessId, peer.PeerProcessCreationUtc!.Value.AddHours(1));
                await client.ReadFrameAsync();
            },
            evidence: peer);

        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.PeerCreationTimeMismatch);
    }

    [Fact]
    public async Task A_nonce_binding_cannot_be_replayed()
    {
        var ledger = new BrokerNonceLedger();
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var optionsFactory = (Func<FakeBrokerTimeProvider, BrokerAuditTrail, BrokerSessionOptions>)((clock, audit) =>
            BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                audit: audit,
                clock: clock,
                ledger: ledger,
                nonceSource: new FixedBrokerNonceSource()));

        var script = (Func<ScriptedBrokerClient, Task>)(async client =>
        {
            client.TransactionId = "tx-t14";
            client.PlanDigest = plan.Digest;
            var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
            await client.SendAuthenticateAsync(challenge);
            try
            {
                await client.SendSubmitAsync(
                    BrokerPlanFixtures.Submit("tx-t14", plan, BrokerPlanFixtures.Authorization(plan)));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A replayed handshake is refused before any transaction is accepted.
            }

            await client.ReadFrameAsync();
        });

        var first = await RunAsync(script, optionsFactory: optionsFactory);
        Assert.True(first.Result.NonceConsumed);
        Assert.True(first.Result.HandshakeCompleted);

        var second = await RunAsync(script, optionsFactory: optionsFactory);
        Assert.False(second.Result.HandshakeCompleted);
        Assert.False(second.Result.NonceConsumed);
        Assert.Equal(BrokerStopReason.ProtocolError, second.Result.StopReason);
        Assert.Contains(second.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.NonceReplayed);
        var error = Assert.Single(second.Client.Received, frame => frame.Kind == BrokerMessageKind.Error);
        var body = error.Payload!.Value.Deserialize<BrokerErrorMessage>(BrokerJson.Options)!;
        Assert.Equal(BrokerRefusalCode.NonceReplayed, body.Code);
    }

    [Fact]
    public async Task A_nonce_binding_that_does_not_cover_the_declared_digest_is_refused()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                var otherDigest = "sha256:" + new string('9', 64);
                await client.SendAuthenticateAsync(
                    challenge,
                    planDigest: plan.Digest,
                    binding: BrokerNonceBinding.Compute(client.ClientNonce, challenge.BrokerNonce, "tx-t14", otherDigest));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(audit: audit, clock: clock));

        Assert.Equal(BrokerStopReason.ProtocolError, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.NonceMismatch);
    }

    [Fact]
    public async Task A_submitted_transaction_must_be_the_one_bound_by_the_nonce()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendSubmitAsync(BrokerPlanFixtures.Submit("tx-other", plan, authorization));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                preflight: BrokerPlanFixtures.MatchingPreflight(plan),
                audit: audit,
                clock: clock));

        Assert.Equal(BrokerStopReason.Refused, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.NonceMismatch);
        Assert.True(run.Result.NonceConsumed);
    }

    [Fact]
    public async Task A_disconnect_stops_the_session_and_reports_the_missing_journal()
    {
        var run = await RunAsync(client =>
        {
            // Say hello, then vanish: the broker must stop new actions and must not claim recovery.
            var evidence = BrokerPlanFixtures.TrustedPeer();
            return client.SendHelloAsync(evidence.PeerProcessId, evidence.PeerProcessCreationUtc!.Value);
        });

        Assert.Equal(BrokerStopReason.Disconnected, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.Disconnected);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.TransactionJournalNotImplemented);
        Assert.Contains(run.Audit.Entries, entry => entry.Kind == BrokerAuditEventKind.Disconnected);
    }

    [Fact]
    public async Task An_unexpected_frame_after_the_handshake_is_a_protocol_error()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendFrameAsync(BrokerMessages.Frame(BrokerMessageKind.Challenge, new BrokerChallengeMessage
                {
                    ProtocolVersion = BrokerProtocol.Version,
                    BrokerNonce = new string('c', 64),
                    IssuedAtUtc = BrokerPlanFixtures.Now,
                    ExpiresAtUtc = BrokerPlanFixtures.Now.AddSeconds(60),
                }));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(audit: audit, clock: clock));

        Assert.Equal(BrokerStopReason.ProtocolError, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.UnexpectedMessage);
    }

    [Fact]
    public async Task An_oversized_frame_header_after_the_handshake_is_rejected()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendRawAsync(Array.Empty<byte>(), declaredLength: BrokerProtocol.MaxFrameBytes + 1);
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(audit: audit, clock: clock));

        Assert.Equal(BrokerStopReason.ProtocolError, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.FrameTooLarge);
    }

    [Fact]
    public async Task A_peer_close_after_the_handshake_is_a_clean_stop()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendCloseAsync();
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(audit: audit, clock: clock));

        Assert.Equal(BrokerStopReason.Closed, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.SessionClosed);
    }

    [Fact]
    public async Task A_peer_cancel_is_reported_and_stops_the_session()
    {
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendFrameAsync(BrokerMessages.Frame(BrokerMessageKind.Cancel, new BrokerCancelMessage
                {
                    TransactionId = "tx-t14",
                    Reason = "user cancelled",
                }));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(audit: audit, clock: clock));

        Assert.Equal(BrokerStopReason.Cancelled, run.Result.StopReason);
        Assert.Contains(run.Result.Refusals, refusal => refusal.Code == BrokerRefusalCode.Cancelled);
        // Cancel happens inside the forward window, so the clock itself still allowed actions;
        // the cancellation, not the budget, stopped the session.
        Assert.True(run.Result.ForwardActionsAllowedAtStop);
    }

    [Fact]
    public async Task The_audit_trail_never_contains_nonce_binding_or_plan_payload()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var nonceSource = new FixedBrokerNonceSource();
        var run = await RunAsync(
            async client =>
            {
                client.TransactionId = "tx-t14";
                client.PlanDigest = plan.Digest;
                var challenge = await HandshakeAsync(client, BrokerPlanFixtures.TrustedPeer());
                await client.SendAuthenticateAsync(challenge);
                await client.SendSubmitAsync(BrokerPlanFixtures.Submit("tx-t14", plan, authorization));
                await client.ReadFrameAsync();
            },
            optionsFactory: (clock, audit) => BrokerPlanFixtures.SessionOptions(
                capabilities: BrokerPlanFixtures.SyntheticMatrix(),
                preflight: BrokerPlanFixtures.MatchingPreflight(plan),
                audit: audit,
                clock: clock,
                nonceSource: nonceSource));

        var binding = BrokerNonceBinding.Compute(
            ScriptedBrokerClient.ClientNonceHex, nonceSource.Hex, "tx-t14", plan.Digest);
        var auditText = run.Audit.ToText();

        Assert.DoesNotContain(nonceSource.Hex, auditText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ScriptedBrokerClient.ClientNonceHex, auditText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(binding, auditText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("interface_metric", auditText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NetScopeRepair.Desktop.exe", auditText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", auditText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(run.Audit.Entries, entry => entry.Kind == BrokerAuditEventKind.TransactionAccepted);
        Assert.Contains(run.Audit.Entries, entry => entry.Kind == BrokerAuditEventKind.ExecutionUnavailable);
    }

    [Fact]
    public async Task A_silent_peer_times_out_at_the_ten_second_handshake_window()
    {
        var clock = new FakeBrokerTimeProvider();
        var (clientStream, serverStream) = BrokerDuplex.Create();
        var connection = new FakeBrokerConnection(serverStream, BrokerPlanFixtures.TrustedPeer());
        var options = BrokerPlanFixtures.SessionOptions(clock: clock, audit: new BrokerAuditTrail(clock));
        var session = new BrokerSession(options);

        var task = session.RunAsync(connection, CancellationToken.None);
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(10.1));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BrokerStopReason.HandshakeTimeout, result.StopReason);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.HandshakeTimeout);
        Assert.False(result.HandshakeCompleted);
        await clientStream.DisposeAsync();
    }

    [Fact]
    public async Task A_silent_peer_after_the_handshake_hits_the_idle_window()
    {
        var clock = new FakeBrokerTimeProvider();
        var audit = new BrokerAuditTrail(clock);
        var (clientStream, serverStream) = BrokerDuplex.Create();
        var peer = BrokerPlanFixtures.TrustedPeer();
        var connection = new FakeBrokerConnection(serverStream, peer);
        var (plan, _) = BrokerPlanFixtures.ValidPlan();
        var options = BrokerPlanFixtures.SessionOptions(clock: clock, audit: audit);
        var session = new BrokerSession(options);
        var sessionTask = session.RunAsync(connection, CancellationToken.None);

        var client = new ScriptedBrokerClient(clientStream);
        client.TransactionId = "tx-t14";
        client.PlanDigest = plan.Digest;
        var challenge = await HandshakeAsync(client, peer);
        await client.SendAuthenticateAsync(challenge);
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(30.1));

        var result = await sessionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BrokerStopReason.IdleTimeout, result.StopReason);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.IdleTimeout);
        Assert.True(result.HandshakeCompleted);
        await clientStream.DisposeAsync();
    }
}





