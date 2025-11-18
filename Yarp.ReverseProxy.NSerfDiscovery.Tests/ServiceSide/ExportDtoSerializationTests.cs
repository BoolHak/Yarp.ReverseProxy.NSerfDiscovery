using FluentAssertions;
using System.Text.Json;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.NSerfDiscovery.Models;

namespace Yarp.ReverseProxy.NSerfDiscovery.Tests.ServiceSide;

/// <summary>
/// 9.1.B - Export DTO serialization tests
/// </summary>
public class ExportDtoSerializationTests
{
    [Fact]
    public void SerializationRoundTrip_WithComplexConfig_ShouldPreserveAllFields()
    {
        // Arrange - Build complex export with headers, query params, transforms
        var export = new NSerfYarpExport
        {
            ServiceName = "billing-api",
            InstanceId = "billing-1",
            Revision = 42,
            Routes = new List<RouteConfig>
            {
                new()
                {
                    RouteId = "billing-route",
                    ClusterId = "billing-cluster",
                    Match = new RouteMatch
                    {
                        Path = "/billing/{**catch-all}",
                        Methods = new[] { "GET", "POST" },
                        Hosts = new[] { "api.example.com" },
                        Headers = new List<RouteHeader>
                        {
                            new()
                            {
                                Name = "X-Tenant",
                                Mode = HeaderMatchMode.ExactHeader,
                                Values = new[] { "tenant-a", "tenant-b" }
                            }
                        },
                        QueryParameters = new List<RouteQueryParameter>
                        {
                            new()
                            {
                                Name = "version",
                                Mode = QueryParameterMatchMode.Exact,
                                Values = new[] { "1", "2" }
                            }
                        }
                    },
                    Transforms = new List<IReadOnlyDictionary<string, string>>
                    {
                        new Dictionary<string, string>
                        {
                            ["PathPattern"] = "/api/billing/{**catch-all}"
                        },
                        new Dictionary<string, string>
                        {
                            ["RequestHeader"] = "X-Origin",
                            ["Set"] = "billing-service"
                        }
                    }
                }
            },
            Clusters = new List<ClusterConfig>
            {
                new()
                {
                    ClusterId = "billing-cluster",
                    LoadBalancingPolicy = "PowerOfTwoChoices",
                    HealthCheck = new HealthCheckConfig
                    {
                        Active = new ActiveHealthCheckConfig
                        {
                            Enabled = true,
                            Path = "/health",
                            Interval = TimeSpan.FromSeconds(10),
                            Timeout = TimeSpan.FromSeconds(3)
                        }
                    }
                }
            }
        };

        // Act - Serialize to JSON
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        // Deserialize back
        var deserialized = JsonSerializer.Deserialize<NSerfYarpExport>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Assert - All fields preserved
        deserialized.Should().NotBeNull();
        deserialized!.ServiceName.Should().Be(export.ServiceName);
        deserialized.InstanceId.Should().Be(export.InstanceId);
        deserialized.Revision.Should().Be(export.Revision);
        
        deserialized.Routes.Should().HaveCount(1);
        deserialized.Routes[0].RouteId.Should().Be("billing-route");
        deserialized.Routes[0].Match.Path.Should().Be("/billing/{**catch-all}");
        deserialized.Routes[0].Match.Methods.Should().Contain("GET", "POST");
        deserialized.Routes[0].Match.Hosts.Should().Contain("api.example.com");
        deserialized.Routes[0].Match.Headers.Should().HaveCount(1);
        deserialized.Routes[0].Match.Headers![0].Name.Should().Be("X-Tenant");
        deserialized.Routes[0].Match.Headers![0].Values.Should().Contain("tenant-a", "tenant-b");
        deserialized.Routes[0].Match.QueryParameters.Should().HaveCount(1);
        deserialized.Routes[0].Match.QueryParameters![0].Name.Should().Be("version");
        deserialized.Routes[0].Transforms.Should().HaveCount(2);
        
        deserialized.Clusters.Should().HaveCount(1);
        deserialized.Clusters[0].ClusterId.Should().Be("billing-cluster");
        deserialized.Clusters[0].LoadBalancingPolicy.Should().Be("PowerOfTwoChoices");
        deserialized.Clusters[0].HealthCheck.Should().NotBeNull();
        deserialized.Clusters[0].HealthCheck!.Active.Should().NotBeNull();
        deserialized.Clusters[0].HealthCheck!.Active!.Enabled.Should().BeTrue();
        deserialized.Clusters[0].HealthCheck!.Active!.Path.Should().Be("/health");
    }

    [Fact]
    public void SerializationRoundTrip_WithMinimalConfig_ShouldWork()
    {
        // Arrange
        var export = new NSerfYarpExport
        {
            ServiceName = "simple-api",
            InstanceId = "simple-1",
            Revision = 1,
            Routes = new List<RouteConfig>
            {
                new() { RouteId = "simple-route", ClusterId = "simple-cluster" }
            },
            Clusters = new List<ClusterConfig>
            {
                new() { ClusterId = "simple-cluster" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(export);
        var deserialized = JsonSerializer.Deserialize<NSerfYarpExport>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.ServiceName.Should().Be("simple-api");
        deserialized.Routes.Should().HaveCount(1);
        deserialized.Clusters.Should().HaveCount(1);
    }

    [Fact]
    public void Serialize_ToBytes_ShouldProduceValidUtf8()
    {
        // Arrange
        var export = new NSerfYarpExport
        {
            ServiceName = "test-api",
            InstanceId = "test-1",
            Revision = 1,
            Routes = new List<RouteConfig>
            {
                new() { RouteId = "test-route", ClusterId = "test-cluster" }
            },
            Clusters = new List<ClusterConfig>
            {
                new() { ClusterId = "test-cluster" }
            }
        };

        // Act - Serialize to bytes (as done in publisher)
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        // Deserialize from bytes
        var jsonFromBytes = System.Text.Encoding.UTF8.GetString(bytes);
        var deserialized = JsonSerializer.Deserialize<NSerfYarpExport>(jsonFromBytes, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.ServiceName.Should().Be("test-api");
        bytes.Length.Should().BeGreaterThan(0);
    }
}
