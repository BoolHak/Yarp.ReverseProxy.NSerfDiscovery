namespace Yarp.ReverseProxy.NSerfDiscovery.Extensions;

/// <summary>
/// Options for configuring an NSerf node that acts as a Service (Broadcaster) or Gateway (Consumer).
/// </summary>
public class NSerfServiceNodeOptions
{
    /// <summary>
    /// The name of the service (e.g., "orders-api").
    /// </summary>
    public required string ServiceName { get; set; }

    /// <summary>
    /// The port the service is listening to on (e.g., 8080).
    /// </summary>
    public required int Port { get; set; }

    /// <summary>
    /// The scheme (http/https). Defaults to "http".
    /// </summary>
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// The unique ID for this instance. If null, one will be generated.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// List of seed nodes to join the cluster (e.g., "127.0.0.1:7946").
    /// </summary>
    public string[] SeedNodes { get; set; } = [];

    /// <summary>
    /// Enable Gzip compression for gossip payloads. Defaults to true.
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// The bind address for the NSerf agent. Defaults to "0.0.0.0:7946".
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0:7946";

    /// <summary>
    /// The configuration section name to look for YARP routes/clusters. Defaults to "ReverseProxyExport".
    /// </summary>
    public string YarpConfigSection { get; set; } = "ReverseProxyExport";

    /// <summary>
    /// Optional: Manually provide the serialized YARP config JSON.
    /// If provided, it overrides the configuration section.
    /// </summary>
    public string? YarpConfigJson { get; set; }
}