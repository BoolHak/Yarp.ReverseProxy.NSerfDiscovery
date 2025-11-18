namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class PassiveHealthCheckFromTag
    {
        public bool Enabled { get; set; }
        public string? Policy { get; set; }
        public string? ReactivationPeriod { get; set; }
    }
}