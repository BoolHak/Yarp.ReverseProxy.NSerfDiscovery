namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class ClusterFromTag
    {
        public string ClusterId { get; set; } = "";
        public string LoadBalancingPolicy { get; set; } = "RoundRobin";
        public SessionAffinityFromTag? SessionAffinity { get; set; }
        public HealthCheckFromTag? HealthCheck { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }
}