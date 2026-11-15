using System.Net;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>
/// R1-B F06/F07 factory regressions. These are written against the contract the fix must provide;
/// before the fix they fail (null/any policy incomparable, explicit family becomes a whole-path
/// gap, CONNECT 407 is lost). Every test uses an in-memory handler or a 127.0.0.1-only fake proxy;
/// none resolves or contacts an external host.
/// </summary>
public sealed class R1BProbeContractTests
{
    [Fact]
    public async Task A_null_family_plan_is_a_valid_no_constraint_policy_and_stays_comparable()
    {
        var plan = ProbePlans.Default() with { AddressFamily = null };
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.True(result.TimingComparable);
        Assert.All(result.Results, probe => Assert.True(probe.TimingComparable));
    }

    [Fact]
    public async Task An_any_family_plan_is_a_valid_no_constraint_policy_and_stays_comparable()
    {
        var plan = ProbePlans.Default() with { AddressFamily = "any" };
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        Assert.True(result.TimingComparable);
    }

    [Fact]
    public async Task Explicit_family_on_the_explicit_proxy_path_still_sends_a_real_request()
    {
        var calls = 0;
        using var transport = new HttpProbeTransport(handlerFactory: _ =>
        {
            calls++;
            return new NoContentHandler();
        });

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://target.invalid/health",
            Method = "GET",
            Path = ProbePath.ExplicitHttpProxy,
            ExplicitProxyEndpoint = "http://127.0.0.1:8100",
            AddressFamily = "ipv4",
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Null(outcome.FailedStage);
        // A custom in-memory handler has no connect seam, so no connection fact may be invented.
        Assert.Null(outcome.Connection);
    }

    [Fact]
    public async Task Explicit_family_on_the_system_default_path_still_sends_a_real_request()
    {
        var calls = 0;
        using var transport = new HttpProbeTransport(handlerFactory: _ =>
        {
            calls++;
            return new NoContentHandler();
        });

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://target.invalid/health",
            Method = "GET",
            Path = ProbePath.SystemDefault,
            AddressFamily = "ipv4",
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(EventStatus.Ok, outcome.Status);
    }

    [Fact]
    public async Task A_real_loopback_CONNECT_407_keeps_the_http_status_and_proxy_attribution()
    {
        await using var proxy = LoopbackConnect407Proxy.Start();
        using var transport = new HttpProbeTransport(connectTimeout: TimeSpan.FromSeconds(5));

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://never-resolve.invalid/health",
            Method = "GET",
            Path = ProbePath.ExplicitHttpProxy,
            ExplicitProxyEndpoint = proxy.ProxyUrl,
        }, CancellationToken.None);

        Assert.Equal(1, proxy.ConnectRequests);
        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(407, outcome.HttpStatus);
        Assert.Equal("proxy_authentication_required", outcome.TransportError);
        Assert.Equal("proxy_connect", outcome.FailedStage);
        Assert.Equal("proxy", outcome.FailedEndpoint);
        Assert.True(outcome.ProxyIsLocal);
        Assert.Equal("proxy", outcome.Connection?.EndpointRole);
        Assert.Equal(proxy.ProxyAddress, outcome.Connection?.RemoteAddress);
        Assert.Equal("any", outcome.RequestedAddressFamily);
        Assert.Equal("ipv4", outcome.LocalConnectionAddressFamily);
        Assert.Null(outcome.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.UnknownThroughProxy, outcome.TargetAddressFamilyStatus);
        Assert.Equal(
            ProbeClassification.AuthenticationRequired,
            ProbeClassificationPolicy.Classify(outcome, new ProbeTarget
            {
                TargetId = "target-r1b",
                Endpoint = "https://never-resolve.invalid/health",
            }));
    }

    [Fact]
    public async Task Explicit_ipv6_target_policy_does_not_filter_the_ipv4_proxy_leg()
    {
        await using var proxy = LoopbackConnect407Proxy.Start();
        using var transport = new HttpProbeTransport(connectTimeout: TimeSpan.FromSeconds(5));

        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://never-resolve.invalid/health",
            Method = "GET",
            Path = ProbePath.ExplicitHttpProxy,
            ExplicitProxyEndpoint = proxy.ProxyUrl,
            AddressFamily = "ipv6",
        }, CancellationToken.None);

        Assert.Equal(1, proxy.ConnectRequests);
        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(407, outcome.HttpStatus);
        Assert.Equal("ipv6", outcome.RequestedAddressFamily);
        Assert.Equal("ipv4", outcome.LocalConnectionAddressFamily); // the real proxy-leg socket
        Assert.Null(outcome.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.UnknownThroughProxy, outcome.TargetAddressFamilyStatus);
    }

    [Fact]
    public async Task Runner_keeps_a_real_CONNECT_407_as_an_authentication_observation()
    {
        await using var proxy = LoopbackConnect407Proxy.Start();
        using var transport = new HttpProbeTransport(connectTimeout: TimeSpan.FromSeconds(5));
        var plan = ProbePlans.Default() with
        {
            Paths = new[] { ProbePath.ExplicitHttpProxy },
            ExplicitProxyEndpoint = proxy.ProxyUrl,
            AddressFamily = null,
        };

        var group = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(plan, CancellationToken.None);

        var probe = Assert.Single(group.Results);
        Assert.Equal(EventStatus.Ok, probe.Status);
        Assert.Equal(407, probe.HttpStatus);
        Assert.Equal(ProbeClassification.AuthenticationRequired, probe.Classification);
        Assert.Equal("proxy", probe.FailedEndpoint);
        Assert.True(probe.ProxyIsLocal);
        Assert.Equal("any", probe.RequestedAddressFamily);
        Assert.Equal("ipv4", probe.LocalConnectionAddressFamily);
        Assert.Null(probe.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.UnknownThroughProxy, probe.TargetAddressFamilyStatus);
        Assert.True(group.TimingComparable);
        Assert.True(probe.TimingComparable);
    }

    private sealed class NoContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
    }
}




