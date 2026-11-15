using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>
/// T04-R2: the runner must publish the transport's measured resolution and connection facts
/// instead of flattening them into an empty address list, and must keep every unmeasured part
/// unknown. All transports here are fakes; no socket is opened.
/// </summary>
public sealed class T04R2ProbeObservationTests
{
    private static readonly DateTimeOffset ResolvedAt =
        new(2026, 11, 5, 11, 0, 2, TimeSpan.FromHours(8));

    [Fact]
    public async Task Runner_publishes_measured_resolution_and_connection_facts()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204) with
        {
            ResolutionLocation = "local",
            ResolvedAddresses = new[] { "198.18.0.11" },
            ResolutionSource = "system-dns",
            ResolvedAt = ResolvedAt,
            Connection = new ProbeConnectionFact
            {
                EndpointRole = "target",
                RemoteAddress = "198.18.0.11",
                RemotePort = 443,
                Reused = false,
            },
        }));

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);
        var direct = result.Results.Single(item => item.Path == ProbePath.NoExplicitProxy);

        Assert.Equal("local", direct.ResolutionLocation);
        Assert.Equal(new[] { "198.18.0.11" }, direct.ResolvedAddresses);
        Assert.Equal("system-dns", direct.ResolutionSource);
        Assert.Equal(ResolvedAt, direct.ResolvedAt);
        Assert.NotNull(direct.Connection);
        Assert.Equal("target", direct.Connection!.EndpointRole);
        Assert.Equal("198.18.0.11", direct.Connection.RemoteAddress);
        Assert.Equal(443, direct.Connection.RemotePort);
        Assert.False(direct.Connection.Reused);
    }

    [Fact]
    public async Task Runner_keeps_unmeasured_resolution_and_connection_unknown()
    {
        // A transport that only reported a status observed nothing about DNS or the socket.
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204)));

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.All(result.Results, item =>
        {
            Assert.Empty(item.ResolvedAddresses);
            Assert.Null(item.ResolutionSource);
            Assert.Null(item.ResolvedAt);
            Assert.Null(item.Connection);
            Assert.Equal("unknown", item.ResolutionLocation);
        });
    }

    [Fact]
    public async Task Runner_keeps_the_reuse_notice_and_the_path_caveat_together()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(TransportOutcome.Reachable(204) with
        {
            Notice = "The transport reused a pooled connection for this attempt.",
        }));

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);
        var direct = result.Results.Single(item => item.Path == ProbePath.NoExplicitProxy);
        var system = result.Results.Single(item => item.Path == ProbePath.SystemDefault);

        Assert.Contains("reused a pooled connection", direct.Notice);
        Assert.Contains("physical-direct", direct.Notice);
        // The system-default transport notice already states the whole caveat; it is not doubled.
        Assert.Equal("The transport reused a pooled connection for this attempt.", system.Notice);
    }

    [Fact]
    public async Task Runner_drops_no_claim_when_the_transport_reports_a_cancel_gap()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(
            TransportOutcome.Failure(EventStatus.Cancelled, "probe_cancelled") with
            {
                ResolvedAddresses = new[] { "198.18.0.11" },
                ResolutionSource = "system-dns",
            }));

        var result = await new HttpProbeRunner(transport, new FakeClock()).RunAsync(ProbePlans.Default(), CancellationToken.None);

        Assert.All(result.Results, item =>
        {
            Assert.Equal(EventStatus.Cancelled, item.Status);
            // A cancelled sample is not a completed resolution: the runner still carries the fact,
            // but the classification stays inconclusive and never becomes success.
            Assert.Equal(ProbeClassification.Inconclusive, item.Classification);
            Assert.False(item.IsSuccess);
        });
    }

    [Fact]
    public async Task Failed_resolution_is_a_local_resolve_gap_with_no_request()
    {
        var resolver = FakeNameResolver.Failing();
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://unresolvable.test/health",
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Failed, outcome.Status);
        Assert.Equal("name_resolution_failed", outcome.TransportError);
        Assert.Equal("resolve", outcome.FailedStage);
        Assert.Equal("target", outcome.FailedEndpoint);
        Assert.Equal("local", outcome.ResolutionLocation);
        Assert.Empty(outcome.ResolvedAddresses);
        Assert.Equal("test-resolver", outcome.ResolutionSource);
        Assert.NotNull(outcome.ResolvedAt);
        Assert.Null(outcome.Connection);
        Assert.Contains("name resolution failed", outcome.Notice);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal("unresolvable.test", resolver.LastCall.Host);
    }

    [Fact]
    public async Task No_address_of_the_requested_family_is_an_explicit_resolve_gap()
    {
        var resolver = FakeNameResolver.Returning("192.0.2.10");
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://v4-only.test/health",
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
            AddressFamily = "ipv6",
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Failed, outcome.Status);
        Assert.Equal("no_address_for_requested_family", outcome.TransportError);
        Assert.Equal("resolve", outcome.FailedStage);
        Assert.Equal("target", outcome.FailedEndpoint);
        Assert.Empty(outcome.ResolvedAddresses);
        Assert.Null(outcome.Connection);
        Assert.Contains("no address of the requested family", outcome.Notice);
    }

    [Fact]
    public async Task Invalid_family_is_rejected_before_any_resolution()
    {
        var resolver = FakeNameResolver.Returning("192.0.2.10");
        using var transport = new HttpProbeTransport(nameResolver: resolver);
        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://target.test/health",
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
            AddressFamily = "carrier-pigeon",
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Unsupported, outcome.Status);
        Assert.Equal("unsupported_address_family", outcome.TransportError);
        Assert.Equal(0, resolver.CallCount);
        Assert.Empty(outcome.ResolvedAddresses);
        Assert.Null(outcome.Connection);
    }

    [Fact]
    public async Task Transport_does_not_infer_connection_facts_from_status_alone()
    {
        var calls = 0;
        using var transport = new HttpProbeTransport(handlerFactory: _ =>
        {
            calls++;
            return new NoContentHandler();
        });
        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = "https://target.test/health",
            Method = "GET",
            // R1-B: the proxy path keeps target family unknown; a custom in-memory handler has no
            // connect seam, so no connection/resolution fact may be invented either.
            Path = ProbePath.ExplicitHttpProxy,
            ExplicitProxyEndpoint = "http://127.0.0.1:8100",
            AddressFamily = "ipv4",
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal("ipv4", outcome.RequestedAddressFamily);
        Assert.Null(outcome.TargetAddressFamily);
        Assert.Equal(ProbeTargetFamilyStatus.UnknownThroughProxy, outcome.TargetAddressFamilyStatus);
        Assert.Empty(outcome.ResolvedAddresses);
        Assert.Null(outcome.ResolutionSource);
        Assert.Null(outcome.Connection);
    }

    private sealed class NoContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
    }
}

