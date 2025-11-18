 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Represents session affinity configuration parsed from NSerf YARP tags.
 /// </summary>
 public class SessionAffinityFromTag
{
    public bool Enabled { get; set; }
    public string? Policy { get; set; }
    public string? FailurePolicy { get; set; }
    public string? AffinityKeyName { get; set; }
}
