using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Hosted service that subscribes to <see cref="ILibraryManager.ItemAdded"/> and
/// queues an ABS metadata refresh for any newly scanned audiobook that hasn't been
/// matched yet. This removes the requirement to manually trigger a metadata refresh
/// after a library scan.
/// </summary>
public sealed partial class LibraryEnrichmentService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibraryEnrichmentService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryEnrichmentService"/> class.
    /// </summary>
    public LibraryEnrichmentService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<LibraryEnrichmentService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        LogStarted(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            return;
        }

        if (e.Item is not Book book)
        {
            return;
        }

        // Already matched — metadata refresh will use the fast provider-ID path.
        // No need to queue again; avoids duplicate refreshes for re-scanned items.
        if (book.TryGetProviderId("Audiobookshelf", out _))
        {
            return;
        }

        LogQueueingRefresh(_logger, book.Name);

        _providerManager.QueueRefresh(
            book.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default
            },
            RefreshPriority.Normal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemAdded;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audiobookshelf library enrichment service started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queuing ABS metadata refresh for newly added book '{BookName}'")]
    private static partial void LogQueueingRefresh(ILogger logger, string bookName);
}
