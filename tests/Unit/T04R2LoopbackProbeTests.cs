using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>
/// T04-R2 controlled loopback integration. A fake resolver decides the addresses; the real
/// transport still opens real TCP sockets to a local in-process HTTP server, so these tests prove
/// actual socket/family selection and connection reuse on 127.0.0.1 / ::1 only. No external request
/// is made and no system proxy or certificate setting is touched.
/// </summary>
public sealed class T04R2LoopbackProbeTests
{
    [Fact]
    public async Task Direct_probe_publishes_the_resolution_that_fed_the_real_connection()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        var resolver = FakeNameResolver.Returning("127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(204, outcome.HttpStatus);
        Assert.Equal("local", outcome.ResolutionLocation);
        Assert.Equal(new[] { "127.0.0.1" }, outcome.ResolvedAddresses);
        Assert.Equal("test-resolver", outcome.ResolutionSource);
        Assert.NotNull(outcome.ResolvedAt);
        Assert.NotNull(outcome.Connection);
        Assert.Equal("target", outcome.Connection!.EndpointRole);
        Assert.Equal("127.0.0.1", outcome.Connection.RemoteAddress);
        Assert.Equal(server.Port, outcome.Connection.RemotePort);
        Assert.False(outcome.Connection.Reused);
        Assert.Equal(1, server.RequestCount);
        Assert.Equal("target.test", resolver.LastCall.Host);
        Assert.Equal(ProbeAddressFamily.Unspecified, resolver.LastCall.Family);
    }

    [Fact]
    public async Task Ipv4_constraint_is_enforced_on_the_real_socket()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        // The resolver deliberately returns both families: only the transport filter decides.
        var resolver = FakeNameResolver.IgnoringFamily("::1", "127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
            AddressFamily = "ipv4",
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(204, outcome.HttpStatus);
        Assert.Equal(new[] { "127.0.0.1" }, outcome.ResolvedAddresses);
        Assert.Equal("127.0.0.1", outcome.Connection!.RemoteAddress);
        Assert.Equal(ProbeAddressFamily.IPv4, resolver.LastCall.Family);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Ipv6_constraint_is_enforced_on_the_real_socket()
    {
        await using var server = LoopbackHttpServer.StartIPv6();
        var resolver = FakeNameResolver.IgnoringFamily("::1", "127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
            AddressFamily = "ipv6",
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(204, outcome.HttpStatus);
        Assert.Equal(new[] { "::1" }, outcome.ResolvedAddresses);
        Assert.Equal("::1", outcome.Connection!.RemoteAddress);
        Assert.Equal(ProbeAddressFamily.IPv6, resolver.LastCall.Family);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_failed_constrained_family_never_turns_into_a_success()
    {
        // An IPv4 server answers, but the plan asked for IPv6: the reachable IPv4 socket must not
        // be used and the result must stay a failure instead of an IPv6 success.
        await using var server = LoopbackHttpServer.StartIPv4();
        var resolver = FakeNameResolver.IgnoringFamily("::1", "127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
            AddressFamily = "ipv6",
        }, CancellationToken.None);

        Assert.NotEqual(EventStatus.Ok, outcome.Status);
        Assert.Null(outcome.HttpStatus);
        Assert.Equal("connect", outcome.FailedStage);
        Assert.Equal("target", outcome.FailedEndpoint);
        Assert.Equal(new[] { "::1" }, outcome.ResolvedAddresses);
        Assert.NotNull(outcome.Connection);
        Assert.Null(outcome.Connection!.RemoteAddress);
        Assert.False(outcome.Connection.Reused);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Reused_pooled_connection_is_not_a_fresh_resolution()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        var resolver = FakeNameResolver.Returning("127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var request = new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
        };

        var first = await transport.SendAsync(request, CancellationToken.None);
        var second = await transport.SendAsync(request, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, first.Status);
        Assert.False(first.Connection!.Reused);
        Assert.Equal(1, resolver.CallCount);

        Assert.Equal(EventStatus.Ok, second.Status);
        Assert.Equal(204, second.HttpStatus);
        Assert.Empty(second.ResolvedAddresses);
        Assert.Null(second.ResolutionSource);
        Assert.Null(second.ResolvedAt);
        Assert.NotNull(second.Connection);
        Assert.True(second.Connection!.Reused);
        Assert.Equal("local", second.ResolutionLocation);
        Assert.Contains("reused a pooled connection", second.Notice);
        // No new name resolution happened for the reused attempt.
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Proxy_path_records_the_proxy_leg_without_claiming_target_resolution()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        var resolver = FakeNameResolver.Returning("127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.ExplicitHttpProxy,
            ExplicitProxyEndpoint = $"http://127.0.0.1:{server.Port}",
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(204, outcome.HttpStatus);
        Assert.Equal("proxy", outcome.ResolutionLocation);
        Assert.Empty(outcome.ResolvedAddresses);
        Assert.Null(outcome.ResolutionSource);
        Assert.Null(outcome.ResolvedAt);
        Assert.True(outcome.ProxyIsLocal);
        Assert.NotNull(outcome.Connection);
        Assert.Equal("proxy", outcome.Connection!.EndpointRole);
        Assert.Equal("127.0.0.1", outcome.Connection.RemoteAddress);
        Assert.Equal(server.Port, outcome.Connection.RemotePort);
        // The local resolution was for the proxy host; the target was never resolved here.
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal("127.0.0.1", resolver.LastCall.Host);
        Assert.DoesNotContain(resolver.Calls, call => call.Host.Contains("target.test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_during_resolution_is_forwarded_and_never_becomes_a_success()
    {
        var resolver = FakeNameResolver.Hanging();
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var request = new ProbeRequest
        {
            Endpoint = "http://target.test:1/health",
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
        };

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync(request, cancelled.Token));
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task System_default_attempt_never_claims_a_local_target_resolution()
    {
        // The system default stack may dial the target or a proxy from the environment/PAC. The
        // attempt may create a real connection to whatever that stack chose, but the target's
        // resolution must stay unknown either way.
        await using var server = LoopbackHttpServer.StartIPv4();
        var resolver = FakeNameResolver.Returning("127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("target.test"),
            Method = "GET",
            Path = ProbePath.SystemDefault,
        }, CancellationToken.None);

        Assert.Equal("unknown", outcome.ResolutionLocation);
        Assert.Empty(outcome.ResolvedAddresses);
        Assert.Null(outcome.ResolutionSource);
        Assert.Null(outcome.ResolvedAt);
        if (outcome.Connection is not null) Assert.Equal("unknown", outcome.Connection.EndpointRole);
        Assert.DoesNotContain("target.test", outcome.Notice ?? string.Empty);
    }

    [Fact]
    public async Task Group_budget_exhaustion_is_cancelled_and_leaves_no_claim()
    {
        var resolver = FakeNameResolver.Hanging();
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var plan = new ProbeGroupPlan
        {
            ProbeGroupId = "group-t04-budget",
            Target = new ProbeTarget { TargetId = "target-t04", Endpoint = "http://target.test:1/health" },
            Paths = new[] { ProbePath.NoExplicitProxy },
            AddressFamily = "ipv4",
            ObservationWindowId = "window-t04",
            BootId = "boot-t04",
            PerProbeTimeout = TimeSpan.FromSeconds(5),
            GroupBudget = TimeSpan.FromMilliseconds(150),
            StartedAt = new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.FromHours(8)),
        };

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        var item = Assert.Single(result.Results);
        Assert.True(result.BudgetExhausted);
        Assert.Equal(EventStatus.Cancelled, item.Status);
        Assert.Equal("group_budget_exhausted", item.TransportError);
        Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
        Assert.False(item.TimingComparable);
        Assert.Empty(item.ResolvedAddresses);
        Assert.Null(item.ResolutionSource);
        Assert.Null(item.ResolvedAt);
        Assert.Null(item.Connection);
    }

    [Fact]
    public async Task Certificate_validation_is_still_enforced_on_a_real_tls_socket()
    {
        using var certificate = CreateSelfSignedCertificate();
        await using var server = new TlsLoopbackServer(certificate);
        var resolver = FakeNameResolver.Returning("127.0.0.1");
        using var transport = new HttpProbeTransport(nameResolver: resolver);

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = $"https://127.0.0.1:{server.Port}/health",
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
        }, CancellationToken.None);

        // The transport still observes the real connection, but an untrusted certificate must keep
        // failing as a TLS stage instead of being silently accepted.
        Assert.Equal(EventStatus.Failed, outcome.Status);
        Assert.Equal("tls_failure", outcome.TransportError);
        Assert.Equal("tls", outcome.FailedStage);
        Assert.Null(outcome.HttpStatus);
        Assert.NotNull(outcome.Connection);
        Assert.Equal("target", outcome.Connection!.EndpointRole);
        Assert.Equal("127.0.0.1", outcome.Connection.RemoteAddress);
        Assert.Equal(server.Port, outcome.Connection.RemotePort);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=loopback.invalid",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        // Re-import so the private key is usable by SslStream on every platform.
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
    }

    /// <summary>Loopback TLS server with a self-signed certificate; it never leaves the machine.</summary>
    private sealed class TlsLoopbackServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;
        private readonly Task _acceptLoop;
        private int _handshakes;

        public TlsLoopbackServer(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptAsync();
        }

        public int Port { get; }

        public int FailedHandshakes => Volatile.Read(ref _handshakes);

        private async Task AcceptAsync()
        {
            var token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                    return;
                }

                _ = HandshakeAsync(client, token);
            }
        }

