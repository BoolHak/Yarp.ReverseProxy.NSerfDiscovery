using Yarp.ReverseProxy.NSerfDiscovery.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Configure NSerf
var nodeName = Environment.GetEnvironmentVariable("SERF_NODE_NAME") ?? "gateway";
var seedNode = Environment.GetEnvironmentVariable("SERF_JOIN");

// Configure YARP with merged NSerf Gateway setup
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

// Map YARP
app.MapReverseProxy();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", node = nodeName }));

// Diagnostic endpoint to view current YARP configuration - returns FULL config
app.MapGet("/debug/yarp-config", (Yarp.ReverseProxy.Configuration.IProxyConfigProvider configProvider) =>
{
    var config = configProvider.GetConfig();

    // Return the complete configuration without filtering
    // This ensures we can see everything YARP has configured
    return Results.Json(new
    {
        routes = config.Routes,
        clusters = config.Clusters,
        changeToken = config.ChangeToken.ToString()
    });
});

await app.RunAsync();
