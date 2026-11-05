using System.Net;

namespace NetScope.Probes;

internal static class ProbeEndpointPolicy
{
    public static bool TryProxy(string? endpoint, out Uri? proxy)
    {
        proxy = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(candidate.Host)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || candidate.AbsolutePath != "/"
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        proxy = candidate;
        return true;
    }

    public static bool? IsLocal(string? endpoint)
    {
        if (!TryProxy(endpoint, out var proxy)) return null;
        if (proxy!.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // DNS names are never resolved just to produce metadata. Their locality is unknown.
        return IPAddress.TryParse(proxy.IdnHost.Trim('[', ']'), out var address)
            ? IPAddress.IsLoopback(address) : null;
    }
}
