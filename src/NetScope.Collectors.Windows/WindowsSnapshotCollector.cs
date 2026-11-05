using NetScope.Application;
using NetScope.Domain;

namespace NetScope.Collectors.Windows;

/// <summary>
/// Aggregates the read-only Windows collectors into one snapshot.
/// <para>
/// Each domain is read independently. If one of them fails, the snapshot still contains the
/// others and the failed domain is reported as a gap, so a permission problem can never look
/// like an empty, healthy configuration.
/// </para>
/// <para>No method here writes to the system, opens a socket or requests elevation.</para>
/// </summary>
public sealed class WindowsSnapshotCollector : ISnapshotCollector
{
    private readonly IInterfaceSource _interfaces;

    private readonly IRouteSource _routes;

    private readonly IDnsSource _dns;

    private readonly IProxySource _proxies;

    public WindowsSnapshotCollector()
        : this(new WindowsInterfaceSource(), new WindowsRouteSource(), new WindowsDnsSource(), new WindowsProxySource())
    {
    }

    public WindowsSnapshotCollector(
        IInterfaceSource interfaces,
        IRouteSource routes,
        IDnsSource dns,
        IProxySource proxies)
    {
        _interfaces = interfaces ?? throw new ArgumentNullException(nameof(interfaces));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _dns = dns ?? throw new ArgumentNullException(nameof(dns));
        _proxies = proxies ?? throw new ArgumentNullException(nameof(proxies));
    }

    public string Source => "windows.readonly-collector";

    public string SourceVersion => "1.0.0";

    public async Task<NetworkSnapshot> CollectAsync(CollectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var interfaces = await Guarded(_interfaces.ReadAsync, "interfaces", cancellationToken).ConfigureAwait(false);
        var routes = await Guarded(_routes.ReadAsync, "routes", cancellationToken).ConfigureAwait(false);
        var dns = await Guarded(_dns.ReadAsync, "dns", cancellationToken).ConfigureAwait(false);
        var proxies = await Guarded(_proxies.ReadAsync, "proxy", cancellationToken).ConfigureAwait(false);

        return new NetworkSnapshot
        {
            SnapshotId = context.SnapshotId,
            ObservedAt = context.ObservedAt,
            MonotonicMs = context.MonotonicMs,
            Synthetic = context.Synthetic,
            Interfaces = interfaces,
            Routes = routes,
            Dns = dns,
            Proxies = proxies,
        };
    }

    private static async Task<Collected<T>> Guarded<T>(
        Func<CancellationToken, Task<Collected<T>>> read,
        string domain,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The collector failed, so the domain is unknown. Reporting it as empty would be a
            // false "nothing is configured" statement.
            return Collected<T>.Unavailable(
                EventStatus.Failed,
                $"The {domain} domain could not be collected.",
                new[] { $"{exception.GetType().Name}: {exception.Message}" });
        }
    }
}
