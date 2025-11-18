using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSerf.ServiceDiscovery;
using System.Text.Json;
using System.Threading;
using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

/// <summary>
/// YARP config provider that reads configuration from NSerf service discovery member tags.
/// </summary>
public class NSerfTagBasedConfigProvider : IProxyConfigProvider
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
        
        // Build initial config SYNCHRONOUSLY to ensure YARP has routes on first GetConfig() call
        // This prevents YARP from starting with an empty config
        _config = new NSerfProxyConfig(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());
        BuildConfigAsync().GetAwaiter().GetResult();

        // Subscribe to service changes for future updates
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

            // Get all services from registry
            var services = _serviceRegistry.GetServices();
            
            _logger.LogInformation("[NSerfTagBasedConfigProvider] Building YARP config from {Count} services", services.Count);

            foreach (var service in services)
            {
                foreach (var instance in service.Instances)
                {
                    // Try to get YARP config from tags
                    if (instance.Metadata.TryGetValue(_yarpTagName, out var yarpConfigJson))
                    {
                        try
                        {
                            var yarpConfig = JsonSerializer.Deserialize<YarpConfigFromTag>(yarpConfigJson);
                            if (yarpConfig?.Routes != null && yarpConfig.Clusters != null)
                            {
                                // Collect routes (deduplicated by cluster ID - one route per cluster)
                                foreach (var route in yarpConfig.Routes)
                                {
                                    if (!routesByCluster.ContainsKey(route.ClusterId))
                                    {
                                        routesByCluster[route.ClusterId] = route;
                                    }
                                }

                                // Add/update clusters with destinations
                                foreach (var clusterDef in yarpConfig.Clusters)
                                {
                                    if (!clusters.TryGetValue(clusterDef.ClusterId, out var existingCluster))
                                    {
                                        // Create new cluster
                                        clusters[clusterDef.ClusterId] = new ClusterConfig
                                        {
                                            ClusterId = clusterDef.ClusterId,
                                            LoadBalancingPolicy = clusterDef.LoadBalancingPolicy,
                                            SessionAffinity = ConvertSessionAffinity(clusterDef.SessionAffinity),
                                            HealthCheck = ConvertHealthCheck(clusterDef.HealthCheck),
                                            Metadata = clusterDef.Metadata,
                                            Destinations = new Dictionary<string, DestinationConfig>
                                            {
                                                [instance.Id] = new DestinationConfig
                                                {
                                                    Address = $"{instance.Scheme}://{instance.Host}:{instance.Port}"
                                                }
                                            }
                                        };
                                    }
                                    else
                                    {
                                        // Add destination to existing cluster
                                        // Note: All instances should have the same cluster config (SessionAffinity, HealthCheck, etc.)
                                        // We use the config from the first instance and just add destinations
                                        var destinations = new Dictionary<string, DestinationConfig>(
                                            existingCluster.Destinations ?? new Dictionary<string, DestinationConfig>())
                                        {
                                            [instance.Id] = new DestinationConfig
                                            {
                                                Address = $"{instance.Scheme}://{instance.Host}:{instance.Port}"
                                            }
                                        };
                                        
                                        clusters[clusterDef.ClusterId] = existingCluster with { Destinations = destinations };
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[NSerfTagBasedConfigProvider] Failed to parse YARP config from instance {InstanceId}", instance.Id);
                        }
                    }
                }
            }

            // Convert routesByCluster to actual RouteConfig list
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
                Transforms = kvp.Value.Transforms?.Select(t => 
                    (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(t)
                ).ToList()
            }).ToList();

            _logger.LogInformation("[NSerfTagBasedConfigProvider] Built YARP config: {RouteCount} routes, {ClusterCount} clusters", 
                routes.Count, clusters.Count);

            // Create new config and trigger change notification
            // Following YARP's InMemoryConfigProvider pattern: create new config, swap atomically, signal old config
            var newConfig = new NSerfProxyConfig(routes, clusters.Values.ToList());
            var oldConfig = Interlocked.Exchange(ref _config, newConfig);
            
            _logger.LogInformation("[NSerfTagBasedConfigProvider] Signaling config change. Old config had {OldRoutes} routes, new config has {NewRoutes} routes", 
                oldConfig.Routes.Count, newConfig.Routes.Count);
            
            oldConfig.SignalChange();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NSerfTagBasedConfigProvider] Failed to build YARP config");
        }
        
        return Task.CompletedTask;
    }

    private class NSerfProxyConfig : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public NSerfProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }
        public IChangeToken ChangeToken { get; }

        internal void SignalChange()
        {
            _cts.Cancel();
        }
    }

    private class YarpConfigFromTag
    {
        public RouteFromTag[]? Routes { get; set; }
        public ClusterFromTag[]? Clusters { get; set; }
    }

    private class RouteFromTag
    {
        public string RouteId { get; set; } = "";
        public string ClusterId { get; set; } = "";
        public int? Order { get; set; }
        public MatchFromTag? Match { get; set; }
        public List<Dictionary<string, string>>? Transforms { get; set; }
    }

    private class MatchFromTag
    {
        public string? Path { get; set; }
        public string[]? Hosts { get; set; }
        public HeaderMatchFromTag[]? Headers { get; set; }
        public QueryParameterFromTag[]? QueryParameters { get; set; }
    }

    private class QueryParameterFromTag
    {
        public string Name { get; set; } = "";
        public string[]? Values { get; set; }
        public string? Mode { get; set; }
    }

    // Transforms are represented as dictionaries in YARP configuration
    // We'll deserialize them as JsonElement to preserve the exact structure

    private class HeaderMatchFromTag
    {
        public string Name { get; set; } = "";
        public string[]? Values { get; set; }
        public string? Mode { get; set; }
    }

    private class ClusterFromTag
    {
        public string ClusterId { get; set; } = "";
        public string LoadBalancingPolicy { get; set; } = "RoundRobin";
        public SessionAffinityFromTag? SessionAffinity { get; set; }
        public HealthCheckFromTag? HealthCheck { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    private static SessionAffinityConfig? ConvertSessionAffinity(SessionAffinityFromTag? source)
    {
        if (source == null) return null;
        
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

    private class SessionAffinityFromTag
    {
        public bool Enabled { get; set; }
        public string? Policy { get; set; }
        public string? FailurePolicy { get; set; }
        public string? AffinityKeyName { get; set; }
    }

    private class HealthCheckFromTag
    {
        public ActiveHealthCheckFromTag? Active { get; set; }
        public PassiveHealthCheckFromTag? Passive { get; set; }
    }

    private class ActiveHealthCheckFromTag
    {
        public bool Enabled { get; set; }
        public string? Interval { get; set; }
        public string? Timeout { get; set; }
        public string? Policy { get; set; }
        public string? Path { get; set; }
    }

    private class PassiveHealthCheckFromTag
    {
        public bool Enabled { get; set; }
        public string? Policy { get; set; }
        public string? ReactivationPeriod { get; set; }
    }
}
