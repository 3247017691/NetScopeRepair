using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Probes;

/// <summary>
/// Maps one transport outcome onto the published classification vocabulary. This is a pure
/// function so the rules for "reachable" and "failed" are testable without any network.
/// </summary>
public static class ProbeClassificationPolicy
{
    /// <summary>HTTP status that proves the proxy requires credentials.</summary>
    public const int ProxyAuthenticationRequired = 407;

    public static ProbeClassification Classify(TransportOutcome outcome, ProbeTarget target)
    {
        // A cancelled, denied or incomplete observation cannot establish target behaviour,
        // even when the transport also retained an HTTP status from an earlier stage.
        if (outcome.Status != EventStatus.Ok)
        {
            return outcome.Status is EventStatus.Failed or EventStatus.Timeout
                ? ProbeClassification.TransportFailure
                : ProbeClassification.Inconclusive;
        }

        if (outcome.HttpStatus is { } httpStatus)
        {
            if (httpStatus == ProxyAuthenticationRequired)
            {
                return ProbeClassification.AuthenticationRequired;
            }

            if (httpStatus is 401 or 403)
            {
                return ProbeClassification.AuthenticationRequired;
            }

            return target.ExpectedStatusCodes.Contains(httpStatus)
                ? ProbeClassification.ReachableExpected
                : ProbeClassification.ReachableUnexpected;
        }

        return ProbeClassification.Inconclusive;
    }

    /// <summary>
    /// A "reachable" classification is only meaningful when the event status produced data.
    /// This keeps "reachable" from being reported on top of a timeout.
    /// </summary>
    public static bool IsConsistent(ProbeClassification classification, EventStatus status)
    {
        var reachable = classification is ProbeClassification.ReachableExpected
            or ProbeClassification.ReachableUnexpected
            or ProbeClassification.AuthenticationRequired;

        return !reachable || status == EventStatus.Ok;
    }
}
