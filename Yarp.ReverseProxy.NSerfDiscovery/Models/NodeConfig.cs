using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

/// <summary>
/// Represents the complete configuration from a single NSerf node.
/// Combines YARP config with node network information from NSerf membership.
/// </summary>
public sealed class NodeConfig
{
    /// <summary>
    /// NSerf node identifier (from membership).
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Logical service name this node belongs to.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Service instance identifier (may differ from NodeId).
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Configuration revision from the exported config.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>
    /// YARP routes exported by this node.
    /// </summary>
    public IReadOnlyList<RouteConfig> Routes { get; init; } = Array.Empty<RouteConfig>();

    /// <summary>
    /// YARP clusters exported by this node.
    /// </summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = Array.Empty<ClusterConfig>();

    /// <summary>
    /// Node's host address (IP or hostname) from NSerf membership.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Node's service port from NSerf tags.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// Protocol scheme (http, https, grpc, etc.).
    /// </summary>
    public string Scheme { get; init; } = "http";

    /// <summary>
    /// NSerf member tags for additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Whether this node is currently online/alive in the NSerf cluster.
    /// </summary>
    public bool IsOnline { get; init; } = true;
}
