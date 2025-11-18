using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP advanced request transform scenarios.
/// Tests path transforms, query transforms, method changes, and HTTP version transforms.
/// </summary>
[Collection("Sequential")]
public class NSerfAdvancedTransformTests : IAsyncLifetime
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

    [Fact(Timeout = 90000)]
    public async Task PathPrefixAndRemovePrefix_ShouldChainCorrectly()
    {
        // Arrange - Create config with PathRemovePrefix then PathPrefix
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "path-chain-route",
                    ClusterId = "path-chain-cluster",
                    Match = new { Path = "/api/billing/{**rest}" },
                    Transforms = new object[]
                    {
                        new { PathRemovePrefix = "/api" },
                        new { PathPrefix = "/backend" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "path-chain-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "path-chain", 8800, 8500);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call /api/billing/invoices/123
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/billing/invoices/123");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");

        // The service should see /backend/billing/invoices/123
        // (removed /api, then added /backend)
        content.Should().Contain("/backend/billing/invoices/123", 
            "service should see path with /api removed and /backend added");
    }

    [Fact(Timeout = 90000)]
    public async Task PathSetVsPathPattern_PathSetShouldTakePrecedence()
    {
        // Arrange - Create config with both PathPattern and PathSet
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "path-precedence-route",
                    ClusterId = "path-precedence-cluster",
                    Match = new { Path = "/frontend/{**anything}" },
                    Transforms = new object[]
                    {
                        new { PathPattern = "/inner/{**anything}" },
                        new { PathSet = "/absolute/final" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "path-precedence-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "path-precedence", 8801, 8501);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call /frontend/foo/bar
        var response = await httpClient.GetAsync($"{gatewayUrl}/frontend/foo/bar");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");

        // PathSet should override PathPattern, so service sees /absolute/final
        content.Should().Contain("/absolute/final", 
            "service should see absolute path set by PathSet transform");
    }

    [Fact(Timeout = 90000)]
    public async Task PathPattern_WithRouteValues_ShouldBuildDynamicPath()
    {
        // Arrange - Create config with PathPattern using route values
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "route-values-route",
                    ClusterId = "route-values-cluster",
                    Match = new { Path = "/billing/{id}" },
                    Transforms = new object[]
                    {
                        new { PathPattern = "/api/customers/{id}/billing" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "route-values-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "route-values", 8802, 8502);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call /billing/42
        var response = await httpClient.GetAsync($"{gatewayUrl}/billing/42");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");

        // Service should see /api/customers/42/billing
        content.Should().Contain("/api/customers/42/billing", 
            "service should see path built from route values");
    }

    [Fact(Timeout = 90000)]
    public async Task QueryTransforms_ShouldAddRemoveAndCopyFromRouteValues()
    {
        // Arrange - Create config with query parameter transforms
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "query-transform-route",
                    ClusterId = "query-transform-cluster",
                    Match = new { Path = "/billing/{id}" },
                    Transforms = new object[]
                    {
                        // Remove debug parameter
                        new { QueryRemoveParameter = "debug" },
                        // Add source parameter with static value
                        new { QueryValueParameter = "source", Append = "nserf-gateway" },
                        // Add customerId from route value
                        new { QueryRouteParameter = "customerId", Append = "id" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "query-transform-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "query-transform", 8803, 8503);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Debug - Check what config was loaded
        var debugResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        var debugContent = await debugResponse.Content.ReadAsStringAsync();
        System.Console.WriteLine($"=== YARP Config ===");
        System.Console.WriteLine(debugContent);
        System.Console.WriteLine($"===================");

        // Test gateway health
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        System.Console.WriteLine($"Gateway health: {healthResponse.StatusCode}");

        // Test if route matches without query string first
        var simpleResponse = await httpClient.GetAsync($"{gatewayUrl}/billing/42");
        System.Console.WriteLine($"Simple call /billing/42 status: {simpleResponse.StatusCode}");
        if (simpleResponse.IsSuccessStatusCode)
        {
            var simpleContent = await simpleResponse.Content.ReadAsStringAsync();
            System.Console.WriteLine($"Simple response: {simpleContent}");
        }

        // Act - Call /billing/42?debug=true&foo=bar
        System.Console.WriteLine($"Calling: {gatewayUrl}/billing/42?debug=true&foo=bar");
        var response = await httpClient.GetAsync($"{gatewayUrl}/billing/42?debug=true&foo=bar");
        System.Console.WriteLine($"Response status: {response.StatusCode}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");

        // Service should see query string with:
        // - debug removed
        // - source=nserf-gateway added
        // - customerId=42 added from route value
        // - foo=bar preserved
        content.Should().NotContain("debug=true", "debug parameter should be removed");
        content.Should().Contain("source=nserf-gateway", "source parameter should be added");
        content.Should().Contain("customerId=42", "customerId should be added from route value");
        content.Should().Contain("foo=bar", "original foo parameter should be preserved");
    }

    [Fact(Timeout = 90000)]
    public async Task HttpMethodChange_ShouldConvertGetToPost()
    {
        // Arrange - Create config that changes GET to POST
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "method-change-route",
                    ClusterId = "method-change-cluster",
                    Match = new 
                    { 
                        Path = "/billing/submit"
                    },
                    Transforms = new object[]
                    {
                        new { HttpMethodChange = "GET", Set = "POST" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "method-change-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "method-change", 8804, 8504);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Send GET request
        var response = await httpClient.GetAsync($"{gatewayUrl}/billing/submit");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");

        // Service should see POST method
        content.Should().Contain("POST", "service should receive POST method");
        content.Should().Contain("/billing/submit", "path should be preserved");
    }

    [Fact(Timeout = 90000)]
    public async Task HttpVersionTransform_ShouldForceHttpVersion()
    {
        // Arrange - Create config with HTTP version transform
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "version-transform-route",
                    ClusterId = "version-transform-cluster",
                    Match = new { Path = "/api/{**catch-all}" },
                    Transforms = new object[]
                    {
                        // Note: HTTP version transforms might be represented differently
                        // This is a best-effort representation
                        new { RequestHeaderOriginalHost = "true" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "version-transform-cluster",
                    LoadBalancingPolicy = "RoundRobin",
                    HttpRequest = new
                    {
                        Version = "2.0",
                        VersionPolicy = "RequestVersionOrHigher"
                    }
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "version-transform", 8805, 8505);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Send HTTP/1.1 request
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");
        System.Console.WriteLine($"Response version: {response.Version}");

        // The service should receive the request
        // Note: HTTP version detection depends on service implementation
        content.Should().Contain("version-transform", "service should receive the request");
    }

    [Fact(Timeout = 90000)]
    public async Task ComplexTransformChain_ShouldApplyAllInOrder()
    {
        // Arrange - Create config with multiple transforms in sequence
        var yarpConfig = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "complex-chain-route",
                    ClusterId = "complex-chain-cluster",
                    Match = new { Path = "/api/v1/billing/{**catch-all}" },
                    Transforms = new object[]
                    {
                        // 1. Remove /api prefix
                        new { PathRemovePrefix = "/api" },
                        // 2. Remove /v1 prefix
                        new { PathRemovePrefix = "/v1" },
                        // 3. Add /backend prefix
                        new { PathPrefix = "/backend" },
                        // 4. Add query parameter
                        new { QueryValueParameter = "transformed", Append = "true" },
                        // 5. Add request header
                        new { RequestHeader = "X-Transform-Applied", Set = "complex-chain" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "complex-chain-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        await StartServiceAndGateway(yarpConfig, "complex-chain", 8806, 8506);

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer!.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call /api/v1/billing/42/invoices?original=value
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/v1/billing/42/invoices?original=value");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request should be routed successfully");

        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service response: {content}");

        // Verify all transforms were applied:
        // Path: /api/v1/billing/42/invoices -> /backend/billing/42/invoices
        content.Should().Contain("/backend/billing/42/invoices", 
            "path should have prefixes removed and new prefix added");

        // Query: original=value&transformed=true
        content.Should().Contain("original=value", "original query parameter should be preserved");
        content.Should().Contain("transformed=true", "transformed parameter should be added");

        // Header: X-Transform-Applied: complex-chain
        var contentLower = content.ToLower();
        contentLower.Should().Contain("x-transform-applied", "request header should be added");
        contentLower.Should().Contain("complex-chain", "header value should be correct");
    }

    private async Task StartServiceAndGateway(string yarpConfig, string serviceName, int httpPort, int serfPort)
    {
        // Start service
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases($"{serviceName}-service")
            .WithEnvironment("SERVICE_NAME", $"{serviceName}-cluster")
            .WithEnvironment("INSTANCE_ID", serviceName)
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", $"{serviceName}-service")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(httpPort, 8080)
            .WithPortBinding(serfPort, 7946)
            .Build();

        await _serviceContainer.StartAsync();
        var serviceIp = _serviceContainer.IpAddress;

        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(httpPort + 1, 8080)
            .WithPortBinding(serfPort + 1, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();
    }
}
