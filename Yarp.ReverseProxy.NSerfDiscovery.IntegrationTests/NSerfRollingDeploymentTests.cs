using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for rolling deployment scenarios.
/// Tests zero-downtime deployments with gradual instance replacement.
/// </summary>
[Collection("Sequential")]
public class NSerfRollingDeploymentTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private readonly List<IContainer> _serviceContainers = new();

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder()
            .WithName($"test-network-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_gatewayContainer != null)
        {
            await _gatewayContainer.StopAsync();
            await _gatewayContainer.DisposeAsync();
        }

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

        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact(Timeout = 180000)]
    public async Task RollingDeployment_ReplaceInstancesOneByOne_ZeroDowntime()
    {
        // Arrange - Start gateway
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9500, 8080)
            .WithPortBinding(9600, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;
        await Task.Delay(TimeSpan.FromSeconds(3));

        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "api-route",
                    ClusterId = "api-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "api-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Deploy 3 instances of Version 1.0
        Console.WriteLine("=== Deploying 3 instances of Version 1.0 ===");
        for (int i = 0; i < 3; i++)
        {
            var container = new ContainerBuilder()
                .WithImage("test-service:latest")
                .WithNetwork(_network!)
                .WithNetworkAliases($"api-v1-{i}")
                .WithEnvironment("SERVICE_NAME", "api-cluster")
                .WithEnvironment("INSTANCE_ID", $"api-v1.0-instance-{i}")
                .WithEnvironment("SERVICE_PORT", "8080")
                .WithEnvironment("SERF_NODE_NAME", $"api-v1.0-{i}")
                .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
                .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                .WithPortBinding(9501 + i, 8080)
                .WithPortBinding(9601 + i, 7946)
                .Build();

            await container.StartAsync();
            _serviceContainers.Add(container);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify all v1.0 instances are serving
        var v1Instances = new HashSet<string>();
        for (int i = 0; i < 15; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            
            if (content.Contains("api-v1.0-instance-"))
            {
                var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
                v1Instances.Add(instanceId);
            }
        }
        
        Console.WriteLine($"Initial deployment: {v1Instances.Count} v1.0 instances discovered");
        v1Instances.Count.Should().Be(3, "should discover all 3 v1.0 instances");

        // Act - Perform rolling deployment (replace instances one by one)
        Console.WriteLine("\n=== Starting rolling deployment to Version 2.0 ===");
        var failedRequests = 0;
        var totalRequests = 0;
        var v2Instances = new HashSet<string>();

        for (int i = 0; i < 3; i++)
        {
            Console.WriteLine($"\n--- Rolling update step {i + 1}/3 ---");
            
            // Stop old instance
            Console.WriteLine($"Stopping v1.0 instance {i}...");
            await _serviceContainers[i].StopAsync();
            
            // Small delay to let Serf detect the leave
            await Task.Delay(TimeSpan.FromSeconds(2));
            
            // Start new instance with v2.0
            Console.WriteLine($"Starting v2.0 instance {i}...");
            var newContainer = new ContainerBuilder()
                .WithImage("test-service:latest")
                .WithNetwork(_network!)
                .WithNetworkAliases($"api-v2-{i}")
                .WithEnvironment("SERVICE_NAME", "api-cluster")
                .WithEnvironment("INSTANCE_ID", $"api-v2.0-instance-{i}")
                .WithEnvironment("SERVICE_PORT", "8080")
                .WithEnvironment("SERF_NODE_NAME", $"api-v2.0-{i}")
                .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
                .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                .WithPortBinding(9510 + i, 8080)
                .WithPortBinding(9610 + i, 7946)
                .Build();

            await newContainer.StartAsync();
            _serviceContainers[i] = newContainer;
            
            // Wait for new instance to join and be discovered
            await TestHelpers.WaitForSerfClusterAsync();
            
            // Send requests during rolling update to verify zero downtime
            Console.WriteLine("Testing traffic during rolling update...");
            for (int req = 0; req < 10; req++)
            {
                totalRequests++;
                try
                {
                    var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
                    if (!response.IsSuccessStatusCode)
                    {
                        failedRequests++;
                        Console.WriteLine($"Request failed: {response.StatusCode}");
                    }
                    else
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        if (content.Contains("api-v2.0-instance-"))
                        {
                            var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
                            v2Instances.Add(instanceId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedRequests++;
                    Console.WriteLine($"Request error: {ex.Message}");
                }
            }
        }

        Console.WriteLine("\n=== Rolling deployment complete ===");
        Console.WriteLine($"Total requests during rollout: {totalRequests}");
        Console.WriteLine($"Failed requests: {failedRequests}");
        Console.WriteLine($"Success rate: {((totalRequests - failedRequests) / (double)totalRequests * 100):F1}%");
        Console.WriteLine($"v2.0 instances discovered: {v2Instances.Count}");

        // Assert - Zero downtime (high success rate)
        var successRate = (totalRequests - failedRequests) / (double)totalRequests;
        successRate.Should().BeGreaterThan(0.95, "should maintain >95% success rate during rolling deployment");
        
        // Verify all v2.0 instances are now serving
        await Task.Delay(TimeSpan.FromSeconds(2));
        var finalV2Instances = new HashSet<string>();
        
        for (int i = 0; i < 20; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            
            if (content.Contains("api-v2.0-instance-"))
            {
                var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
                finalV2Instances.Add(instanceId);
            }
        }
        
        Console.WriteLine($"Final deployment: {finalV2Instances.Count} v2.0 instances serving traffic");
        finalV2Instances.Count.Should().Be(3, "all 3 instances should be v2.0 after rolling deployment");
    }

    [Fact(Timeout = 120000)]
    public async Task RollingDeployment_ScaleUpThenReplaceInstances_MaintainsCapacity()
    {
        // Arrange - Start gateway
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9520, 8080)
            .WithPortBinding(9620, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;
        await Task.Delay(TimeSpan.FromSeconds(3));

        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "api-route",
                    ClusterId = "api-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "api-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Deploy 2 instances of Version 1.0
        Console.WriteLine("=== Deploying 2 instances of Version 1.0 ===");
        for (int i = 0; i < 2; i++)
        {
            var container = new ContainerBuilder()
                .WithImage("test-service:latest")
                .WithNetwork(_network!)
                .WithNetworkAliases($"api-v1-{i}")
                .WithEnvironment("SERVICE_NAME", "api-cluster")
                .WithEnvironment("INSTANCE_ID", $"api-v1.0-instance-{i}")
                .WithEnvironment("SERVICE_PORT", "8080")
                .WithEnvironment("SERF_NODE_NAME", $"api-v1.0-{i}")
                .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
                .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                .WithPortBinding(9521 + i, 8080)
                .WithPortBinding(9621 + i, 7946)
                .Build();

            await container.StartAsync();
            _serviceContainers.Add(container);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Scale up with v2.0 instances first (blue-green style)
        Console.WriteLine("\n=== Scaling up with 2 v2.0 instances ===");
        for (int i = 0; i < 2; i++)
        {
            var container = new ContainerBuilder()
                .WithImage("test-service:latest")
                .WithNetwork(_network!)
                .WithNetworkAliases($"api-v2-{i}")
                .WithEnvironment("SERVICE_NAME", "api-cluster")
                .WithEnvironment("INSTANCE_ID", $"api-v2.0-instance-{i}")
                .WithEnvironment("SERVICE_PORT", "8080")
                .WithEnvironment("SERF_NODE_NAME", $"api-v2.0-{i}")
                .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
                .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                .WithPortBinding(9530 + i, 8080)
                .WithPortBinding(9630 + i, 7946)
                .Build();

            await container.StartAsync();
            _serviceContainers.Add(container);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        await TestHelpers.WaitForSerfClusterAsync();

        // Verify we now have 4 instances (2 v1.0 + 2 v2.0)
        var instances = new HashSet<string>();
        for (int i = 0; i < 20; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            
            var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
            instances.Add(instanceId);
        }
        
        Console.WriteLine($"After scale-up: {instances.Count} total instances");
        instances.Count.Should().Be(4, "should have 4 instances after scale-up");

        // Scale down v1.0 instances
        Console.WriteLine("\n=== Scaling down v1.0 instances ===");
        for (int i = 0; i < 2; i++)
        {
            Console.WriteLine($"Stopping v1.0 instance {i}...");
            await _serviceContainers[i].StopAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        // Verify only v2.0 instances remain
        await Task.Delay(TimeSpan.FromSeconds(2));
        var finalInstances = new HashSet<string>();
        var v1Count = 0;
        var v2Count = 0;
        
        for (int i = 0; i < 20; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            
            if (content.Contains("api-v1.0"))
            {
                v1Count++;
            }
            else if (content.Contains("api-v2.0"))
            {
                v2Count++;
            }
            
            var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
            finalInstances.Add(instanceId);
        }
        
        Console.WriteLine($"Final deployment: {finalInstances.Count} instances (v1.0: {v1Count}, v2.0: {v2Count})");
        finalInstances.Count.Should().Be(2, "should have 2 instances after scale-down");
        v1Count.Should().Be(0, "no v1.0 instances should remain");
        v2Count.Should().Be(20, "all traffic should go to v2.0 instances");
    }

    [Fact(Timeout = 120000)]
    public async Task RollingDeployment_WithHealthChecks_WaitsForHealthy()
    {
        // Arrange - Start gateway
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9540, 8080)
            .WithPortBinding(9640, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;
        await Task.Delay(TimeSpan.FromSeconds(3));

        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "api-route",
                    ClusterId = "api-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "api-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Deploy initial instance
        Console.WriteLine("=== Deploying initial instance ===");
        var container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v1")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v1.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v1.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9541, 8080)
            .WithPortBinding(9641, 7946)
            .Build();

        await container.StartAsync();
        _serviceContainers.Add(container);
        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify initial instance is healthy
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - Deploy new instance and wait for health check
        Console.WriteLine("\n=== Deploying new instance with health check ===");
        var newContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v2")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v2.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v2.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9542, 8080)
            .WithPortBinding(9642, 7946)
            .Build();

        await newContainer.StartAsync();
        _serviceContainers.Add(newContainer);

        // Wait for new instance to be healthy
        var serviceUrl = $"http://{newContainer.Hostname}:{newContainer.GetMappedPublicPort(8080)}";
        var healthy = await TestHelpers.WaitForServiceHealthyAsync(serviceUrl, maxWaitSeconds: 10);
        healthy.Should().BeTrue("new instance should become healthy");

        await TestHelpers.WaitForSerfClusterAsync();

        // Verify both instances are serving
        var instances = new HashSet<string>();
        for (int i = 0; i < 10; i++)
        {
            response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            
            var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
            instances.Add(instanceId);
        }
        
        Console.WriteLine($"Instances serving traffic: {string.Join(", ", instances)}");
        instances.Count.Should().Be(2, "both instances should be serving traffic");

        // Remove old instance
        Console.WriteLine("\n=== Removing old instance ===");
        await _serviceContainers[0].StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Verify only new instance remains
        instances.Clear();
        for (int i = 0; i < 10; i++)
        {
            response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            
            var instanceId = content.Split("instance\":\"")[1].Split("\"")[0];
            instances.Add(instanceId);
        }
        
        Console.WriteLine($"Final instance: {string.Join(", ", instances)}");
        instances.Count.Should().Be(1, "only new instance should remain");
        instances.Should().Contain("api-v2.0", "new instance should be serving all traffic");
    }
}
