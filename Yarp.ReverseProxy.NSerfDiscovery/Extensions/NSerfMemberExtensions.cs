using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSerf.Extensions;
using Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

namespace Yarp.ReverseProxy.NSerfDiscovery.Extensions;

public static class NSerfMemberExtensions
{
    /// <summary>
    /// Configures this application as an NSerf Service Node (Broadcaster).
    /// It will automatically advertise its existence and YARP configuration to the cluster.
    /// </summary>
    public static IServiceCollection AddNSerfService(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NSerfServiceNodeOptions> configure)
    {
        var options = new NSerfServiceNodeOptions
        {
            ServiceName = "unknown", 
            Port = 0
        };

        configure(options);

        var instanceId = options.InstanceId ?? $"{options.ServiceName}-{Guid.NewGuid():N}";
        var yarpConfigTag = !string.IsNullOrEmpty(options.YarpConfigJson) 
            ? options.YarpConfigJson :
            NSerfYarpTagExporter.BuildYarpConfigTag(configuration, options.YarpConfigSection);

        return services.AddNSerf(agent =>
        {
            agent.NodeName = instanceId;
            agent.BindAddr = options.BindAddress;

            // Standard tags for service discovery
            agent.Tags["role"] = "service";
            agent.Tags[$"service:{options.ServiceName}"] = "true";
            agent.Tags[$"port:{options.ServiceName}"] = options.Port.ToString();
            agent.Tags[$"scheme:{options.ServiceName}"] = options.Scheme;
            agent.Tags["yarp:config"] = yarpConfigTag;
            agent.Profile = "lan";

            if (options.SeedNodes.Length <= 0) return;
            
            agent.StartJoin = options.SeedNodes;
            agent.RetryJoin = options.SeedNodes;
            agent.RetryInterval = TimeSpan.FromSeconds(2);
            agent.RetryMaxAttempts = 30;
        });
    }

    /// <summary>
    /// Configures this application as an NSerf Gateway Node (Consumer).
    /// It will join the cluster to discover other services.
    /// </summary>
    public static IServiceCollection AddNSerfGatewayNode(
        this IServiceCollection services,
        Action<NSerfGatewayNodeOptions> configure)
    {
        var options = new NSerfGatewayNodeOptions();
        configure(options);

        return services.AddNSerf(agent =>
        {
            agent.NodeName = options.NodeName;
            agent.BindAddr = options.BindAddress;

            agent.Tags["role"] = "gateway";
            agent.Profile = "lan";

            if (options.SeedNodes.Length <= 0) return;
            
            agent.StartJoin = options.SeedNodes;
            agent.RetryJoin = options.SeedNodes;
            agent.RetryInterval = TimeSpan.FromSeconds(2);
            agent.RetryMaxAttempts = 30;
        });
    }
}
