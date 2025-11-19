namespace Yarp.ReverseProxy.NSerfDiscovery.Extensions;

public class NSerfGatewayNodeOptions
{
    /// <summary>
    /// The name of this gateway node. Defaults to "gateway".
    /// </summary>
    public string NodeName { get; set; } = "gateway";

    /// <summary>
    /// List of seed nodes to join the cluster.
    /// </summary>
    public string[] SeedNodes { get; set; } = [];

    /// <summary>
    /// The bind address for the NSerf agent. Defaults to "0.0.0.0:7946".
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0:7946";
}