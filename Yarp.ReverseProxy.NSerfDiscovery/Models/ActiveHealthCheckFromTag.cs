 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Represents active health check configuration parsed from NSerf YARP tags.
 /// </summary>
 public class ActiveHealthCheckFromTag
{
    public bool Enabled { get; set; }
    public string? Interval { get; set; }
    public string? Timeout { get; set; }
    public string? Policy { get; set; }
    public string? Path { get; set; }
}
