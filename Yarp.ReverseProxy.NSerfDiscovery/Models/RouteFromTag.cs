namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class RouteFromTag
    {
        public string RouteId { get; set; } = "";
        public string ClusterId { get; set; } = "";
        public int? Order { get; set; }
        public MatchFromTag? Match { get; set; }
        public List<Dictionary<string, string>>? Transforms { get; set; }
    }
}