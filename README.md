# Yarp.ReverseProxy.NSerfDiscovery

A dynamic service discovery plugin that connects NSerf with YARP reverse proxy.

Services automatically advertise their routing configuration via NSerf gossip protocol, and the gateway dynamically builds its routing table without manual configuration.

## Requirements

- .NET 8.0 or later
- Yarp.ReverseProxy 2.1.0 or later

## How it works

- **Service side**: Each service instance publishes its YARP configuration as JSON in a NSerf tag (default: `yarp:config`).
- **Gateway side**: The gateway reads these tags through NSerf's service discovery and dynamically builds YARP routes and clusters.
- **Dynamic updates**: When services join/leave the cluster, the gateway automatically rebuilds its routing configuration.

## Gateway Setup

The gateway uses `AddNSerfGateway` to configure both NSerf and YARP in one call:

```csharp
using Yarp.ReverseProxy.NSerfDiscovery.Extensions;

var builder = WebApplication.CreateBuilder(args);

var nodeName = Environment.GetEnvironmentVariable("SERF_NODE_NAME") ?? "gateway";
var seedNode = Environment.GetEnvironmentVariable("SERF_JOIN");

// Configure YARP with NSerf Gateway
builder.Services
    .AddReverseProxy()
    .AddNSerfGateway(options =>
    {
        options.NodeName = nodeName;
        options.BindAddress = "0.0.0.0:7946";
        if (!string.IsNullOrEmpty(seedNode))
        {
            options.SeedNodes = [seedNode];
        }
    });

var app = builder.Build();

app.MapReverseProxy();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", node = nodeName }));

await app.RunAsync();
```

## Service Setup

Services use `AddNSerfService` to automatically advertise themselves and their YARP configuration:

```csharp
using Yarp.ReverseProxy.NSerfDiscovery.Extensions;

var builder = WebApplication.CreateBuilder(args);

var serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? "orders-api";
var instanceId = Environment.GetEnvironmentVariable("INSTANCE_ID") ?? $"{serviceName}-{Guid.NewGuid():N}";
var servicePort = int.Parse(Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "8080");
var seedNode = Environment.GetEnvironmentVariable("SERF_JOIN");

// Configure NSerf Service with YARP config
builder.Services.AddNSerfService(builder.Configuration, options =>
{
    options.ServiceName = serviceName;
    options.Port = servicePort;
    options.InstanceId = instanceId;
    options.BindAddress = "0.0.0.0:7946";
    
    if (!string.IsNullOrEmpty(seedNode))
    {
        options.SeedNodes = [seedNode];
    }
});

var app = builder.Build();

app.MapGet("/api/orders", () => Results.Ok(new { service = serviceName, instance = instanceId }));

await app.RunAsync();
```

## Service Configuration (appsettings.json)

Services define their YARP routes and clusters in the `ReverseProxyExport` section:

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

## Advanced: Manual YARP Config

You can also provide YARP configuration manually via environment variable:

```csharp
var customYarpConfig = Environment.GetEnvironmentVariable("YARP_CONFIG_JSON");

builder.Services.AddNSerfService(builder.Configuration, options =>
{
    options.ServiceName = serviceName;
    options.Port = servicePort;
    options.YarpConfigJson = customYarpConfig; // Override appsettings.json
    // ...
});
```

## Features

- **Zero-configuration service discovery**: Services automatically register and deregister
- **Dynamic routing**: Gateway routing table updates in real-time as services scale
- **Load balancing**: Multiple instances of the same service are automatically load-balanced
- **Health checks**: Supports YARP's active and passive health checking
- **Session affinity**: Cookie-based sticky sessions
- **Flexible matching**: Path, host, header, and query parameter-based routing

## Architecture

```
┌─────────────┐         ┌─────────────┐         ┌─────────────┐
│  Service A  │         │  Service B  │         │  Service C  │
│  Instance 1 │         │  Instance 1 │         │  Instance 1 │
└──────┬──────┘         └──────┬──────┘         └──────┬──────┘
       │                       │                       │
       └───────────────────────┼───────────────────────┘
                               │ NSerf Gossip Protocol
                               │
                      ┌────────▼────────┐
                      │   YARP Gateway  │
                      │  (Auto-config)  │
                      └─────────────────┘
                               │
                      ┌────────▼────────┐
                      │  HTTP Requests  │
                      └─────────────────┘
```
