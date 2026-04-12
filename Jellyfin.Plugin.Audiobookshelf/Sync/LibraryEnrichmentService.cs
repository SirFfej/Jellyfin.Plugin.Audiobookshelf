using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Singleton service that subscribes to <see cref="ILibraryManager.ItemAdded"/> and
/// queues an ABS metadata refresh for any newly scanned audiobook that hasn't been
/// matched yet.
/// <para>
/// <see cref="IProviderManager"/> and <see cref="IFileSystem"/> are resolved lazily
/// from <see cref="IServiceProvider"/> inside the event handler (method-body scope)
/// rather than held as typed fields. This keeps the class-level type signature minimal
/// so the CLR can load the type even if those interfaces are unavailable at scan time.
/// </para>
/// </summary>
public sealed partial class LibraryEnrichmentService : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LibraryEnrichmentService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryEnrichmentService"/> class.
    /// </summary>
    public LibraryEnrichmentService(
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider,
        ILogger<LibraryEnrichmentService> logger)
    {
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _libraryManager.ItemAdded += OnItemAdded;
        LogStarted(_logger);
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

        // Already matched — no need to queue again.
        if (book.TryGetProviderId("Audiobookshelf", out _))
        {
            return;
        }

        // Resolve lazily so these types are only accessed at JIT time, not during
        // the CLR's type-load scan of the assembly.
        var providerManager = _serviceProvider.GetService<IProviderManager>();
        var fileSystem = _serviceProvider.GetService<IFileSystem>();

        if (providerManager is null || fileSystem is null)
        {
            return;
        }

        LogQueueingRefresh(_logger, book.Name);

        providerManager.QueueRefresh(
            book.Id,
            new MetadataRefreshOptions(new DirectoryService(fileSystem))
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
