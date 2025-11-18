using Microsoft.Extensions.Logging;
using NSerf.ServiceDiscovery;
using System.Text.Json;
using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

/// <summary>
/// YARP config provider that reads configuration from NSerf service discovery member tags.
/// </summary>
public partial class NSerfTagBasedConfigProvider : IProxyConfigProvider
{
    private readonly IServiceRegistry _serviceRegistry;
    private readonly ILogger<NSerfTagBasedConfigProvider> _logger;
    private volatile NSerfProxyConfig _config;
    private readonly string _yarpTagName;

    public NSerfTagBasedConfigProvider(
        IServiceRegistry serviceRegistry,
        ILogger<NSerfTagBasedConfigProvider> logger,
        string yarpTagName = "yarp:config")
    {
        _serviceRegistry = serviceRegistry;
        _logger = logger;
        _yarpTagName = yarpTagName;
        _config = new NSerfProxyConfig([], []);
        BuildConfigAsync().GetAwaiter().GetResult();
        _serviceRegistry.ServiceChanged += OnServiceChanged;
    }

    public IProxyConfig GetConfig()
    {
        _logger.LogDebug("[NSerfTagBasedConfigProvider] GetConfig called, returning config with {RouteCount} routes, {ClusterCount} clusters", 
            _config.Routes.Count, _config.Clusters.Count);
        return _config;
    }

    private void OnServiceChanged(object? sender, ServiceChangedEventArgs e)
    {
        _logger.LogInformation("[NSerfTagBasedConfigProvider] Service changed: {ServiceName} - {ChangeType}", 
            e.ServiceName, e.ChangeType);
        _ = Task.Run(BuildConfigAsync);
    }

    private Task BuildConfigAsync()
    {
        try
        {
            var routesByCluster = new Dictionary<string, RouteFromTag>();
            var clusters = new Dictionary<string, ClusterConfig>();

            var services = _serviceRegistry.GetServices();
            
            _logger.LogInformation("[NSerfTagBasedConfigProvider] Building YARP config from {Count} services", services.Count);

            foreach (var service in services)
            {
                foreach (var instance in service.Instances)
                {
                    
                    if (!instance.Metadata.TryGetValue(_yarpTagName, out var yarpConfigJson)) continue;
                    
                    try
                    {
                        var yarpConfig = JsonSerializer.Deserialize<YarpConfigFromTag>(yarpConfigJson);
                        if (yarpConfig is { Routes: not null, Clusters: not null })
                        {
                            CollectRoutes(yarpConfig, routesByCluster);
                            AddOrUpdateClusters(yarpConfig, clusters, instance);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, 
                            "[NSerfTagBasedConfigProvider] Failed to parse YARP config from instance {InstanceId}",
                            instance.Id);
                    }
                }
            }

            var routes = ConvertRoutesByClusterToActualRouteConfigList(routesByCluster);
            _logger.LogInformation("[NSerfTagBasedConfigProvider] Built YARP config: {RouteCount} routes, {ClusterCount} clusters", 
                routes.Count, clusters.Count);
            CreateNewConfigAndTriggerChange(routes, clusters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NSerfTagBasedConfigProvider] Failed to build YARP config");
        }
        
        return Task.CompletedTask;
    }

    private static void AddOrUpdateClusters(YarpConfigFromTag yarpConfig, Dictionary<string, ClusterConfig> clusters, ServiceInstance instance)
    {
        if (yarpConfig.Clusters == null) return;
        
        foreach (var clusterDef in yarpConfig.Clusters)
        {
            if (!clusters.TryGetValue(clusterDef.ClusterId, out var existingCluster))
                CreateANewCluster(clusters, clusterDef, instance);
            else
                AddDestinationToExistingCluster(existingCluster, instance, clusters, clusterDef);
        }
    }

    private static void CollectRoutes(YarpConfigFromTag yarpConfig, Dictionary<string, RouteFromTag> routesByCluster)
    {
        if (yarpConfig.Routes == null) return;
        
        foreach (var route in yarpConfig.Routes)
        {
            routesByCluster.TryAdd(route.ClusterId, route);
        }
    }

