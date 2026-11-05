using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Probes;

/// <summary>Selected handler configuration, exposed so transport tests need no sockets.</summary>
public sealed record ProbeHandlerOptions(ProbePath Path, Uri? ExplicitProxyEndpoint, TimeSpan ConnectTimeout);

/// <summary>
/// HTTP transport whose request selects the proxy endpoint. Clients and pools are isolated by
/// path and normalized proxy endpoint. The optional factory can supply an entirely in-memory handler.
/// </summary>
public sealed class HttpProbeTransport : IProbeTransport, IDisposable
{
    private readonly Uri? _expectedProxy;
    private readonly TimeSpan _connectTimeout;
    private readonly Func<ProbeHandlerOptions, HttpMessageHandler> _handlerFactory;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <param name="explicitProxyEndpoint">
    /// Optional compatibility constraint. Requests must still provide their endpoint; a mismatch
    /// is rejected before creating a handler, rather than silently using this endpoint.
    /// </param>
    public HttpProbeTransport(Uri? explicitProxyEndpoint = null, TimeSpan? connectTimeout = null,
        Func<ProbeHandlerOptions, HttpMessageHandler>? handlerFactory = null)
    {
        if (explicitProxyEndpoint is not null
            && (!explicitProxyEndpoint.IsAbsoluteUri
                || !ProbeEndpointPolicy.TryProxy(explicitProxyEndpoint.AbsoluteUri, out _)))
            throw new ArgumentException("A proxy must be an HTTP(S) authority without credentials or URL data.", nameof(explicitProxyEndpoint));
        _expectedProxy = explicitProxyEndpoint;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(8);
        if (_connectTimeout <= TimeSpan.Zero || _connectTimeout.TotalMilliseconds > uint.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        _handlerFactory = handlerFactory ?? CreateHandler;
    }

    public async Task<TransportOutcome> SendAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Path is not (ProbePath.SystemDefault or ProbePath.NoExplicitProxy or ProbePath.ExplicitHttpProxy))
            return Gap("unknown_path");
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(target.UserInfo)
            || string.IsNullOrWhiteSpace(request.Method) || request.MaxResponseBytes < 0)
            return Gap("invalid_request_options");

        HttpMethod method;
        try { method = new HttpMethod(request.Method); }
        catch (FormatException) { return Gap("invalid_request_options"); }

        if (!string.IsNullOrWhiteSpace(request.AddressFamily))
        {
            return Gap("address_family_not_enforced", "resolve",
                "Address family selection is not enforced by this transport; no request was sent.");
        }

        Uri? proxy = null;
        if (request.Path == ProbePath.ExplicitHttpProxy)
        {
            if (string.IsNullOrWhiteSpace(request.ExplicitProxyEndpoint))
                return Gap("explicit_proxy_not_configured", "proxy_connect");
            if (!ProbeEndpointPolicy.TryProxy(request.ExplicitProxyEndpoint, out proxy))
                return Gap("invalid_explicit_proxy_endpoint", "proxy_connect");
            if (_expectedProxy is not null && _expectedProxy.AbsoluteUri != proxy!.AbsoluteUri)
                return Gap("explicit_proxy_endpoint_mismatch", "proxy_connect",
                    "The request proxy differs from the transport constraint; no request was sent.");
        }

        var proxyIsLocal = request.Path == ProbePath.ExplicitHttpProxy
            ? ProbeEndpointPolicy.IsLocal(request.ExplicitProxyEndpoint) : null;
        var client = ClientFor(request.Path, proxy);
        using var message = new HttpRequestMessage(method, target);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            await DrainAsync(response, request.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            return new TransportOutcome
            {
                Status = EventStatus.Ok,
                HttpStatus = (int)response.StatusCode,
                ResolutionLocation = request.Path switch
                {
                    ProbePath.NoExplicitProxy => "local",
                    ProbePath.ExplicitHttpProxy when target.HostNameType == UriHostNameType.Dns => "proxy",
                    _ => "unknown",
                },
                ProxyIsLocal = proxyIsLocal,
                Notice = request.Path == ProbePath.SystemDefault
                    ? ".NET DefaultProxy may use process environment or platform settings and bypass rules. Actual proxy choice and DNS resolution are unknown; PAC/auto-detection and browser behaviour are not verified."
                    : null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return Classify(exception, request) with { ProxyIsLocal = proxyIsLocal };
        }
        catch (UnauthorizedAccessException)
        {
            return TransportOutcome.Failure(EventStatus.AccessDenied, "access_denied")
                with { FailedEndpoint = "unknown", ProxyIsLocal = proxyIsLocal };
        }
        catch (IOException exception)
        {
            return TransportOutcome.Failure(EventStatus.Failed, "io_error", "http")
                with { Notice = exception.GetType().Name, FailedEndpoint = "unknown", ProxyIsLocal = proxyIsLocal };
        }
    }

    private HttpClient ClientFor(ProbePath path, Uri? proxy)
    {
        var key = $"{(int)path}|{proxy?.AbsoluteUri}";
        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(key, out var client)) return client;
            var handler = _handlerFactory(new ProbeHandlerOptions(path, proxy, _connectTimeout));
            client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            _clients.Add(key, client);
            return client;
        }
    }

    private static HttpMessageHandler CreateHandler(ProbeHandlerOptions options) => new SocketsHttpHandler
    {
        // Null Proxy delegates to .NET DefaultProxy, which may include process environment.
        UseProxy = options.Path != ProbePath.NoExplicitProxy,
        Proxy = options.Path == ProbePath.ExplicitHttpProxy ? new WebProxy(options.ExplicitProxyEndpoint!) : null,
        ConnectTimeout = options.ConnectTimeout,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
    };

    public void Dispose()
    {
        lock (_clientLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }

    private static TransportOutcome Gap(string error, string stage = "unknown", string? notice = null) => new()
    {
        Status = EventStatus.Unsupported,
        TransportError = error,
        FailedStage = stage,
        FailedEndpoint = "unknown",
        ResolutionLocation = "unknown",
        Notice = notice ?? "No request was sent. Reason: " + error,
    };

    private static async Task DrainAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0) return;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Min(maxBytes, 4096)];
        var remaining = maxBytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0) return;
            remaining -= read;
        }
    }

    private static TransportOutcome Classify(HttpRequestException exception, ProbeRequest request)
    {
        var socket = FindInner<SocketException>(exception);
        if (socket is not null)
        {
            var stage = socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? "resolve" : "connect";
            return new TransportOutcome
            {
                Status = socket.SocketErrorCode switch
                {
                    SocketError.AccessDenied => EventStatus.AccessDenied,
                    SocketError.TimedOut => EventStatus.Timeout,
                    SocketError.OperationAborted or SocketError.Interrupted => EventStatus.Cancelled,
                    _ => EventStatus.Failed,
                },
                TransportError = socket.SocketErrorCode.ToString(),
                OsErrorCode = (int)socket.SocketErrorCode,
                FailedStage = stage,
                ResolutionLocation = request.Path == ProbePath.NoExplicitProxy ? "local" : "unknown",
                // HttpRequestException does not identify which connection leg produced a socket
                // error. A proxy may report an upstream failure after CONNECT was established.
                FailedEndpoint = request.Path == ProbePath.NoExplicitProxy ? "target" : "unknown",
            };
        }

        var tls = FindInner<AuthenticationException>(exception) is not null;
        return new TransportOutcome
        {
            Status = EventStatus.Failed,
            TransportError = tls ? "tls_failure" : exception.HttpRequestError.ToString(),
            FailedStage = tls ? "tls" : exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "resolve",
                HttpRequestError.ProxyTunnelError => "proxy_connect",
                HttpRequestError.SecureConnectionError => "tls",
                _ => "http",
            },
            FailedEndpoint = request.Path == ProbePath.NoExplicitProxy ? "target" : "unknown",
            ResolutionLocation = "unknown",
        };
    }

    private static TException? FindInner<TException>(Exception exception) where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TException match) return match;
        return null;
    }
}
