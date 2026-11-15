using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NetScope.Broker;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>
/// Real Windows integration for the local IPC boundary: ACL content, random pipe names, kernel
/// peer facts and Authenticode/deployment inspection on this development tree. It runs with normal
/// user rights, creates only loopback named pipes, never elevates and never writes the network.
/// </summary>
public sealed class BrokerWindowsTests
{
    [Fact]
    public void The_pipe_acl_is_limited_to_the_user_system_and_administrators()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = WindowsNamedPipeBrokerListener.NewPipeSecurity();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        var user = identity.User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var anonymous = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);

        Assert.Contains(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Equals(user));
        Assert.Contains(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Equals(system));
        Assert.Contains(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Equals(administrators));
        Assert.DoesNotContain(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Equals(everyone));
        Assert.DoesNotContain(rules, rule => rule.IdentityReference is SecurityIdentifier sid && sid.Equals(anonymous));
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
        // Least privilege: only the three explicit principals above.
        Assert.Equal(3, rules.Length);
    }

    [Fact]
    public void Pipe_names_carry_fresh_random_material()
    {
        var first = WindowsNamedPipeBrokerListener.NewRandomPipeName();
        var second = WindowsNamedPipeBrokerListener.NewRandomPipeName();

        Assert.NotEqual(first, second);
        Assert.True(WindowsNamedPipeBrokerListener.IsRandomPipeName(first));
        Assert.False(WindowsNamedPipeBrokerListener.IsRandomPipeName("NetScopeRepair.Broker.fixed-name"));
        Assert.False(WindowsNamedPipeBrokerListener.IsRandomPipeName("NetScopeRepair.Broker." + new string('g', 32)));
        Assert.False(WindowsNamedPipeBrokerListener.IsRandomPipeName("OtherServer." + new string('a', 32)));
    }

    [Fact]
    public async Task A_real_loopback_pipe_collects_kernel_reported_peer_facts()
    {
        await using var listener = WindowsNamedPipeBrokerListener.Create(new[] { Environment.SystemDirectory });
        Assert.True(WindowsNamedPipeBrokerListener.IsRandomPipeName(listener.PipeName));
        Assert.True(listener.FirstPipeInstance);

        using var clientPipe = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var acceptTask = listener.AcceptAsync(CancellationToken.None);
        await clientPipe.ConnectAsync(5000);
        var connection = await acceptTask;
        try
        {
            var evidence = connection.PeerEvidence;
            Assert.False(evidence.RemoteClient);
            Assert.Equal(Environment.ProcessId, evidence.PeerProcessId);
            Assert.NotNull(evidence.PeerProcessCreationUtc);
            Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, evidence.PeerUserSid);
            Assert.NotNull(evidence.PeerLogonSessionId);
            Assert.True(evidence.FirstPipeInstance);
            Assert.True(evidence.PipeNameRandom);
            Assert.True(connection.Lease.IsHeld);
            Assert.True(evidence.ProcessHandleHeld);
            // This development tree is not a signed/trusted deployment.
            Assert.NotEqual(BrokerSignatureState.ValidTrustedPublisher, evidence.PeerSignature);

            // The same kernel facts applied by the identity policy are refused on this machine.
            var decision = BrokerPeerIdentityPolicy.Evaluate(evidence with
            {
                ClaimedProcessId = evidence.PeerProcessId,
                ClaimedProcessCreationUtc = evidence.PeerProcessCreationUtc,
            });
            Assert.False(decision.Allowed);
            Assert.NotEmpty(decision.Refusals);
        }
        finally
        {
            await connection.DisposeAsync();
        }

        Assert.False(connection.Lease.IsHeld);
    }

    [Fact]
    public async Task A_real_pipe_session_refuses_an_unsigned_development_peer()
    {
        await using var listener = WindowsNamedPipeBrokerListener.Create(new[] { Environment.SystemDirectory });
        using var clientPipe = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var acceptTask = listener.AcceptAsync(CancellationToken.None);
        await clientPipe.ConnectAsync(5000);
        var connection = await acceptTask;

        var clock = new FakeBrokerTimeProvider();
        var audit = new BrokerAuditTrail(clock);
        var options = BrokerPlanFixtures.SessionOptions(clock: clock, audit: audit);
        var session = new BrokerSession(options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sessionTask = session.RunAsync(connection, cancellation.Token);
        var client = new ScriptedBrokerClient(clientPipe);

        var creation = connection.PeerEvidence.PeerProcessCreationUtc
            ?? Process.GetCurrentProcess().StartTime.ToUniversalTime();
        try
        {
            await client.SendHelloAsync(Environment.ProcessId, creation);
            await client.ReadFrameAsync();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or EndOfStreamException)
        {
            // The broker may close the pipe immediately after the refusal.
        }

        var result = await sessionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BrokerStopReason.Refused, result.StopReason);
        Assert.False(result.IdentityVerified);
        Assert.False(result.NonceConsumed);
        Assert.Contains(result.Refusals, refusal => refusal.Code is
            BrokerRefusalCode.PeerPathUntrusted
            or BrokerRefusalCode.PeerSignatureUntrusted
            or BrokerRefusalCode.PeerParentUntrusted
            or BrokerRefusalCode.PeerIdentityUnknown
            or BrokerRefusalCode.PeerCreationTimeMismatch);
        Assert.Contains(audit.Entries, entry => entry.Kind == BrokerAuditEventKind.PeerRejected);
    }

    [Fact]
    public void The_real_development_tree_is_not_a_trusted_deployment()
    {
        var facts = new WindowsBrokerDeploymentVerifier().Inspect(developmentIsolation: false);

        Assert.False(string.IsNullOrWhiteSpace(facts.BrokerExecutablePath));
        Assert.False(string.IsNullOrWhiteSpace(facts.TrustedInstallDirectory));
        Assert.NotEqual(BrokerSignatureState.ValidTrustedPublisher, facts.Signature);

        var decision = BrokerTrustPolicy.DeliveryDefault.Evaluate(facts);
        Assert.False(decision.RealElevationEnabled);
        Assert.False(decision.DevelopmentIsolationAllowed);
        Assert.False(decision.CanServe);
    }

    [Fact]
    public void An_unsigned_test_binary_is_never_reported_as_a_trusted_publisher()
    {
        var location = typeof(BrokerWindowsTests).Assembly.Location;
        var verification = new BrokerAuthenticodeVerifier().Inspect(location);

        Assert.NotEqual(BrokerSignatureState.ValidTrustedPublisher, verification.State);
        Assert.Contains(
            verification.State,
            new[]
            {
                BrokerSignatureState.Unsigned,
                BrokerSignatureState.InvalidSignature,
                BrokerSignatureState.Unreadable,
                BrokerSignatureState.ValidUntrustedPublisher,
            });
    }

    [Fact]
    public async Task A_real_verifier_cannot_start_the_delivery_host()
    {
        var clock = new FakeBrokerTimeProvider();
        var host = new BrokerHost(new BrokerHostOptions
        {
            TrustPolicy = BrokerTrustPolicy.DeliveryDefault,
            Deployment = new WindowsBrokerDeploymentVerifier(),
            Capabilities = PlatformCapabilityMatrix.UnverifiedDefault,
            Preflight = new BrokerPlanFixtures.FixedPreflightSource(BrokerPreflightSnapshot.Unavailable("test")),
            NonceSource = new FixedBrokerNonceSource(),
            ListenerFactory = _ => throw new InvalidOperationException("a refused host must not create a pipe"),
            InitiatingUserSid = BrokerPlanFixtures.UserSid,
            LogonSessionId = BrokerPlanFixtures.LogonSession,
            BrokerSessionId = BrokerPlanFixtures.SessionId,
            Clock = clock,
            Audit = new BrokerAuditTrail(clock),
        });

        var result = await host.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(result.Trust.CanServe);
        Assert.NotEmpty(result.Refusals);
    }
}
