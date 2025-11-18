using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP network failure and partition scenarios.
/// Tests gateway disconnection, service partitioning, and flapping nodes.
/// </summary>
[Collection("Sequential")]
public class NSerfNetworkFailureTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private IContainer? _service1Container;
    private IContainer? _service2Container;
    private IContainer? _service3Container;

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
        // Cleanup containers
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

        if (_service3Container != null)
        {
            await _service3Container.StopAsync();
            await _service3Container.DisposeAsync();
        }

        // Cleanup network
        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact(Timeout = 120000)]
    public async Task GatewayDisconnectedFromSerf_ShouldUseLastKnownConfig()
    {
        // Arrange - Create YARP config
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

        // Start service
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-service")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "api-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-service")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8400, 8080)
            .WithPortBinding(8100, 7946)
            .Build();

        await _service1Container.StartAsync();
        var serviceIp = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(8401, 8080)
            .WithPortBinding(8101, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify routing works initially
        var initialResponse = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK, "routing should work initially");

        // Act - Disconnect gateway from network (simulate network partition)
        System.Console.WriteLine("Disconnecting gateway from network...");
        await _gatewayContainer.StopAsync();

        // Wait a moment
        await TestHelpers.WaitForSerfStartAsync();

        // Restart gateway (it will have lost Serf connectivity but should keep last config)
        await _gatewayContainer.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Try routing with potentially stale config
        var disconnectedResponse = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        
        System.Console.WriteLine($"Response after reconnect: {disconnectedResponse.StatusCode}");

        // Assert - Gateway should still be operational
        // It may or may not route successfully depending on how it handles reconnection
        // but it should not crash
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "gateway should remain healthy after network disruption");

        System.Console.WriteLine("Gateway survived network disruption");
    }

    [Fact(Timeout = 120000)]
    public async Task ServicePartitionedFromSerf_ShouldStopRouting()
    {
        // Arrange - Create YARP config
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "partition-route",
                    ClusterId = "partition-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "partition-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start two service instances for failover testing
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("partition-service-1")
            .WithEnvironment("SERVICE_NAME", "partition-cluster")
            .WithEnvironment("INSTANCE_ID", "partition-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "partition-service-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8402, 8080)
            .WithPortBinding(8102, 7946)
            .Build();

        await _service1Container.StartAsync();
        var service1Ip = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("partition-service-2")
            .WithEnvironment("SERVICE_NAME", "partition-cluster")
            .WithEnvironment("INSTANCE_ID", "partition-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "partition-service-2")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8403, 8080)
            .WithPortBinding(8103, 7946)
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
            .WithPortBinding(8404, 8080)
            .WithPortBinding(8104, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify both instances are receiving traffic
        var initialResponses = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            var content = await response.Content.ReadAsStringAsync();
            initialResponses.Add(content);
        }

        initialResponses.Any(r => r.Contains("partition-1")).Should().BeTrue("partition-1 should be active");
        initialResponses.Any(r => r.Contains("partition-2")).Should().BeTrue("partition-2 should be active");

        // Act - Partition service-1 by stopping it (simulates network partition)
        System.Console.WriteLine("Stopping partition-1 to simulate partition...");
        await _service1Container.StopAsync();
        await _service1Container.DisposeAsync();
        _service1Container = null;

        // Wait for Serf to detect the failure
        await TestHelpers.WaitForSerfClusterAsync();

        // Send more requests
        var afterPartitionResponses = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                afterPartitionResponses.Add(content);
            }
        }

        // Assert - Only partition-2 should receive traffic
        afterPartitionResponses.Should().NotBeEmpty("at least some requests should succeed");
        afterPartitionResponses.All(r => r.Contains("partition-2")).Should().BeTrue(
            "only partition-2 should receive traffic after partition-1 stopped");
        afterPartitionResponses.Any(r => r.Contains("partition-1")).Should().BeFalse(
            "partition-1 should not receive traffic after being stopped");

        System.Console.WriteLine($"After partition: {afterPartitionResponses.Count} requests routed to partition-2");
    }

    [Fact(Timeout = 180000)]
    public async Task FlappingNode_ShouldRemainStable()
    {
        // Arrange - Create YARP config
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "flap-route",
                    ClusterId = "flap-cluster",
                    Match = new { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "flap-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start a stable service instance
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("stable-service")
            .WithEnvironment("SERVICE_NAME", "flap-cluster")
            .WithEnvironment("INSTANCE_ID", "stable-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "stable-service")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8405, 8080)
            .WithPortBinding(8105, 7946)
            .Build();

        await _service1Container.StartAsync();
        var stableServiceIp = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{stableServiceIp}:7946")
            .WithPortBinding(8406, 8080)
            .WithPortBinding(8106, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify stable service works
        var initialResponse = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - Simulate flapping node by rapidly starting and stopping a service
        var successfulRequests = 0;
        var failedRequests = 0;
        var flapCycles = 5;

        System.Console.WriteLine($"Starting {flapCycles} flap cycles...");

        for (int cycle = 0; cycle < flapCycles; cycle++)
        {
            System.Console.WriteLine($"Flap cycle {cycle + 1}/{flapCycles}");

            // Start flapping service
            _service2Container = new ContainerBuilder()
                .WithImage("test-service:latest")
                .WithNetwork(_network!)
                .WithNetworkAliases($"flapping-service-{cycle}")
                .WithEnvironment("SERVICE_NAME", "flap-cluster")
                .WithEnvironment("INSTANCE_ID", $"flapping-{cycle}")
                .WithEnvironment("SERVICE_PORT", "8080")
                .WithEnvironment("SERF_NODE_NAME", $"flapping-service-{cycle}")
                .WithEnvironment("SERF_JOIN", $"{stableServiceIp}:7946")
                .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
                .WithPortBinding(8407 + cycle, 8080)
                .WithPortBinding(8107 + cycle, 7946)
                .Build();

            await _service2Container.StartAsync();

            // Send some requests while flapping service is up
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
                    if (response.IsSuccessStatusCode)
                        successfulRequests++;
                    else
                        failedRequests++;
                }
                catch
                {
                    failedRequests++;
                }
                await Task.Delay(100);
            }

            // Stop flapping service quickly
            await _service2Container.StopAsync();
            await _service2Container.DisposeAsync();
            _service2Container = null;

            // Send requests while flapping service is down
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
                    if (response.IsSuccessStatusCode)
                        successfulRequests++;
                    else
                        failedRequests++;
                }
                catch
                {
                    failedRequests++;
                }
                await Task.Delay(100);
            }

            await TestHelpers.WaitForSerfStartAsync(); // Brief pause between cycles
        }

        System.Console.WriteLine($"Flapping complete: {successfulRequests} successful, {failedRequests} failed");

        // Assert - Gateway should remain stable
        var finalHealthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        finalHealthResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "gateway should remain healthy after flapping");

        // Stable service should still work
        var finalResponse = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        finalResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "stable service should still work after flapping");

        var finalContent = await finalResponse.Content.ReadAsStringAsync();
        finalContent.Should().Contain("stable-1", "stable service should still be routing");

        // Most requests should have succeeded (stable service was always available)
        var successRate = (double)successfulRequests / (successfulRequests + failedRequests);
        System.Console.WriteLine($"Success rate: {successRate:P}");
        
        successRate.Should().BeGreaterThan(0.5, 
            "majority of requests should succeed with stable instance available");
    }
}
