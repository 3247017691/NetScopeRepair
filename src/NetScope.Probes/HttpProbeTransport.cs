using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Probes;

/// <summary>Selected handler configuration, exposed so transport tests need no sockets.</summary>
public sealed record ProbeHandlerOptions(ProbePath Path, Uri? ExplicitProxyEndpoint, TimeSpan ConnectTimeout)
{
    /// <summary>Address family the request asked this transport to enforce; Unspecified means none.</summary>
    public ProbeAddressFamily AddressFamily { get; init; }
}

/// <summary>
/// HTTP transport whose request selects the proxy endpoint. Clients and pools are isolated by
/// path, normalized proxy endpoint and enforced address family.
///
/// The default handler hands the TCP connection to a connect seam, so the transport can publish
/// what it really observed: the local resolution that fed the connection, the address the socket
/// actually reached, whether a pooled connection was reused and which leg failed. Nothing is
/// inferred from a name or an address family label alone: what was not measured stays unknown.
/// The optional factory can still supply an entirely in-memory handler.
/// </summary>
public sealed class HttpProbeTransport : IProbeTransport, IDisposable
{
    private const string SystemDefaultNotice =
        ".NET DefaultProxy may use process environment or platform settings and bypass rules. Actual proxy choice and DNS resolution are unknown; PAC/auto-detection and browser behaviour are not verified.";

    private const string ReuseNotice =
        "The transport reused a pooled connection for this attempt; no new name resolution or TCP connection was observed, so no fresh address sample is claimed.";

    private readonly Uri? _expectedProxy;
    private readonly TimeSpan _connectTimeout;
    private readonly Func<ProbeHandlerOptions, HttpMessageHandler> _handlerFactory;
    private readonly IProbeNameResolver _nameResolver;
    private readonly TimeProvider _timeProvider;
    private readonly bool _observesConnections;
    private readonly AsyncLocal<ProbeAttemptObservation?> _currentAttempt = new();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _clientLock = new();
    private long _observedConnectAttempts;
    private bool _disposed;

