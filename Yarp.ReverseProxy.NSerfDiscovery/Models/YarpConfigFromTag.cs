namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class YarpConfigFromTag
    {
        public RouteFromTag[]? Routes { get; set; }
        public ClusterFromTag[]? Clusters { get; set; }
    }
}