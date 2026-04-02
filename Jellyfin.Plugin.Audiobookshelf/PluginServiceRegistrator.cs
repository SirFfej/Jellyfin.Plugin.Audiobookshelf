using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Audiobookshelf;

/// <summary>
/// Registers the plugin's services with the Jellyfin dependency-injection container.
/// </summary>
/// <remarks>
/// Jellyfin requires this class to have a <b>public parameterless constructor</b> — it is
/// instantiated via reflection before the DI container is built.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(AbsApiClient.HttpClientName, client =>
        {
            client.Timeout = System.TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        serviceCollection.AddSingleton<AbsApiClientFactory>();
        serviceCollection.AddSingleton<ProgressSyncService>();
    }
}
