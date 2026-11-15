using NetScope.Repair;
namespace NetScope.Broker;

public sealed record BrokerHostOptions
{
    public required BrokerTrustPolicy TrustPolicy { get; init; }

    public required IBrokerDeploymentVerifier Deployment { get; init; }

    public required PlatformCapabilityMatrix Capabilities { get; init; }

    public required IBrokerPreflightSource Preflight { get; init; }

    public required IBrokerNonceSource NonceSource { get; init; }

    /// <summary>Creates the listener only after the trust gate allowed a session.</summary>
    public required Func<BrokerDeploymentFacts, IBrokerListener> ListenerFactory { get; init; }

    public required string InitiatingUserSid { get; init; }

    public required string LogonSessionId { get; init; }

    public required int BrokerSessionId { get; init; }

    public required TimeProvider Clock { get; init; }

    public required BrokerAuditTrail Audit { get; init; }

    public IBrokerElevationProbe? Elevation { get; init; }

    /// <summary>
    /// Only tests set this. It allows the protocol host to run from an unsigned, user-writable
    /// development tree; it never enables real elevation or a privileged write.
    /// </summary>
    public bool DevelopmentIsolation { get; init; }

    /// <summary>Optional startup journal scan; it reports unresolved transactions and never executes.</summary>
    public RepairBrokerRecoveryStartup? RecoveryStartup { get; init; }

    /// <summary>Optional transaction handler owned by the journal/coordinator layer.</summary>
    public IBrokerTransactionHandler? TransactionHandler { get; init; }
}

public sealed record BrokerHostResult
{
    public required bool Started { get; init; }

    public required BrokerTrustDecision Trust { get; init; }

    public required IReadOnlyList<BrokerRefusal> Refusals { get; init; }

    public BrokerSessionResult? Session { get; init; }

    /// <summary>Startup recovery preview; present when a journal scanner is configured.</summary>
    public BrokerRecoveryStartupScan? Recovery { get; init; }

    public static BrokerHostResult NotStarted(
        BrokerTrustDecision trust, IEnumerable<BrokerRefusal> refusals, BrokerRecoveryStartupScan? recovery = null) => new()
    {
        Started = false,
        Trust = trust,
        Refusals = refusals.DistinctBy(refusal => refusal.Code).ToArray(),
        Recovery = recovery,
    };
}

/// <summary>
/// One-shot broker host. It evaluates the deployment trust gate before creating any listener, so a
/// signed/trusted deployment that is not satisfied means no pipe, no session and no privileged
/// capability. It exits after one transaction, a refusal or a bounded timeout.
/// </summary>
public sealed class BrokerHost
{
    private readonly BrokerHostOptions _options;

    public BrokerHost(BrokerHostOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<BrokerHostResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var facts = _options.Deployment.Inspect(_options.DevelopmentIsolation);
        var trust = _options.TrustPolicy.Evaluate(facts);
        var recovery = _options.RecoveryStartup?.Scan(trust);
        _options.Audit.Record(
            trust.CanServe ? BrokerAuditEventKind.IdentityVerified : BrokerAuditEventKind.HostRefused,
            trust.Refusals.Select(refusal => refusal.Code));

        if (!trust.CanServe)
            return BrokerHostResult.NotStarted(trust, trust.Refusals, recovery);

        if (trust.RealElevationEnabled && _options.Elevation is { } elevation
            && elevation.Probe() != BrokerElevationState.Elevated)
        {
            var refusal = BrokerRefusal.Of(
                BrokerRefusalCode.BrokerNotElevated,
                "The broker did not obtain an elevated token; no privileged transaction may start.");
            _options.Audit.Record(BrokerAuditEventKind.HostRefused, new[] { refusal.Code });
            return BrokerHostResult.NotStarted(trust, new[] { refusal }, recovery);
        }

        IBrokerListener listener;
        try
        {
            listener = _options.ListenerFactory(facts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var refusal = BrokerRefusal.Of(
                BrokerRefusalCode.TrustedDeploymentUnavailable,
                "The broker could not create its protected local listener.");
            _options.Audit.Record(BrokerAuditEventKind.HostRefused, new[] { refusal.Code });
            return BrokerHostResult.NotStarted(trust, new[] { refusal }, recovery);
        }

        if (listener is null)
        {
            var refusal = BrokerRefusal.Of(
                BrokerRefusalCode.TrustedDeploymentUnavailable,
                "The listener factory returned no local listener.");
            _options.Audit.Record(BrokerAuditEventKind.HostRefused, new[] { refusal.Code });
            return BrokerHostResult.NotStarted(trust, new[] { refusal }, recovery);
        }

        await using (listener.ConfigureAwait(false))
        {
            _options.Audit.Record(BrokerAuditEventKind.ListenerStarted);

            IBrokerConnection connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var refusal = BrokerRefusal.Of(BrokerRefusalCode.Cancelled, "The host cancelled before a peer connected.");
                return BrokerHostResult.NotStarted(trust, new[] { refusal }, recovery);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                var refusal = BrokerRefusal.Of(
                    BrokerRefusalCode.TrustedDeploymentUnavailable,
                    "The protected listener failed before a peer connected.");
                _options.Audit.Record(BrokerAuditEventKind.HostRefused, new[] { refusal.Code });
                return BrokerHostResult.NotStarted(trust, new[] { refusal }, recovery);
            }

            var session = new BrokerSession(new BrokerSessionOptions
            {
                Trust = trust,
                Capabilities = _options.Capabilities,
                Preflight = _options.Preflight,
                NonceSource = _options.NonceSource,
                InitiatingUserSid = _options.InitiatingUserSid,
                LogonSessionId = _options.LogonSessionId,
                BrokerSessionId = _options.BrokerSessionId,
                Clock = _options.Clock,
                Audit = _options.Audit,
                TransactionHandler = _options.TransactionHandler,
            });

            var result = await session.RunAsync(connection, cancellationToken).ConfigureAwait(false);
            return new BrokerHostResult
            {
                Started = true,
                Trust = trust,
                Refusals = result.Refusals,
                Session = result,
                Recovery = recovery,
            };
        }
    }
}



