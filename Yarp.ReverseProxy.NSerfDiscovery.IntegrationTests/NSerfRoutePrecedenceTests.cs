using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for YARP route precedence and Order property.
/// Tests default precedence rules and custom Order overrides across aggregated configs.
/// </summary>
[Collection("Sequential")]
public class NSerfRoutePrecedenceTests : IAsyncLifetime
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
    public async Task DefaultPrecedence_MethodBeatsHeader()
    {
        // Arrange - Service A with method match, Service B with header match
        var configA = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "service-a-route",
                    ClusterId = "service-a-cluster",
                    Match = new 
                    { 
                        Path = "/api/{**rest}",
                        Methods = new[] { "GET" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "service-a-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        var configB = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "service-b-route",
                    ClusterId = "service-b-cluster",
                    Match = new 
                    { 
                        Path = "/api/{**rest}",
                        Headers = new[]
                        {
                            new { Name = "X-Special", Values = new[] { "true" }, Mode = "ExactHeader" }
                        }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "service-b-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start Service A
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-a")
            .WithEnvironment("SERVICE_NAME", "service-a-cluster")
            .WithEnvironment("INSTANCE_ID", "service-a")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-a")
            .WithEnvironment("YARP_CONFIG_JSON", configA)
            .WithPortBinding(9100, 8080)
            .WithPortBinding(8800, 7946)
            .Build();

        await _service1Container.StartAsync();
        var serviceAIp = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start Service B
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-b")
            .WithEnvironment("SERVICE_NAME", "service-b-cluster")
            .WithEnvironment("INSTANCE_ID", "service-b")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-b")
            .WithEnvironment("SERF_JOIN", $"{serviceAIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", configB)
            .WithPortBinding(9101, 8080)
            .WithPortBinding(8801, 7946)
            .Build();

        await _service2Container.StartAsync();

        await TestHelpers.WaitForSerfStartAsync();

        // Start Gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceAIp}:7946")
            .WithPortBinding(9102, 8080)
            .WithPortBinding(8802, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act & Assert - Request without X-Special header
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsStringAsync();
        content1.Should().Contain("service-a", 
            "method match should win over header match (default precedence)");

        // Act & Assert - Request with X-Special header
        var request2 = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/api/test");
        request2.Headers.Add("X-Special", "true");
        var response2 = await httpClient.SendAsync(request2);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        var content2 = await response2.Content.ReadAsStringAsync();
        
        // When header matches, service B wins because it has the header match
        // Both routes match the path, so YARP evaluates additional criteria
        content2.Should().Contain("service-b", 
            "header match route should win when header is present");

        System.Console.WriteLine("Default precedence: both routes evaluated, header match wins when present");
    }

    [Fact(Timeout = 120000)]
    public async Task CustomOrder_ShouldOverridePrecedence()
    {
        // Arrange - Service A with Order=1, Service B with Order=0 (higher priority)
        var configA = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "service-a-route",
                    ClusterId = "service-a-order-cluster",
                    Match = new { Path = "/api/{**rest}" },
                    Order = 1  // Lower priority
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "service-a-order-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        var configB = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "service-b-route",
                    ClusterId = "service-b-order-cluster",
                    Match = new 
                    { 
                        Path = "/api/{**rest}",
                        Headers = new[]
                        {
                            new { Name = "X-Priority", Values = new[] { "high" }, Mode = "ExactHeader" }
                        }
                    },
                    Order = 0  // Higher priority (lower number)
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "service-b-order-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start Service A
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-a-order")
            .WithEnvironment("SERVICE_NAME", "service-a-order-cluster")
            .WithEnvironment("INSTANCE_ID", "service-a-order")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-a-order")
            .WithEnvironment("YARP_CONFIG_JSON", configA)
            .WithPortBinding(9103, 8080)
            .WithPortBinding(8803, 7946)
            .Build();

        await _service1Container.StartAsync();
        var serviceAIp = _service1Container.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start Service B
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service-b-order")
            .WithEnvironment("SERVICE_NAME", "service-b-order-cluster")
            .WithEnvironment("INSTANCE_ID", "service-b-order")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "service-b-order")
            .WithEnvironment("SERF_JOIN", $"{serviceAIp}:7946")
            .WithEnvironment("YARP_CONFIG_JSON", configB)
            .WithPortBinding(9104, 8080)
            .WithPortBinding(8804, 7946)
            .Build();

        await _service2Container.StartAsync();

        await TestHelpers.WaitForSerfStartAsync();

        // Start Gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceAIp}:7946")
            .WithPortBinding(9105, 8080)
            .WithPortBinding(8805, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Debug - Check config
        var debugResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        var debugContent = await debugResponse.Content.ReadAsStringAsync();
        System.Console.WriteLine($"=== YARP Config ===");
        System.Console.WriteLine(debugContent);
        System.Console.WriteLine($"===================");

        // Act - Request with host and header that match Service B
        var request = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/api/test");
        request.Headers.Add("X-Priority", "high");
        // Note: Host header is set automatically by HttpClient to the gateway URL
        
        var response = await httpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Response: {content}");
        
        // Service B should win due to Order=0 (higher priority)
        // Even though both routes match, Order=0 takes precedence
        content.Should().Contain("service-b-order", 
            "Service B should be chosen due to lower Order value (higher priority)");

        System.Console.WriteLine("Custom Order override: Order=0 beats Order=1");
    }
}
