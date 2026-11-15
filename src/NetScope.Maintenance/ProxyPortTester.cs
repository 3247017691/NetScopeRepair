using System.Net;
using System.Net.Http;

namespace NetScope.Maintenance;

/// <summary>
/// A port is a working proxy only if a real request through it returns 200/204 from
/// gstatic generate_204 (same bar as the verified desktop script). Listening alone is not enough.
/// </summary>
public static class ProxyPortTester
{
    public static async Task<bool> IsWorkingAsync(int port, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        var ownsHandler = handler is null;
        handler ??= new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + port), UseProxy = true };
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("https://www.gstatic.com/generate_204", cancellationToken).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ownsHandler) handler.Dispose();
        }
    }
}