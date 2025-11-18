using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

/// <summary>
/// Represents the YARP configuration exported by a service instance.
/// This is the DTO that gets serialized and transmitted via NSerf events.
/// </summary>
public sealed class NSerfYarpExport
{
    /// <summary>
    /// Logical service name (e.g., "billing-api", "orders-api").
    /// Must be unique across all services in the cluster.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Unique identifier for this service instance.
    /// Defaults to NSerf node name if not specified.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Monotonically increasing revision number.
    /// Used for conflict resolution when multiple nodes export the same config.
    /// Higher revision wins.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>
    /// YARP route configurations exported by this service.
    /// Routes define how incoming requests are matched and routed to clusters.
    /// </summary>
    public IReadOnlyList<RouteConfig> Routes { get; init; } = [];

    /// <summary>
    /// YARP cluster configurations exported by this service.
    /// Clusters define backend destinations and load balancing policies.
    /// </summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = [];
}
