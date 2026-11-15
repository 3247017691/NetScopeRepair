using System.Net;
using NetScope.Application;
using NetScope.Domain;
using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>
/// T04-R2 pre-fix reproducers. They use only the transport API that existed before the repair and
/// both failed on the old implementation:
/// 1. the transport never published the addresses that really took part in the connection, so
///    diagnostic rules saw an empty ResolvedAddresses list for every probe;
/// 2. any address-family constraint was answered with "address_family_not_enforced" (Unsupported)
///    instead of being enforced on the real socket.
/// Kept as evidence after the repair; they now pass.
/// </summary>
public sealed class T04R2PrefixReproducerTests
{
    [Fact]
    public async Task Local_transport_did_not_publish_the_addresses_that_really_answered()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        using var transport = new HttpProbeTransport();
        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("localhost"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
        }, CancellationToken.None);

        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(204, outcome.HttpStatus);
        Assert.NotEmpty(outcome.ResolvedAddresses); // failed before T04-R2: the list was always empty
        Assert.Contains(outcome.ResolvedAddresses, address => IPAddress.TryParse(address, out var parsed)
            && IPAddress.IsLoopback(parsed));
    }

    [Fact]
    public async Task Address_family_constraint_was_reported_unsupported_instead_of_enforced()
    {
        await using var server = LoopbackHttpServer.StartIPv4();
        using var transport = new HttpProbeTransport();
        var outcome = await transport.SendAsync(new ProbeRequest
        {
            Endpoint = server.UrlFor("localhost"),
            Method = "GET",
            Path = ProbePath.NoExplicitProxy,
            AddressFamily = "ipv4",
        }, CancellationToken.None);

        // Failed before T04-R2: Unsupported / address_family_not_enforced and no request was sent.
        Assert.Equal(EventStatus.Ok, outcome.Status);
        Assert.Equal(204, outcome.HttpStatus);
        Assert.True(server.RequestCount >= 1);
    }
}
