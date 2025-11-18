using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSerf.ServiceDiscovery;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;
using Yarp.ReverseProxy.NSerfDiscovery.Models;
using Moq;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.GatewaySide;

public class RouteCollectionTests
{
    [Fact]
    public void CollectRoutes_WithMultipleRoutesForSameCluster_ShouldProduceMultipleRouteConfigs()
    {
        var yarpConfig = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "route-1",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/a/{**catch-all}" }
                },
                new RouteFromTag
                {
                    RouteId = "route-2",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/b/{**catch-all}" }
                },
                new RouteFromTag
                {
                    RouteId = "route-3",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/c/{**catch-all}" }
                }
            }
        };

        var routesDict = new Dictionary<string, RouteFromTag>();

        var collectRoutes = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("CollectRoutes", BindingFlags.NonPublic | BindingFlags.Static);
        collectRoutes.Should().NotBeNull();

        collectRoutes!.Invoke(null, new object?[] { yarpConfig, routesDict });

        // Create an instance to call the instance method
        var mockRegistry = new Mock<IServiceRegistry>();
        var provider = new NSerfTagBasedConfigProvider(mockRegistry.Object, NullLogger<NSerfTagBasedConfigProvider>.Instance);
        
        var convertRoutes = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("ConvertRoutesByClusterToActualRouteConfigList", BindingFlags.NonPublic | BindingFlags.Instance);
        convertRoutes.Should().NotBeNull();

        var result = (List<RouteConfig>?)convertRoutes!.Invoke(provider, new object?[] { routesDict });

        result.Should().NotBeNull();
        result!.Should().HaveCount(3);
        result.Select(r => r.RouteId).Should().BeEquivalentTo("route-1", "route-2", "route-3");
        result.Select(r => r.ClusterId).Distinct().Should().ContainSingle().Which.Should().Be("cluster-a");
        result.Select(r => r.Match.Path).Should().BeEquivalentTo("/a/{**catch-all}", "/b/{**catch-all}", "/c/{**catch-all}");
    }

    [Fact]
    public void CollectRoutes_WithDuplicateRouteIds_ShouldKeepLastRoute()
    {
        var config1 = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "duplicate-route",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/first/{**catch-all}" }
                }
            }
        };

        var config2 = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "duplicate-route",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/second/{**catch-all}" }
                }
            }
        };

        var routesDict = new Dictionary<string, RouteFromTag>();

        var collectRoutes = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("CollectRoutes", BindingFlags.NonPublic | BindingFlags.Static);
        collectRoutes.Should().NotBeNull();

        collectRoutes!.Invoke(null, new object?[] { config1, routesDict });
        collectRoutes.Invoke(null, new object?[] { config2, routesDict });

        // Create an instance to call the instance method
        var mockRegistry = new Mock<IServiceRegistry>();
        var provider = new NSerfTagBasedConfigProvider(mockRegistry.Object, NullLogger<NSerfTagBasedConfigProvider>.Instance);
        
        var convertRoutes = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("ConvertRoutesByClusterToActualRouteConfigList", BindingFlags.NonPublic | BindingFlags.Instance);
        convertRoutes.Should().NotBeNull();

        var result = (List<RouteConfig>?)convertRoutes!.Invoke(provider, new object?[] { routesDict });

        result.Should().NotBeNull();
        result!.Should().HaveCount(1);
        result[0].RouteId.Should().Be("duplicate-route");
        result[0].Match.Path.Should().Be("/second/{**catch-all}");
    }

    [Fact]
    public void CollectRoutes_WithNullRoutes_ShouldProduceEmptyList()
    {
        var yarpConfig = new YarpConfigFromTag
        {
            Routes = null
        };

        var routesDict = new Dictionary<string, RouteFromTag>();

        var collectRoutes = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("CollectRoutes", BindingFlags.NonPublic | BindingFlags.Static);
        collectRoutes.Should().NotBeNull();

        collectRoutes!.Invoke(null, new object?[] { yarpConfig, routesDict });

        // Create an instance to call the instance method
        var mockRegistry = new Mock<IServiceRegistry>();
        var provider = new NSerfTagBasedConfigProvider(mockRegistry.Object, NullLogger<NSerfTagBasedConfigProvider>.Instance);
        
        var convertRoutes = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("ConvertRoutesByClusterToActualRouteConfigList", BindingFlags.NonPublic | BindingFlags.Instance);
        convertRoutes.Should().NotBeNull();

        var result = (List<RouteConfig>?)convertRoutes!.Invoke(provider, new object?[] { routesDict });

        result.Should().NotBeNull();
        result!.Should().BeEmpty();
    }
}
