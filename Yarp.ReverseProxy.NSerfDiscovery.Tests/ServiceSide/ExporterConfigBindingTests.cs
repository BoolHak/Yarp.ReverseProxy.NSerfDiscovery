using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.ServiceSide;

/// <summary>
/// 9.1.A - Config binding tests
/// </summary>
public class ExporterConfigBindingTests
{
    [Fact]
    public void BindValidConfig_ShouldPopulateAllFields()
    {
        // Arrange
        var configData = new Dictionary<string, string>
        {
            ["ReverseProxyExport:ServiceName"] = "billing-api",
            ["ReverseProxyExport:InstanceId"] = "billing-1",
            ["ReverseProxyExport:InitialRevision"] = "5",
            ["ReverseProxyExport:Routes:billing-route:RouteId"] = "billing-route",
            ["ReverseProxyExport:Routes:billing-route:ClusterId"] = "billing-cluster",
            ["ReverseProxyExport:Routes:billing-route:Match:Path"] = "/billing/{**catch-all}",
            ["ReverseProxyExport:Routes:billing-route:Match:Methods:0"] = "GET",
            ["ReverseProxyExport:Routes:billing-route:Match:Methods:1"] = "POST",
            ["ReverseProxyExport:Clusters:billing-cluster:ClusterId"] = "billing-cluster",
            ["ReverseProxyExport:Clusters:billing-cluster:LoadBalancingPolicy"] = "RoundRobin"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<NSerfYarpExportOptions>()
            .Bind(configuration.GetSection("ReverseProxyExport"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NSerfYarpExportOptions>>().Value;

        // Assert
        options.ServiceName.Should().Be("billing-api");
        options.InstanceId.Should().Be("billing-1");
        options.InitialRevision.Should().Be(5);
        options.Routes.Should().ContainKey("billing-route");
        options.Routes["billing-route"].RouteId.Should().Be("billing-route");
        options.Routes["billing-route"].ClusterId.Should().Be("billing-cluster");
        options.Routes["billing-route"].Match.Path.Should().Be("/billing/{**catch-all}");
        options.Routes["billing-route"].Match.Methods.Should().Contain("GET", "POST");
        options.Clusters.Should().ContainKey("billing-cluster");
        options.Clusters["billing-cluster"].LoadBalancingPolicy.Should().Be("RoundRobin");
    }

    [Fact]
    public void BindConfig_WithComplexYarpConfig_ShouldBindCorrectly()
    {
        // Arrange - Complex YARP config with headers, query params, transforms
        var configData = new Dictionary<string, string>
        {
            ["ReverseProxyExport:ServiceName"] = "api",
            ["ReverseProxyExport:Routes:api-route:RouteId"] = "api-route",
            ["ReverseProxyExport:Routes:api-route:ClusterId"] = "api-cluster",
            ["ReverseProxyExport:Routes:api-route:Match:Path"] = "/api/{**catch-all}",
            ["ReverseProxyExport:Routes:api-route:Match:Hosts:0"] = "api.example.com",
            ["ReverseProxyExport:Routes:api-route:Match:Headers:0:Name"] = "X-Tenant",
            ["ReverseProxyExport:Routes:api-route:Match:Headers:0:Values:0"] = "tenant-a",
            ["ReverseProxyExport:Routes:api-route:Match:Headers:0:Mode"] = "ExactHeader",
            ["ReverseProxyExport:Routes:api-route:Match:QueryParameters:0:Name"] = "version",
            ["ReverseProxyExport:Routes:api-route:Match:QueryParameters:0:Values:0"] = "1",
            ["ReverseProxyExport:Clusters:api-cluster:ClusterId"] = "api-cluster"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<NSerfYarpExportOptions>()
            .Bind(configuration.GetSection("ReverseProxyExport"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NSerfYarpExportOptions>>().Value;

        // Assert
        options.Routes["api-route"].Match.Hosts.Should().Contain("api.example.com");
        options.Routes["api-route"].Match.Headers.Should().HaveCount(1);
        options.Routes["api-route"].Match.Headers![0].Name.Should().Be("X-Tenant");
        options.Routes["api-route"].Match.Headers[0].Values.Should().Contain("tenant-a");
        options.Routes["api-route"].Match.QueryParameters.Should().HaveCount(1);
        options.Routes["api-route"].Match.QueryParameters![0].Name.Should().Be("version");
    }

    [Fact]
    public void Validate_WithMissingServiceName_ShouldThrow()
    {
        // Arrange
        var options = new NSerfYarpExportOptions
        {
            ServiceName = "", // Missing
            Routes = new Dictionary<string, RouteConfig>
            {
                ["route1"] = new RouteConfig { RouteId = "route1", ClusterId = "cluster1" }
            },
            Clusters = new Dictionary<string, ClusterConfig>
            {
                ["cluster1"] = new ClusterConfig { ClusterId = "cluster1" }
            }
        };

        // Act & Assert
        var act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ServiceName*required*");
    }

    [Fact]
    public void Validate_WithEmptyRoutes_ShouldThrow()
    {
        // Arrange
        var options = new NSerfYarpExportOptions
        {
            ServiceName = "test-service",
            Routes = new Dictionary<string, RouteConfig>(), // Empty
            Clusters = new Dictionary<string, ClusterConfig>
            {
                ["cluster1"] = new ClusterConfig { ClusterId = "cluster1" }
            }
        };

        // Act & Assert
        var act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Routes*at least one*");
    }

    [Fact]
    public void Validate_WithEmptyClusters_ShouldThrow()
    {
        // Arrange
        var options = new NSerfYarpExportOptions
        {
            ServiceName = "test-service",
            Routes = new Dictionary<string, RouteConfig>
            {
                ["route1"] = new RouteConfig { RouteId = "route1", ClusterId = "cluster1" }
            },
            Clusters = new Dictionary<string, ClusterConfig>() // Empty
        };

        // Act & Assert
        var act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Clusters*at least one*");
    }

    [Fact]
    public void Validate_WithInvalidRevision_ShouldThrow()
    {
        // Arrange
        var options = new NSerfYarpExportOptions
        {
            ServiceName = "test-service",
            InitialRevision = 0, // Invalid
            Routes = new Dictionary<string, RouteConfig>
            {
                ["route1"] = new RouteConfig { RouteId = "route1", ClusterId = "cluster1" }
            },
            Clusters = new Dictionary<string, ClusterConfig>
            {
                ["cluster1"] = new ClusterConfig { ClusterId = "cluster1" }
            }
        };

        // Act & Assert
        var act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InitialRevision*greater than 0*");
    }
}
