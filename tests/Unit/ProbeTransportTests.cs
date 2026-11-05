using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>All handlers are injected. These tests never open external or local sockets.</summary>
public sealed class ProbeTransportTests
{
    [Fact]
    public async Task Request_endpoint_selects_the_explicit_proxy_without_constructor_configuration()
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = Transport(selected);
        var result = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(EventStatus.Ok, result.Status);
        Assert.Equal(204, result.HttpStatus);
        var options = Assert.Single(selected);
        Assert.Equal(ProbePath.ExplicitHttpProxy, options.Path);
        Assert.Equal(new Uri("http://127.0.0.1:8100"), options.ExplicitProxyEndpoint);
        Assert.Equal("proxy", result.ResolutionLocation);
        Assert.True(result.ProxyIsLocal);
    }

    [Fact]
    public async Task Different_proxy_endpoints_have_distinct_pools_and_equal_endpoints_reuse_their_pool()
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = Transport(selected);
        await transport.SendAsync(Request() with { ExplicitProxyEndpoint = "http://proxy-a.invalid:80/" }, CancellationToken.None);
        await transport.SendAsync(Request() with { ExplicitProxyEndpoint = "HTTP://PROXY-A.INVALID/" }, CancellationToken.None);
        await transport.SendAsync(Request() with { ExplicitProxyEndpoint = "http://proxy-b.invalid:8100" }, CancellationToken.None);

        Assert.Equal(2, selected.Count);
        Assert.Equal(new Uri("http://proxy-a.invalid/"), selected[0].ExplicitProxyEndpoint);
        Assert.Equal(new Uri("http://proxy-b.invalid:8100"), selected[1].ExplicitProxyEndpoint);
    }

    [Fact]
    public async Task Every_path_has_its_own_handler_and_only_explicit_path_receives_a_proxy_endpoint()
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = Transport(selected);
        foreach (var path in Enum.GetValues<ProbePath>())
            await transport.SendAsync(Request() with { Path = path }, CancellationToken.None);

        Assert.Equal(3, selected.Count);
        Assert.All(selected, options => Assert.Equal(
            options.Path == ProbePath.ExplicitHttpProxy ? new Uri("http://127.0.0.1:8100") : null,
            options.ExplicitProxyEndpoint));
    }

    [Fact]
    public async Task Constructor_constraint_mismatch_is_rejected_before_handler_creation()
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = new HttpProbeTransport(new Uri("http://127.0.0.1:8200"), handlerFactory: options =>
        {
            selected.Add(options);
            return new MemoryHandler();
        });
        var result = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(EventStatus.Unsupported, result.Status);
        Assert.Equal("explicit_proxy_endpoint_mismatch", result.TransportError);
        Assert.Empty(selected);
        Assert.Null(result.HttpStatus);
        Assert.Equal("unknown", result.FailedEndpoint);
    }

    [Fact]
    public async Task Constructor_proxy_does_not_replace_a_missing_request_endpoint()
    {
        var calls = 0;
        using var transport = new HttpProbeTransport(new Uri("http://127.0.0.1:8100"), handlerFactory: _ =>
        {
            calls++;
            return new MemoryHandler();
        });
        var result = await transport.SendAsync(Request() with { ExplicitProxyEndpoint = null }, CancellationToken.None);

        Assert.Equal("explicit_proxy_not_configured", result.TransportError);
        Assert.Equal(EventStatus.Unsupported, result.Status);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("socks5://127.0.0.1:8100")]
    [InlineData("http://name@127.0.0.1:8100")]
    [InlineData("http://127.0.0.1:8100/private/path")]
    [InlineData("http://127.0.0.1:8100/?token=secret")]
    [InlineData("http://127.0.0.1:8100/#secret")]
    public async Task Invalid_proxy_authorities_do_not_create_a_handler(string endpoint)
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = Transport(selected);
        var result = await transport.SendAsync(Request() with { ExplicitProxyEndpoint = endpoint }, CancellationToken.None);

        Assert.Empty(selected);
        Assert.Equal("invalid_explicit_proxy_endpoint", result.TransportError);
        Assert.Equal(EventStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task Family_selection_remains_an_explicit_gap_without_creating_a_handler()
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = Transport(selected);
        var result = await transport.SendAsync(Request() with { AddressFamily = "ipv6" }, CancellationToken.None);

        Assert.Equal(EventStatus.Unsupported, result.Status);
        Assert.Equal("address_family_not_enforced", result.TransportError);
        Assert.Equal("unknown", result.FailedEndpoint);
        Assert.Empty(selected);
    }

    [Fact]
    public async Task System_default_success_keeps_actual_proxy_selection_and_resolution_unknown()
    {
        var selected = new List<ProbeHandlerOptions>();
        using var transport = Transport(selected);
        var result = await transport.SendAsync(Request() with { Path = ProbePath.SystemDefault }, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, result.Status);
        Assert.Equal("unknown", result.ResolutionLocation);
        Assert.Null(result.ProxyIsLocal);
        Assert.Null(Assert.Single(selected).ExplicitProxyEndpoint);
        Assert.Contains(".NET DefaultProxy", result.Notice);
        Assert.Contains("environment", result.Notice);
        Assert.Contains("bypass", result.Notice);
        Assert.Contains("PAC", result.Notice);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8100", true)]
    [InlineData("http://[::1]:8100", true)]
    [InlineData("http://localhost:8100", true)]
    [InlineData("http://192.0.2.10:8100", false)]
    [InlineData("http://proxy.invalid:8100", null)]
    public async Task Proxy_locality_uses_literal_identity_without_name_resolution(string endpoint, bool? local)
    {
        using var transport = new HttpProbeTransport(handlerFactory: _ => new MemoryHandler());
        var result = await transport.SendAsync(Request() with { ExplicitProxyEndpoint = endpoint }, CancellationToken.None);

        Assert.Equal(local, result.ProxyIsLocal);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, EventStatus.Failed)]
    [InlineData(SocketError.TimedOut, EventStatus.Timeout)]
    [InlineData(SocketError.AccessDenied, EventStatus.AccessDenied)]
    public async Task A_socket_error_does_not_prove_the_proxy_connection_leg_failed(SocketError error, EventStatus status)
    {
        using var transport = new HttpProbeTransport(handlerFactory: _ => new MemoryHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError,
                "synthetic", new SocketException((int)error)))));
        var result = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal("unknown", result.FailedEndpoint);
        Assert.True(result.ProxyIsLocal);
        Assert.Null(result.HttpStatus);
        if (status == EventStatus.AccessDenied)
            Assert.Equal(ProbeClassification.Inconclusive, ProbeClassificationPolicy.Classify(result, ProbePlans.Default().Target));
    }

    [Fact]
    public async Task Tls_failure_through_proxy_does_not_prove_a_dead_proxy()
    {
        using var transport = new HttpProbeTransport(handlerFactory: _ => new MemoryHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic", new AuthenticationException()))));
        var result = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(EventStatus.Failed, result.Status);
        Assert.Equal("tls", result.FailedStage);
        Assert.Equal("unknown", result.FailedEndpoint);
        Assert.True(result.ProxyIsLocal);
    }

    [Fact]
    public async Task Connect_tunnel_failure_does_not_prove_the_proxy_listener_is_unavailable()
    {
        using var transport = new HttpProbeTransport(handlerFactory: _ => new MemoryHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ProxyTunnelError, "synthetic"))));
        var result = await transport.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("proxy_connect", result.FailedStage);
        Assert.Equal("unknown", result.FailedEndpoint);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_the_in_memory_handler()
    {
        using var source = new CancellationTokenSource();
        CancellationToken received = default;
        using var transport = new HttpProbeTransport(handlerFactory: _ => new MemoryHandler(async (_, token) =>
        {
            received = token;
            source.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(Request(), source.Token));
        Assert.True(received.IsCancellationRequested);
    }

    private static ProbeRequest Request() => new()
    {
        Endpoint = "https://target.invalid/health",
        Method = "GET",
        Path = ProbePath.ExplicitHttpProxy,
        ExplicitProxyEndpoint = "http://127.0.0.1:8100",
    };

    private static HttpProbeTransport Transport(List<ProbeHandlerOptions> selected) => new(handlerFactory: options =>
    {
        selected.Add(options);
        return new MemoryHandler();
    });

    private sealed class MemoryHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _send;
        public MemoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _send?.Invoke(request, cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
    }
}
