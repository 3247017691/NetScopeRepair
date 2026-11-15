using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetScope.Unit.Tests;

/// <summary>
/// Minimal loopback CONNECT proxy used by the R1-B F07 counterexample. It binds 127.0.0.1 only,
/// reads the CONNECT request line, answers 407 and closes. It never forwards, never resolves the
/// requested host and never contacts an external network.
/// </summary>
internal sealed class LoopbackConnect407Proxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _connectRequests;

    private LoopbackConnect407Proxy(TcpListener listener)
    {
        _listener = listener;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public string ProxyUrl => "http://127.0.0.1:" + Port;

    public int ConnectRequests => Volatile.Read(ref _connectRequests);

    public string ProxyAddress => "127.0.0.1";

    public static LoopbackConnect407Proxy Start() =>
        new(new TcpListener(IPAddress.Loopback, 0));

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
            var buffer = new byte[4096];
            var pending = new StringBuilder();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read <= 0) return;
                    pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    var text = pending.ToString();
                    if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
                    Interlocked.Increment(ref _connectRequests);
                    // Always answer 407: authentication is required before any tunnel exists.
                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 407 Proxy Authentication Required\r\n"
                        + "Proxy-Authenticate: Basic realm=\"loopback-407\"\r\n"
                        + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
            {
                // The transport closed the connection after reading the 407; that is expected.
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
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}
