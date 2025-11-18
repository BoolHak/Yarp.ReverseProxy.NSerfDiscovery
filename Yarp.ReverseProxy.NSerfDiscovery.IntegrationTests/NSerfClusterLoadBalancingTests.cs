using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP multi-instance service clusters and load balancing.
/// Tests multiple service instances, failover, and rolling restarts.
/// </summary>
[Collection("Sequential")]
public class NSerfClusterLoadBalancingTests : IAsyncLifetime
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
    public async Task TwoInstancesOfSameService_ShouldLoadBalance()
    {
        // Arrange - Create YARP config for billing service
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "billing-route",
                    ClusterId = "billing-api",
                    Match = new { Path = "/billing/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "billing-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start first service instance
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service-1")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8220, 8080)
            .WithPortBinding(7990, 7946)
            .Build();

        await _service1Container.StartAsync();
        var service1Ip = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start second service instance (joins first)
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service-2")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-2")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8221, 8080)
            .WithPortBinding(7991, 7946)
            .Build();

        await _service2Container.StartAsync();

        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway (joins cluster)
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithPortBinding(8222, 8080)
            .WithPortBinding(7992, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for cluster to stabilize
        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Send 20 requests
        var responses = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var content = await response.Content.ReadAsStringAsync();
            responses.Add(content);
        }

        // Assert - Both instances should have received requests
        var instance1Hits = responses.Count(r => r.Contains("billing-1"));
        var instance2Hits = responses.Count(r => r.Contains("billing-2"));

        instance1Hits.Should().BeGreaterThan(0, "billing-1 should receive some requests");
        instance2Hits.Should().BeGreaterThan(0, "billing-2 should receive some requests");
        (instance1Hits + instance2Hits).Should().Be(20, "all requests should be served");

        System.Console.WriteLine($"Load balancing results: billing-1={instance1Hits}, billing-2={instance2Hits}");
    }

    [Fact(Timeout = 120000)]
    public async Task OneInstanceStops_RemainingInstanceStillUsed()
    {
        // Arrange - Create YARP config
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "billing-route",
                    ClusterId = "billing-api",
                    Match = new { Path = "/billing/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "billing-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start first service instance
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service-1")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8223, 8080)
            .WithPortBinding(7993, 7946)
            .Build();

        await _service1Container.StartAsync();
        var service1Ip = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start second service instance
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service-2")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-2")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8224, 8080)
            .WithPortBinding(7994, 7946)
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
            .WithPortBinding(8225, 8080)
            .WithPortBinding(7995, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify both instances are receiving traffic
        var initialResponses = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");
            var content = await response.Content.ReadAsStringAsync();
            initialResponses.Add(content);
        }

        initialResponses.Any(r => r.Contains("billing-1")).Should().BeTrue("billing-1 should be active");
        initialResponses.Any(r => r.Contains("billing-2")).Should().BeTrue("billing-2 should be active");

        // Act - Stop one instance
        await _service2Container.StopAsync();
        await _service2Container.DisposeAsync();
        _service2Container = null;

        // Wait for NSerf to detect the failure and update config
        await TestHelpers.WaitForSerfClusterAsync();

        // Send new requests
        var afterStopResponses = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");
            response.StatusCode.Should().Be(HttpStatusCode.OK, 
                "requests should still succeed with one instance");
            
            var content = await response.Content.ReadAsStringAsync();
            afterStopResponses.Add(content);
        }

        // Assert - Only billing-1 should receive requests
        afterStopResponses.All(r => r.Contains("billing-1")).Should().BeTrue(
            "all requests should go to billing-1 after billing-2 stopped");
        afterStopResponses.Any(r => r.Contains("billing-2")).Should().BeFalse(
            "no requests should go to stopped billing-2");

        System.Console.WriteLine($"After stop: billing-1 received {afterStopResponses.Count} requests");
    }

    [Fact(Timeout = 180000)]
    public async Task RollingRestart_ServiceAlwaysAvailable()
    {
        // Arrange - Create YARP config
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
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

        // Start instance A
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-service-a")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "instance-a")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "instance-a")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8226, 8080)
            .WithPortBinding(7996, 7946)
            .Build();

        await _service1Container.StartAsync();
        var serviceAIp = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start instance B
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-service-b")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "instance-b")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "instance-b")
            .WithEnvironment("SERF_JOIN", $"{serviceAIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8227, 8080)
            .WithPortBinding(7997, 7946)
            .Build();

        await _service2Container.StartAsync();

        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceAIp}:7946")
            .WithPortBinding(8228, 8080)
            .WithPortBinding(7998, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        var allResponses = new List<(string instance, bool success)>();

        // Helper to send requests
        async Task<List<(string, bool)>> SendRequests(int count)
        {
            var results = new List<(string, bool)>();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var instance = content.Contains("instance-a") ? "instance-a" :
                                     content.Contains("instance-b") ? "instance-b" :
                                     content.Contains("instance-c") ? "instance-c" : "unknown";
                        results.Add((instance, true));
                    }
                    else
                    {
                        results.Add(("none", false));
                    }
                }
                catch
                {
                    results.Add(("error", false));
                }
                await Task.Delay(100); // Small delay between requests
            }
            return results;
        }

        // Step 1: Verify A and B are both serving
        var step1 = await SendRequests(10);
        step1.Should().OnlyContain(r => r.Item2, "all requests should succeed initially");
        step1.Any(r => r.Item1 == "instance-a").Should().BeTrue("instance-a should be active");
        step1.Any(r => r.Item1 == "instance-b").Should().BeTrue("instance-b should be active");
        allResponses.AddRange(step1);

        // Step 2: Stop instance A
        System.Console.WriteLine("Stopping instance A...");
        await _service1Container.StopAsync();
        await _service1Container.DisposeAsync();
        _service1Container = null;

        await TestHelpers.WaitForSerfClusterAsync(); // Wait for convergence

        // Step 3: Verify only B is serving
        var step3 = await SendRequests(10);
        step3.Should().OnlyContain(r => r.Item2, "requests should succeed with only instance-b");
        step3.Should().OnlyContain(r => r.Item1 == "instance-b", "only instance-b should serve");
        allResponses.AddRange(step3);

        // Step 4: Start instance C
        System.Console.WriteLine("Starting instance C...");
        _service3Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-service-c")
            .WithEnvironment("SERVICE_NAME", "api-cluster")
            .WithEnvironment("INSTANCE_ID", "instance-c")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "instance-c")
            .WithEnvironment("SERF_JOIN", $"{_service2Container.IpAddress}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8229, 8080)
            .WithPortBinding(7999, 7946)
            .Build();

        await _service3Container.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync(); // Wait for C to join

        // Step 5: Verify B and C are both serving
        var step5 = await SendRequests(10);
        step5.Should().OnlyContain(r => r.Item2, "requests should succeed with B and C");
        step5.Any(r => r.Item1 == "instance-b").Should().BeTrue("instance-b should still be active");
        step5.Any(r => r.Item1 == "instance-c").Should().BeTrue("instance-c should be active");
        allResponses.AddRange(step5);

        // Step 6: Stop instance B
        System.Console.WriteLine("Stopping instance B...");
        await _service2Container.StopAsync();
        await _service2Container.DisposeAsync();
        _service2Container = null;

        await TestHelpers.WaitForSerfClusterAsync(); // Wait for convergence

        // Step 7: Verify only C is serving
        var step7 = await SendRequests(10);
        step7.Should().OnlyContain(r => r.Item2, "requests should succeed with only instance-c");
        step7.Should().OnlyContain(r => r.Item1 == "instance-c", "only instance-c should serve");
        allResponses.AddRange(step7);

        // Assert - Service was always available
        var failedRequests = allResponses.Count(r => !r.Item2);
        failedRequests.Should().Be(0, "no requests should fail during rolling restart");

        System.Console.WriteLine($"Rolling restart complete: {allResponses.Count} total requests, {failedRequests} failures");
    }
}
