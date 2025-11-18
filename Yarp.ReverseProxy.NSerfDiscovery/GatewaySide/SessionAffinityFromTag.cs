namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class SessionAffinityFromTag
    {
        public bool Enabled { get; set; }
        public string? Policy { get; set; }
        public string? FailurePolicy { get; set; }
        public string? AffinityKeyName { get; set; }
    }
}