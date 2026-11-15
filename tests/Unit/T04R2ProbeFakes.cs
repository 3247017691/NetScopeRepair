using NetScope.Probes;

namespace NetScope.Unit.Tests;

/// <summary>
/// Injectable resolution boundary for transport tests. It records every call so tests can prove
/// whether a name was resolved, and for which host and family, without touching real DNS.
/// </summary>
internal sealed class FakeNameResolver : IProbeNameResolver
{
    private readonly Func<string, ProbeAddressFamily, CancellationToken, Task<ProbeNameResolution>> _handler;

    public FakeNameResolver(Func<string, ProbeAddressFamily, CancellationToken, Task<ProbeNameResolution>> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public List<(string Host, ProbeAddressFamily Family)> Calls { get; } = new();

    public int CallCount
    {
        get { lock (Calls) return Calls.Count; }
    }

    public (string Host, ProbeAddressFamily Family) LastCall
    {
        get { lock (Calls) return Calls[^1]; }
    }

    public Task<ProbeNameResolution> ResolveAsync(
        string host, ProbeAddressFamily family, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add((host, family));
        return _handler(host, family, cancellationToken);
    }

    public static FakeNameResolver Returning(params string[] addresses) =>
        new((host, _, _) => Task.FromResult(new ProbeNameResolution
        {
            Host = host,
            Addresses = addresses,
            Source = "test-resolver",
        }));

    /// <summary>Returns every address regardless of the requested family, so the transport filter is proved.</summary>
    public static FakeNameResolver IgnoringFamily(params string[] addresses) =>
        ReturnIgnoringFamily(addresses);

    private static FakeNameResolver ReturnIgnoringFamily(string[] addresses) =>
        new((host, _, _) => Task.FromResult(new ProbeNameResolution
        {
            Host = host,
            Addresses = addresses,
            Source = "test-resolver",
        }));

    public static FakeNameResolver Failing(string reason = ProbeNameResolution.NameResolutionFailed) =>
        new((host, _, _) => Task.FromResult(new ProbeNameResolution
        {
            Host = host,
            Source = "test-resolver",
            FailureReason = reason,
        }));

    /// <summary>Never answers until the caller cancels; used for cancellation tests.</summary>
    public static FakeNameResolver Hanging() =>
        new(async (host, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new ProbeNameResolution { Host = host, Source = "test-resolver" };
        });
}
