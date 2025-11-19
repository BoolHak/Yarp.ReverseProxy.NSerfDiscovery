using Yarp.ReverseProxy.NSerfDiscovery.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Get service configuration from the environment
var serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? "test-service";
var instanceId = Environment.GetEnvironmentVariable("INSTANCE_ID") ?? $"{serviceName}-{Guid.NewGuid():N}";
var servicePort = int.Parse(Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "8080");
var seedNode = Environment.GetEnvironmentVariable("SERF_JOIN");

// Check if custom YARP config is provided via environment variable
var customYarpConfig = Environment.GetEnvironmentVariable("YARP_CONFIG_JSON");

// Configure NSerf with service discovery tags + YARP config
builder.Services.AddNSerfService(builder.Configuration, options =>
{
    options.ServiceName = serviceName;
    options.Port = servicePort;
    options.InstanceId = instanceId;
    options.BindAddress = "0.0.0.0:7946";
    options.YarpConfigJson = !string.IsNullOrEmpty(customYarpConfig) ? customYarpConfig : null;

    if (!string.IsNullOrEmpty(seedNode))
    {
        options.SeedNodes = [seedNode];
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

// Health check endpoint - returns status based on a health flag
app.MapGet("/health", () => isHealthy ?
    Results.Ok(new { status = "healthy", instance = instanceId }) : Results.StatusCode(503));

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
    Results.Ok(new { isHealthy, instance = instanceId }));

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
