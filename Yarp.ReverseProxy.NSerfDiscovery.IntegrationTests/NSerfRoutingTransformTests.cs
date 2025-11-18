using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP routing and transform scenarios.
/// Tests path matching, path transforms, host-based routing, and header-based routing.
/// </summary>
[Collection("Sequential")]
public class NSerfRoutingTransformTests : IAsyncLifetime
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
    public async Task PathMatchAndTransform_ShouldRewritePath()
    {
        // Arrange - Create custom YARP config with path pattern
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

        // Start service with custom YARP configuration
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8200, 8080)
            .WithPortBinding(7970, 7946)
            .Build();

        await _serviceContainer.StartAsync();
        var serviceIp = _serviceContainer.IpAddress;

        // Wait for service to be ready
        await TestHelpers.WaitForSerfStartAsync();

        // Start gateway
        _gatewayContainer = new ContainerBuilder()
            .WithImage("test-gateway:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("gateway")
            .WithEnvironment("SERF_NODE_NAME", "gateway")
            .WithEnvironment("SERF_JOIN", $"{serviceIp}:7946")
            .WithPortBinding(8201, 8080)
            .WithPortBinding(7971, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        // Wait for cluster to form and config to propagate
        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call /billing/hello/world through gateway
        var response = await httpClient.GetAsync($"{gatewayUrl}/billing/hello/world");

        // Assert - Gateway should route to service
        response.StatusCode.Should().Be(HttpStatusCode.OK, 
            "gateway should successfully route the request");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("billing-1", 
            "response should come from the billing service");
        content.Should().Contain("/billing/hello/world",
            "service should receive the path as /billing/hello/world");
    }

    [Fact(Timeout = 60000)]
    public async Task HostBasedRouting_ShouldMatchOnlySpecificHost()
    {
        // Arrange - Create custom YARP config with host-based routing
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "billing-host-route",
                    ClusterId = "billing-api",
                    Match = new 
                    { 
                        Path = "/billing/{**catch-all}",
                        Hosts = new[] { "billing.myapp.local" }
                    }
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
        
        // Start service with host-based routing configuration
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("billing-service")
            .WithEnvironment("SERVICE_NAME", "billing-api")
            .WithEnvironment("INSTANCE_ID", "billing-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "billing-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8202, 8080)
            .WithPortBinding(7972, 7946)
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
            .WithPortBinding(8203, 8080)
            .WithPortBinding(7973, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act 1 - Request with correct host header
        var request1 = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/billing/ping");
        request1.Headers.Host = "billing.myapp.local";
        var response1 = await httpClient.SendAsync(request1);
        
        // Assert 1 - Should route successfully
        response1.StatusCode.Should().Be(HttpStatusCode.OK,
            "request with correct Host header should match the route");

        // Act 2 - Request with different host header
        var request2 = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/billing/ping");
        request2.Headers.Host = "other.myapp.local";
        var response2 = await httpClient.SendAsync(request2);
        
        // Assert 2 - Should not match any route
        response2.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "request with incorrect Host header should not match the route");
    }

    [Fact(Timeout = 60000)]
    public async Task HeaderBasedRouting_ShouldMatchOnlyWithCorrectHeader()
    {
        // Arrange - Create custom YARP config with header-based routing
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "tenant-route",
                    ClusterId = "tenant-api",
                    Match = new 
                    { 
                        Path = "/api/{**catch-all}",
                        Headers = new[]
                        {
                            new
                            {
                                Name = "X-Tenant",
                                Values = new[] { "tenant-a" },
                                Mode = "ExactHeader"
                            }
                        }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "tenant-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service with header-based routing configuration
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("tenant-service")
            .WithEnvironment("SERVICE_NAME", "tenant-api")
            .WithEnvironment("INSTANCE_ID", "tenant-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "tenant-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8204, 8080)
            .WithPortBinding(7974, 7946)
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
            .WithPortBinding(8205, 8080)
            .WithPortBinding(7975, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act 1 - Request without X-Tenant header
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response1.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "request without X-Tenant header should not match the route");

        // Act 2 - Request with X-Tenant: tenant-b
        var request2 = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/api/info");
        request2.Headers.Add("X-Tenant", "tenant-b");
        var response2 = await httpClient.SendAsync(request2);
        response2.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "request with incorrect X-Tenant value should not match the route");

        // Act 3 - Request with X-Tenant: tenant-a
        var request3 = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/api/info");
        request3.Headers.Add("X-Tenant", "tenant-a");
        var response3 = await httpClient.SendAsync(request3);
        
        // Assert 3 - Should route successfully
        response3.StatusCode.Should().Be(HttpStatusCode.OK,
            "request with correct X-Tenant header should match the route");
        
        var content = await response3.Content.ReadAsStringAsync();
        content.Should().Contain("tenant-1",
            "response should come from the tenant service");
    }

    [Fact(Timeout = 60000)]
    public async Task ComplexPathRouting_ShouldMatchCorrectly()
    {
        // Arrange - Start service with standard path-based routing
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("api-service")
            .WithEnvironment("SERVICE_NAME", "complex-api")
            .WithEnvironment("INSTANCE_ID", "complex-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "complex-1")
            .WithPortBinding(8206, 8080)
            .WithPortBinding(7976, 7946)
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
            .WithPortBinding(8207, 8080)
            .WithPortBinding(7977, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Test various path patterns
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        var response2 = await httpClient.GetAsync($"{gatewayUrl}/api/data");
        var response3 = await httpClient.GetAsync($"{gatewayUrl}/api/nested/path/test");

        // Assert - All should route successfully with catch-all pattern
        response1.StatusCode.Should().Be(HttpStatusCode.OK,
            "/api/info should match the route pattern");
        response2.StatusCode.Should().Be(HttpStatusCode.OK,
            "/api/data should match the route pattern");
        response3.StatusCode.Should().Be(HttpStatusCode.OK,
            "/api/nested/path/test should match the catch-all pattern");

        // Verify responses come from the service
        var content1 = await response1.Content.ReadAsStringAsync();
        content1.Should().Contain("complex-1");
    }

    [Fact(Timeout = 60000)]
    public async Task QueryParameterBasedRouting_ShouldMatchOnlyWithCorrectParameter()
    {
        // Arrange - Create custom YARP config with query parameter-based routing
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "versioned-route",
                    ClusterId = "versioned-api",
                    Match = new 
                    { 
                        Path = "/api/{**catch-all}",
                        QueryParameters = new[]
                        {
                            new
                            {
                                Name = "version",
                                Values = new[] { "1" },
                                Mode = "Exact"
                            }
                        }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "versioned-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service with query parameter-based routing configuration
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("versioned-service")
            .WithEnvironment("SERVICE_NAME", "versioned-api")
            .WithEnvironment("INSTANCE_ID", "versioned-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "versioned-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8208, 8080)
            .WithPortBinding(7978, 7946)
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
            .WithPortBinding(8209, 8080)
            .WithPortBinding(7979, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act 1 - Request without version parameter
        var response1 = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        response1.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "request without version parameter should not match the route");

        // Act 2 - Request with version=2
        var response2 = await httpClient.GetAsync($"{gatewayUrl}/api/info?version=2");
        response2.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "request with incorrect version parameter should not match the route");

        // Act 3 - Request with version=1
        var response3 = await httpClient.GetAsync($"{gatewayUrl}/api/info?version=1");
        
        // Assert 3 - Should route successfully
        response3.StatusCode.Should().Be(HttpStatusCode.OK,
            "request with correct version parameter should match the route");
        
        var content = await response3.Content.ReadAsStringAsync();
        content.Should().Contain("versioned-1",
            "response should come from the versioned service");
    }

    [Fact(Timeout = 60000)]
    public async Task RequestHeaderTransformation_ShouldAddHeaderToRequest()
    {
        // Arrange - Create custom YARP config with request header transformation
        // Using correct YARP transform format: { "RequestHeader": "name", "Set": "value" }
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "transform-route",
                    ClusterId = "transform-api",
                    Match = new { Path = "/api/{**catch-all}" },
                    Transforms = new[]
                    {
                        new { RequestHeader = "X-Forwarded-Service", Set = "billing-api" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "transform-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service with request header transformation configuration
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("transform-service")
            .WithEnvironment("SERVICE_NAME", "transform-api")
            .WithEnvironment("INSTANCE_ID", "transform-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "transform-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8210, 8080)
            .WithPortBinding(7980, 7946)
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
            .WithPortBinding(8211, 8080)
            .WithPortBinding(7981, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Debug - Check YARP configuration
        var debugResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        var debugContent = await debugResponse.Content.ReadAsStringAsync();
        System.Console.WriteLine($"YARP Config: {debugContent}");

        // Act - Call through gateway to a path that hits the catch-all endpoint (which returns headers)
        // The /api/info endpoint is explicitly defined and doesn't return headers
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test-transform");
        
        // Assert - Service should receive the transformed header
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "request should be routed successfully");
        
        var content = await response.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Service Response: {content}");
        
        content.Should().Contain("transform-1",
            "response should come from the transform service");
        
        // The service's catch-all endpoint returns all headers in the response
        // Headers might be lowercase in the JSON
        var contentLower = content.ToLower();
        contentLower.Should().Contain("x-forwarded-service",
            "response should include the header name");
        contentLower.Should().Contain("billing-api",
            "response should include the header value set by the transform");
    }

    [Fact(Timeout = 60000)]
    public async Task ResponseHeaderTransformation_ShouldAddHeaderToResponse()
    {
        // Arrange - Create custom YARP config with response header transformation
        // Using correct YARP transform format: { "ResponseHeader": "name", "Set": "value" }
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "response-transform-route",
                    ClusterId = "response-transform-api",
                    Match = new { Path = "/api/{**catch-all}" },
                    Transforms = new[]
                    {
                        new { ResponseHeader = "X-Gateway-Version", Set = "1.0.0" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "response-transform-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service with response header transformation configuration
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("response-transform-service")
            .WithEnvironment("SERVICE_NAME", "response-transform-api")
            .WithEnvironment("INSTANCE_ID", "response-transform-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "response-transform-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8212, 8080)
            .WithPortBinding(7982, 7946)
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
            .WithPortBinding(8213, 8080)
            .WithPortBinding(7983, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call through gateway
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/info");
        
        // Assert - Response should include the transformed header
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "request should be routed successfully");
        
        response.Headers.Should().ContainKey("X-Gateway-Version",
            "response should include the transformed header");
        
        response.Headers.GetValues("X-Gateway-Version").Should().Contain("1.0.0",
            "transformed header should have the expected value");
    }

    [Fact(Timeout = 60000)]
    public async Task MultipleRequestHeaderTransforms_ShouldApplyAll()
    {
        // Arrange - Create custom YARP config with multiple request header transforms
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "multi-transform-route",
                    ClusterId = "multi-transform-api",
                    Match = new { Path = "/api/{**catch-all}" },
                    Transforms = new object[]
                    {
                        new { RequestHeader = "X-Service-Name", Set = "billing-api" },
                        new { RequestHeader = "X-Environment", Set = "test" },
                        new { RequestHeader = "X-Custom-Header", Append = "value1" },
                        new { RequestHeader = "X-Custom-Header", Append = "value2" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "multi-transform-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("multi-transform-service")
            .WithEnvironment("SERVICE_NAME", "multi-transform-api")
            .WithEnvironment("INSTANCE_ID", "multi-transform-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "multi-transform-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8214, 8080)
            .WithPortBinding(7984, 7946)
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
            .WithPortBinding(8215, 8080)
            .WithPortBinding(7985, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call through gateway
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test-multi");
        
        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var contentLower = content.ToLower();
        
        // Verify all Set transforms
        contentLower.Should().Contain("x-service-name", "should have X-Service-Name header");
        contentLower.Should().Contain("billing-api", "should have correct service name value");
        contentLower.Should().Contain("x-environment", "should have X-Environment header");
        contentLower.Should().Contain("test", "should have correct environment value");
        
        // Verify Append transforms (both values should be present)
        contentLower.Should().Contain("x-custom-header", "should have X-Custom-Header");
        contentLower.Should().Contain("value1", "should have first appended value");
        contentLower.Should().Contain("value2", "should have second appended value");
    }

    [Fact(Timeout = 60000)]
    public async Task ResponseHeaderTransforms_WithAppend_ShouldAddMultipleValues()
    {
        // Arrange - Create custom YARP config with response header Append transforms
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "response-append-route",
                    ClusterId = "response-append-api",
                    Match = new { Path = "/api/{**catch-all}" },
                    Transforms = new object[]
                    {
                        new { ResponseHeader = "X-API-Version", Set = "v1" },
                        new { ResponseHeader = "X-Features", Append = "feature-a" },
                        new { ResponseHeader = "X-Features", Append = "feature-b" },
                        new { ResponseHeader = "X-Gateway-Info", Set = "YARP-NSerf" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "response-append-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("response-append-service")
            .WithEnvironment("SERVICE_NAME", "response-append-api")
            .WithEnvironment("INSTANCE_ID", "response-append-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "response-append-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8216, 8080)
            .WithPortBinding(7986, 7946)
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
            .WithPortBinding(8217, 8080)
            .WithPortBinding(7987, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call through gateway
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        
        // Assert - Verify Set transforms
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        response.Headers.Should().ContainKey("X-API-Version");
        response.Headers.GetValues("X-API-Version").Should().Contain("v1");
        
        response.Headers.Should().ContainKey("X-Gateway-Info");
        response.Headers.GetValues("X-Gateway-Info").Should().Contain("YARP-NSerf");
        
        // Verify Append transforms - should have multiple values
        response.Headers.Should().ContainKey("X-Features");
        var features = response.Headers.GetValues("X-Features").ToList();
        features.Should().Contain("feature-a", "should have first appended feature");
        features.Should().Contain("feature-b", "should have second appended feature");
    }

    [Fact(Timeout = 60000)]
    public async Task MixedTransforms_RequestAndResponse_ShouldApplyBoth()
    {
        // Arrange - Create custom YARP config with both request and response transforms
        var yarpConfig = System.Text.Json.JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "mixed-transform-route",
                    ClusterId = "mixed-transform-api",
                    Match = new { Path = "/api/{**catch-all}" },
                    Transforms = new object[]
                    {
                        // Request transforms
                        new { RequestHeader = "X-Request-Id", Set = "test-request-123" },
                        new { RequestHeader = "X-Client-Type", Set = "integration-test" },
                        // Response transforms
                        new { ResponseHeader = "X-Processed-By", Set = "YARP-Gateway" },
                        new { ResponseHeader = "X-Response-Time", Set = "fast" }
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "mixed-transform-api",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });
        
        // Start service
        _serviceContainer = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("mixed-transform-service")
            .WithEnvironment("SERVICE_NAME", "mixed-transform-api")
            .WithEnvironment("INSTANCE_ID", "mixed-transform-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "mixed-transform-1")
            .WithEnvironment("YARP_CONFIG_JSON", yarpConfig)
            .WithPortBinding(8218, 8080)
            .WithPortBinding(7988, 7946)
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
            .WithPortBinding(8219, 8080)
            .WithPortBinding(7989, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Call through gateway
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/mixed-test");
        
        // Assert - Verify response transforms
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        response.Headers.Should().ContainKey("X-Processed-By");
        response.Headers.GetValues("X-Processed-By").Should().Contain("YARP-Gateway");
        
        response.Headers.Should().ContainKey("X-Response-Time");
        response.Headers.GetValues("X-Response-Time").Should().Contain("fast");
        
        // Verify request transforms by checking service response
        var content = await response.Content.ReadAsStringAsync();
        var contentLower = content.ToLower();
        
        contentLower.Should().Contain("x-request-id", "service should receive X-Request-Id header");
        contentLower.Should().Contain("test-request-123", "should have correct request ID");
        contentLower.Should().Contain("x-client-type", "service should receive X-Client-Type header");
        contentLower.Should().Contain("integration-test", "should have correct client type");
    }
}

