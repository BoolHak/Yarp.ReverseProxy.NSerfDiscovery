namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

public partial class NSerfTagBasedConfigProvider
{
    public class MatchFromTag
    {
        public string? Path { get; set; }
        public string[]? Hosts { get; set; }
        public HeaderMatchFromTag[]? Headers { get; set; }
        public QueryParameterFromTag[]? QueryParameters { get; set; }
    }
}