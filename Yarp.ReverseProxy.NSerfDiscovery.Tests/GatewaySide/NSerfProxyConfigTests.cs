using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.GatewaySide;

public class NSerfProxyConfigTests
{
    [Fact]
    public void Constructor_ShouldExposeRoutesClustersAndChangeToken()
    {
        var routes = new List<RouteConfig>
        {
            new RouteConfig { RouteId = "r1", ClusterId = "c1" }
        };
        var clusters = new List<ClusterConfig>
        {
            new ClusterConfig { ClusterId = "c1" }
        };

        var config = new NSerfProxyConfig(routes, clusters);

        config.Routes.Should().BeEquivalentTo(routes);
        config.Clusters.Should().BeEquivalentTo(clusters);
        config.ChangeToken.Should().NotBeNull();
    }

    [Fact]
    public async Task SignalChange_ShouldTriggerChangeToken()
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        var config = new NSerfProxyConfig(routes, clusters);

        var tcs = new TaskCompletionSource<bool>();
        config.ChangeToken.RegisterChangeCallback(_ => tcs.TrySetResult(true), null);

        config.SignalChange();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        tcs.Task.IsCompleted.Should().BeTrue();
    }
}
