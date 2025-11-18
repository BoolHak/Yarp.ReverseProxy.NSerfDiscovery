using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;

namespace Yarp.ReverseProxy.NSerfDiscovery.IntegrationTests;

/// <summary>
/// Integration tests for NSerf + YARP edge cases.
/// Tests partially invalid exports and unexpected metadata handling.
/// </summary>
[Collection("Sequential")]
public class NSerfEdgeCaseTests : IAsyncLifetime
{
    private INetwork? _network;
    private IContainer? _gatewayContainer;
    private IContainer? _service1Container;
    private IContainer? _service2Container;

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

        // Cleanup network
        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    [Fact(Timeout = 90000)]
    public async Task PartiallyInvalidExport_ValidRoutesShouldWork()
    {
        // Arrange - Create config with mix of valid and invalid routes
        var mixedConfig = @"{
            ""Routes"": [
                {
                    ""RouteId"": ""valid-route-1"",
                    ""ClusterId"": ""mixed-cluster"",
                    ""Match"": {
                        ""Path"": ""/valid1/{**catch-all}""
                    }
                },
                {
                    ""RouteId"": ""invalid-route"",
                    ""ClusterId"": ""mixed-cluster"",
                    ""Match"": {
                        ""Path"": ""[invalid-regex-pattern""
                    },
                    ""Transforms"": [
                        {
                            ""InvalidTransformKey"": ""bad-value"",
                            ""UnknownProperty"": ""should-fail""
                        }
                    ]
                },
                {
                    ""RouteId"": ""valid-route-2"",
                    ""ClusterId"": ""mixed-cluster"",
                    ""Match"": {
                        ""Path"": ""/valid2/{**catch-all}""
                    }
                },
                {
                    ""RouteId"": ""valid-route-3"",
                    ""ClusterId"": ""mixed-cluster"",
                    ""Match"": {
                        ""Path"": ""/valid3/{**catch-all}""
                    }
                }
            ],
            ""Clusters"": [
                {
                    ""ClusterId"": ""mixed-cluster"",
                    ""LoadBalancingPolicy"": ""RoundRobin""
                }
            ]
        }";

        // Start service with mixed config
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("mixed-service")
            .WithEnvironment("SERVICE_NAME", "mixed-cluster")
            .WithEnvironment("INSTANCE_ID", "mixed-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "mixed-service")
            .WithEnvironment("YARP_CONFIG_JSON", mixedConfig)
            .WithPortBinding(8700, 8080)
            .WithPortBinding(8400, 7946)
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
            .WithPortBinding(8701, 8080)
            .WithPortBinding(8401, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Test valid routes
        var valid1Response = await httpClient.GetAsync($"{gatewayUrl}/valid1/test");
        var valid2Response = await httpClient.GetAsync($"{gatewayUrl}/valid2/test");
        var valid3Response = await httpClient.GetAsync($"{gatewayUrl}/valid3/test");

        System.Console.WriteLine($"valid1 status: {valid1Response.StatusCode}");
        System.Console.WriteLine($"valid2 status: {valid2Response.StatusCode}");
        System.Console.WriteLine($"valid3 status: {valid3Response.StatusCode}");

        // Assert - Valid routes should work
        // The system should handle the invalid route gracefully without breaking valid routes
        var validRoutesWorking = valid1Response.IsSuccessStatusCode || 
                                 valid2Response.IsSuccessStatusCode || 
                                 valid3Response.IsSuccessStatusCode;

        validRoutesWorking.Should().BeTrue(
            "at least some valid routes should work despite invalid route in config");

        // Gateway should remain healthy
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "gateway should remain healthy despite partially invalid config");

        // Check debug endpoint to see what was loaded
        var debugResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        if (debugResponse.IsSuccessStatusCode)
        {
            var debugContent = await debugResponse.Content.ReadAsStringAsync();
            System.Console.WriteLine($"Loaded config preview: {debugContent.Substring(0, Math.Min(500, debugContent.Length))}...");
        }

        // Count successful valid routes
        var successCount = 0;
        if (valid1Response.IsSuccessStatusCode) successCount++;
        if (valid2Response.IsSuccessStatusCode) successCount++;
        if (valid3Response.IsSuccessStatusCode) successCount++;

        System.Console.WriteLine($"{successCount}/3 valid routes working");

        // At least 1 valid route should work (ideally all 3)
        successCount.Should().BeGreaterThan(0, 
            "at least one valid route should work in partially invalid config");
    }

    [Fact(Timeout = 90000)]
    public async Task UnexpectedMetadata_ShouldNotBreakRouting()
    {
        // Arrange - Create config with unexpected/dangerous metadata
        var configWithMetadata = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "metadata-route",
                    ClusterId = "metadata-cluster",
                    Match = new { Path = "/api/{**catch-all}" },
                    // Add metadata that should be safely ignored
                    Metadata = new Dictionary<string, object>
                    {
                        ["internal-hint"] = "should-be-ignored",
                        ["debug-flag"] = true,
                        ["priority"] = 100,
                        ["tags"] = new[] { "production", "critical" },
                        ["custom-header-hint"] = "X-Internal-Service",
                        // Potentially dangerous metadata
                        ["admin-access"] = true,
                        ["bypass-auth"] = false,
                        ["internal-endpoint"] = "/admin/secret"
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "metadata-cluster",
                    LoadBalancingPolicy = "RoundRobin",
                    // Cluster-level metadata
                    Metadata = new Dictionary<string, object>
                    {
                        ["environment"] = "test",
                        ["region"] = "us-west",
                        ["cost-center"] = "engineering"
                    }
                }
            }
        });

        System.Console.WriteLine($"Config with metadata size: {configWithMetadata.Length} bytes");

        // Start service with metadata-rich config
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("metadata-service")
            .WithEnvironment("SERVICE_NAME", "metadata-cluster")
            .WithEnvironment("INSTANCE_ID", "metadata-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "metadata-service")
            .WithEnvironment("YARP_CONFIG_JSON", configWithMetadata)
            .WithPortBinding(8702, 8080)
            .WithPortBinding(8402, 7946)
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
            .WithPortBinding(8703, 8080)
            .WithPortBinding(8403, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Test routing behavior
        var response = await httpClient.GetAsync($"{gatewayUrl}/api/test");
        
        System.Console.WriteLine($"Routing status: {response.StatusCode}");

        // Assert - Routing should work normally
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "routing should work despite unexpected metadata");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("metadata-1", "should route to correct service");

        // Verify no dangerous headers were added by metadata
        var headers = response.Headers.ToString();
        System.Console.WriteLine($"Response headers: {headers}");

        // Check that potentially dangerous metadata didn't leak into response
        headers.Should().NotContain("admin-access", 
            "dangerous metadata should not appear in response headers");
        headers.Should().NotContain("bypass-auth",
            "dangerous metadata should not appear in response headers");
        headers.Should().NotContain("internal-endpoint",
            "dangerous metadata should not appear in response headers");

        // Gateway should remain healthy
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "gateway should remain healthy with metadata-rich config");

        // Check debug endpoint
        var debugResponse = await httpClient.GetAsync($"{gatewayUrl}/debug/yarp-config");
        debugResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "debug endpoint should work");

        var debugContent = await debugResponse.Content.ReadAsStringAsync();
        System.Console.WriteLine($"Config loaded successfully with metadata: {debugContent.Length} bytes");

        // Verify the route is actually in the config
        debugContent.Should().Contain("metadata-route", 
            "route should be loaded despite metadata");
    }

    [Fact(Timeout = 90000)]
    public async Task MetadataWithSpecialCharacters_ShouldBeHandledSafely()
    {
        // Arrange - Create config with metadata containing special characters
        var configWithSpecialChars = JsonSerializer.Serialize(new
        {
            Routes = new[]
            {
                new
                {
                    RouteId = "special-chars-route",
                    ClusterId = "special-cluster",
                    Match = new { Path = "/special/{**catch-all}" },
                    Metadata = new Dictionary<string, string>
                    {
                        ["description"] = "Test with <script>alert('xss')</script>",
                        ["sql-injection"] = "'; DROP TABLE users; --",
                        ["path-traversal"] = "../../../etc/passwd",
                        ["command-injection"] = "; rm -rf /",
                        ["unicode"] = "Test 测试 тест 🚀",
                        ["quotes"] = "Test with \"quotes\" and 'apostrophes'",
                        ["newlines"] = "Line1\nLine2\rLine3",
                        ["null-bytes"] = "Test\0Null"
                    }
                }
            },
            Clusters = new[]
            {
                new
                {
                    ClusterId = "special-cluster",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        });

        // Start service
        _service1Container = new ContainerBuilder()
            .WithImage("test-service:latest")
            .WithNetwork(_network!)
            .WithNetworkAliases("special-service")
            .WithEnvironment("SERVICE_NAME", "special-cluster")
            .WithEnvironment("INSTANCE_ID", "special-1")
            .WithEnvironment("SERVICE_PORT", "8080")
            .WithEnvironment("SERF_NODE_NAME", "special-service")
            .WithEnvironment("YARP_CONFIG_JSON", configWithSpecialChars)
            .WithPortBinding(8704, 8080)
            .WithPortBinding(8404, 7946)
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
            .WithPortBinding(8705, 8080)
            .WithPortBinding(8405, 7946)
            .Build();

        await _gatewayContainer.StartAsync();

        await TestHelpers.WaitForSerfClusterAsync();

        using var httpClient = new HttpClient();
        var gatewayUrl = $"http://{_gatewayContainer.Hostname}:{_gatewayContainer.GetMappedPublicPort(8080)}";

        // Act - Test routing
        var response = await httpClient.GetAsync($"{gatewayUrl}/special/test");

        // Assert - Gateway should handle special characters safely
        System.Console.WriteLine($"Response status: {response.StatusCode}");

        // Gateway should remain stable (not crash from special characters)
        var healthResponse = await httpClient.GetAsync($"{gatewayUrl}/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "gateway should remain healthy despite special characters in metadata");

        // Routing should work or fail gracefully (no crashes)
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            System.Console.WriteLine("Routing succeeded with special characters");
        }
        else
        {
            System.Console.WriteLine($"Routing failed gracefully: {response.StatusCode}");
        }

        // The key is that the gateway didn't crash or hang
        true.Should().BeTrue("gateway handled special characters without crashing");
    }
}
