 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Root object representing the YARP configuration payload stored in NSerf member tags.
 /// </summary>
 public class YarpConfigFromTag
{
    public RouteFromTag[]? Routes { get; set; }
    public ClusterFromTag[]? Clusters { get; set; }
}
