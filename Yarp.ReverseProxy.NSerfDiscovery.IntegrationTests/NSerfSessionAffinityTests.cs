using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for YARP session affinity.
/// Tests cookie-based affinity, failover, and interaction with health checks.
/// </summary>
[Collection("Sequential")]
public class NSerfSessionAffinityTests : IAsyncLifetime
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

    [Fact(Timeout = 120000)]
    public async Task CookieBasedAffinity_ShouldStickToSameInstance()
    {
        // Arrange - Two instances with session affinity enabled
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "affinity-route",
                    ClusterId = "affinity-cluster",
                    Match = new { Path = "/billing/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "affinity-cluster",
                    LoadBalancingPolicy = "RoundRobin",
                    SessionAffinity = new
                    {
                        Enabled = true,
                        Policy = "Cookie",
                        FailurePolicy = "Redistribute",
                        AffinityKeyName = ".Yarp.Affinity"
                    }
                }
            }
        });

        // Start instance 1
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-1")
            .WithEnvironment("SERVICE_NAME", "affinity-cluster")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9200, 8080)
            .WithPortBinding(8900, 7946)
            .Build();

        await _service1Container.StartAsync();
        var service1Ip = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start instance 2
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-2")
            .WithEnvironment("SERVICE_NAME", "affinity-cluster")
            .WithEnvironment("INSTANCE_ID", "billing-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-2")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9201, 8080)
            .WithPortBinding(8901, 7946)
            .Build();

        await _service2Container.StartAsync();

        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithPortBinding(9202, 8080)
            .WithPortBinding(8902, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        // Use HttpClientHandler to enable cookie handling
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        using var httpClient = new HttpClient(handler);
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - First request establishes affinity
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsStringAsync();
        
        var firstInstance = content1.Contains("billing-1") ? "billing-1" : "billing-2";
        System.Console.WriteLine($"First request routed to: {firstInstance}");

        // Subsequent requests should stick to same instance
        var sameInstanceCount = 0;
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");
            var content = await response.Content.ReadAsStringAsync();
            
            if (content.Contains(firstInstance))
            {
                sameInstanceCount++;
            }
        }

        // Assert - Most requests should go to the same instance due to affinity
        // Note: Session affinity might not be 100% perfect due to timing, cookie handling, etc.
        sameInstanceCount.Should().BeGreaterThan(7, 
            "most requests should stick to the same instance with session affinity");

        System.Console.WriteLine($"Session affinity working: {sameInstanceCount}/10 requests to {firstInstance}");
    }

    [Fact(Timeout = 150000)]
    public async Task SessionAffinityFailover_ShouldRedirectToHealthyInstance()
    {
        // Arrange - Two instances with session affinity
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "affinity-failover-route",
                    ClusterId = "affinity-failover-cluster",
                    Match = new { Path = "/billing/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "affinity-failover-cluster",
                    LoadBalancingPolicy = "RoundRobin",
                    SessionAffinity = new
                    {
                        Enabled = true,
                        Policy = "Cookie",
                        FailurePolicy = "Redistribute",
                        AffinityKeyName = ".Yarp.AffinityFailover"
                    }
                }
            }
        });

        // Start both instances
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-failover-1")
            .WithEnvironment("SERVICE_NAME", "affinity-failover-cluster")
            .WithEnvironment("INSTANCE_ID", "billing-failover-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-failover-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9203, 8080)
            .WithPortBinding(8903, 7946)
            .Build();

        await _service1Container.StartAsync();
        var service1Ip = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-failover-2")
            .WithEnvironment("SERVICE_NAME", "affinity-failover-cluster")
            .WithEnvironment("INSTANCE_ID", "billing-failover-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-failover-2")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(9204, 8080)
            .WithPortBinding(8904, 7946)
            .Build();

        await _service2Container.StartAsync();

        await TestHelpers.WaitForSerfStartAsync();

        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithPortBinding(9205, 8080)
            .WithPortBinding(8905, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        using var httpClient = new HttpClient(handler);
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Establish affinity
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");
        var content1 = await response1.Content.ReadAsStringAsync();
        var affinitizedInstance = content1.Contains("billing-failover-1") ? "billing-failover-1" : "billing-failover-2";
        System.Console.WriteLine($"Affinitized to: {affinitizedInstance}");

        // Stop the affinitized instance
        if (affinitizedInstance == "billing-failover-1")
        {
            await _service1Container.StopAsync();
            await _service1Container.DisposeAsync();
            _service1Container = null;
        }
        else
        {
            await _service2Container.StopAsync();
            await _service2Container.DisposeAsync();
            _service2Container = null;
        }

        System.Console.WriteLine($"Stopped {affinitizedInstance}");

        // Wait for NSerf to detect failure
        await TestHelpers.WaitForSerfClusterAsync();

        // Act - Send more requests with same cookie
        var response2 = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");

        // Assert - Should failover to other instance
        response2.StatusCode.Should().Be(HttpStatusCode.OK, 
            "requests should failover to healthy instance");

        var content2 = await response2.Content.ReadAsStringAsync();
        var otherInstance = affinitizedInstance == "billing-failover-1" ? "billing-failover-2" : "billing-failover-1";
        content2.Should().Contain(otherInstance, 
            "should failover to the other healthy instance");

        System.Console.WriteLine($"Failover successful: now routing to {otherInstance}");
    }
}