    private static void CreateANewCluster(Dictionary<string, ClusterConfig> clusters, ClusterFromTag clusterDef, ServiceInstance instance)
    {
        clusters[clusterDef.ClusterId] = new ClusterConfig
        {
            ClusterId = clusterDef.ClusterId,
            LoadBalancingPolicy = clusterDef.LoadBalancingPolicy,
            SessionAffinity = ConvertSessionAffinity(clusterDef.SessionAffinity),
            HealthCheck = ConvertHealthCheck(clusterDef.HealthCheck),
            Metadata = clusterDef.Metadata,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                [instance.Id] = new()
                {
                    Address = $"{instance.Scheme}://{instance.Host}:{instance.Port}"
                }
            }
        };
    }

    private static void AddDestinationToExistingCluster(ClusterConfig existingCluster, ServiceInstance instance,
        Dictionary<string, ClusterConfig> clusters, ClusterFromTag clusterDef)
    {
        var destinations = new Dictionary<string, DestinationConfig>(
            existingCluster.Destinations ?? new Dictionary<string, DestinationConfig>())
        {
            [instance.Id] = new()
            {
                Address = $"{instance.Scheme}://{instance.Host}:{instance.Port}"
            }
        };

        clusters[clusterDef.ClusterId] = existingCluster with { Destinations = destinations };
    }

    private static List<RouteConfig> ConvertRoutesByClusterToActualRouteConfigList(Dictionary<string, RouteFromTag> routesByCluster)
    {
        var routes = routesByCluster.Select(kvp => new RouteConfig
        {
            RouteId = kvp.Value.RouteId,
            ClusterId = kvp.Value.ClusterId,
            Order = kvp.Value.Order,
            Match = new RouteMatch
            {
                Path = kvp.Value.Match?.Path,
                Hosts = kvp.Value.Match?.Hosts,
                Headers = kvp.Value.Match?.Headers?.Select(h => new RouteHeader
                {
                    Name = h.Name,
                    Values = h.Values,
                    Mode = h.Mode switch
                    {
                        "ExactHeader" => HeaderMatchMode.ExactHeader,
                        "HeaderPrefix" => HeaderMatchMode.HeaderPrefix,
                        "Exists" => HeaderMatchMode.Exists,
                        _ => HeaderMatchMode.ExactHeader
                    }
                }).ToList(),
                QueryParameters = kvp.Value.Match?.QueryParameters?.Select(q => new RouteQueryParameter
                {
                    Name = q.Name,
                    Values = q.Values,
                    Mode = q.Mode switch
                    {
                        "Exact" => QueryParameterMatchMode.Exact,
                        "Prefix" => QueryParameterMatchMode.Prefix,
                        "Exists" => QueryParameterMatchMode.Exists,
                        _ => QueryParameterMatchMode.Exact
                    }
                }).ToList()
            },
            // Convert transforms to the format YARP expects
            Transforms = kvp.Value.Transforms?.Select(IReadOnlyDictionary<string, string> (t) => 
                new Dictionary<string, string>(t)
            ).ToList()
        }).ToList();
        return routes;
    }

    private void CreateNewConfigAndTriggerChange(List<RouteConfig> routes, Dictionary<string, ClusterConfig> clusters)
    {
        var newConfig = new NSerfProxyConfig(routes, clusters.Values.ToList());
        var oldConfig = Interlocked.Exchange(ref _config, newConfig);
            
        _logger.LogInformation("[NSerfTagBasedConfigProvider] Signaling config change. Old config had {OldRoutes} routes, new config has {NewRoutes} routes", 
            oldConfig.Routes.Count, newConfig.Routes.Count);
            
        oldConfig.SignalChange();
    }


    private static SessionAffinityConfig? ConvertSessionAffinity(SessionAffinityFromTag? source)
    {
        if (source?.AffinityKeyName == null) return null;
        
        return new SessionAffinityConfig
        {
            Enabled = source.Enabled,
            Policy = source.Policy,
            FailurePolicy = source.FailurePolicy,
            AffinityKeyName = source.AffinityKeyName
        };
    }

    private static HealthCheckConfig? ConvertHealthCheck(HealthCheckFromTag? source)
    {
        if (source == null) return null;
        
        return new HealthCheckConfig
        {
            Active = source.Active != null ? new ActiveHealthCheckConfig
            {
                Enabled = source.Active.Enabled,
                Interval = source.Active.Interval != null ? TimeSpan.Parse(source.Active.Interval) : null,
                Timeout = source.Active.Timeout != null ? TimeSpan.Parse(source.Active.Timeout) : null,
                Policy = source.Active.Policy,
                Path = source.Active.Path
            } : null,
            Passive = source.Passive != null ? new PassiveHealthCheckConfig
            {
                Enabled = source.Passive.Enabled,
                Policy = source.Passive.Policy,
                ReactivationPeriod = source.Passive.ReactivationPeriod != null ? TimeSpan.Parse(source.Passive.ReactivationPeriod) : null
            } : null
        };
    }
}
