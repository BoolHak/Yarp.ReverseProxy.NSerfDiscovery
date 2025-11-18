using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for A/B deployment scenarios.
/// Tests gradual traffic shifting between versions using metadata-based routing.
/// </summary>
[Collection("Sequential")]
public class NSerfABDeploymentTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private IContainer? _versionAContainer;
    private IContainer? _versionBContainer;

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

        if (_versionAContainer != null)
        {
            await _versionAContainer.StopAsync();
            await _versionAContainer.DisposeAsync();
        }

        if (_versionBContainer != null)
        {
            await _versionBContainer.StopAsync();
            await _versionBContainer.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ABDeployment_VersionAOnly_AllTrafficToVersionA()
    {
        // Arrange - Start gateway first
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9300, 8080)
            .WithPortBinding(9400, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Start Version A
        Console.WriteLine("=== Deploying Version A ===");
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
                    LoadBalancingPolicy = "RoundRobin",
                    Metadata = new Dictionary<string, string>
                    {
                        { "version", "A" }
                    }
                }
            }
        });

        _versionAContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v-a")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v1.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v1.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9301, 8080)
            .WithPortBinding(9401, 7946)
            .Build();

        await _versionAContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        // Act - Send requests
        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        var versionACount = 0;
        for (int i = 0; i < 20; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var content = await response.Content.ReadAsStringAsync();
            if (content.Contains("api-v1.0"))
            {
                versionACount++;
            }
        }

        // Assert - All traffic should go to Version A
        Console.WriteLine($"Version A received: {versionACount}/20 requests");
        versionACount.Should().Be(20, "all traffic should go to Version A");
    }

    [Fact(Timeout = 120000)]
    public async Task ABDeployment_BothVersions_TrafficDistributed()
    {
        // Arrange - Start gateway
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9310, 8080)
            .WithPortBinding(9410, 7946)
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

        // Deploy Version A
        Console.WriteLine("=== Deploying Version A ===");
        _versionAContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v-a")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v1.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v1.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9311, 8080)
            .WithPortBinding(9411, 7946)
            .Build();

        await _versionAContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        // Deploy Version B (A/B deployment in progress)
        Console.WriteLine("=== Deploying Version B (A/B test) ===");
        _versionBContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v-b")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v2.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v2.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9312, 8080)
            .WithPortBinding(9412, 7946)
            .Build();

        await _versionBContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        // Act - Send requests
        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        var versionACount = 0;
        var versionBCount = 0;

        for (int i = 0; i < 50; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var content = await response.Content.ReadAsStringAsync();
            if (content.Contains("api-v1.0"))
            {
                versionACount++;
            }
            else if (content.Contains("api-v2.0"))
            {
                versionBCount++;
            }
        }

        // Assert - Traffic should be distributed between both versions
        Console.WriteLine($"Version A: {versionACount}/50, Version B: {versionBCount}/50");
        versionACount.Should().BeGreaterThan(0, "Version A should receive some traffic");
        versionBCount.Should().BeGreaterThan(0, "Version B should receive some traffic");
        
        // With round-robin, should be roughly 50/50
        var distributionDiff = Math.Abs(versionACount - versionBCount);
        distributionDiff.Should().BeLessThan(15, "traffic should be roughly balanced");
    }

    [Fact(Timeout = 120000)]
    public async Task ABDeployment_VersionBRollback_TrafficBackToVersionA()
    {
        // Arrange - Start gateway and both versions
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9320, 8080)
            .WithPortBinding(9420, 7946)
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

        // Deploy both versions
        Console.WriteLine("=== Deploying Version A ===");
        _versionAContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v-a")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v1.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v1.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9321, 8080)
            .WithPortBinding(9421, 7946)
            .Build();

        await _versionAContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        Console.WriteLine("=== Deploying Version B ===");
        _versionBContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v-b")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v2.0")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v2.0")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9322, 8080)
            .WithPortBinding(9422, 7946)
            .Build();

        await _versionBContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify both versions are serving traffic
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - Rollback Version B (stop it)
        Console.WriteLine("=== Rolling back Version B ===");
        await _versionBContainer.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(5)); // Wait for gateway to detect

        // Send requests after rollback
        var versionACount = 0;
        var versionBCount = 0;

        for (int i = 0; i < 20; i++)
        {
            response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var content = await response.Content.ReadAsStringAsync();
            if (content.Contains("api-v1.0"))
            {
                versionACount++;
            }
            else if (content.Contains("api-v2.0"))
            {
                versionBCount++;
            }
        }

        // Assert - All traffic should go back to Version A
        Console.WriteLine($"After rollback - Version A: {versionACount}/20, Version B: {versionBCount}/20");
        versionACount.Should().Be(20, "all traffic should go to Version A after rollback");
        versionBCount.Should().Be(0, "no traffic should go to Version B after rollback");
    }

    [Fact(Timeout = 120000)]
    public async Task ABDeployment_CanaryRelease_SmallPercentageToNewVersion()
    {
        // Arrange - Start gateway
        Console.WriteLine("=== Starting gateway ===");
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9330, 8080)
            .WithPortBinding(9430, 7946)
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

        // Deploy 4 instances of Version A (80% capacity)
        Console.WriteLine("=== Deploying 4 instances of Version A ===");
        var versionAContainers = new List<IContainer>();
        
        for (int i = 0; i < 4; i++)
        {
            var container = new ContainerBuilder()
                .WithImage("test-service:latest")
                .WithNetwork(_network!)
                .WithNetworkAliases($"api-v-a-{i}")
                .WithEnvironment("SERVICE_NAME", "api-cluster")
                .WithEnvironment("INSTANCE_ID", $"api-v1.0-{i}")
                .WithEnvironment("SERVICE_PORT", "8080")
                .WithEnvironment("SERF_NODE_NAME", $"api-v1.0-{i}")
                .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
                .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                .WithPortBinding(9331 + i, 8080)
                .WithPortBinding(9431 + i, 7946)
                .Build();

            await container.StartAsync();
            versionAContainers.Add(container);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        await TestHelpers.WaitForSerfClusterAsync();

        // Deploy 1 instance of Version B (20% capacity - canary)
        Console.WriteLine("=== Deploying 1 canary instance of Version B ===");
        _versionBContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-v-b-canary")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-v2.0-canary")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-v2.0-canary")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9340, 8080)
            .WithPortBinding(9440, 7946)
            .Build();

        await _versionBContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        // Act - Send requests
        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        var versionACount = 0;
        var versionBCount = 0;

        for (int i = 0; i < 100; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var content = await response.Content.ReadAsStringAsync();
            if (content.Contains("api-v1.0"))
            {
                versionACount++;
            }
            else if (content.Contains("api-v2.0"))
            {
                versionBCount++;
            }
        }

        // Assert - ~80% to Version A, ~20% to Version B (canary)
        Console.WriteLine($"Canary deployment - Version A: {versionACount}/100, Version B: {versionBCount}/100");
        
        versionACount.Should().BeGreaterThan(60, "Version A should receive majority of traffic");
        versionBCount.Should().BeGreaterThan(10, "Version B canary should receive some traffic");
        versionBCount.Should().BeLessThan(40, "Version B canary should receive minority of traffic");

        // Cleanup version A containers
        foreach (var container in versionAContainers)
        {
            await container.StopAsync();
            await container.DisposeAsync();
        }
    }
}
