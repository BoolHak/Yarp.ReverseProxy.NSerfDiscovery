using System.Text.Json;
using FluentAssertions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.Models;
using Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.ServiceSide;

public class NSerfYarpTagExporterTests
{
    [Fact]
    public void BuildYarpConfigTag_FromOptions_ShouldProduceConfigConsumableByGatewayModels()
    {
        var options = new NSerfYarpExportOptions
        {
            ServiceName = "test-service"
        };

        options.Routes["route-1"] = new RouteConfig
        {
            RouteId = "route-1",
            ClusterId = "cluster-a",
            Match = new RouteMatch
            {
                Path = "/api/{**catch-all}"
            }
        };

        options.Clusters["cluster-a"] = new ClusterConfig
        {
            ClusterId = "cluster-a",
            LoadBalancingPolicy = "RoundRobin"
        };

        var json = NSerfYarpTagExporter.BuildYarpConfigTag(options);

        var deserialized = JsonSerializer.Deserialize<YarpConfigFromTag>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        deserialized.Should().NotBeNull();
        deserialized!.Routes.Should().NotBeNull();
        deserialized.Routes!.Should().HaveCount(1);
        deserialized.Routes![0].RouteId.Should().Be("route-1");
        deserialized.Routes[0].ClusterId.Should().Be("cluster-a");
        deserialized.Routes[0].Match!.Path.Should().Be("/api/{**catch-all}");

        deserialized.Clusters.Should().NotBeNull();
        deserialized.Clusters!.Should().HaveCount(1);
        deserialized.Clusters![0].ClusterId.Should().Be("cluster-a");
        deserialized.Clusters[0].LoadBalancingPolicy.Should().Be("RoundRobin");
    }
}
