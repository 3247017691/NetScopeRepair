using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetScope.Unit.Tests;

/// <summary>
/// Minimal in-process HTTP/1.1 server bound to a loopback address. It is used to prove what the
/// transport really does at the socket level. It never leaves the machine and never contacts a
/// real network, so tests stay offline and deterministic.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _requests;

    private LoopbackHttpServer(TcpListener listener, IPAddress address)
    {
        _listener = listener;
        Address = address;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public IPAddress Address { get; }

    public int Port { get; }

    /// <summary>Number of fully received HTTP requests.</summary>
    public int RequestCount => Volatile.Read(ref _requests);

    public string Url => $"http://{HostFor(Address)}:{Port}/health";

    /// <summary>Plain-HTTP URL for the same server on a caller-supplied host name.</summary>
    public string UrlFor(string host) => $"http://{host}:{Port}/health";

    public static LoopbackHttpServer StartIPv4() => new(new TcpListener(IPAddress.Loopback, 0), IPAddress.Loopback);

    public static LoopbackHttpServer StartIPv6() =>
        new(new TcpListener(IPAddress.IPv6Loopback, 0), IPAddress.IPv6Loopback);

    private static string HostFor(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private async Task AcceptAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var pending = new StringBuilder();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read <= 0) return;
                    pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    while (true)
                    {
                        var text = pending.ToString();
                        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (end < 0) break;
                        pending.Remove(0, end + 4);
                        Interlocked.Increment(ref _requests);
                        var response = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
                        await stream.WriteAsync(response, token).ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
            {
                // The client closed the connection or the server is shutting down; nothing to do.
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
