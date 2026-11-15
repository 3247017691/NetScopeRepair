using System.Net.Http;
using System.Net.Sockets;

namespace NetScope.Maintenance;

/// <summary>Real post-repair measurement: fresh snapshot + DNS + direct HTTP + proxy HTTP.</summary>
public sealed class WindowsPostCheckProbe : IPostCheckProbe
{
    private readonly IMaintenanceProbe _probe;

    public WindowsPostCheckProbe(IMaintenanceProbe? probe = null)
        => _probe = probe ?? new WindowsMaintenanceProbe();

    public async Task<PostCheckObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _probe.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var dnsOk = await DnsResolvesAsync(cancellationToken).ConfigureAwait(false);
        var directOk = await DirectHttpAsync(cancellationToken).ConfigureAwait(false);
        var proxyAlive = snapshot.WorkingProxyPort is not null;
        var proxyOk = proxyAlive
            && await ProxyPortTester.IsWorkingAsync(snapshot.WorkingProxyPort!.Value, null, cancellationToken).ConfigureAwait(false);
        return new PostCheckObservation(
            snapshot.BestRouteIndex,
            snapshot.Interfaces.Any(item => item.Connectivity == "Internet"),
            dnsOk, directOk, proxyOk, proxyAlive);
    }

    private static async Task<bool> DnsResolvesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync("www.baidu.com", cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> DirectHttpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("http://www.baidu.com/", cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode > 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}
