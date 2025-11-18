 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Represents a single query parameter match condition in a route definition parsed from NSerf YARP tags.
 /// </summary>
 public class QueryParameterFromTag
{
    public string Name { get; set; } = "";
    public string[]? Values { get; set; }
    public string? Mode { get; set; }
}
