 namespace Yarp.ReverseProxy.NSerfDiscovery.Models;

 /// <summary>
 /// Represents combined active and passive health check configuration parsed from NSerf YARP tags.
 /// </summary>
 public class HealthCheckFromTag
{
    public ActiveHealthCheckFromTag? Active { get; set; }
    public PassiveHealthCheckFromTag? Passive { get; set; }
}