    /// <param name="explicitProxyEndpoint">
    /// Optional compatibility constraint. Requests must still provide their endpoint; a mismatch
    /// is rejected before creating a handler, rather than silently using this endpoint.
    /// </param>
    /// <param name="nameResolver">
    /// Local resolution boundary used by the default connect seam. Inject a fake to test the
    /// observation path without touching the operating system resolver.
    /// </param>
    public HttpProbeTransport(
        Uri? explicitProxyEndpoint = null,
        TimeSpan? connectTimeout = null,
        Func<ProbeHandlerOptions, HttpMessageHandler>? handlerFactory = null,
        IProbeNameResolver? nameResolver = null,
        TimeProvider? timeProvider = null)
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
        _observesConnections = handlerFactory is null;
        _nameResolver = nameResolver ?? new SystemProbeNameResolver();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TransportOutcome> SendAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Path is not (ProbePath.SystemDefault or ProbePath.NoExplicitProxy or ProbePath.ExplicitHttpProxy))
            return Gap(request, "unknown_path");
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(target.UserInfo)
            || string.IsNullOrWhiteSpace(request.Method) || request.MaxResponseBytes < 0)
            return Gap(request, "invalid_request_options");

        HttpMethod method;
        try { method = new HttpMethod(request.Method); }
        catch (FormatException) { return Gap(request, "invalid_request_options"); }

        if (!ProbeAddressFamilyPolicy.TryParse(request.AddressFamily, out var family))
            return Gap(request, "unsupported_address_family", "resolve",
                "The requested address family is not one this transport can enforce; no request was sent.");

        Uri? proxy = null;
        if (request.Path == ProbePath.ExplicitHttpProxy)
        {
            if (string.IsNullOrWhiteSpace(request.ExplicitProxyEndpoint))
                return Gap(request, "explicit_proxy_not_configured", "proxy_connect");
            if (!ProbeEndpointPolicy.TryProxy(request.ExplicitProxyEndpoint, out proxy))
                return Gap(request, "invalid_explicit_proxy_endpoint", "proxy_connect");
            if (_expectedProxy is not null && _expectedProxy.AbsoluteUri != proxy!.AbsoluteUri)
                return Gap(request, "explicit_proxy_endpoint_mismatch", "proxy_connect",
                    "The request proxy differs from the transport constraint; no request was sent.");
        }

        // R1-B: the requested family is a policy. Only the no-explicit-proxy path can enforce and
        // prove the target family; the proxy/system paths still send the request and keep the
        // target family honestly unknown instead of turning the whole path into a gap.
        var requestedPolicy = ProbeFamilyPolicy.Normalize(request.AddressFamily);
        var proxyIsLocal = request.Path == ProbePath.ExplicitHttpProxy
            ? ProbeEndpointPolicy.IsLocal(request.ExplicitProxyEndpoint) : null;
        var client = ClientFor(request.Path, proxy, family);
        var attempt = new ProbeAttemptObservation(request.Path, family, target.Host, target.Port);
        var connectsBefore = Interlocked.Read(ref _observedConnectAttempts);
        var previousAttempt = _currentAttempt.Value;
        _currentAttempt.Value = attempt;
        try
        {
            using var message = new HttpRequestMessage(method, target);
            try
            {
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                await DrainAsync(response, request.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                var newConnects = Interlocked.Read(ref _observedConnectAttempts) - connectsBefore;
                return WithFamilyFacts(Succeeded(response, request, attempt, proxyIsLocal, newConnects), request, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                return WithFamilyFacts(
                    Classify(exception, request, attempt) with { ProxyIsLocal = proxyIsLocal }, request, attempt);
            }
            catch (UnauthorizedAccessException)
            {
                return WithFamilyFacts(new TransportOutcome
                {
                    Status = EventStatus.AccessDenied,
                    TransportError = "access_denied",
                    FailedStage = attempt.FailureStage,
                    FailedEndpoint = "unknown",
                    ResolutionLocation = ResolutionLocationFor(request),
                    ResolvedAddresses = TargetAddresses(request, attempt),
                    ResolutionSource = TargetSource(request, attempt),
                    ResolvedAt = TargetResolvedAt(request, attempt),
                    Connection = ObservedConnection(attempt, claimReuse: false),
                    ProxyIsLocal = proxyIsLocal,
                }, request, attempt);
            }
            catch (IOException exception)
            {
                return WithFamilyFacts(new TransportOutcome
                {
                    Status = EventStatus.Failed,
                    TransportError = "io_error",
                    FailedStage = "http",
                    Notice = exception.GetType().Name,
                    FailedEndpoint = "unknown",
                    ResolutionLocation = ResolutionLocationFor(request),
                    ResolvedAddresses = TargetAddresses(request, attempt),
                    ResolutionSource = TargetSource(request, attempt),
                    ResolvedAt = TargetResolvedAt(request, attempt),
                    Connection = ObservedConnection(attempt, claimReuse: false),
                    ProxyIsLocal = proxyIsLocal,
                }, request, attempt);
            }
        }
        finally
        {
            _currentAttempt.Value = previousAttempt;
        }
    }

    private TransportOutcome Succeeded(
        HttpResponseMessage response, ProbeRequest request, ProbeAttemptObservation attempt,
        bool? proxyIsLocal, long newConnects)
    {
        // A pooled connection means the handler never dialled in this attempt, so it observed no
        // fresh resolution. Without the connect seam this cannot be claimed at all.
        var reused = _observesConnections && attempt.ConnectAttempts == 0 && newConnects == 0;
        return new TransportOutcome
        {
            Status = EventStatus.Ok,
            HttpStatus = (int)response.StatusCode,
            ResolutionLocation = ResolutionLocationFor(request),
            ResolvedAddresses = TargetAddresses(request, attempt),
            ResolutionSource = TargetSource(request, attempt),
            ResolvedAt = TargetResolvedAt(request, attempt),
            Connection = ObservedConnection(attempt, reused),
            ProxyIsLocal = proxyIsLocal,
            Notice = SuccessNotice(request.Path, reused),
        };
    }

    private HttpClient ClientFor(ProbePath path, Uri? proxy, ProbeAddressFamily family)
    {
        // A constrained attempt must never reuse an unconstrained (or other-family) connection.
        var key = $"{(int)path}|{proxy?.AbsoluteUri}|{(int)family}";
        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(key, out var client)) return client;
            var handler = _handlerFactory(new ProbeHandlerOptions(path, proxy, _connectTimeout)
            {
                AddressFamily = family,
            });
            client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            _clients.Add(key, client);
            return client;
        }
    }

    private HttpMessageHandler CreateHandler(ProbeHandlerOptions options)
    {
        var family = options.AddressFamily;
        // The requested target family is enforceable only when this process dials the target.
        // For proxy/system-default paths the connection goes to a proxy or an unknown next hop,
        // so the requested target policy must not filter that leg; the local connection family is
        // observed from the real socket instead.
        var enforcedFamily = options.Path == ProbePath.NoExplicitProxy
            ? family : ProbeAddressFamily.Unspecified;
        return new SocketsHttpHandler
        {
            // Null Proxy delegates to .NET DefaultProxy, which may include process environment.
            UseProxy = options.Path != ProbePath.NoExplicitProxy,
            Proxy = options.Path == ProbePath.ExplicitHttpProxy ? new WebProxy(options.ExplicitProxyEndpoint!) : null,
            ConnectTimeout = options.ConnectTimeout,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            // Certificate validation is left entirely to the handler; it is never disabled here.
            ConnectCallback = (context, token) => ConnectAsync(context, token, enforcedFamily),
        };
    }

    /// <summary>
    /// Resolves locally, enforces the requested family on the real socket and records what
    /// happened. This is the only place where the transport observes DNS/connect facts.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken, ProbeAddressFamily family)
    {
        Interlocked.Increment(ref _observedConnectAttempts);
        var endpoint = context.DnsEndPoint;
        var attempt = _currentAttempt.Value;

        ProbeNameResolution resolution;
        try
        {
            resolution = await _nameResolver.ResolveAsync(endpoint.Host, family, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The resolver's own error text may contain the private host name; only the category is kept.
            resolution = new ProbeNameResolution
            {
                Host = endpoint.Host,
                FailureReason = ProbeNameResolution.NameResolutionFailed,
            };
        }

        // Only addresses of the requested family may feed the socket; the published sample keeps
        // exactly that filtered set so a reader cannot mistake another family's answer for ours.
        var matching = new List<string>(resolution.Addresses.Count);
        if (resolution.Succeeded)
        {
            foreach (var text in resolution.Addresses)
            {
                if (!IPAddress.TryParse(text, out var candidate)) continue;
                if (!ProbeAddressFamilyPolicy.Matches(candidate, family)) continue;
                matching.Add(candidate.ToString());
            }
        }

        var resolvedForTarget = resolution with { Addresses = matching };
        attempt?.RecordResolution(resolvedForTarget, endpoint.Host, _timeProvider.GetUtcNow());

        if (!resolution.Succeeded)
        {
            attempt?.RecordResolutionFailure(resolution.FailureReason!);
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var addresses = new List<IPAddress>(matching.Count);
        foreach (var text in matching) addresses.Add(IPAddress.Parse(text));

        if (addresses.Count == 0)
        {
            attempt?.RecordResolutionFailure(ProbeNameResolution.NoAddressForRequestedFamily);
            throw new SocketException((int)SocketError.HostNotFound);
        }

        SocketException? lastFailure = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                attempt?.RecordConnected(address, endpoint.Port);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastFailure = exception;
                attempt?.RecordConnectFailure(exception.SocketErrorCode);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastFailure ?? new SocketException((int)SocketError.NoData);
    }

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

    private static string? SuccessNotice(ProbePath path, bool reused)
    {
        var systemNotice = path == ProbePath.SystemDefault ? SystemDefaultNotice : null;
        if (!reused) return systemNotice;
        return systemNotice is null ? ReuseNotice : systemNotice + " " + ReuseNotice;
    }

    private ProbeConnectionFact? ObservedConnection(ProbeAttemptObservation attempt, bool claimReuse) =>
        _observesConnections ? attempt.BuildConnectionFact(claimReuse) : null;

    private static IReadOnlyList<string> TargetAddresses(ProbeRequest request, ProbeAttemptObservation attempt) =>
        request.Path == ProbePath.NoExplicitProxy && attempt.TargetResolution is { } resolution
            ? resolution.Addresses
            : Array.Empty<string>();

    private static string? TargetSource(ProbeRequest request, ProbeAttemptObservation attempt) =>
        request.Path == ProbePath.NoExplicitProxy ? attempt.TargetResolution?.Source : null;

    private static DateTimeOffset? TargetResolvedAt(ProbeRequest request, ProbeAttemptObservation attempt) =>
        request.Path == ProbePath.NoExplicitProxy ? attempt.TargetResolutionAt : null;

    private static string ResolutionLocationFor(ProbeRequest request) => request.Path switch
    {
        ProbePath.NoExplicitProxy => "local",
        ProbePath.ExplicitHttpProxy when Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var target)
            && target.HostNameType == UriHostNameType.Dns => "proxy",
        _ => "unknown",
    };

    private static TransportOutcome Gap(
        ProbeRequest request, string error, string stage = "unknown", string? notice = null) =>
        WithFamilyFacts(new TransportOutcome
        {
            Status = EventStatus.Unsupported,
            TransportError = error,
            FailedStage = stage,
            FailedEndpoint = "unknown",
            ResolutionLocation = "unknown",
            Notice = notice ?? "No request was sent. Reason: " + error,
        }, request, attempt: null);

    /// <summary>
    /// Publishes the three family facts without inventing any: the request policy is normalized,
    /// the local connection family comes only from a real socket, and the target family is only
    /// proven when this process dialled the target itself.
    /// </summary>
    private static TransportOutcome WithFamilyFacts(
        TransportOutcome outcome, ProbeRequest request, ProbeAttemptObservation? attempt)
    {
        var policy = ProbeFamilyPolicy.Normalize(request.AddressFamily);
        var localFamily = attempt?.ConnectedAddressFamily;
        string status;
        string? targetFamily;
        if (request.Path == ProbePath.NoExplicitProxy)
        {
            targetFamily = localFamily;
            status = localFamily is not null
                ? ProbeTargetFamilyStatus.Observed
                : attempt is null ? ProbeTargetFamilyStatus.NotAttempted : ProbeTargetFamilyStatus.UnknownAfterAttempt;
        }
        else if (request.Path == ProbePath.ExplicitHttpProxy)
        {
            targetFamily = null;
            status = attempt is null ? ProbeTargetFamilyStatus.NotAttempted : ProbeTargetFamilyStatus.UnknownThroughProxy;
        }
        else
        {
            targetFamily = null;
            status = attempt is null ? ProbeTargetFamilyStatus.NotAttempted : ProbeTargetFamilyStatus.UnknownSystemDefault;
        }

        var notice = outcome.Notice;
        if (ProbeFamilyPolicy.IsEnforcedFamily(policy)
            && request.Path != ProbePath.NoExplicitProxy
            && status != ProbeTargetFamilyStatus.NotAttempted)
        {
            var familyNotice = "Requested target family " + policy
                + " cannot be enforced or observed on the " + (request.Path == ProbePath.ExplicitHttpProxy
                    ? "explicit proxy" : "system default") + " path; target family is "
                + status + ", local connection family is " + (localFamily ?? "unknown") + ".";
            notice = string.IsNullOrWhiteSpace(notice) ? familyNotice : notice + " " + familyNotice;
        }

        return outcome with
        {
            RequestedAddressFamily = policy,
            LocalConnectionAddressFamily = localFamily,
            TargetAddressFamily = targetFamily,
            TargetAddressFamilyStatus = status,
            Notice = notice,
        };
    }

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

    private TransportOutcome Classify(HttpRequestException exception, ProbeRequest request, ProbeAttemptObservation attempt)
    {
        // The connect seam already proved which leg and stage failed; prefer its measured facts
        // over the handler's opaque aggregate error.
        if (attempt.FailureStage is { } observedStage)
        {
            var socketError = attempt.FailureSocketError;
            return new TransportOutcome
            {
                Status = socketError is { } error ? StatusFor(error) : EventStatus.Failed,
                TransportError = attempt.FailureReason ?? socketError?.ToString() ?? "connection_failed",
                OsErrorCode = socketError is { } code ? (int)code : null,
                FailedStage = observedStage,
                FailedEndpoint = attempt.EndpointRole,
                ResolutionLocation = ResolutionLocationFor(request),
                ResolvedAddresses = TargetAddresses(request, attempt),
                ResolutionSource = TargetSource(request, attempt),
                ResolvedAt = TargetResolvedAt(request, attempt),
                Connection = ObservedConnection(attempt, claimReuse: false),
                Notice = FailureNotice(observedStage, attempt),
            };
        }

        // R1-B/F07: .NET surfaces a proxy CONNECT rejection through HttpRequestException with the
        // response status code. A 407 is a definite proxy authentication answer, not a generic
        // transport failure, and the proxy leg is the endpoint that answered.
        if (exception.HttpRequestError == HttpRequestError.ProxyTunnelError
            && exception.StatusCode is { } tunnelStatus)
        {
            var statusCode = (int)tunnelStatus;
            return new TransportOutcome
            {
                Status = EventStatus.Ok,
                HttpStatus = statusCode,
                TransportError = statusCode == 407 ? "proxy_authentication_required" : "proxy_tunnel_status_" + statusCode,
                FailedStage = "proxy_connect",
                FailedEndpoint = request.Path == ProbePath.ExplicitHttpProxy ? "proxy" : "unknown",
                ResolutionLocation = ResolutionLocationFor(request),
                ResolvedAddresses = TargetAddresses(request, attempt),
                ResolutionSource = TargetSource(request, attempt),
                ResolvedAt = TargetResolvedAt(request, attempt),
                Connection = ObservedConnection(attempt, claimReuse: false),
                Notice = "The proxy tunnel request was rejected with HTTP " + statusCode
                    + "; no tunnel to the target was created and no target request was sent.",
            };
        }

        var connection = ObservedConnection(attempt, claimReuse: false);
        var socket = FindInner<SocketException>(exception);
        if (socket is not null)
        {
            var stage = socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? "resolve" : "connect";
            return new TransportOutcome
            {
                Status = StatusFor(socket.SocketErrorCode),
                TransportError = socket.SocketErrorCode.ToString(),
                OsErrorCode = (int)socket.SocketErrorCode,
                FailedStage = stage,
                ResolutionLocation = request.Path == ProbePath.NoExplicitProxy ? "local" : "unknown",
                // HttpRequestException does not identify which connection leg produced a socket
                // error. A proxy may report an upstream failure after CONNECT was established.
                FailedEndpoint = request.Path == ProbePath.NoExplicitProxy ? "target" : "unknown",
                ResolvedAddresses = TargetAddresses(request, attempt),
                ResolutionSource = TargetSource(request, attempt),
                ResolvedAt = TargetResolvedAt(request, attempt),
                Connection = connection,
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
                HttpRequestError.ConnectionError => "connect",
                _ => "http",
            },
            FailedEndpoint = request.Path == ProbePath.NoExplicitProxy ? "target" : "unknown",
            ResolutionLocation = ResolutionLocationFor(request),
            ResolvedAddresses = TargetAddresses(request, attempt),
            ResolutionSource = TargetSource(request, attempt),
            ResolvedAt = TargetResolvedAt(request, attempt),
            Connection = connection,
        };
    }

    private static string FailureNotice(string stage, ProbeAttemptObservation attempt) => stage switch
    {
        "resolve" => attempt.FailureReason == ProbeNameResolution.NoAddressForRequestedFamily
            ? "Local name resolution produced no address of the requested family; the request was never sent and this family is unmeasured."
            : "Local name resolution failed before any connection was made; the request was never sent.",
        "connect" => attempt.EndpointRole == "proxy"
            ? "The TCP connection to the configured proxy endpoint failed; the target was never reached."
            : attempt.EndpointRole == "target"
                ? "The TCP connection to the target address failed after local resolution; no HTTP response was received."
                : "A TCP connection for the system default stack failed; whether it was the proxy or the target is unknown.",
        _ => "The attempt failed after a connection was established; the failing stage could not be proven.",
    };

    private static EventStatus StatusFor(SocketError error) => error switch
    {
        SocketError.AccessDenied => EventStatus.AccessDenied,
        SocketError.TimedOut => EventStatus.Timeout,
        SocketError.OperationAborted or SocketError.Interrupted => EventStatus.Cancelled,
        _ => EventStatus.Failed,
    };

    private static TException? FindInner<TException>(Exception exception) where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TException match) return match;
        return null;
    }
}



