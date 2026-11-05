using NetScope.Application;

namespace NetScope.Collectors.Windows;

/// <summary>Reads the interface table. Must not change any adapter state.</summary>
public interface IInterfaceSource
{
    Task<Collected<IReadOnlyList<InterfaceState>>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the routing table. Must not add, delete or modify a route.</summary>
public interface IRouteSource
{
    Task<Collected<IReadOnlyList<RouteEntry>>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads DNS configuration. Must not flush or change a resolver.</summary>
public interface IDnsSource
{
    Task<Collected<DnsState>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads proxy configuration layers. Must never write an environment variable.</summary>
public interface IProxySource
{
    Task<Collected<ProxyState>> ReadAsync(CancellationToken cancellationToken);
}
