 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Represents route matching criteria (path, hosts, headers, query parameters) parsed from NSerf YARP tags.
 /// </summary>
 public class MatchFromTag
{
    public string? Path { get; set; }
    public string[]? Hosts { get; set; }
    public HeaderMatchFromTag[]? Headers { get; set; }
    public QueryParameterFromTag[]? QueryParameters { get; set; }
}
