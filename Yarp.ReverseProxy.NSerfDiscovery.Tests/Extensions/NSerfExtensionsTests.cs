using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.Extensions;
using Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;
using NSerf.ServiceDiscovery;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.Extensions;

public class NSerfExtensionsTests
{
    [Fact]
    public void AddNSerfServiceDiscovery_ShouldRegisterRegistryAndHostedService()
    {
        var services = new ServiceCollection();

        services.AddNSerfServiceDiscovery();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IServiceRegistry) &&
            d.ImplementationType == typeof(ServiceRegistry) &&
            d.Lifetime == ServiceLifetime.Singleton);

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(NSerfServiceDiscoveryHostedService));
    }

    [Fact]
    public void LoadFromNSerfTags_ShouldRegisterProxyConfigProviderAndDependencyServices()
    {
        var services = new ServiceCollection();
        var builderMock = new Mock<IReverseProxyBuilder>();
        builderMock.SetupGet(b => b.Services).Returns(services);

        builderMock.Object.LoadFromNSerfTags();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IServiceRegistry) &&
            d.ImplementationType == typeof(ServiceRegistry));

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(NSerfServiceDiscoveryHostedService));

        services.Should().ContainSingle(d => d.ServiceType == typeof(IProxyConfigProvider));
        var descriptor = services.Single(d => d.ServiceType == typeof(IProxyConfigProvider));
        descriptor.ImplementationFactory.Should().NotBeNull();
    }
}
