using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.Broker;

/// <summary>One accepted local IPC connection. The lease must stay held for the whole transaction.</summary>
public interface IBrokerConnection : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>Kernel-reported facts collected at connect time; the hello claims are merged later.</summary>
    BrokerPeerEvidence PeerEvidence { get; }

    IBrokerPeerLease Lease { get; }
}

/// <summary>Accepts exactly one local peer for one transaction.</summary>
public interface IBrokerListener : IAsyncDisposable
{
    string PipeName { get; }

    Task<IBrokerConnection> AcceptAsync(CancellationToken cancellationToken);
}

public enum BrokerStopReason
{
    /// <summary>The peer closed the session after the handshake.</summary>
    Closed,

    Cancelled,
    Disconnected,
    Refused,
    HandshakeTimeout,
    IdleTimeout,
    TotalBudgetExhausted,
    ProtocolError,

    /// <summary>The validated transaction ran to a successful terminal state.</summary>
    Completed,
}

public sealed record BrokerSessionResult
{
    public required BrokerStopReason StopReason { get; init; }

    public required bool HandshakeCompleted { get; init; }

    public required bool IdentityVerified { get; init; }

    /// <summary>True only when the handshake binding was consumed exactly once.</summary>
    public required bool NonceConsumed { get; init; }

    public required BrokerTransactionDecision? Decision { get; init; }

    public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>Whether a new forward action could still start at the moment the session stopped.</summary>
    public required bool ForwardActionsAllowedAtStop { get; init; }

    public required TimeSpan ObservationWindowAtStop { get; init; }

    /// <summary>True only after the broker released the held peer process handle.</summary>
    public required bool PeerLeaseReleased { get; init; }

    /// <summary>Outcome of the injected transaction handler, when one is registered.</summary>
    public BrokerTransactionExecutionOutcome? Execution { get; init; }
}

public sealed record BrokerSessionOptions
{
    /// <summary>Deployment trust decision. Only the delivery/development form may start a session.</summary>
    public required BrokerTrustDecision Trust { get; init; }

    public required PlatformCapabilityMatrix Capabilities { get; init; }

    public required IBrokerPreflightSource Preflight { get; init; }

    public required IBrokerNonceSource NonceSource { get; init; }

    public required string InitiatingUserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required int BrokerSessionId { get; init; }

    public required TimeProvider Clock { get; init; }

    public required BrokerAuditTrail Audit { get; init; }

    public BrokerNonceLedger? NonceLedger { get; init; }

    /// <summary>
    /// Optional transaction handler. When null, a validated transaction keeps the T14 behaviour:
    /// it is refused with execution_not_implemented and no write is attempted.
    /// </summary>
    public IBrokerTransactionHandler? TransactionHandler { get; init; }
}

/// <summary>
/// The one-shot broker session: bounded handshake, kernel-verified peer identity, one-time nonce
/// binding, then exactly one typed transaction decision. It has no privileged write path: an
/// accepted transaction still ends with <c>execution_not_implemented</c>, because T14 owns the
/// protocol/identity/whitelist boundary only.
/// </summary>
public sealed class BrokerSession
{
    private readonly BrokerSessionOptions _options;
    private readonly BrokerNonceLedger _ledger;

    public BrokerSession(BrokerSessionOptions options, BrokerNonceLedger? nonceLedger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ledger = nonceLedger ?? _options.NonceLedger ?? new BrokerNonceLedger();
    }

    public async Task<BrokerSessionResult> RunAsync(IBrokerConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        BrokerSessionResult result;
        try
        {
            result = await RunSessionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The lease must be released only after the transaction or refusal is final.
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A broken pipe after the outcome is already decided must not replace that outcome.
            }
        }

        _options.Audit.Record(
            BrokerAuditEventKind.SessionEnded,
            result.Refusals.Select(refusal => refusal.Code),
            result.Decision?.TransactionId);

