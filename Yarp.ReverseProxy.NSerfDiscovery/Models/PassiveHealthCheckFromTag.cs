 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Represents passive health check configuration parsed from NSerf YARP tags.
 /// </summary>
 public class PassiveHealthCheckFromTag
{
    public bool Enabled { get; set; }
    public string? Policy { get; set; }
    public string? ReactivationPeriod { get; set; }
}
