namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class ActiveHealthCheckFromTag
    {
        public bool Enabled { get; set; }
        public string? Interval { get; set; }
        public string? Timeout { get; set; }
        public string? Policy { get; set; }
        public string? Path { get; set; }
    }
}