        return result with { PeerLeaseReleased = !connection.Lease.IsHeld };
    }

    private async Task<BrokerSessionResult> RunSessionAsync(
        IBrokerConnection connection, CancellationToken cancellationToken)
    {
        var audit = _options.Audit;
        var budget = new BrokerSessionBudget(_options.Clock);

        if (!_options.Trust.CanServe)
        {
            // Defence in depth: a session cannot run outside the deployment trust decision, even
            // if a caller constructs it directly instead of going through BrokerHost.
            audit.Record(BrokerAuditEventKind.HostRefused, _options.Trust.Refusals.Select(refusal => refusal.Code));
            return new BrokerSessionResult
            {
                StopReason = BrokerStopReason.Refused,
                HandshakeCompleted = false,
                IdentityVerified = false,
                NonceConsumed = false,
                Decision = null,
                Execution = null,
                Refusals = _options.Trust.Refusals,
                Elapsed = budget.Elapsed,
                ForwardActionsAllowedAtStop = false,
                ObservationWindowAtStop = TimeSpan.Zero,
                PeerLeaseReleased = false,
            };
        }
        var refusals = new List<BrokerRefusal>();
        var handshakeCompleted = false;
        var identityVerified = false;
        var nonceConsumed = false;
        BrokerTransactionDecision? decision = null;
        BrokerTransactionExecutionOutcome? execution = null;
        string? boundTransactionId = null;
        string? boundPlanDigest = null;
        var peerProcessId = connection.PeerEvidence.PeerProcessId;

        BrokerSessionResult Result(BrokerStopReason stop, IEnumerable<BrokerRefusal>? extra = null)
        {
            if (extra is not null) refusals.AddRange(extra);
            return new BrokerSessionResult
            {
                StopReason = stop,
                HandshakeCompleted = handshakeCompleted,
                IdentityVerified = identityVerified,
                NonceConsumed = nonceConsumed,
                Decision = decision,
                Execution = execution,
                Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
                Elapsed = budget.Elapsed,
                ForwardActionsAllowedAtStop = budget.CheckForwardAction() == BrokerBudgetStatus.Ok,
                ObservationWindowAtStop = budget.EffectiveObservationWindow(),
                PeerLeaseReleased = false,
            };
        }

        try
        {
            // 1. The peer must announce itself within the 10 second handshake window.
            var helloFrame = await ReadFrameAsync(connection.Stream, budget, BrokerSessionPhase.Handshake, cancellationToken)
                .ConfigureAwait(false);
            var hello = BrokerMessages.ReadHello(helloFrame);
            if (!string.Equals(hello.ProtocolVersion, BrokerProtocol.Version, StringComparison.Ordinal))
            {
                throw new BrokerProtocolException(
                    BrokerRefusalCode.ProtocolVersionMismatch, "The hello protocol version is not supported.");
            }

            // 2. Kernel facts plus the claimed pid/creation time must prove the peer identity.
            var evidence = connection.PeerEvidence with
            {
                ClaimedProcessId = hello.ClientProcessId,
                ClaimedProcessCreationUtc = hello.ClientProcessCreationUtc,
            };
            var identity = BrokerPeerIdentityPolicy.Evaluate(evidence);
            if (!identity.Allowed)
            {
                await TrySendAsync(connection.Stream, BrokerMessages.Error(
                    identity.Refusals[0].Code,
                    "The peer identity was rejected.",
                    identity.Refusals.Select(refusal => refusal.Code)), cancellationToken).ConfigureAwait(false);
                audit.Record(
                    BrokerAuditEventKind.PeerRejected,
                    identity.Refusals.Select(refusal => refusal.Code),
                    peerProcessId: peerProcessId);
                return Result(BrokerStopReason.Refused, identity.Refusals);
            }

            identityVerified = true;
            audit.Record(BrokerAuditEventKind.IdentityVerified, peerProcessId: peerProcessId);

            // 3. Challenge with a fresh 256-bit nonce. It travels only over the verified pipe.
            var brokerNonce = BrokerNonceBinding.NextNonceHex(_options.NonceSource);
            var issuedAt = _options.Clock.GetUtcNow();
            await WriteFrameAsync(connection.Stream, BrokerMessages.Frame(BrokerMessageKind.Challenge, new BrokerChallengeMessage
            {
                ProtocolVersion = BrokerProtocol.Version,
                BrokerNonce = brokerNonce,
                IssuedAtUtc = issuedAt,
                ExpiresAtUtc = issuedAt + BrokerProtocol.NonceLifetime,
            }), cancellationToken).ConfigureAwait(false);
            audit.Record(BrokerAuditEventKind.ChallengeIssued, peerProcessId: peerProcessId);

            // 4. Authenticate inside the same handshake window and consume the binding once.
            var authenticateFrame = await ReadFrameAsync(connection.Stream, budget, BrokerSessionPhase.Handshake, cancellationToken)
                .ConfigureAwait(false);
            var authenticate = BrokerMessages.ReadAuthenticate(authenticateFrame);
            if (budget.CheckHandshake() != BrokerBudgetStatus.Ok)
            {
                throw new BrokerSessionTimeoutException(BrokerRefusalCode.HandshakeTimeout);
            }

            var expectedBinding = BrokerNonceBinding.Compute(
                hello.ClientNonce, brokerNonce, authenticate.TransactionId, authenticate.PlanDigest);
            var nonceMatches = string.Equals(authenticate.ClientNonce, hello.ClientNonce, StringComparison.Ordinal)
                && string.Equals(authenticate.BrokerNonce, brokerNonce, StringComparison.Ordinal)
                && BrokerNonceBinding.Matches(expectedBinding, authenticate.NonceBinding);
            if (!nonceMatches)
            {
                audit.Record(BrokerAuditEventKind.NonceRejected, new[] { BrokerRefusalCode.NonceMismatch });
                throw new BrokerProtocolException(
                    BrokerRefusalCode.NonceMismatch,
                    "The handshake nonce binding does not match the challenges, transaction id and plan digest.");
            }

            if (_options.Clock.GetUtcNow() > issuedAt + BrokerProtocol.NonceLifetime)
            {
                audit.Record(BrokerAuditEventKind.NonceRejected, new[] { BrokerRefusalCode.NonceExpired });
                throw new BrokerProtocolException(BrokerRefusalCode.NonceExpired, "The challenge nonce has expired.");
            }

            if (!_ledger.TryConsume(
                    BrokerNonceLedger.Fingerprint(authenticate.NonceBinding),
                    _options.Clock.GetUtcNow(),
                    BrokerProtocol.NonceLifetime))
            {
                audit.Record(BrokerAuditEventKind.NonceRejected, new[] { BrokerRefusalCode.NonceReplayed });
                throw new BrokerProtocolException(BrokerRefusalCode.NonceReplayed, "The handshake binding was already consumed.");
            }

            nonceConsumed = true;
            handshakeCompleted = true;
            boundTransactionId = authenticate.TransactionId;
            boundPlanDigest = authenticate.PlanDigest;
            budget.Touch();
            budget.StartTransaction();

            // 5. Exactly one typed transaction decision, inside the bounded windows.
            while (true)
            {
                var frame = await ReadFrameAsync(connection.Stream, budget, BrokerSessionPhase.Transaction, cancellationToken)
                    .ConfigureAwait(false);
                budget.Touch();

                switch (frame.Kind)
                {
                    case BrokerMessageKind.SubmitTransaction:
                    {
                        var forward = budget.CheckForwardAction();
                        if (forward != BrokerBudgetStatus.Ok)
                        {
                            var code = forward == BrokerBudgetStatus.TotalBudgetExhausted
                                ? BrokerRefusalCode.TotalBudgetExhausted
                                : BrokerRefusalCode.ForwardWindowClosed;
                            var refusal = BrokerRefusal.Of(
                                code,
                                "No new forward action may start after the forward window closed or the budget ran out.");
                            await TrySendAsync(connection.Stream, BrokerMessages.Error(code, refusal.Detail), cancellationToken)
                                .ConfigureAwait(false);
                            audit.Record(
                                forward == BrokerBudgetStatus.TotalBudgetExhausted
                                    ? BrokerAuditEventKind.BudgetExhausted
                                    : BrokerAuditEventKind.TransactionRefused,
                                new[] { code },
                                peerProcessId: peerProcessId);
                            return Result(forward == BrokerBudgetStatus.TotalBudgetExhausted
                                ? BrokerStopReason.TotalBudgetExhausted
                                : BrokerStopReason.Refused, new[] { refusal });
                        }

                        var submit = BrokerMessages.ReadSubmitTransaction(frame);
                        audit.Record(
                            BrokerAuditEventKind.TransactionSubmitted,
                            transactionId: submit.TransactionId,
                            actionCount: submit.Plan.Actions.Count);

                        if (!string.Equals(submit.TransactionId, boundTransactionId, StringComparison.Ordinal)
                            || !string.Equals(submit.PlanDigest, boundPlanDigest, StringComparison.Ordinal))
                        {
                            var refusal = BrokerRefusal.Of(
                                BrokerRefusalCode.NonceMismatch,
                                "The submitted transaction is not the transaction bound by the handshake nonce.");
                            await TrySendAsync(connection.Stream, BrokerMessages.Error(refusal.Code, refusal.Detail), cancellationToken)
                                .ConfigureAwait(false);
                            return Result(BrokerStopReason.Refused, new[] { refusal });
                        }

                        var preflight = await CollectPreflightAsync(submit.Plan, cancellationToken).ConfigureAwait(false);
                        decision = BrokerTransactionValidator.Validate(new BrokerTransactionValidationRequest
                        {
                            Submit = submit,
                            InitiatingUserSid = _options.InitiatingUserSid,
                            LogonSessionId = _options.LogonSessionId,
                            NowUtc = _options.Clock.GetUtcNow(),
                            Capabilities = _options.Capabilities,
                            Preflight = preflight,
                        });

                        audit.Record(
                            decision.Accepted
                                ? BrokerAuditEventKind.TransactionAccepted
                                : BrokerAuditEventKind.TransactionRefused,
                            decision.Refusals.Select(refusal => refusal.Code),
                            decision.TransactionId,
                            peerProcessId,
                            submit.Plan.Actions.Count);

                        if (!decision.Accepted)
                        {
                            await TrySendAsync(connection.Stream, BrokerMessages.TransactionResult(new BrokerTransactionResultMessage
                            {
                                TransactionId = decision.TransactionId,
                                PlanDigest = decision.PlanDigest,
                                Accepted = false,
                                Executed = false,
                                RequiresNewPreview = decision.RequiresNewPreview,
                                Codes = decision.Refusals.Select(refusal => refusal.Code).ToArray(),
                            }), cancellationToken).ConfigureAwait(false);
                            return Result(BrokerStopReason.Refused, decision.Refusals);
                        }

                        // A validated transaction reaches the execution boundary. Without a
                        // registered handler the T14 behaviour is preserved: no write is attempted.
                        if (_options.TransactionHandler is not { } handler)
                        {
                            var executionRefusal = BrokerRefusal.Of(
                                BrokerRefusalCode.ExecutionNotImplemented,
                                "This broker validates the transaction but has no privileged execution path; no write was attempted.");
                            await TrySendAsync(connection.Stream, BrokerMessages.TransactionResult(new BrokerTransactionResultMessage
                            {
                                TransactionId = decision.TransactionId,
                                PlanDigest = decision.PlanDigest,
                                Accepted = true,
                                Executed = false,
                                RequiresNewPreview = false,
                                Codes = new[] { executionRefusal.Code },
                            }), cancellationToken).ConfigureAwait(false);
                            audit.Record(
                                BrokerAuditEventKind.ExecutionUnavailable,
                                new[] { executionRefusal.Code },
                                decision.TransactionId);
                            return Result(BrokerStopReason.Refused, new[] { executionRefusal });
                        }

                        execution = await handler.ExecuteAsync(new BrokerTransactionExecutionRequest
                        {
                            TransactionId = decision.TransactionId,
                            Plan = submit.Plan,
                            Authorization = submit.Authorization,
                            UserSid = _options.InitiatingUserSid,
                            LogonSessionId = _options.LogonSessionId,
                            NowUtc = _options.Clock.GetUtcNow(),
                        }, cancellationToken).ConfigureAwait(false);

                        var executionCodes = execution.Refusals.Select(refusal => refusal.Code).ToArray();
                        await TrySendAsync(connection.Stream, BrokerMessages.TransactionResult(new BrokerTransactionResultMessage
                        {
                            TransactionId = decision.TransactionId,
                            PlanDigest = decision.PlanDigest,
                            Accepted = true,
                            Executed = execution.Executed,
                            RequiresNewPreview = false,
                            Codes = executionCodes,
                        }), cancellationToken).ConfigureAwait(false);
                        audit.Record(
                            execution.Executed
                                ? BrokerAuditEventKind.TransactionExecuted
                                : execution.Refusals.Any(refusal => refusal.Code is BrokerRefusalCode.JournalUnavailable
                                    or BrokerRefusalCode.JournalCorrupt
                                    or BrokerRefusalCode.ExecutionRecoveryRequired
                                    or BrokerRefusalCode.ExecutionRollbackConflict
                                    or BrokerRefusalCode.ExecutionRollbackFailed)
                                    ? BrokerAuditEventKind.TransactionRolledBack
                                    : BrokerAuditEventKind.ExecutionUnavailable,
                            executionCodes,
                            decision.TransactionId,
                            peerProcessId,
                            submit.Plan.Actions.Count);
                        return Result(
                            execution.Executed ? BrokerStopReason.Completed : BrokerStopReason.Refused,
                            execution.Refusals);
                    }

                    case BrokerMessageKind.Cancel:
                    {
                        var cancel = BrokerMessages.ReadCancel(frame);
                        audit.Record(BrokerAuditEventKind.CancelRequested, transactionId: cancel.TransactionId);
                        var refusal = BrokerRefusal.Of(BrokerRefusalCode.Cancelled, "The peer cancelled the transaction.");
                        await TrySendAsync(connection.Stream, BrokerMessages.TransactionResult(new BrokerTransactionResultMessage
                        {
                            TransactionId = boundTransactionId ?? cancel.TransactionId,
                            PlanDigest = boundPlanDigest ?? string.Empty,
                            Accepted = false,
                            Executed = false,
                            RequiresNewPreview = false,
                            Codes = new[] { BrokerRefusalCode.Cancelled },
                        }), cancellationToken).ConfigureAwait(false);
                        return Result(BrokerStopReason.Cancelled, new[] { refusal });
                    }

                    case BrokerMessageKind.Close:
                    {
                        audit.Record(BrokerAuditEventKind.CloseRequested);
                        var refusal = BrokerRefusal.Of(BrokerRefusalCode.SessionClosed, "The peer closed the session.");
                        await TrySendAsync(connection.Stream, BrokerMessages.TransactionResult(new BrokerTransactionResultMessage
                        {
                            TransactionId = boundTransactionId ?? string.Empty,
                            PlanDigest = boundPlanDigest ?? string.Empty,
                            Accepted = false,
                            Executed = false,
                            RequiresNewPreview = false,
                            Codes = new[] { BrokerRefusalCode.SessionClosed },
                        }), cancellationToken).ConfigureAwait(false);
                        return Result(BrokerStopReason.Closed, new[] { refusal });
                    }

                    default:
                        throw new BrokerProtocolException(
                            BrokerRefusalCode.UnexpectedMessage,
                            "The frame kind is not valid after the handshake.");
                }
            }
        }
        catch (BrokerSessionTimeoutException timeout)
        {
            await TrySendAsync(connection.Stream, BrokerMessages.Error(timeout.Code, "The bounded session window expired."), cancellationToken)
                .ConfigureAwait(false);
            audit.Record(
                timeout.Code switch
                {
                    BrokerRefusalCode.HandshakeTimeout => BrokerAuditEventKind.HandshakeTimeout,
                    BrokerRefusalCode.IdleTimeout => BrokerAuditEventKind.IdleTimeout,
                    _ => BrokerAuditEventKind.BudgetExhausted,
                },
                new[] { timeout.Code },
                peerProcessId: peerProcessId);
            return Result(timeout.Code switch
            {
                BrokerRefusalCode.HandshakeTimeout => BrokerStopReason.HandshakeTimeout,
                BrokerRefusalCode.IdleTimeout => BrokerStopReason.IdleTimeout,
                _ => BrokerStopReason.TotalBudgetExhausted,
            }, new[] { BrokerRefusal.Of(timeout.Code, "The bounded session window expired before the protocol completed.") });
        }
        catch (BrokerProtocolException protocol)
        {
            await TrySendAsync(connection.Stream, BrokerMessages.Error(protocol.Code, "The protocol request was rejected."), cancellationToken)
                .ConfigureAwait(false);
            audit.Record(BrokerAuditEventKind.ProtocolError, new[] { protocol.Code }, peerProcessId: peerProcessId);
            return Result(BrokerStopReason.ProtocolError, new[] { BrokerRefusal.Of(protocol.Code, "The protocol request was rejected.") });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            audit.Record(BrokerAuditEventKind.Disconnected, new[] { BrokerRefusalCode.Cancelled }, peerProcessId: peerProcessId);
            return Result(BrokerStopReason.Cancelled, new[] { BrokerRefusal.Of(BrokerRefusalCode.Cancelled, "The host cancelled the session.") });
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        {
            audit.Record(BrokerAuditEventKind.Disconnected, new[] { BrokerRefusalCode.Disconnected }, peerProcessId: peerProcessId);
            return Result(BrokerStopReason.Disconnected, new[]
            {
                BrokerRefusal.Of(BrokerRefusalCode.Disconnected, "The peer disconnected before the session completed."),
                // A disconnect stops new actions; compensation is journal work that does not exist yet.
                BrokerRefusal.Of(
                    BrokerRefusalCode.TransactionJournalNotImplemented,
                    "Compensation after a disconnect needs the authoritative journal, which this delivery does not have."),
            });
        }
    }

    private async Task<BrokerPreflightSnapshot> CollectPreflightAsync(
        NetScope.Repair.RepairPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            return await _options.Preflight.CollectAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return BrokerPreflightSnapshot.Unavailable("broker_preflight_error");
        }
    }

    private async Task<BrokerFrame> ReadFrameAsync(
        Stream stream, BrokerSessionBudget budget, BrokerSessionPhase phase, CancellationToken cancellationToken)
    {
        var window = budget.NextReadTimeout(phase);
        using var delay = new CancellationTokenSource(window <= TimeSpan.Zero ? TimeSpan.Zero : window, _options.Clock);
        if (window <= TimeSpan.Zero) delay.Cancel();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delay.Token);

        try
        {
            return await BrokerFrameCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerSessionTimeoutException(MapTimeout(budget, phase));
        }
    }

    private static BrokerRefusalCode MapTimeout(BrokerSessionBudget budget, BrokerSessionPhase phase)
    {
        if (phase == BrokerSessionPhase.Handshake && budget.CheckHandshake() != BrokerBudgetStatus.Ok)
            return BrokerRefusalCode.HandshakeTimeout;
        if (budget.CheckTotal() != BrokerBudgetStatus.Ok) return BrokerRefusalCode.TotalBudgetExhausted;
        if (budget.CheckIdle() != BrokerBudgetStatus.Ok) return BrokerRefusalCode.IdleTimeout;
        return phase == BrokerSessionPhase.Handshake
            ? BrokerRefusalCode.HandshakeTimeout
            : BrokerRefusalCode.IdleTimeout;
    }

    private static async Task WriteFrameAsync(Stream stream, BrokerFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            await BrokerFrameCodec.WriteAsync(stream, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new IOException("The peer is no longer connected.", exception);
        }
    }

    private static async Task TrySendAsync(Stream stream, BrokerFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            await BrokerFrameCodec.WriteAsync(stream, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer is gone; the outcome is still recorded locally.
        }
    }

    private sealed class BrokerSessionTimeoutException : Exception
    {
        public BrokerSessionTimeoutException(BrokerRefusalCode code) : base("The bounded session window expired.")
            => Code = code;

        public BrokerRefusalCode Code { get; }
    }
}





