using System.Net;
using System.Net.Sockets;
using NetScope.Application;

namespace NetScope.Probes;

/// <summary>
/// The facts one probe attempt really observed while it dialled, written by the transport's
/// connect seam. Everything starts unknown and is only published when it was actually seen: a
/// probe never guesses an address, a leg or a connection reuse.
/// </summary>
internal sealed class ProbeAttemptObservation
{
    private readonly object _sync = new();
    private int _connectAttempts;
    private string? _connectedAddress;
    private int? _connectedPort;
    private string? _failureStage;
    private string? _failureReason;
    private SocketError? _failureSocketError;
    private ProbeNameResolution? _targetResolution;
    private DateTimeOffset? _targetResolutionAt;

    public ProbeAttemptObservation(ProbePath path, ProbeAddressFamily family, string targetHost, int targetPort)
    {
        Path = path;
        AddressFamily = family;
        TargetHost = targetHost;
        TargetPort = targetPort;
        EndpointRole = path switch
        {
            ProbePath.NoExplicitProxy => "target",
            ProbePath.ExplicitHttpProxy => "proxy",
            _ => "unknown",
        };
    }

    public ProbePath Path { get; }

    public ProbeAddressFamily AddressFamily { get; }

    public string TargetHost { get; }

    public int TargetPort { get; }

    /// <summary>Which leg this attempt dialled: target / proxy / unknown.</summary>
    public string EndpointRole { get; }

    public int ConnectAttempts
    {
        get { lock (_sync) return _connectAttempts; }
    }

    public string? ConnectedAddress
    {
        get { lock (_sync) return _connectedAddress; }
    }

    public int? ConnectedPort
    {
        get { lock (_sync) return _connectedPort; }
    }

    public string? FailureStage
    {
        get { lock (_sync) return _failureStage; }
    }

    /// <summary>Address family of the really established local socket, parsed from the address.</summary>
    public string? ConnectedAddressFamily
    {
        get
        {
            lock (_sync)
            {
                if (_connectedAddress is null || !IPAddress.TryParse(_connectedAddress, out var address)) return null;
                return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "ipv4"
                    : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "ipv6" : null;
            }
        }
    }

    public string? FailureReason
    {
        get { lock (_sync) return _failureReason; }
    }

    public SocketError? FailureSocketError
    {
        get { lock (_sync) return _failureSocketError; }
    }

    public ProbeNameResolution? TargetResolution
    {
        get { lock (_sync) return _targetResolution; }
    }

    public DateTimeOffset? TargetResolutionAt
    {
        get { lock (_sync) return _targetResolutionAt; }
    }

    /// <summary>
    /// Keeps a resolution sample only when the dialled host is the target of a locally resolving
    /// path. Proxy-leg and system-default resolutions are never published as target resolution.
    /// The first sample wins so it stays aligned with the first established connection.
    /// </summary>
    public void RecordResolution(ProbeNameResolution resolution, string dialedHost, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        lock (_sync)
        {
            if (EndpointRole != "target") return;
            if (!string.Equals(dialedHost, TargetHost, StringComparison.OrdinalIgnoreCase)) return;
            if (_targetResolution is not null) return;
            _targetResolution = resolution;
            _targetResolutionAt = observedAt;
        }
    }

    /// <summary>Records a socket that was really established; the first one wins.</summary>
    public void RecordConnected(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (_sync)
        {
            _connectAttempts++;
            // A successful connection retires an earlier failed address: a later TLS/HTTP error
            // must not be attributed to the old connect failure.
            _failureStage = null;
            _failureReason = null;
            _failureSocketError = null;
            if (_connectedAddress is not null) return;
            _connectedAddress = address.ToString();
            _connectedPort = port;
        }
    }

    public void RecordConnectFailure(SocketError error)
    {
        lock (_sync)
        {
            _connectAttempts++;
            _failureStage = "connect";
            _failureReason = error.ToString();
            _failureSocketError = error;
        }
    }

    public void RecordResolutionFailure(string reason)
    {
        lock (_sync)
        {
            _failureStage = "resolve";
            _failureReason = reason;
            _failureSocketError = null;
        }
    }

    /// <summary>
    /// The published connection fact. <paramref name="claimReuse"/> may only be true when this
    /// attempt provably made no new connection at all; otherwise reuse stays unknown.
    /// </summary>
    public ProbeConnectionFact? BuildConnectionFact(bool claimReuse)
    {
        lock (_sync)
        {
            if (_connectAttempts > 0)
            {
                return new ProbeConnectionFact
                {
                    EndpointRole = EndpointRole,
                    RemoteAddress = _connectedAddress,
                    RemotePort = _connectedPort,
                    Reused = false,
                };
            }

            return claimReuse ? new ProbeConnectionFact { EndpointRole = EndpointRole, Reused = true } : null;
        }
    }
}


