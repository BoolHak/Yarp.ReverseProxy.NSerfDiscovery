using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSerf.ServiceDiscovery;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;
using Yarp.ReverseProxy.NSerfDiscovery.Models;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.GatewaySide;

public class NSerfTagBasedConfigProviderTests
{
    [Fact]
    public async Task BuildConfig_WithMultipleInstancesAndRoutes_ShouldCreateRoutesAndClusterWithAllDestinations()
    {
        using var registry = new ServiceRegistry();
        var logger = NullLogger<NSerfTagBasedConfigProvider>.Instance;

        var yarpConfig = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "route-1",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag
                    {
                        Path = "/a/{**catch-all}",
                        Hosts = new[] { "api.example.com" }
                    }
                },
                new RouteFromTag
                {
                    RouteId = "route-2",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag
                    {
                        Path = "/b/{**catch-all}"
                    }
                }
            },
            Clusters = new[]
            {
                new ClusterFromTag
                {
                    ClusterId = "cluster-a",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        };

        var json = JsonSerializer.Serialize(yarpConfig);

        var instance1 = new ServiceInstance
        {
            Id = "instance-1",
            ServiceName = "api",
            Host = "api-1",
            Port = 8080,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = json }
        };

        var instance2 = new ServiceInstance
        {
            Id = "instance-2",
            ServiceName = "api",
            Host = "api-2",
            Port = 8081,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = json }
        };

        await registry.RegisterInstanceAsync(instance1);
        await registry.RegisterInstanceAsync(instance2);

        var provider = new NSerfTagBasedConfigProvider(registry, logger, "yarp:config");
        var config = provider.GetConfig();

        config.Routes.Should().HaveCount(2);
        config.Routes.Select(r => r.RouteId).Should().BeEquivalentTo("route-1", "route-2");
        config.Routes.Select(r => r.ClusterId).Distinct().Should().ContainSingle("cluster-a");

        var cluster = config.Clusters.Should().ContainSingle().Subject;
        cluster.ClusterId.Should().Be("cluster-a");
        cluster.LoadBalancingPolicy.Should().Be("RoundRobin");
        cluster.Destinations.Should().NotBeNull();
        cluster.Destinations!.Keys.Should().BeEquivalentTo("instance-1", "instance-2");
        cluster.Destinations["instance-1"].Address.Should().Be("http://api-1:8080");
        cluster.Destinations["instance-2"].Address.Should().Be("http://api-2:8081");
    }

    [Fact]
    public async Task BuildConfig_ShouldIgnoreInstancesWithoutYarpConfigTag()
    {
        using var registry = new ServiceRegistry();
        var logger = NullLogger<NSerfTagBasedConfigProvider>.Instance;

        var yarpConfig = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "route-1",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new ClusterFromTag
                {
                    ClusterId = "cluster-a",
                    LoadBalancingPolicy = "RoundRobin"
                }
            }
        };

        var json = JsonSerializer.Serialize(yarpConfig);

        var taggedInstance = new ServiceInstance
        {
            Id = "tagged-instance",
            ServiceName = "api",
            Host = "api-tagged",
            Port = 8080,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = json }
        };

        var untaggedInstance = new ServiceInstance
        {
            Id = "untagged-instance",
            ServiceName = "api",
            Host = "api-untagged",
            Port = 8081,
            Scheme = "http",
            Metadata = new Dictionary<string, string>()
        };

        await registry.RegisterInstanceAsync(taggedInstance);
        await registry.RegisterInstanceAsync(untaggedInstance);

        var provider = new NSerfTagBasedConfigProvider(registry, logger, "yarp:config");
        var config = provider.GetConfig();

        var cluster = config.Clusters.Should().ContainSingle().Subject;
        cluster.Destinations.Should().NotBeNull();
        cluster.Destinations!.Keys.Should().ContainSingle("tagged-instance");
        cluster.Destinations.Keys.Should().NotContain("untagged-instance");
    }

    [Fact]
    public async Task BuildConfig_WithInvalidYarpJson_ShouldSkipInvalidInstancesAndStillProduceConfig()
    {
        using var registry = new ServiceRegistry();
        var logger = NullLogger<NSerfTagBasedConfigProvider>.Instance;

        var validConfig = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "route-1",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new ClusterFromTag { ClusterId = "cluster-a" }
            }
        };

        var validJson = JsonSerializer.Serialize(validConfig);
        var invalidJson = "{ this is not valid json";

        var validInstance = new ServiceInstance
        {
            Id = "valid-instance",
            ServiceName = "api",
            Host = "api-valid",
            Port = 8080,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = validJson }
        };

        var invalidInstance = new ServiceInstance
        {
            Id = "invalid-instance",
            ServiceName = "api",
            Host = "api-invalid",
            Port = 8081,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = invalidJson }
        };

        await registry.RegisterInstanceAsync(validInstance);
        await registry.RegisterInstanceAsync(invalidInstance);

        var provider = new NSerfTagBasedConfigProvider(registry, logger, "yarp:config");
        var config = provider.GetConfig();

        config.Routes.Should().HaveCount(1);
        var cluster = config.Clusters.Should().ContainSingle().Subject;
        cluster.Destinations.Should().NotBeNull();
        cluster.Destinations!.Keys.Should().ContainSingle("valid-instance");
    }

    [Fact]
    public async Task ServiceRegistryChange_ShouldTriggerConfigChangeAndAddNewDestination()
    {
        using var registry = new ServiceRegistry();
        var logger = NullLogger<NSerfTagBasedConfigProvider>.Instance;

        var yarpConfig = new YarpConfigFromTag
        {
            Routes = new[]
            {
                new RouteFromTag
                {
                    RouteId = "route-1",
                    ClusterId = "cluster-a",
                    Match = new MatchFromTag { Path = "/api/{**catch-all}" }
                }
            },
            Clusters = new[]
            {
                new ClusterFromTag { ClusterId = "cluster-a" }
            }
        };

        var json = JsonSerializer.Serialize(yarpConfig);

        var instance1 = new ServiceInstance
        {
            Id = "instance-1",
            ServiceName = "api",
            Host = "api-1",
            Port = 8080,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = json }
        };

        await registry.RegisterInstanceAsync(instance1);

        var provider = new NSerfTagBasedConfigProvider(registry, logger, "yarp:config");
        var initialConfig = (NSerfProxyConfig)provider.GetConfig();

        var tcs = new TaskCompletionSource<bool>();
        initialConfig.ChangeToken.RegisterChangeCallback(_ => tcs.TrySetResult(true), null);

        var instance2 = new ServiceInstance
        {
            Id = "instance-2",
            ServiceName = "api",
            Host = "api-2",
            Port = 8081,
            Scheme = "http",
            Metadata = new Dictionary<string, string> { ["yarp:config"] = json }
        };

        await registry.RegisterInstanceAsync(instance2);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var updatedConfig = (NSerfProxyConfig)provider.GetConfig();
        updatedConfig.Should().NotBeSameAs(initialConfig);

        var cluster = updatedConfig.Clusters.Should().ContainSingle().Subject;
        cluster.Destinations.Should().NotBeNull();
        cluster.Destinations!.Keys.Should().BeEquivalentTo("instance-1", "instance-2");
    }
}
