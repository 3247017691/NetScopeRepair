using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Broker;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Protocol/format invariants: strict frames, closed message and action sets, one-time nonce
/// binding and every bounded session window. No socket, pipe or file is opened.
/// </summary>
public sealed class BrokerProtocolTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Plan_round_trips_over_a_frame_and_keeps_the_same_digest()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var submit = new BrokerSubmitTransactionMessage
        {
            TransactionId = "tx-t14",
            PlanDigest = plan.Digest,
            TargetFingerprint = plan.TargetFingerprint,
            Plan = plan,
            Authorization = authorization,
        };
        var frame = BrokerMessages.Frame(BrokerMessageKind.SubmitTransaction, submit);

        using var stream = new MemoryStream();
        await BrokerFrameCodec.WriteAsync(stream, frame, Ct);
        stream.Position = 0;
        var read = await BrokerFrameCodec.ReadAsync(stream, Ct);
        var parsed = BrokerMessages.ReadSubmitTransaction(read);

        Assert.Equal(plan.Digest, parsed.Plan.Digest);
        Assert.Equal(plan.Digest, RepairPlanDigest.Compute(parsed.Plan));
        Assert.Equal(plan.TargetFingerprint, parsed.Plan.TargetFingerprint);
        Assert.Equal(authorization.PlanDigest, parsed.Authorization.PlanDigest);
        var action = Assert.Single(parsed.Plan.Actions);
        Assert.Equal(plan.Actions[0].ActionId, action.ActionId);
        var before = Assert.IsType<InterfaceMetricValue>(action.Before);
        var desired = Assert.IsType<InterfaceMetricValue>(action.Desired);
        Assert.Equal(35u, before.RawValue);
        Assert.Equal(20u, desired.RawValue);
        Assert.Equal(MetricValueKind.ManualConfigured, desired.Kind);
        Assert.Equal(RepairActionKind.SetTunIpv4InterfaceMetric, action.Kind);
        Assert.Equal(RepairField.InterfaceMetric, action.Field);
    }

    [Fact]
    public async Task A_frame_declared_larger_than_64_kib_is_rejected_before_its_body()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, BrokerProtocol.MaxFrameBytes + 1);
        stream.Write(header);
        stream.Position = 0;

        var error = await Assert.ThrowsAsync<BrokerProtocolException>(() => BrokerFrameCodec.ReadAsync(stream, Ct));
        Assert.Equal(BrokerRefusalCode.FrameTooLarge, error.Code);
    }

    [Fact]
    public async Task Writing_an_oversized_frame_is_refused()
    {
        var huge = new string('x', BrokerProtocol.MaxFrameBytes);
        var frame = BrokerMessages.Frame(BrokerMessageKind.Hello, new BrokerHelloMessage
        {
            ProtocolVersion = BrokerProtocol.Version,
            ClientNonce = new string('a', BrokerProtocol.NonceHexLength),
            ClientProcessId = 1,
            ClientProcessCreationUtc = BrokerPlanFixtures.Now,
            // Impossible on the wire; the string is what makes the frame oversized.
        });
        _ = huge;

        using var stream = new MemoryStream();
        var oversized = frame with
        {
            Payload = JsonSerializer.SerializeToElement(new { padding = new string('z', BrokerProtocol.MaxFrameBytes) }),
        };
        var error = await Assert.ThrowsAsync<BrokerProtocolException>(
            () => BrokerFrameCodec.WriteAsync(stream, oversized, Ct));
        Assert.Equal(BrokerRefusalCode.FrameTooLarge, error.Code);
    }

    [Fact]
    public async Task An_empty_frame_is_rejected()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[4]);
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<BrokerProtocolException>(() => BrokerFrameCodec.ReadAsync(stream, Ct));
        Assert.Equal(BrokerRefusalCode.FrameEmpty, error.Code);
    }

    [Fact]
    public async Task Unknown_envelope_members_and_kinds_are_rejected()
    {
        var unknownMember = Encoding.UTF8.GetBytes(
            "{\"protocol_version\":\"1.0\",\"kind\":\"hello\",\"message_id\":\"m1\",\"payload\":{},\"command\":\"run\"}");
        var unknownKind = Encoding.UTF8.GetBytes(
            "{\"protocol_version\":\"1.0\",\"kind\":\"run_command\",\"message_id\":\"m1\",\"payload\":{}}");
        var badVersion = Encoding.UTF8.GetBytes(
            "{\"protocol_version\":\"2.0\",\"kind\":\"hello\",\"message_id\":\"m1\",\"payload\":{}}");

        var unknownMemberError = await ReadRawErrorAsync(unknownMember);
        Assert.Equal(BrokerRefusalCode.InvalidPayload, unknownMemberError);

        var unknownKindError = await ReadRawErrorAsync(unknownKind);
        Assert.Equal(BrokerRefusalCode.InvalidPayload, unknownKindError);

        var versionError = await ReadRawErrorAsync(badVersion);
        Assert.Equal(BrokerRefusalCode.ProtocolVersionMismatch, versionError);
    }

    [Fact]
    public void A_hello_payload_cannot_carry_unknown_members_or_bad_claims()
    {
        var withUnknown = new BrokerFrame
        {
            ProtocolVersion = BrokerProtocol.Version,
            Kind = BrokerMessageKind.Hello,
            MessageId = "m1",
            Payload = JsonSerializer.SerializeToElement(new
            {
                protocol_version = BrokerProtocol.Version,
                client_nonce = new string('a', BrokerProtocol.NonceHexLength),
                client_process_id = 1,
                client_process_creation_utc = BrokerPlanFixtures.Now,
                command = "run",
            }),
        };
        var unknownError = Assert.Throws<BrokerProtocolException>(() => BrokerMessages.ReadHello(withUnknown));
        Assert.Equal(BrokerRefusalCode.InvalidPayload, unknownError.Code);

        var shortNonce = BrokerMessages.Frame(BrokerMessageKind.Hello, new BrokerHelloMessage
        {
            ProtocolVersion = BrokerProtocol.Version,
            ClientNonce = "abcd",
            ClientProcessId = 1,
            ClientProcessCreationUtc = BrokerPlanFixtures.Now,
        });
        var nonceError = Assert.Throws<BrokerProtocolException>(() => BrokerMessages.ReadHello(shortNonce));
        Assert.Equal(BrokerRefusalCode.NonceMissing, nonceError.Code);
    }

    [Fact]
    public void ReadHello_rejects_a_frame_of_another_kind()
    {
        var challenge = BrokerMessages.Frame(BrokerMessageKind.Challenge, new BrokerChallengeMessage
        {
            ProtocolVersion = BrokerProtocol.Version,
            BrokerNonce = new string('b', BrokerProtocol.NonceHexLength),
            IssuedAtUtc = BrokerPlanFixtures.Now,
            ExpiresAtUtc = BrokerPlanFixtures.Now.AddSeconds(60),
        });
        var error = Assert.Throws<BrokerProtocolException>(() => BrokerMessages.ReadHello(challenge));
        Assert.Equal(BrokerRefusalCode.UnexpectedMessage, error.Code);
    }

    [Fact]
    public async Task A_plan_with_an_unknown_field_or_action_value_is_rejected_at_parse_time()
    {
        var (plan, authorization) = BrokerPlanFixtures.ValidPlan();
        var submit = new BrokerSubmitTransactionMessage
        {
            TransactionId = "tx-t14",
            PlanDigest = plan.Digest,
            TargetFingerprint = plan.TargetFingerprint,
            Plan = plan,
            Authorization = authorization,
        };
        var node = JsonSerializer.SerializeToNode(submit, BrokerJson.Options)!.AsObject();
        node["plan"]!["unexpected_plan_field"] = "run";
        node["plan"]!["actions"]![0]!["kind"] = "set_property";

        var payload = node.Deserialize<JsonElement>(BrokerJson.Options);
        var frame = new BrokerFrame
        {
            ProtocolVersion = BrokerProtocol.Version,
            Kind = BrokerMessageKind.SubmitTransaction,
            MessageId = "m1",
            Payload = payload,
        };
        var error = Assert.Throws<BrokerProtocolException>(() => BrokerMessages.ReadSubmitTransaction(frame));
        Assert.Equal(BrokerRefusalCode.InvalidPayload, error.Code);
    }

    [Theory]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\payload")]
    [InlineData("%TEMP%\\payload.exe")]
    [InlineData("tx with space")]
    [InlineData("tx\nnewline")]
    public void Path_like_or_control_identifiers_are_rejected(string value)
    {
        Assert.False(BrokerIdentifiers.IsSafe(value));
    }

    [Theory]
    [InlineData("tx-t14")]
    [InlineData("sha256:0123456789abcdef")]
    [InlineData("guid|ipv4|0")]
    [InlineData("plan-t14.1")]
    public void Bounded_identifiers_are_accepted(string value)
    {
        Assert.True(BrokerIdentifiers.IsSafe(value));
    }

    [Fact]
    public void Nonce_binding_depends_on_both_challenges_transaction_and_plan_digest()
    {
        var client = new string('1', 64);
        var broker = new string('2', 64);
        var baseline = BrokerNonceBinding.Compute(client, broker, "tx-a", "sha256:a");
        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, BrokerNonceBinding.Compute(client, broker, "tx-a", "sha256:a"));
        Assert.NotEqual(baseline, BrokerNonceBinding.Compute(client, broker, "tx-b", "sha256:a"));
        Assert.NotEqual(baseline, BrokerNonceBinding.Compute(client, broker, "tx-a", "sha256:b"));
        Assert.NotEqual(baseline, BrokerNonceBinding.Compute(new string('3', 64), broker, "tx-a", "sha256:a"));
        Assert.NotEqual(baseline, BrokerNonceBinding.Compute(client, new string('4', 64), "tx-a", "sha256:a"));
        Assert.True(BrokerNonceBinding.Matches(baseline, baseline));
        Assert.False(BrokerNonceBinding.Matches(baseline, "00" + baseline[2..]));
    }

    [Fact]
    public void The_nonce_ledger_consumes_a_binding_once()
    {
        var ledger = new BrokerNonceLedger();
        var fingerprint = BrokerNonceLedger.Fingerprint(new string('a', 64));
        var now = BrokerPlanFixtures.Now;

        Assert.True(ledger.TryConsume(fingerprint, now, TimeSpan.FromSeconds(60)));
        Assert.False(ledger.TryConsume(fingerprint, now, TimeSpan.FromSeconds(60)));
        Assert.False(ledger.TryConsume(fingerprint, now.AddSeconds(59), TimeSpan.FromSeconds(60)));
        Assert.Equal(1, ledger.Count);

        // After its lifetime the entry is pruned; the challenge nonce itself is new per session,
        // so an old binding can never be replayed against a fresh challenge.
        Assert.True(ledger.TryConsume(fingerprint, now.AddSeconds(61), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void The_protocol_message_and_action_sets_are_closed()
    {
        var kinds = Enum.GetNames<BrokerMessageKind>();
        Assert.Equal(
            new[]
            {
                nameof(BrokerMessageKind.Hello),
                nameof(BrokerMessageKind.Challenge),
                nameof(BrokerMessageKind.Authenticate),
                nameof(BrokerMessageKind.SubmitTransaction),
                nameof(BrokerMessageKind.TransactionResult),
                nameof(BrokerMessageKind.Cancel),
                nameof(BrokerMessageKind.Close),
                nameof(BrokerMessageKind.Error),
            }.Order(StringComparer.Ordinal),
            kinds.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(kinds, name => name.Contains("Command", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Script", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Property", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(3, BrokerActionWhitelist.AllowedKinds.Count);
        Assert.DoesNotContain(RepairActionKind.RecoverTransaction, BrokerActionWhitelist.AllowedKinds);
        Assert.False(BrokerActionWhitelist.IsAllowed(RepairActionKind.RecoverTransaction));
        Assert.True(BrokerActionWhitelist.IsAllowed(RepairActionKind.SetTunIpv4InterfaceMetric));
        Assert.False(BrokerActionWhitelist.IsAllowed((RepairActionKind)123));
    }

    [Fact]
    public void Handshake_window_stops_at_ten_seconds()
    {
        var clock = new FakeBrokerTimeProvider();
        var budget = new BrokerSessionBudget(clock);

        Assert.Equal(BrokerBudgetStatus.Ok, budget.CheckHandshake());
        clock.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(BrokerBudgetStatus.Ok, budget.CheckHandshake());
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(BrokerBudgetStatus.HandshakeTimeout, budget.CheckHandshake());
    }

    [Fact]
    public void Idle_window_stops_at_thirty_seconds_and_activity_resets_it()
    {
        var clock = new FakeBrokerTimeProvider();
        var budget = new BrokerSessionBudget(clock);

        clock.Advance(TimeSpan.FromSeconds(29.9));
        Assert.Equal(BrokerBudgetStatus.Ok, budget.CheckIdle());
        budget.Touch();
        clock.Advance(TimeSpan.FromSeconds(29.9));
        Assert.Equal(BrokerBudgetStatus.Ok, budget.CheckIdle());
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(BrokerBudgetStatus.IdleTimeout, budget.CheckIdle());
    }

    [Fact]
    public void Total_budget_and_the_120_second_forward_window_are_enforced()
    {
        var clock = new FakeBrokerTimeProvider();
        var budget = new BrokerSessionBudget(clock);
        budget.StartTransaction();

        clock.Advance(TimeSpan.FromSeconds(119.9));
        Assert.Equal(BrokerBudgetStatus.Ok, budget.CheckForwardAction());
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(BrokerBudgetStatus.ForwardWindowClosed, budget.CheckForwardAction());

        clock.Advance(TimeSpan.FromSeconds(60.1));
        Assert.Equal(BrokerBudgetStatus.TotalBudgetExhausted, budget.CheckTotal());
        Assert.Equal(BrokerBudgetStatus.TotalBudgetExhausted, budget.CheckForwardAction());
    }

    [Fact]
    public void Observation_window_is_the_smaller_of_sixty_seconds_and_the_forward_budget()
    {
        var clock = new FakeBrokerTimeProvider();
        var budget = new BrokerSessionBudget(clock);
        budget.StartTransaction();

        Assert.Equal(TimeSpan.FromSeconds(60), budget.EffectiveObservationWindow());

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(60), budget.EffectiveObservationWindow());

        clock.Advance(TimeSpan.FromSeconds(60)); // 90 s
        Assert.Equal(TimeSpan.FromSeconds(30), budget.EffectiveObservationWindow());

        clock.Advance(TimeSpan.FromSeconds(29.9)); // 119.9 s
        Assert.Equal(TimeSpan.FromMilliseconds(100), budget.EffectiveObservationWindow());

        clock.Advance(TimeSpan.FromMilliseconds(100)); // 120 s
        Assert.Equal(TimeSpan.Zero, budget.EffectiveObservationWindow());
    }

    [Fact]
    public void The_compensation_reserve_is_never_spendable_by_forward_actions()
    {
        var clock = new FakeBrokerTimeProvider();
        var budget = new BrokerSessionBudget(clock);
        budget.StartTransaction();

        Assert.Equal(TimeSpan.FromSeconds(60), budget.RemainingCompensationReserve());
        clock.Advance(TimeSpan.FromSeconds(150));
        Assert.Equal(TimeSpan.FromSeconds(30), budget.RemainingCompensationReserve());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.Zero, budget.RemainingCompensationReserve());
    }

    [Fact]
    public void Read_timeout_is_the_smallest_of_handshake_idle_and_total_remaining()
    {
        var clock = new FakeBrokerTimeProvider();
        var budget = new BrokerSessionBudget(clock);

        Assert.Equal(TimeSpan.FromSeconds(10), budget.NextReadTimeout(BrokerSessionPhase.Handshake));

        budget.StartTransaction();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(25), budget.NextReadTimeout(BrokerSessionPhase.Transaction));

        clock.Advance(TimeSpan.FromSeconds(170)); // idle exceeded and only 5 s of total left
        Assert.Equal(TimeSpan.Zero, budget.NextReadTimeout(BrokerSessionPhase.Transaction));
    }

    private static async Task<BrokerRefusalCode> ReadRawErrorAsync(byte[] body)
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<BrokerProtocolException>(() => BrokerFrameCodec.ReadAsync(stream, Ct));
        return error.Code;
    }
}
