using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

/// <summary>
/// Implementation of <see cref="IProxyConfig"/> used to supply YARP with configuration
/// built from NSerf service discovery tags. Instances are immutable and signal changes
/// via an associated <see cref="IChangeToken"/>.
/// </summary>
public class NSerfProxyConfig : IProxyConfig
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Initializes a new instance of <see cref="NSerfProxyConfig"/> with the specified
    /// routes and clusters.
    /// </summary>
    /// <param name="routes">The collection of routes that YARP should expose.</param>
    /// <param name="clusters">The collection of clusters that YARP should route to.</param>
    public NSerfProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        Routes = routes;
        Clusters = clusters;
        ChangeToken = new CancellationChangeToken(_cts.Token);
    }

    /// <summary>
    /// Gets the collection of routes that YARP should expose.
    /// </summary>
    public IReadOnlyList<RouteConfig> Routes { get; }

    /// <summary>
    /// Gets the collection of clusters that YARP should route to.
    /// </summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; }

    /// <summary>
    /// Gets a token that signals changes to the configuration.
    /// </summary>
    public IChangeToken ChangeToken { get; }

    internal void SignalChange()
    {
        _cts.Cancel();
    }
}