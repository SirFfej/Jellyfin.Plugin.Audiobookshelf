using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Logging;
using Jellyfin.Plugin.Audiobookshelf.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // File logger — resolves IApplicationPaths lazily from the built container so the
        // log directory path is always available by the time the first log entry is written.
        // All ILogger<T> calls from within the Jellyfin.Plugin.Audiobookshelf namespace are
        // automatically mirrored to audiobookshelf-yyyyMMdd.log in Jellyfin's log directory.
        serviceCollection.AddSingleton<ILoggerProvider>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new AbsFileLoggerProvider(paths.LogDirectoryPath);
        });

        serviceCollection.AddHttpClient(AbsApiClient.HttpClientName, client =>
        {
            client.Timeout = System.TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        serviceCollection.AddSingleton<AbsApiClientFactory>();
        serviceCollection.AddSingleton<ProgressSyncService>();
    }
}
