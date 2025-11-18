using System;
using System.Reflection;
using FluentAssertions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.GatewaySide;
using Yarp.ReverseProxy.NSerfDiscovery.Models;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.GatewaySide;

public class HealthCheckConversionTests
{
    [Fact]
    public void ConvertHealthCheck_WithValidTimeSpans_ShouldParseCorrectly()
    {
        var source = new HealthCheckFromTag
        {
            Active = new ActiveHealthCheckFromTag
            {
                Enabled = true,
                Interval = "00:00:10",
                Timeout = "00:00:05",
                Policy = "MyPolicy",
                Path = "/health"
            },
            Passive = new PassiveHealthCheckFromTag
            {
                Enabled = true,
                Policy = "MyPassivePolicy",
                ReactivationPeriod = "00:01:00"
            }
        };

        var method = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("ConvertHealthCheck", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var result = (HealthCheckConfig?)method!.Invoke(null, new object?[] { source });

        result.Should().NotBeNull();
        result!.Active.Should().NotBeNull();
        result.Active!.Enabled.Should().BeTrue();
        result.Active.Interval.Should().Be(TimeSpan.FromSeconds(10));
        result.Active.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        result.Active.Policy.Should().Be("MyPolicy");
        result.Active.Path.Should().Be("/health");

        result.Passive.Should().NotBeNull();
        result.Passive!.Enabled.Should().BeTrue();
        result.Passive.Policy.Should().Be("MyPassivePolicy");
        result.Passive.ReactivationPeriod.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ConvertHealthCheck_WithInvalidTimeSpans_ShouldNotThrow_AndReturnNullDurations()
    {
        var source = new HealthCheckFromTag
        {
            Active = new ActiveHealthCheckFromTag
            {
                Enabled = true,
                Interval = "not-a-timespan",
                Timeout = "",
                Policy = "Policy",
                Path = "/health"
            },
            Passive = new PassiveHealthCheckFromTag
            {
                Enabled = true,
                Policy = "PassivePolicy",
                ReactivationPeriod = "invalid-duration"
            }
        };

        var method = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("ConvertHealthCheck", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        Action act = () => method!.Invoke(null, new object?[] { source });
        act.Should().NotThrow();

        var result = (HealthCheckConfig?)method!.Invoke(null, new object?[] { source });

        result.Should().NotBeNull();
        result!.Active.Should().NotBeNull();
        result.Active!.Enabled.Should().BeTrue();
        result.Active.Interval.Should().BeNull();
        result.Active.Timeout.Should().BeNull();

        result.Passive.Should().NotBeNull();
        result.Passive!.Enabled.Should().BeTrue();
        result.Passive.ReactivationPeriod.Should().BeNull();
    }

    [Fact]
    public void ConvertHealthCheck_WithNullSource_ShouldReturnNull()
    {
        var method = typeof(NSerfTagBasedConfigProvider)
            .GetMethod("ConvertHealthCheck", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var result = method!.Invoke(null, new object?[] { null });

        result.Should().BeNull();
    }
}
