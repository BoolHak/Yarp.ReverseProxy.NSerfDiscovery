using System.Text.Json;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Shared helper methods for integration tests.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Waits until YARP discovers the expected number of services/routes.
    /// Polls the gateway's debug endpoint instead of using fixed delays.
    /// </summary>
    /// <param name="gatewayUrl">Base URL of the gateway</param>
    /// <param name="expectedRouteCount">Expected number of routes (0 means any)</param>
    /// <param name="expectedClusterCount">Expected number of clusters (0 means any)</param>
    /// <param name="expectedDestinationCount">Expected total destinations across all clusters (0 means any)</param>
    /// <param name="maxWaitSeconds">Maximum time to wait in seconds</param>
    /// <returns>True if services discovered within timeout, false otherwise</returns>
    public static async Task<bool> WaitForYarpConfigAsync(
        string gatewayUrl,
        int expectedRouteCount = 0,
        int expectedClusterCount = 0,
        int expectedDestinationCount = 0,
        int maxWaitSeconds = 15)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var debugUrl = $"{gatewayUrl}/debug/yarp-config";
        var startTime = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(500);

        while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds)
        {
            try
            {
                var response = await httpClient.GetAsync(debugUrl);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var config = JsonSerializer.Deserialize<YarpConfigDebug>(content);

                    if (config != null)
                    {
                        var routeCountMatch = expectedRouteCount == 0 || config.Routes?.Count >= expectedRouteCount;
                        var clusterCountMatch = expectedClusterCount == 0 || config.Clusters?.Count >= expectedClusterCount;
                        
                        var totalDestinations = config.Clusters?.Sum(c => 
                            c.Destinations?.Count ?? 0) ?? 0;
                        var destinationCountMatch = expectedDestinationCount == 0 || totalDestinations >= expectedDestinationCount;

                        if (routeCountMatch && clusterCountMatch && destinationCountMatch)
                        {
                            Console.WriteLine($"[TestHelper] YARP config ready: {config.Routes?.Count ?? 0} routes, " +
                                            $"{config.Clusters?.Count ?? 0} clusters, {totalDestinations} destinations " +
                                            $"(waited {(DateTime.UtcNow - startTime).TotalSeconds:F1}s)");
                            return true;
                        }

                        Console.WriteLine($"[TestHelper] Waiting for YARP config: " +
                                        $"routes={config.Routes?.Count ?? 0}/{expectedRouteCount}, " +
                                        $"clusters={config.Clusters?.Count ?? 0}/{expectedClusterCount}, " +
                                        $"destinations={totalDestinations}/{expectedDestinationCount}");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Gateway not ready yet, continue polling
                Console.WriteLine($"[TestHelper] Gateway not ready yet: {ex.Message}");
            }

            await Task.Delay(pollInterval);
        }

        Console.WriteLine($"[TestHelper] Timeout waiting for YARP config after {maxWaitSeconds}s");
        return false;
    }

    /// <summary>
    /// Waits for the gateway to be responsive (health check passes).
    /// </summary>
    public static async Task<bool> WaitForGatewayHealthyAsync(string gatewayUrl, int maxWaitSeconds = 10)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var healthUrl = $"{gatewayUrl}/health";
        var startTime = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(500);

        while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[TestHelper] Gateway healthy (waited {(DateTime.UtcNow - startTime).TotalSeconds:F1}s)");
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Gateway not ready yet
            }

            await Task.Delay(pollInterval);
        }

        Console.WriteLine($"[TestHelper] Timeout waiting for gateway health after {maxWaitSeconds}s");
        return false;
    }

    /// <summary>
    /// Waits for a service's health endpoint to be responsive, indicating Serf is ready.
    /// This ensures the service is ready to accept Serf join requests.
    /// </summary>
    public static async Task<bool> WaitForServiceHealthyAsync(string serviceUrl, int maxWaitSeconds = 10)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var healthUrl = $"{serviceUrl}/health";
        var startTime = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(500);

        while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[TestHelper] Service healthy and ready for Serf join (waited {(DateTime.UtcNow - startTime).TotalSeconds:F1}s)");
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Service not ready yet
            }

            await Task.Delay(pollInterval);
        }

        Console.WriteLine($"[TestHelper] Timeout waiting for service health after {maxWaitSeconds}s");
        return false;
    }

    /// <summary>
    /// Smart wait for Serf cluster formation and YARP config discovery.
    /// Waits minimum time needed: service health + config discovery.
    /// </summary>
    public static async Task WaitForClusterFormationAsync(
        string serviceUrl, 
        string gatewayUrl, 
        int expectedRoutes = 1,
        int expectedClusters = 1,
        int expectedDestinations = 1)
    {
        // Step 1: Wait for service to be healthy (Serf ready)
        var serviceReady = await WaitForServiceHealthyAsync(serviceUrl, maxWaitSeconds: 5);
        if (!serviceReady)
        {
            throw new TimeoutException("Service failed to become healthy");
        }

        // Step 2: Wait for YARP to discover services
        var configReady = await WaitForYarpConfigAsync(
            gatewayUrl, 
            expectedRoutes, 
            expectedClusters, 
            expectedDestinations, 
            maxWaitSeconds: 10);
        
        if (!configReady)
        {
            throw new TimeoutException("YARP failed to discover services");
        }
    }

    /// <summary>
    /// Minimal wait for Serf agent to start (1 second is usually enough).
    /// </summary>
    public static Task WaitForSerfStartAsync() => Task.Delay(TimeSpan.FromSeconds(1));

    /// <summary>
    /// Wait for Serf cluster to form and YARP to discover services (typically 4-5 seconds).
    /// </summary>
    public static Task WaitForSerfClusterAsync() => Task.Delay(TimeSpan.FromSeconds(5));

    // DTOs for deserializing debug endpoint response
    private class YarpConfigDebug
    {
        public List<RouteDebug>? Routes { get; set; }
        public List<ClusterDebug>? Clusters { get; set; }
    }

    private class RouteDebug
    {
        public string? RouteId { get; set; }
    }

    private class ClusterDebug
    {
        public string? ClusterId { get; set; }
        public Dictionary<string, DestinationDebug>? Destinations { get; set; }
    }

    private class DestinationDebug
    {
        public string? Address { get; set; }
    }
}
