using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP bootstrapping and lifecycle scenarios.
/// Tests gateway behavior with service discovery during startup, shutdown, and restart.
/// </summary>
public class NSerfBootstrapLifecycleTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private IContainer? _serviceContainer;

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

        if (_serviceContainer != null)
        {
            await _serviceContainer.StopAsync();
            await _serviceContainer.DisposeAsync();
        }

        // Cleanup network
        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact(Timeout = 60000)]
    public async Task GatewayWithNoServices_ShouldReturnNoRoute()
    {
        // Arrange - Start gateway with no services
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(8080, 8080)
            .WithPortBinding(7946, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for gateway HTTP server to be ready (needs more time than just Serf)
        await Task.Delay(TimeSpan.FromSeconds(3));

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Request a path that would be exposed by a service
        var response = await httpClient.GetAsync($"{gatewayUrl}/billing/ping");

        // Assert - Gateway should return 404 (no route configured)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 60000)]
    public async Task ServiceJoinsAfterGateway_ShouldDiscoverAndRoute()
    {
        // Arrange - Start gateway first (no services)
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(8080, 8080)
            .WithPortBinding(7946, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;

        // Wait for gateway HTTP server to be ready (needs more time than just Serf)
        await Task.Delay(TimeSpan.FromSeconds(3));

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act 1 - Before service starts, request should fail
        var responseBefore = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        responseBefore.StatusCode.Should().Be(HttpStatusCode.NotFound, 
            "no routes should be configured before service joins");

        // Act 2 - Start service and join cluster
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithPortBinding(8081, 8080)
            .WithPortBinding(7947, 7946)
            .Build();

        await _serviceContainer.StartAsync();

        // Wait for service to be discovered and config to propagate
        await TestHelpers.WaitForSerfClusterAsync();

        // Act 3 - After service joins, request should succeed
        var responseAfter = await httpClient.GetAsync($"{gatewayUrl}/api/info");

        // Assert - Gateway should now route to the service
        responseAfter.StatusCode.Should().Be(HttpStatusCode.OK, 
            "gateway should route to service after it joins cluster");
        
        var content = await responseAfter.Content.ReadAsStringAsync();
        content.Should().Contain("billing-1", 
            "response should come from the billing service instance");
    }

    [Fact(Timeout = 90000)]
    public async Task GatewayRestart_ShouldReconstructConfigFromCluster()
    {
        // Arrange - Start service first
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithPortBinding(8081, 8080)
            .WithPortBinding(7946, 7946)
            .Build();

        await _serviceContainer.StartAsync();
        var serviceIp = _serviceContainer.IpAddress;

        // Wait minimal time for service's Serf to start
        await TestHelpers.WaitForSerfStartAsync();

        // Start first gateway instance (use unique ports to avoid conflicts with other tests)
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway-1")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(8085, 8080) // Use different port to avoid conflicts
            .WithPortBinding(7950, 7946) // Use different port to avoid conflicts
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for cluster to form (minimal time needed)
        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Verify routing works with first gateway
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response1.StatusCode.Should().Be(HttpStatusCode.OK, 
            "first gateway instance should route correctly");

        // Act - Stop and restart gateway (service stays running)
        await _gatewayContainer.StopAsync();
        await _gatewayContainer.DisposeAsync();
        
        // Wait for ports to be released
        await TestHelpers.WaitForSerfStartAsync();

        // Start new gateway instance (use different host ports to avoid conflicts)
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway-new")
            .WithEnvironment("SERF_NODE_NAME", "gateway-2")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(8090, 8080) // Different host port to avoid conflict
            .WithPortBinding(7949, 7946) // Different host port to avoid conflict
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for new gateway to join cluster and reconstruct config
        await TestHelpers.WaitForSerfClusterAsync();

        var newGatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Assert - New gateway should route correctly without service restart
        var response2 = await httpClient.GetAsync($"{newGatewayUrl}/api/info");
        response2.StatusCode.Should().Be(HttpStatusCode.OK, 
            "new gateway instance should reconstruct config from cluster");
        
        var content = await response2.Content.ReadAsStringAsync();
        content.Should().Contain("billing-1", 
            "new gateway should route to existing service");
    }

    [Fact(Timeout = 60000)]
    public async Task MultipleServicesJoinSequentially_ShouldUpdateRoutingDynamically()
    {
        // Arrange - Start gateway first
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithPortBinding(8080, 8080)
            .WithPortBinding(7946, 7946)
            .Build();

        await _gatewayContainer.StartAsync();
        var gatewayIp = _gatewayContainer.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act 1 - Start first service
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service1")
            .WithEnvironment("SERVICE_NAME", "api")
            .WithEnvironment("INSTANCE_ID", "api-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-1")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithPortBinding(8081, 8080)
            .WithPortBinding(7947, 7946)
            .Build();

        await _serviceContainer.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        // Assert 1 - First service should be routable
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsStringAsync();
        content1.Should().Contain("api-1");

        // Act 2 - Start second service with same service name (should join same cluster)
        var service2Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("service2")
            .WithEnvironment("SERVICE_NAME", "api")
            .WithEnvironment("INSTANCE_ID", "api-2")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "api-2")
            .WithEnvironment("SERF_JOIN", $"{gatewayIp}:7946")
            .WithPortBinding(8082, 8080)
            .WithPortBinding(7948, 7946)
            .Build();

        await service2Container.StartAsync();
        await TestHelpers.WaitForSerfClusterAsync();

        // Assert 2 - Both services should be load balanced
        var responses = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var response = await httpClient.GetAsync($"{gatewayUrl}/api/info");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            responses.Add(content);
        }

        responses.Should().Contain(r => r.Contains("api-1"), 
            "some requests should be routed to first instance");
        responses.Should().Contain(r => r.Contains("api-2"), 
            "some requests should be routed to second instance");

        // Cleanup second service
        await service2Container.StopAsync();
        await service2Container.DisposeAsync();
    }
}
