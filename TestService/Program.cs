using NSerf.Extensions;
using System.Text.Json;
using Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

var builder = WebApplication.CreateBuilder(args);

// Get service configuration from environment
var serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? "test-service";
var instanceId = Environment.GetEnvironmentVariable("INSTANCE_ID") ?? $"{serviceName}-{Guid.NewGuid():N}";
var servicePort = int.Parse(Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "8080");
var seedNode = Environment.GetEnvironmentVariable("SERF_JOIN");

// Check if custom YARP config is provided via environment variable
var customYarpConfig = Environment.GetEnvironmentVariable("YARP_CONFIG_JSON");
string yarpConfigTag;

if (!string.IsNullOrEmpty(customYarpConfig))
{
    // Use custom config from environment variable
    yarpConfigTag = customYarpConfig;
}
else
{
    // Build YARP config tag using the library from appsettings.json
    yarpConfigTag = NSerfYarpTagExporter.BuildYarpConfigTag(builder.Configuration, "ReverseProxyExport");
}

// Configure NSerf with service discovery tags + YARP config
builder.Services.AddNSerf(options =>
{
    options.NodeName = instanceId;
    options.BindAddr = "0.0.0.0:7946";
    options.Tags["role"] = "service";
    options.Tags[$"service:{serviceName}"] = "true";
    options.Tags[$"port:{serviceName}"] = servicePort.ToString();
    options.Tags[$"scheme:{serviceName}"] = "http";
    options.Tags["yarp:config"] = yarpConfigTag; // Store YARP config in tags
    options.Profile = "lan";
    
    if (!string.IsNullOrEmpty(seedNode))
    {
        options.StartJoin = [seedNode];
    }
});

var app = builder.Build();

// Health state management
var isHealthy = true;

// API endpoints
app.MapGet("/api/info", (HttpContext context) => Results.Ok(new 
{ 
    service = serviceName,
    instance = instanceId,
    path = context.Request.Path.ToString(),
    timestamp = DateTime.UtcNow
}));

app.MapGet("/api/data", (HttpContext context) => Results.Ok(new 
{ 
    data = $"Response from {instanceId}",
    path = context.Request.Path.ToString(),
    timestamp = DateTime.UtcNow
}));

// Health check endpoint - returns status based on health flag
app.MapGet("/health", () => 
{
    if (isHealthy)
    {
        return Results.Ok(new { status = "healthy", instance = instanceId });
    }
    else
    {
        return Results.StatusCode(503); // Service Unavailable
    }
});

// Admin endpoint to toggle health status
app.MapPost("/admin/health/fail", () => 
{
    isHealthy = false;
    return Results.Ok(new { status = "unhealthy", instance = instanceId, message = "Health set to unhealthy" });
});

app.MapPost("/admin/health/recover", () => 
{
    isHealthy = true;
    return Results.Ok(new { status = "healthy", instance = instanceId, message = "Health set to healthy" });
});

app.MapGet("/admin/health/status", () => 
{
    return Results.Ok(new { isHealthy, instance = instanceId });
});

// Catch-all endpoint to capture any path
app.MapFallback((HttpContext context) => Results.Ok(new 
{ 
    service = serviceName,
    instance = instanceId,
    path = context.Request.Path.ToString(),
    query = context.Request.QueryString.ToString(),
    method = context.Request.Method,
    host = context.Request.Host.ToString(),
    headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
    protocol = context.Request.Protocol,
    timestamp = DateTime.UtcNow
}));

await app.RunAsync();
