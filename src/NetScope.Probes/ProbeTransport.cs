using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Probes;

/// <summary>One request to send over exactly one explicit path.</summary>
public sealed record ProbeRequest
{
    public required string Endpoint { get; init; }

    public required string Method { get; init; }

    public required ProbePath Path { get; init; }

    /// <summary>Endpoint of the explicit proxy, for example http://127.0.0.1:7890.</summary>
    public string? ExplicitProxyEndpoint { get; init; }

    public string? AddressFamily { get; init; }

    public int MaxResponseBytes { get; init; } = 16 * 1024;
}

/// <summary>
/// What the transport actually observed. A failure is reported, never thrown away and never
/// turned into an empty success.
/// </summary>
public sealed record TransportOutcome
{
    public required EventStatus Status { get; init; }

    public int? HttpStatus { get; init; }

    public string? TransportError { get; init; }

    public int? OsErrorCode { get; init; }

    /// <summary>resolve / connect / proxy_connect / tls / http / unknown.</summary>
    public string? FailedStage { get; init; }

    public IReadOnlyList<string> ResolvedAddresses { get; init; } = Array.Empty<string>();

    public string? ResolutionLocation { get; init; }

    public string? Notice { get; init; }

    /// <summary>Proven failing connection endpoint: proxy / target / unknown.</summary>
    public string? FailedEndpoint { get; init; }

    /// <summary>True/false only when proxy locality is established without resolving a name.</summary>
    public bool? ProxyIsLocal { get; init; }

    public static TransportOutcome Reachable(int httpStatus) => new()
    {
        Status = EventStatus.Ok,
        HttpStatus = httpStatus,
    };

    public static TransportOutcome Failure(
        EventStatus status,
        string transportError,
        string failedStage = "unknown",
        int? osErrorCode = null) => new()
    {
        Status = status,
        TransportError = transportError,
        FailedStage = failedStage,
        OsErrorCode = osErrorCode,
    };
}

/// <summary>
/// Sends one probe request. Implementations must respect cancellation and must not retry
/// aggressively, so one outage does not turn into a request storm.
/// </summary>
public interface IProbeTransport
{
    Task<TransportOutcome> SendAsync(ProbeRequest request, CancellationToken cancellationToken);
}