        private async Task HandshakeAsync(System.Net.Sockets.TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    var stream = new System.Net.Security.SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await stream.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                    }, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException)
                {
                    // The client rejects the self-signed certificate; that is exactly what is proved.
                    Interlocked.Increment(ref _handshakes);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already stopped.
            }

            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The accept loop only stops through cancellation or listener disposal.
            }

            _stop.Dispose();
        }
    }

    [Fact]
    public async Task Cancelled_group_result_stays_cancelled_inconclusive_and_fact_free()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        var resolver = FakeNameResolver.Hanging();
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var plan = new ProbeGroupPlan
        {
            ProbeGroupId = "group-t04-cancel",
            Target = new ProbeTarget { TargetId = "target-t04", Endpoint = server.UrlFor("target.test") },
            Paths = new[] { ProbePath.NoExplicitProxy },
            AddressFamily = "ipv4",
            ObservationWindowId = "window-t04",
            BootId = "boot-t04",
            PerProbeTimeout = TimeSpan.FromSeconds(5),
            GroupBudget = TimeSpan.FromSeconds(5),
            StartedAt = new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.FromHours(8)),
        };

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, cancelled.Token);

        var item = Assert.Single(result.Results);
        Assert.Equal(EventStatus.Cancelled, item.Status);
        Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
        Assert.False(item.IsSuccess);
        Assert.False(item.TimingComparable);
        Assert.Empty(item.ResolvedAddresses);
        Assert.Null(item.ResolutionSource);
        Assert.Null(item.ResolvedAt);
        Assert.Null(item.Connection);
        Assert.Equal(0, server.RequestCount);
    }
}
