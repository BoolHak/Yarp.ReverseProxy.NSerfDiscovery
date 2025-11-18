using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests using Testcontainers to verify YARP + NSerf plugin
/// with real containers running Gateway and Services.
/// </summary>
public class NSerfYarpIntegrationTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private IContainer? _service1Container;
    private IContainer? _service2Container;

    public async Task InitializeAsync()
    {
        // Create Docker network for containers to communicate
        _network = new NetworkBuilder()
            .WithName($"nserf-test-{Guid.NewGuid():N}")
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

    [Fact(Timeout = 60000)]
    public async Task SingleService_ShouldRouteTrafficThroughGateway()
    {
        // Arrange - Build and start service container
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service1")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithPortBinding(8091, 8080)
            .WithPortBinding(7951, 7946)
            .Build();

        await _service1Container.StartAsync();

        // Get service1 IP for Serf join
        var service1Ip = _service1Container.IpAddress;

        // Arrange - Build and start gateway container
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithPortBinding(8092, 8080)
            .WithPortBinding(7952, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for NSerf cluster to stabilize and config to propagate (periodic republish is every 5s)
        await TestHelpers.WaitForSerfClusterAsync();

        // Act - Make request through gateway
        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/info");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("billing-1");
    }

    [Fact(Timeout = 60000)]
    public async Task MultipleServices_ShouldLoadBalanceRequests()
    {
        // Arrange - Start first service
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service1")
            .WithEnvironment("SERVICE_NAME", "api")
            .WithEnvironment("INSTANCE_ID", "api-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-1")
            .WithPortBinding(8093, 8080)
            .WithPortBinding(7953, 7946)
            .Build();

        await _service1Container.StartAsync();
        var service1Ip = _service1Container.IpAddress;

        // Arrange - Start second service
        _service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service2")
            .WithEnvironment("SERVICE_NAME", "api")
            .WithEnvironment("INSTANCE_ID", "api-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-2")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithPortBinding(8094, 8080)
            .WithPortBinding(7954, 7946)
            .Build();

        await _service2Container.StartAsync();

        // Arrange - Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{service1Ip}:7946")
            .WithPortBinding(8097, 8080)
            .WithPortBinding(7957, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for cluster to stabilize and config to propagate
        await TestHelpers.WaitForSerfClusterAsync();

        // Act - Make multiple requests
        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";
        
        var responses = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/data");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            responses.Add(content);
        }

        // Assert - Requests should be distributed across both instances
        responses.Should().Contain(r => r.Contains("api-1"));
        responses.Should().Contain(r => r.Contains("api-2"));
    }

    [Fact(Timeout = 60000)]
    public async Task ServiceJoinAndLeave_ShouldUpdateRouting()
    {
        // Arrange - Start gateway first
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(8095, 8080)
            .WithPortBinding(7955, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;

        // Wait for gateway HTTP server to be ready (needs more time than just Serf)
        await Task.Delay(TimeSpan.FromSeconds(3));

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act 1 - No services, request should fail (404 because no routes configured)
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response1.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Act 2 - Start service and join cluster
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service1")
            .WithEnvironment("SERVICE_NAME", "api")
            .WithEnvironment("INSTANCE_ID", "api-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-1")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithPortBinding(8096, 8080)
            .WithPortBinding(7956, 7946)
            .Build();

        await _service1Container.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - Request should now succeed
        var response2 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 3 - Stop service
        await _service1Container.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - Request should fail again (404 because routes removed)
        var response3 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response3.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
