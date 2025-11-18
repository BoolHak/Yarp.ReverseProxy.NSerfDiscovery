using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

/// <summary>
/// Configuration options for exporting YARP configuration from a service.
/// Binds from the "ReverseProxyExport" configuration section.
/// </summary>
public sealed class NSerfYarpExportOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "ReverseProxyExport";

    /// <summary>
    /// Logical service name. Must be unique across all services.
    /// REQUIRED.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Instance identifier. If not specified, defaults to NSerf node name.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Initial revision number. Defaults to 1.
    /// </summary>
    public long InitialRevision { get; set; } = 1;

    /// <summary>
    /// Routes to export. Keys are RouteIds.
    /// </summary>
    public IDictionary<string, RouteConfig> Routes { get; set; } 
        = new Dictionary<string, RouteConfig>();

    /// <summary>
    /// Clusters to export. Keys are ClusterIds.
    /// </summary>
    public IDictionary<string, ClusterConfig> Clusters { get; set; } 
        = new Dictionary<string, ClusterConfig>();

    /// <summary>
    /// NSerf user event name for publishing config updates.
    /// Default: "yarp-config-update"
    /// </summary>
    public string EventName { get; set; } = "yarp-config-update";

    /// <summary>
    /// Whether to republish config on a timer (for resilience).
    /// Default: false
    /// </summary>
    public bool EnablePeriodicRepublish { get; set; } = false;

    /// <summary>
    /// Interval for periodic republishing if enabled.
    /// Default: 5 minutes
    /// </summary>
    public TimeSpan RepublishInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum retry attempts for publishing failures.
    /// Default: 3
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Delay between retry attempts.
    /// Default: 5 seconds
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Validates the options.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException(
                $"{SectionName}.{nameof(ServiceName)} is required and cannot be empty.");
        }

        if (Routes.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}.{nameof(Routes)} must contain at least one route.");
        }

        if (Clusters.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}.{nameof(Clusters)} must contain at least one cluster.");
        }

        if (InitialRevision < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}.{nameof(InitialRevision)} must be greater than 0.");
        }
    }
}
