using Yarp.ReverseProxy.NSerfDiscovery.Models;

namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

/// <summary>
/// Configuration options for the YARP gateway's NSerf integration.
/// </summary>
public sealed class NSerfGatewayOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "ReverseProxy:NSerf";

    /// <summary>
    /// Whether to drop clusters that have no online destinations.
    /// Default: false (keeps cluster definition even if empty)
    /// </summary>
    public bool DropClustersWithoutDestinations { get; set; } = false;

    /// <summary>
    /// Optional filter to exclude specific nodes from configuration.
    /// </summary>
    public Func<NodeConfig, bool>? NodeFilter { get; set; }

    /// <summary>
    /// Optional filter to determine if a node's destination should be included based on health.
    /// </summary>
    public Func<NodeConfig, bool>? DestinationHealthFilter { get; set; }

    /// <summary>
    /// Whether to fail (throw exception) when route conflicts are detected.
    /// If false, uses canonical route and logs warning.
    /// Default: false
    /// </summary>
    public bool FailOnRouteConflict { get; set; } = false;

    /// <summary>
    /// Whether to fail (throw exception) when cluster conflicts are detected.
    /// If false, uses canonical cluster and logs warning.
    /// Default: false
    /// </summary>
    public bool FailOnClusterConflict { get; set; } = false;

    /// <summary>
    /// Maximum allowed size for exported config payload (in bytes).
    /// Default: 256 KB
    /// </summary>
    public int MaxConfigPayloadSize { get; set; } = 256 * 1024;

    /// <summary>
    /// NSerf user event name to listen for config updates.
    /// Default: "yarp-config-update"
    /// </summary>
    public string EventName { get; set; } = "yarp-config-update";

    /// <summary>
    /// Debounce interval for config updates (prevents rapid successive updates).
    /// Default: 1 second
    /// </summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(1);
}
