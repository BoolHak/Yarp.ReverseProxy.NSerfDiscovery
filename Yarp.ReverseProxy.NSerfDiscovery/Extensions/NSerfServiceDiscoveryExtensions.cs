using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSerf.ServiceDiscovery;

namespace Yarp.ReverseProxy.NSerfDiscovery.Extensions;

/// <summary>
/// Extension methods for setting up NSerf service discovery.
/// </summary>
public static class NSerfServiceDiscoveryExtensions
{
    /// <summary>
    /// Adds NSerf service discovery with automatic wiring to ServiceRegistry.
    /// Call this after AddNSerf().
    /// </summary>
    public static IServiceCollection AddNSerfServiceDiscovery(this IServiceCollection services)
    {
        // Register service registry
        services.AddSingleton<IServiceRegistry, ServiceRegistry>();
        
        // Register hosted service to wire NSerfServiceProvider to registry
        services.AddHostedService<NSerfServiceDiscoveryHostedService>();
        
        return services;
    }
}

/// <summary>
/// Hosted service that wires NSerfServiceProvider to IServiceRegistry.
/// </summary>
internal class NSerfServiceDiscoveryHostedService : IHostedService
{
    private readonly System.IServiceProvider _serviceProvider;
    private readonly IServiceRegistry _registry;
    private readonly ILogger<NSerfServiceDiscoveryHostedService> _logger;
    private NSerfServiceProvider? _provider;

    public NSerfServiceDiscoveryHostedService(
        System.IServiceProvider serviceProvider,
        IServiceRegistry registry,
        ILogger<NSerfServiceDiscoveryHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[NSerfServiceDiscovery] Starting service discovery provider");

        // Wait for SerfAgent to start
        await Task.Delay(2000, cancellationToken);

        try
        {
            var serf = _serviceProvider.GetRequiredService<NSerf.Serf.Serf>();
            
            _provider = new NSerfServiceProvider(serf);
            _provider.ServiceDiscovered += async (_, e) =>
            {
                _logger.LogInformation(
                    "[NSerfServiceDiscovery] Service event: {ChangeType} - {ServiceName}/{InstanceId}",
                    e.ChangeType, e.ServiceName, e.Instance?.Id);

                switch (e.ChangeType)
                {
                    case ServiceChangeType.InstanceRegistered:
                        if (e.Instance != null) 
                            await _registry.RegisterInstanceAsync(e.Instance, cancellationToken);
                        break;
                    case ServiceChangeType.InstanceDeregistered:
                        if (e.Instance?.Id != null)
                            await _registry.DeregisterInstanceAsync(e.ServiceName, e.Instance.Id, cancellationToken);
                        break;
                    case ServiceChangeType.InstanceUpdated:
                        if (e.Instance != null) 
                            await _registry.RegisterInstanceAsync(e.Instance, cancellationToken);
                        break;
                }
            };

            await _provider.StartAsync(cancellationToken);
            _logger.LogInformation("[NSerfServiceDiscovery] Service discovery provider started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NSerfServiceDiscovery] Failed to start service discovery provider");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_provider != null)
        {
            await _provider.StopAsync(cancellationToken);
            _logger.LogInformation("[NSerfServiceDiscovery] Service discovery provider stopped");
        }
    }
}
