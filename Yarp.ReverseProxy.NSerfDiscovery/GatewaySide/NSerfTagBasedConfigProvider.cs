using Microsoft.Extensions.Logging;
using NSerf.ServiceDiscovery;
using System.Text.Json;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.Models;

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

    /// <summary>
    /// Initializes a new instance of <see cref="NSerfTagBasedConfigProvider"/> that builds
    /// YARP configuration from NSerf service discovery member tags.
    /// </summary>
    /// <param name="serviceRegistry">The service registry used to query discovered services.</param>
    /// <param name="logger">Logger used for diagnostics and observability.</param>
    /// <param name="yarpTagName">The member tag key that contains the serialized YARP config payload.</param>
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

    /// <summary>
    /// Gets the current proxy configuration snapshot built from NSerf tags.
    /// </summary>
    /// <returns>The current <see cref="IProxyConfig"/> instance used by YARP.</returns>
    public IProxyConfig GetConfig()
    {
        _logger.LogDebug("[NSerfTagBasedConfigProvider] GetConfig called, returning config with {RouteCount} routes, {ClusterCount} clusters", 
            _config.Routes.Count, _config.Clusters.Count);
        return _config;
    }

    /// <summary>
    /// Handles service registry change notifications by triggering an asynchronous
    /// rebuild of the YARP configuration from the latest NSerf state.
    /// </summary>
    private void OnServiceChanged(object? sender, ServiceChangedEventArgs e)
    {
        _logger.LogInformation("[NSerfTagBasedConfigProvider] Service changed: {ServiceName} - {ChangeType}", 
            e.ServiceName, e.ChangeType);
        _ = Task.Run(BuildConfigAsync);
    }

    /// <summary>
    /// Rebuilds routes and clusters from all services currently registered in NSerf
    /// and updates the internal proxy configuration.
    /// </summary>
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

    /// <summary>
    /// Adds new clusters or updates existing clusters with destination information
    /// derived from a single service instance's configuration.
    /// </summary>
    /// <param name="yarpConfig">The parsed YARP config exported by the instance.</param>
    /// <param name="clusters">The aggregate cluster map being built.</param>
    /// <param name="instance">The NSerf service instance providing destinations.</param>
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

    /// <summary>
    /// Collects all routes from the specified config into a dictionary keyed by route ID.
    /// Routes with missing or empty route IDs are ignored.
    /// </summary>
    /// <param name="yarpConfig">The parsed YARP config containing route definitions.</param>
    /// <param name="routesByCluster">The dictionary that will receive routes keyed by route ID.</param>
    private static void CollectRoutes(YarpConfigFromTag yarpConfig, Dictionary<string, RouteFromTag> routesByCluster)
    {
        if (yarpConfig.Routes == null) return;
        
        foreach (var route in yarpConfig.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.RouteId))
            {
                continue;
            }

            routesByCluster[route.RouteId] = route;
        }
    }

    /// <summary>
    /// Creates a new cluster entry and initializes it with a single destination
    /// corresponding to the specified service instance.
    /// </summary>
    /// <param name="clusters">The aggregate cluster map being built.</param>
    /// <param name="clusterDef">The cluster definition exported in the YARP config.</param>
    /// <param name="instance">The service instance that contributes a destination.</param>
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

    /// <summary>
    /// Adds a new destination to an existing cluster, returning an updated cluster
    /// instance with the merged destinations.
    /// </summary>
    /// <param name="existingCluster">The cluster to extend with a new destination.</param>
    /// <param name="instance">The service instance that contributes the additional destination.</param>
    /// <param name="clusters">The aggregate cluster map being built.</param>
    /// <param name="clusterDef">The cluster definition that the instance belongs to.</param>
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

    /// <summary>
    /// Converts the collected route definitions into concrete <see cref="RouteConfig"/>
    /// instances that YARP can consume. Invalid routes and duplicate match criteria are skipped with a warning.
    /// </summary>
    /// <param name="routesByCluster">The map of route IDs to route definitions.</param>
    /// <returns>A list of <see cref="RouteConfig"/> objects ready for YARP.</returns>
    private List<RouteConfig> ConvertRoutesByClusterToActualRouteConfigList(Dictionary<string, RouteFromTag> routesByCluster)
    {
        var routes = new List<RouteConfig>();
        var seenMatchCriteria = new HashSet<string>();
        
        foreach (var kvp in routesByCluster)
        {
            try
            {
                // Basic validation before conversion
                if (string.IsNullOrWhiteSpace(kvp.Value.RouteId))
                {
                    _logger.LogWarning("[NSerfTagBasedConfigProvider] Skipping route with empty RouteId");
                    continue;
                }
                
                if (string.IsNullOrWhiteSpace(kvp.Value.ClusterId))
                {
                    _logger.LogWarning("[NSerfTagBasedConfigProvider] Skipping route {RouteId} with empty ClusterId", kvp.Value.RouteId);
                    continue;
                }
                
                var path = kvp.Value.Match?.Path;
                if (ValidateRoutePathPattern(path))
                {
                    _logger.LogWarning("[NSerfTagBasedConfigProvider] Skipping route {RouteId} with invalid path pattern: {Path}", 
                        kvp.Value.RouteId, path);
                    continue;
                }
                
                var matchSignature = CreateRouteSignature(kvp, path);
                
                if (!seenMatchCriteria.Add(matchSignature))
                {
                    _logger.LogWarning("[NSerfTagBasedConfigProvider] Skipping duplicate route {RouteId} with identical match criteria to a previous route", 
                        kvp.Value.RouteId);
                    continue;
                }
                
                var routeConfig = new RouteConfig
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
                    
                    
                    Transforms = kvp.Value.Transforms?.Select(IReadOnlyDictionary<string, string> (t) => 
                        new Dictionary<string, string>(t)
                    ).ToList()
                };
                
                routes.Add(routeConfig);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, 
                    "[NSerfTagBasedConfigProvider] Skipping invalid route {RouteId} during conversion", 
                    kvp.Value.RouteId);
            }
        }
        
        return routes;
    }

    /// <summary>
    /// Detects obviously invalid route path patterns which would cause YARP to reject the route.
    /// Currently, it catches unbalanced bracket expressions like "[foo" which fail regex parsing.
    /// </summary>
    /// <param name="path">Route path template to validate.</param>
    /// <returns><c>true</c> when the path pattern is invalid and should be skipped; otherwise <c>false</c>.</returns>
    private static bool ValidateRoutePathPattern(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Contains('[') && !path.Contains(']');
    }

    /// <summary>
    /// Builds a signature string that uniquely identifies a route's match criteria
    /// (path, hosts, headers, query parameters) so duplicates can be detected.
    /// </summary>
    /// <param name="kvp">Key/value pair containing the route ID and definition.</param>
    /// <param name="path">Normalized path value extracted from the route.</param>
    /// <returns>A string containing the concatenated match criteria used for duplicate detection.</returns>
    private static string CreateRouteSignature(KeyValuePair<string, RouteFromTag> kvp, string? path)
    {
        var hosts = string.Join(",", kvp.Value.Match?.Hosts ?? []);
        var headers = kvp.Value.Match?.Headers != null 
            ? string.Join(";", kvp.Value.Match.Headers.Select(h => $"{h.Name}={string.Join(",", h.Values ?? [])}"))
            : "";
        var queryParams = kvp.Value.Match?.QueryParameters != null
            ? string.Join(";", kvp.Value.Match.QueryParameters.Select(q => $"{q.Name}={string.Join(",", q.Values ?? [])}"))
            : "";
                
        var matchSignature = $"{path}|{hosts}|{headers}|{queryParams}";
        return matchSignature;
    }

    /// <summary>
    /// Creates a new proxy configuration snapshot and signals a change on the
    /// previously active configuration so that YARP can reload its state.
    /// </summary>
    /// <param name="routes">The computed set of routes.</param>
    /// <param name="clusters">The computed set of clusters.</param>
    private void CreateNewConfigAndTriggerChange(List<RouteConfig> routes, Dictionary<string, ClusterConfig> clusters)
    {
        var newConfig = new NSerfProxyConfig(routes, clusters.Values.ToList());
        var oldConfig = Interlocked.Exchange(ref _config, newConfig);
            
        _logger.LogInformation("[NSerfTagBasedConfigProvider] Signaling config change. Old config had {OldRoutes} routes, new config has {NewRoutes} routes", 
            oldConfig.Routes.Count, newConfig.Routes.Count);
            
        oldConfig.SignalChange();
    }


    /// <summary>
    /// Converts a session affinity DTO parsed from tags into the corresponding
    /// YARP <see cref="SessionAffinityConfig"/> representation.
    /// </summary>
    /// <param name="source">The session affinity definition exported in tags.</param>
    /// <returns>The converted <see cref="SessionAffinityConfig"/>, or <c>null</c> if none is configured.</returns>
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

    /// <summary>
    /// Converts a health check DTO parsed from tags into the corresponding
    /// YARP <see cref="HealthCheckConfig"/> representation.
    /// </summary>
    /// <param name="source">The health check definition exported in tags.</param>
    /// <returns>The converted <see cref="HealthCheckConfig"/>, or <c>null</c> if none is configured.</returns>
    private static HealthCheckConfig? ConvertHealthCheck(HealthCheckFromTag? source)
    {
        if (source == null) return null;
        
        return new HealthCheckConfig
        {
            Active = source.Active != null ? new ActiveHealthCheckConfig
            {
                Enabled = source.Active.Enabled,
                Interval = TryParseTimeSpan(source.Active.Interval),
                Timeout = TryParseTimeSpan(source.Active.Timeout),
                Policy = source.Active.Policy,
                Path = source.Active.Path
            } : null,
            Passive = source.Passive != null ? new PassiveHealthCheckConfig
            {
                Enabled = source.Passive.Enabled,
                Policy = source.Passive.Policy,
                ReactivationPeriod = TryParseTimeSpan(source.Passive.ReactivationPeriod)
            } : null
        };
    }

    /// <summary>
    /// Attempts to parse a <see cref="TimeSpan"/> from the provided string, returning
    /// <c>null</c> if the value is empty or invalid instead of throwing.
    /// </summary>
    /// <param name="value">The string representation of the time span.</param>
    /// <returns>The parsed <see cref="TimeSpan"/>, or <c>null</c> if parsing fails.</returns>
    private static TimeSpan? TryParseTimeSpan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeSpan.TryParse(value, out var parsed) ? parsed : null;
    }
}
