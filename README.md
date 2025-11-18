# Yarp.ReverseProxy.NSerfDiscovery

This plugin connects NSerf service discovery with YARP.

It lets a gateway read its YARP routes and clusters from NSerf member tags.
Services export their YARP config as JSON in a tag. The gateway reads these tags
and builds its routing config at runtime.

## Requirements

- .NET 8.0 or later
- Yarp.ReverseProxy 2.1.0 or later
- NSerf 0.1.5-beta or later

## How it works

- On the **service** side, each service instance publishes a YARP config JSON
  into a NSerf tag (by default `yarp:config`).
- On the **gateway** side, the plugin reads these tags through `IServiceRegistry`
  and builds YARP `RouteConfig` and `ClusterConfig` objects.
- When NSerf reports changes, the plugin rebuilds the YARP config and signals
  a change token so YARP reloads routing.

## Basic setup example

This example shows a very small gateway that uses NSerf and this plugin.

```csharp
using NSerf.Extensions;
using Yarp.ReverseProxy.NSerfDiscovery.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Configure NSerf for the gateway node
var nodeName = Environment.GetEnvironmentVariable("SERF_NODE_NAME") ?? "gateway";
var seedNode = Environment.GetEnvironmentVariable("SERF_JOIN");

builder.Services.AddNSerf(options =>
{
    options.NodeName = nodeName;
    options.BindAddr = "0.0.0.0:7946";
    options.Tags["role"] = "gateway";

    if (!string.IsNullOrEmpty(seedNode))
    {
        options.StartJoin = [seedNode];
    }
});

// Configure YARP to load config from NSerf tags
builder.Services
    .AddReverseProxy()
    .LoadFromNSerfTags();

var app = builder.Build();

app.MapReverseProxy();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", node = nodeName }));

await app.RunAsync();
```

For services, use `NSerfYarpTagExporter` or your own code to build the YARP
config JSON and store it in the `yarp:config` tag for each service instance.

## Service example

This example shows a simple service that exports its YARP config in a NSerf tag.

```csharp
using NSerf.Extensions;
using Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

var builder = WebApplication.CreateBuilder(args);

var serviceName = "orders-api";
var instanceId = $"{serviceName}-{Guid.NewGuid():N}";

// Build YARP config tag from configuration (section "ReverseProxyExport")
var yarpTag = NSerfYarpTagExporter.BuildYarpConfigTag(builder.Configuration, "ReverseProxyExport");

builder.Services.AddNSerf(options =>
{
    options.NodeName = instanceId;
    options.BindAddr = "0.0.0.0:7946";
    options.Tags["role"] = "service";
    options.Tags[$"service:{serviceName}"] = "true";
    options.Tags["yarp:config"] = yarpTag;
});

var app = builder.Build();

app.MapGet("/api/orders", () => Results.Ok(new { service = serviceName, instance = instanceId }));

await app.RunAsync();
```

### Example appsettings.json

The service example expects a `ReverseProxyExport` section in configuration.

```json
{
  "ReverseProxyExport": {
    "ServiceName": "orders-api",
    "Routes": {
      "orders-route": {
        "RouteId": "orders-route",
        "ClusterId": "orders-cluster",
        "Match": {
          "Path": "/api/orders/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "orders-cluster": {
        "ClusterId": "orders-cluster",
        "LoadBalancingPolicy": "RoundRobin"
      }
    }
  }
}
```
