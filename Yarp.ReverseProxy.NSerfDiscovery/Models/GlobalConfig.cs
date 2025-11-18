using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

/// <summary>
/// Represents the merged global YARP configuration aggregated from all nodes.
/// This is the final configuration provided to YARP's IProxyConfigProvider.
/// </summary>
public sealed class GlobalConfig
{
    /// <summary>
    /// All routes merged from all nodes, keyed by RouteId.
    /// </summary>
    public IReadOnlyDictionary<string, RouteConfig> Routes { get; init; } 
        = new Dictionary<string, RouteConfig>();

    /// <summary>
    /// All clusters merged from all nodes, keyed by ClusterId.
    /// Destinations are populated from online node instances.
    /// </summary>
    public IReadOnlyDictionary<string, ClusterConfig> Clusters { get; init; } 
        = new Dictionary<string, ClusterConfig>();

    /// <summary>
    /// Timestamp when this configuration was built.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Number of nodes that contributed to this configuration.
    /// </summary>
    public int NodeCount { get; init; }
}
