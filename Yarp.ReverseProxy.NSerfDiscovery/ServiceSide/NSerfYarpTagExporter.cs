using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Yarp.ReverseProxy.NSerfDiscovery.ServiceSide;

/// <summary>
/// Exports YARP configuration as Serf member tags.
/// This is a simple utility that serializes YARP config to JSON and returns it as a tag value.
/// </summary>
public static class NSerfYarpTagExporter
{
    /// <summary>
    /// Builds a YARP config tag value from the provided options.
    /// This should be set as a Serf member tag (e.g., "yarp:config").
    /// </summary>
    public static string BuildYarpConfigTag(NSerfYarpExportOptions options)
    {
        var config = new
        {
            Routes = options.Routes.Values.ToArray(),
            Clusters = options.Clusters.Values.ToArray()
        };

        return JsonSerializer.Serialize(config);
    }

    /// <summary>
    /// Builds YARP config tag from a configuration section.
    /// </summary>
    public static string BuildYarpConfigTag(IConfiguration configuration, string sectionName = "ReverseProxyExport")
    {
        var options = new NSerfYarpExportOptions();
        configuration.GetSection(sectionName).Bind(options);
        return BuildYarpConfigTag(options);
    }
}
