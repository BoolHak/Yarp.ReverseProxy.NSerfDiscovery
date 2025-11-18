namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class HealthCheckFromTag
    {
        public ActiveHealthCheckFromTag? Active { get; set; }
        public PassiveHealthCheckFromTag? Passive { get; set; }
    }
}