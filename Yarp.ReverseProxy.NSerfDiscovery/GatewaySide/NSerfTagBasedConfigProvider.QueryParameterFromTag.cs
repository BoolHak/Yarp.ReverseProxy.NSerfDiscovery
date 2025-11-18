namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class QueryParameterFromTag
    {
        public string Name { get; set; } = "";
        public string[]? Values { get; set; }
        public string? Mode { get; set; }
    }
}