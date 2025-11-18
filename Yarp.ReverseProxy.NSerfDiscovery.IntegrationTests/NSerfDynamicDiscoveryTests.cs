using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Tests for dynamic service discovery - services can start in any order.
/// In real world, there's no guaranteed launch sequence.
/// </summary>
[Collection("Sequential")]
public class NSerfDynamicDiscoveryTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private IContainer? _service1Container;
    private IContainer? _service2Container;

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

        if (_service1Container != null)
        {
            await _service1Container.StopAsync();
            await _service1Container.DisposeAsync();
        }

        if (_service2Container != null)
        {
            await _service2Container.StopAsync();
            await _service2Container.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact]
    public async Task GatewayStartsFirst_ThenServicesJoin_ShouldDiscoverDynamically()
    {
        // Arrange - Start gateway FIRST with no services
        Console.WriteLine("=== Starting gateway first (no services yet) ===");
        
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(9100, 8080)
            .WithPortBinding(9200, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;
        
        // Wait for gateway to fully start (needs more time than just Serf)
        await Task.Delay(TimeSpan.FromSeconds(3));

        var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify gateway has no routes yet
        var configResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        var configContent = await configResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Initial config: {configContent}");
        configContent.Should().Contain("\"routes\":[]", "gateway should start with no routes");

        // Act 1 - Start first service and join gateway's cluster
        Console.WriteLine("\n=== Starting service 1 and joining gateway ===");
        
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "dynamic-route",
                    ClusterId = "dynamic-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "dynamic-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-1")
            .WithEnvironment("SERVICE_NAME", "dynamic-cluster")
            .WithEnvironment("INSTANCE_ID", "service-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-1")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")  // Join gateway
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9101, 8080)
            .WithPortBinding(9201, 7946)
            .Build();

        await _service1Container.StartAsync();

        // Wait for service to join and be discovered
        Console.WriteLine("Waiting for gateway to discover service 1...");
        await TestHelpers.WaitForSerfClusterAsync();

        // Assert - Gateway should now have routes
        configResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        configContent = await configResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Config after service 1 joined: {configContent}");
        
        configContent.Should().Contain("dynamic-route", "gateway should discover service 1's route");
        configContent.Should().Contain("service-1:dynamic-cluster", "gateway should have service 1 as destination");

        // Verify routing works
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response1.StatusCode.Should().Be(HttpStatusCode.OK, "gateway should route to service 1");
        var content1 = await response1.Content.ReadAsStringAsync();
        content1.Should().Contain("service-1", "request should be routed to service 1");

        // Act 2 - Start second service and join
        Console.WriteLine("\n=== Starting service 2 and joining cluster ===");
        
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-2")
            .WithEnvironment("SERVICE_NAME", "dynamic-cluster")
            .WithEnvironment("INSTANCE_ID", "service-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-2")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")  // Join gateway
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9102, 8080)
            .WithPortBinding(9202, 7946)
            .Build();

        await _service2Container.StartAsync();

        // Wait for service to join and be discovered
        Console.WriteLine("Waiting for gateway to discover service 2...");
        await TestHelpers.WaitForSerfClusterAsync();

        // Assert - Gateway should now have both destinations
        configResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        configContent = await configResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Config after service 2 joined: {configContent}");
        
        configContent.Should().Contain("service-1:dynamic-cluster", "gateway should still have service 1");
        configContent.Should().Contain("service-2:dynamic-cluster", "gateway should now have service 2");

        // Verify load balancing across both services
        var service1Count = 0;
        var service2Count = 0;
        
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            var content = await response.Content.ReadAsStringAsync();
            
            if (content.Contains("service-1")) service1Count++;
            if (content.Contains("service-2")) service2Count++;
        }

        Console.WriteLine($"Load distribution: service-1={service1Count}, service-2={service2Count}");
        service1Count.Should().BeGreaterThan(0, "some requests should go to service 1");
        service2Count.Should().BeGreaterThan(0, "some requests should go to service 2");
    }

    [Fact]
    public async Task ServiceStartsFirst_ThenGatewayJoins_ShouldDiscoverImmediately()
    {
        // Arrange - Start service FIRST
        Console.WriteLine("=== Starting service first (no gateway yet) ===");
        
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "reverse-order-route",
                    ClusterId = "reverse-order-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "reverse-order-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-first")
            .WithEnvironment("SERVICE_NAME", "reverse-order-cluster")
            .WithEnvironment("INSTANCE_ID", "service-first")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-first")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9103, 8080)
            .WithPortBinding(9203, 7946)
            .Build();

        await _service1Container.StartAsync();
        var serviceIp = _service1Container.IpAddress;
        
        // Wait for service to start
        await TestHelpers.WaitForSerfStartAsync();

        // Act - Start gateway and join service's cluster
        Console.WriteLine("\n=== Starting gateway and joining service ===");
        
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway-late")
            .WithEnvironment("SERF_NODE_NAME", "gateway-late")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")  // Join service
            .WithPortBinding(9104, 8080)
            .WithPortBinding(9204, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for gateway to join and discover
        Console.WriteLine("Waiting for gateway to discover existing service...");
        await TestHelpers.WaitForSerfClusterAsync();

        // Assert - Gateway should immediately have the service's routes
        var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";
        
        var configResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        var configContent = await configResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Gateway config after joining: {configContent}");
        
        configContent.Should().Contain("reverse-order-route", "gateway should discover existing service's route");
        configContent.Should().Contain("service-first:reverse-order-cluster", "gateway should have service as destination");

        // Verify routing works
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "gateway should route to existing service");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("service-first", "request should be routed to service");
    }

    [Fact]
    public async Task ServiceLeavesCluster_GatewayShouldRemoveRoutes()
    {
        // Arrange - Start service then gateway
        Console.WriteLine("=== Starting service and gateway ===");
        
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "leave-test-route",
                    ClusterId = "leave-test-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "leave-test-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-leaving")
            .WithEnvironment("SERVICE_NAME", "leave-test-cluster")
            .WithEnvironment("INSTANCE_ID", "service-leaving")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-leaving")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9105, 8080)
            .WithPortBinding(9205, 7946)
            .Build();

        await _service1Container.StartAsync();
        var serviceIp = _service1Container.IpAddress;
        await Task.Delay(TimeSpan.FromSeconds(2));

        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway-watching")
            .WithEnvironment("SERF_NODE_NAME", "gateway-watching")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(9106, 8080)
            .WithPortBinding(9206, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify service is discovered
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response1.StatusCode.Should().Be(HttpStatusCode.OK, "service should be reachable initially");

        // Act - Stop service (simulates service leaving cluster)
        Console.WriteLine("\n=== Stopping service ===");
        await _service1Container.StopAsync();

        // Wait for gateway to detect service left
        Console.WriteLine("Waiting for gateway to detect service left...");
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - Gateway should remove routes/destinations
        var configResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        var configContent = await configResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Config after service left: {configContent}");
        
        // Routes might still exist but destinations should be empty or service should be removed
        var response2 = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response2.StatusCode.Should().Be(HttpStatusCode.NotFound, 
            "gateway should return 404 when no services available");
    }
}
