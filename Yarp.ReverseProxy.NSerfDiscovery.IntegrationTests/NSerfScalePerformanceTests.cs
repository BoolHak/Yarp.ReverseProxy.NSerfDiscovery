using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP scale and performance scenarios.
/// Tests many services/instances and large configurations.
/// </summary>
[Collection("Sequential")]
public class NSerfScalePerformanceTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private readonly List<IContainer> _serviceContainers = new();

    public async Task InitializeAsync()
    {
        // Create Docker network for tests
        _network = new NetworkBuilder()
            .WithName($"test-network-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        // Cleanup gateway
        if (_gatewayContainer != null)
        {
            await _gatewayContainer.StopAsync();
            await _gatewayContainer.DisposeAsync();
        }

        // Cleanup all service containers
        foreach (var container in _serviceContainers)
        {
            try
            {
                await container.StopAsync();
                await container.DisposeAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        _serviceContainers.Clear();

        // Cleanup network
        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact(Timeout = 300000)] // 5 minutes timeout for scale test
    public async Task ManyServicesAndInstances_ShouldAllBeReachable()
    {
        // Arrange - Create multiple services with multiple instances each
        var serviceCount = 5; // 5 different services
        var instancesPerService = 2; // 2 instances per service
        var baseHttpPort = 8500;
        var baseSerfPort = 8200;
        var portOffset = 0;

        var stopwatch = Stopwatch.StartNew();
        System.Console.WriteLine($"Starting {serviceCount} services with {instancesPerService} instances each...");

        string? firstServiceIp = null;

        // Create services and instances
        for (int serviceIdx = 0; serviceIdx < serviceCount; serviceIdx++)
        {
            var serviceName = $"service-{serviceIdx}";
            var clusterName = $"cluster-{serviceIdx}";

            for (int instanceIdx = 0; instanceIdx < instancesPerService; instanceIdx++)
            {
                var instanceId = $"{serviceName}-instance-{instanceIdx}";
                var nodeName = $"node-{serviceIdx}-{instanceIdx}";

                // Create YARP config for this service
                var yarpConfig = JsonSerializer.Serialize(new
                {
                    Routes = new[]
                    {
                        new
                        {
                            RouteId = $"route-{serviceName}",
                            ClusterId = clusterName,
                            Match = new { Path = $"/{serviceName}/{{**catch-all}}" }
                        }
                    },
                    Clusters = new[]
                    {
                        new
                        {
                            ClusterId = clusterName,
                            LoadBalancingPolicy = "RoundRobin"
                        }
                    }
                });

                var containerBuilder = new ContainerBuilder()
                    .WithImage("test-service:latest")
                    .WithNetwork(_network!)
                    .WithNetworkAliases(nodeName)
                    .WithEnvironment("SERVICE_NAME", clusterName)
                    .WithEnvironment("INSTANCE_ID", instanceId)
                    .WithEnvironment("SERVICE_PORT", "8080")
                    .WithEnvironment("SERF_NODE_NAME", nodeName)
                    .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                    .WithPortBinding(baseHttpPort + portOffset, 8080)
                    .WithPortBinding(baseSerfPort + portOffset, 7946);

                // Join first service for cluster formation
                if (firstServiceIp != null)
                {
                    containerBuilder = containerBuilder.WithEnvironment("SERF_JOIN", $"{firstServiceIp}:7946");
                }

                var container = containerBuilder.Build();
                await container.StartAsync();
                _serviceContainers.Add(container);

                if (firstServiceIp == null)
                {
                    firstServiceIp = container.IpAddress;
                }

                portOffset++;
                await Task.Delay(TimeSpan.FromSeconds(2)); // Stagger starts
            }
        }

        var setupTime = stopwatch.Elapsed;
        System.Console.WriteLine($"All services started in {setupTime.TotalSeconds:F2}s");

        // Start gateway
        System.Console.WriteLine("Starting gateway...");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{firstServiceIp}:7946")
            .WithPortBinding(8499, 8080)
            .WithPortBinding(8199, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for cluster to stabilize and config to propagate
        // With many services, need more time for all to join and be discovered
        System.Console.WriteLine("Waiting for cluster stabilization...");
        await Task.Delay(TimeSpan.FromSeconds(20));

        var totalSetupTime = stopwatch.Elapsed;
        System.Console.WriteLine($"Total setup time: {totalSetupTime.TotalSeconds:F2}s");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Test each service
        var results = new Dictionary<string, (int success, int failed, double avgMs)>();
        var requestsPerService = 10;

        stopwatch.Restart();

        for (int serviceIdx = 0; serviceIdx < serviceCount; serviceIdx++)
        {
            var serviceName = $"service-{serviceIdx}";
            var successCount = 0;
            var failCount = 0;
            var responseTimes = new List<double>();

            for (int reqIdx = 0; reqIdx < requestsPerService; reqIdx++)
            {
                var reqStopwatch = Stopwatch.StartNew();
                try
                {
                    var response = await httpClient.GetAsync($"{gatewayUrl}/{serviceName}/test");
                    reqStopwatch.Stop();
                    responseTimes.Add(reqStopwatch.Elapsed.TotalMilliseconds);

                    if (response.IsSuccessStatusCode)
                    {
                        successCount++;
                        var content = await response.Content.ReadAsStringAsync();
                        // Verify it's from the correct service
                        content.Should().Contain(serviceName, $"response should be from {serviceName}");
                    }
                    else
                    {
                        failCount++;
                        System.Console.WriteLine($"{serviceName} request failed: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    reqStopwatch.Stop();
                    failCount++;
                    System.Console.WriteLine($"{serviceName} request error: {ex.Message}");
                }
            }

            var avgResponseTime = responseTimes.Any() ? responseTimes.Average() : 0;
            results[serviceName] = (successCount, failCount, avgResponseTime);
        }

        var testTime = stopwatch.Elapsed;
        System.Console.WriteLine($"\nTest execution time: {testTime.TotalSeconds:F2}s");

        // Assert - All services should be reachable
        System.Console.WriteLine("\n=== Results ===");
        foreach (var (service, (success, failed, avgMs)) in results)
        {
            System.Console.WriteLine($"{service}: {success}/{requestsPerService} success, avg {avgMs:F2}ms");
            
            success.Should().BeGreaterThan(0, $"{service} should have at least some successful requests");
            
            // At least 70% success rate (allowing for some timing issues during startup)
            var successRate = (double)success / requestsPerService;
            successRate.Should().BeGreaterThan(0.7, $"{service} should have >70% success rate");
        }

        // Check gateway health
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK, "gateway should be healthy");

        System.Console.WriteLine($"\nTotal services: {serviceCount}");
        System.Console.WriteLine($"Total instances: {serviceCount * instancesPerService}");
        System.Console.WriteLine($"Total requests: {serviceCount * requestsPerService}");
    }

    [Fact(Timeout = 180000)] // 3 minutes timeout
    public async Task LargeConfigPerService_ShouldHandleEfficiently()
    {
        // Arrange - Create a service with many routes
        var routeCount = 50; // 50 routes per service (reasonable but substantial)
        var routes = new List<object>();
        var clusters = new List<object>();

        System.Console.WriteLine($"Creating service with {routeCount} routes...");

        for (int i = 0; i < routeCount; i++)
        {
            routes.Add(new
            {
                RouteId = $"large-route-{i}",
                ClusterId = $"large-cluster-{i}",
                Match = new { Path = $"/api/endpoint{i}/{{**catch-all}}" }
            });

            clusters.Add(new
            {
                ClusterId = $"large-cluster-{i}",
                LoadBalancingPolicy = "RoundRobin"
            });
        }

        var largeConfig = JsonSerializer.Serialize(new
        {
            Routes = routes,
            Clusters = clusters
        });

        System.Console.WriteLine($"Config size: {largeConfig.Length} bytes ({largeConfig.Length / 1024}KB)");

        // Start service with large config
        var serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("large-config-service")
            .WithEnvironment("SERVICE_NAME", "large-cluster-0")
            .WithEnvironment("INSTANCE_ID", "large-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "large-config-service")
            .WithEnvironment("YARP_CONFIG_JSON", largeConfig)
            .WithPortBinding(8600, 8080)
            .WithPortBinding(8300, 7946)
            .Build();

        var stopwatch = Stopwatch.StartNew();
        await serviceContainer.StartAsync();
        _serviceContainers.Add(serviceContainer);
        var serviceIp = serviceContainer.IpAddress;

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(8601, 8080)
            .WithPortBinding(8301, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for config to load
        System.Console.WriteLine("Waiting for large config to load...");
        await Task.Delay(TimeSpan.FromSeconds(10));

        var setupTime = stopwatch.Elapsed;
        System.Console.WriteLine($"Setup completed in {setupTime.TotalSeconds:F2}s");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Test random routes
        var testRouteCount = Math.Min(20, routeCount); // Test 20 random routes
        var random = new Random(42); // Fixed seed for reproducibility
        var routesToTest = Enumerable.Range(0, routeCount)
            .OrderBy(_ => random.Next())
            .Take(testRouteCount)
            .ToList();

        System.Console.WriteLine($"Testing {testRouteCount} random routes...");

        stopwatch.Restart();
        var successCount = 0;
        var failCount = 0;
        var responseTimes = new List<double>();

        foreach (var routeIdx in routesToTest)
        {
            var reqStopwatch = Stopwatch.StartNew();
            try
            {
                var response = await httpClient.GetAsync($"{gatewayUrl}/api/endpoint{routeIdx}/test");
                reqStopwatch.Stop();
                responseTimes.Add(reqStopwatch.Elapsed.TotalMilliseconds);

                if (response.IsSuccessStatusCode)
                {
                    successCount++;
                }
                else
                {
                    failCount++;
                    System.Console.WriteLine($"Route {routeIdx} failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                reqStopwatch.Stop();
                failCount++;
                System.Console.WriteLine($"Route {routeIdx} error: {ex.Message}");
            }
        }

        var testTime = stopwatch.Elapsed;

        // Assert - Routes should work
        System.Console.WriteLine($"\n=== Performance Results ===");
        System.Console.WriteLine($"Routes tested: {testRouteCount}");
        System.Console.WriteLine($"Success: {successCount}");
        System.Console.WriteLine($"Failed: {failCount}");
        System.Console.WriteLine($"Test time: {testTime.TotalSeconds:F2}s");
        
        if (responseTimes.Any())
        {
            System.Console.WriteLine($"Avg response time: {responseTimes.Average():F2}ms");
            System.Console.WriteLine($"Min response time: {responseTimes.Min():F2}ms");
            System.Console.WriteLine($"Max response time: {responseTimes.Max():F2}ms");
            System.Console.WriteLine($"P95 response time: {responseTimes.OrderBy(x => x).ElementAt((int)(responseTimes.Count * 0.95)):F2}ms");
        }

        // At least 70% of routes should work (allowing for some edge cases)
        var successRate = (double)successCount / testRouteCount;
        successRate.Should().BeGreaterThan(0.7, "at least 70% of routes should work");

        // Gateway should remain healthy
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK, "gateway should remain healthy with large config");

        // Check debug endpoint to verify config loaded
        var debugResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        debugResponse.StatusCode.Should().Be(HttpStatusCode.OK, "debug endpoint should work");
        
        var debugContent = await debugResponse.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Config response size: {debugContent.Length} bytes ({debugContent.Length / 1024}KB)");

        // Response times should be reasonable (< 5 seconds on average)
        if (responseTimes.Any())
        {
            responseTimes.Average().Should().BeLessThan(5000, 
                "average response time should be reasonable even with large config");
        }
    }
}
