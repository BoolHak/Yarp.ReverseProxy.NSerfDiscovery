using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Yarp.ReverseProxy.NSerfDiscovery.Internal;

/// <summary>
/// Implementation of IProxyConfig that wraps the merged global configuration.
/// </summary>
internal sealed class NSerfProxyConfig : IProxyConfig
{
    public NSerfProxyConfig(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        IChangeToken changeToken)
    {
        Routes = routes ?? throw new ArgumentNullException(nameof(routes));
        Clusters = clusters ?? throw new ArgumentNullException(nameof(clusters));
        ChangeToken = changeToken ?? throw new ArgumentNullException(nameof(changeToken));
    }

    public IReadOnlyList<RouteConfig> Routes { get; }

    public IReadOnlyList<ClusterConfig> Clusters { get; }

    public IChangeToken ChangeToken { get; }
}

/// <summary>
/// Simple IChangeToken implementation using CancellationToken.
/// </summary>
internal sealed class NSerfCancellationChangeToken : IChangeToken
{
    private readonly CancellationTokenSource _cts = new();

    public bool HasChanged => _cts.Token.IsCancellationRequested;

    public bool ActiveChangeCallbacks => true;

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        return _cts.Token.Register(callback, state);
    }

    public void SignalChange()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }
}
