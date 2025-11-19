using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSerf.ServiceDiscovery;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

namespace Yarp.ReverseProxy.NSerfDiscovery.Extensions;

/// <summary>
/// Extension methods for configuring YARP with NSerf tag-based service discovery.
/// </summary>
public static class NSerfTagBasedExtensions
{
    /// <summary>
    /// Configures YARP to load configuration from NSerf member tags.
    /// Also configures the application as an NSerf Gateway Node.
    /// </summary>
    public static IReverseProxyBuilder AddNSerfGateway(
        this IReverseProxyBuilder builder,
        Action<NSerfGatewayNodeOptions> configure,
        string yarpTagName = "yarp:config")
    {
        // Configure NSerf as a Gateway Node
        builder.Services.AddNSerfGatewayNode(configure);

        // Add the service discovery components
        builder.Services.AddNSerfServiceDiscovery();

        // Add the config provider
        builder.Services.AddSingleton<IProxyConfigProvider>(sp =>
        {
            var registry = sp.GetRequiredService<IServiceRegistry>();
            var logger = sp.GetRequiredService<ILogger<NSerfTagBasedConfigProvider>>();
            return new NSerfTagBasedConfigProvider(registry, logger, yarpTagName);
        });

        return builder;
    }

    /// <summary>
    /// Configures YARP to load configuration from NSerf member tags.
    /// Automatically sets up NSerf service discovery.
    /// </summary>
    public static IReverseProxyBuilder LoadFromNSerfTags(this IReverseProxyBuilder builder, string yarpTagName = "yarp:config")
    {
        builder.Services.AddNSerfServiceDiscovery();
        builder.Services.AddSingleton<IProxyConfigProvider>(sp =>
        {
            var registry = sp.GetRequiredService<IServiceRegistry>();
            var logger = sp.GetRequiredService<ILogger<NSerfTagBasedConfigProvider>>();
            return new NSerfTagBasedConfigProvider(registry, logger, yarpTagName);
        });

        return builder;
    }
}
