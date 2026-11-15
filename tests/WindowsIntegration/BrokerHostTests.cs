using NetScope.Broker;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Host-level trust gate: the delivery policy must refuse to create a listener at all, a fake UAC
/// denial must stop a would-be trusted broker, and only explicit development isolation may start a
/// one-shot protocol host (which still has no privileged write).
/// </summary>
public sealed class BrokerHostTests
{
    private static BrokerHostOptions Options(
        BrokerTrustPolicy policy,
        BrokerDeploymentFacts facts,
        Func<BrokerDeploymentFacts, IBrokerListener>? listenerFactory = null,
        IBrokerElevationProbe? elevation = null,
        bool developmentIsolation = false,
        BrokerAuditTrail? audit = null,
        PlatformCapabilityMatrix? capabilities = null)
    {
        var clock = new FakeBrokerTimeProvider();
        return new BrokerHostOptions
        {
            TrustPolicy = policy,
            Deployment = new FakeDeploymentVerifier(facts),
            Capabilities = capabilities ?? PlatformCapabilityMatrix.UnverifiedDefault,
            Preflight = new BrokerPlanFixtures.FixedPreflightSource(BrokerPreflightSnapshot.Unavailable("test")),
            NonceSource = new FixedBrokerNonceSource(),
            ListenerFactory = listenerFactory ?? (_ => throw new InvalidOperationException("no listener may be created")),
            InitiatingUserSid = BrokerPlanFixtures.UserSid,
            LogonSessionId = BrokerPlanFixtures.LogonSession,
            BrokerSessionId = BrokerPlanFixtures.SessionId,
            Clock = clock,
            Audit = audit ?? new BrokerAuditTrail(clock),
            Elevation = elevation,
            DevelopmentIsolation = developmentIsolation,
        };
    }

    [Fact]
    public async Task The_delivery_policy_refuses_to_start_and_creates_no_listener()
    {
        var created = false;
        var audit = new BrokerAuditTrail(new FakeBrokerTimeProvider());
        var host = new BrokerHost(Options(
            BrokerTrustPolicy.DeliveryDefault,
            BrokerPlanFixtures.TrustedDeployment(),
            listenerFactory: _ =>
            {
                created = true;
                return new FakeBrokerListener(_ => throw new InvalidOperationException());
            },
            audit: audit));

        var result = await host.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(created);
        Assert.False(result.Trust.RealElevationEnabled);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.SignatureNotTrusted);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.RealElevationDisabled);
        Assert.Contains(audit.Entries, entry => entry.Kind == BrokerAuditEventKind.HostRefused);
    }

    [Fact]
    public async Task A_fake_uac_denial_stops_a_would_be_trusted_broker()
    {
        var created = false;
        var elevation = new FakeElevationProbe(BrokerElevationState.NotElevated);
        var host = new BrokerHost(Options(
            BrokerTrustPolicy.ForSyntheticTests(new[] { BrokerPlanFixtures.TrustedThumbprint }),
            BrokerPlanFixtures.TrustedDeployment(),
            listenerFactory: _ =>
            {
                created = true;
                return new FakeBrokerListener(_ => throw new InvalidOperationException());
            },
            elevation: elevation));

        var result = await host.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(created);
        Assert.Equal(1, elevation.ProbeCount);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.BrokerNotElevated);
        Assert.True(result.Trust.RealElevationEnabled);
    }

    [Fact]
    public async Task A_listener_creation_failure_is_reported()
    {
        var host = new BrokerHost(Options(
            BrokerTrustPolicy.ForSyntheticTests(new[] { BrokerPlanFixtures.TrustedThumbprint }),
            BrokerPlanFixtures.TrustedDeployment(),
            listenerFactory: _ => throw new IOException("synthetic pipe failure"),
            elevation: new FakeElevationProbe(BrokerElevationState.Elevated)));

        var result = await host.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.TrustedDeploymentUnavailable);
    }

    [Fact]
    public async Task Development_isolation_runs_one_session_and_releases_the_peer()
    {
        var (clientStream, serverStream) = BrokerDuplex.Create();
        await clientStream.DisposeAsync();
        var connection = new FakeBrokerConnection(serverStream, BrokerPlanFixtures.TrustedPeer());
        var listener = new FakeBrokerListener(_ => Task.FromResult<IBrokerConnection>(connection));
        var audit = new BrokerAuditTrail(new FakeBrokerTimeProvider());

        var result = await new BrokerHost(Options(
            BrokerTrustPolicy.ForSyntheticTests(),
            BrokerPlanFixtures.DevelopmentDeployment(),
            listenerFactory: _ => listener,
            developmentIsolation: true,
            audit: audit)).RunOnceAsync(CancellationToken.None);

        Assert.True(result.Started);
        Assert.NotNull(result.Session);
        Assert.False(result.Trust.RealElevationEnabled);
        Assert.True(result.Trust.DevelopmentIsolationAllowed);
        Assert.Equal(BrokerStopReason.Disconnected, result.Session!.StopReason);
        Assert.Equal(1, listener.AcceptCount);
        Assert.False(connection.FakeLease.IsHeld);
        Assert.True(connection.FakeLease.DisposeCount >= 1);
        Assert.Contains(audit.Entries, entry => entry.Kind == BrokerAuditEventKind.ListenerStarted);
    }

    [Fact]
    public async Task A_cancelled_accept_is_reported_as_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var listener = new FakeBrokerListener(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var host = new BrokerHost(Options(
            BrokerTrustPolicy.ForSyntheticTests(),
            BrokerPlanFixtures.DevelopmentDeployment(),
            listenerFactory: _ => listener,
            developmentIsolation: true));

        var run = host.RunOnceAsync(cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Started);
        Assert.Contains(result.Refusals, refusal => refusal.Code == BrokerRefusalCode.Cancelled);
    }

    [Fact]
    public async Task Development_isolation_does_not_enable_a_privileged_transaction()
    {
        var (clientStream, serverStream) = BrokerDuplex.Create();
        await clientStream.DisposeAsync();
        var connection = new FakeBrokerConnection(serverStream, BrokerPlanFixtures.TrustedPeer());
        var listener = new FakeBrokerListener(_ => Task.FromResult<IBrokerConnection>(connection));

        var result = await new BrokerHost(Options(
            BrokerTrustPolicy.ForSyntheticTests(),
            BrokerPlanFixtures.DevelopmentDeployment(),
            listenerFactory: _ => listener,
            developmentIsolation: true,
            capabilities: BrokerPlanFixtures.SyntheticMatrix())).RunOnceAsync(CancellationToken.None);

        Assert.True(result.Started);
        Assert.False(result.Trust.RealElevationEnabled);
        // The trust gate starts a protocol session, never a write: a session that ends without a
        // transaction still reports a stop reason and no accepted execution.
        Assert.NotNull(result.Session);
        Assert.NotEqual(BrokerStopReason.Closed, result.Session!.StopReason);
        Assert.False(result.Session.Decision?.Accepted ?? false);
    }
}
