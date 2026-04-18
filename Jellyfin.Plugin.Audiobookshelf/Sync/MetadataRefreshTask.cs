using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that queues metadata refresh for all ABS-linked audiobooks.
/// Run on a weekly schedule to keep title, overview, cover, and author in sync with Audiobookshelf.
/// </summary>
public sealed partial class MetadataRefreshTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MetadataRefreshTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataRefreshTask"/> class.
    /// </summary>
    public MetadataRefreshTask(
        AbsApiClientFactory clientFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<MetadataRefreshTask> logger)
    {
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf: Refresh Metadata";

    /// <inheritdoc />
    public string Key => "AbsMetadataRefresh";

    /// <inheritdoc />
    public string Description =>
        "Queues metadata refresh for all audiobooks linked to Audiobookshelf. " +
        "Updates title, overview, cover, and author from ABS.";

    /// <inheritdoc />
    public string Category => "Audiobookshelf";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Monday,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping metadata refresh");
            progress.Report(100);
            return Task.CompletedTask;
        }

        try
        {
            _ = _clientFactory.GetAdminClient();
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("ABS not configured — skipping metadata refresh");
            progress.Report(100);
            return Task.CompletedTask;
        }

        var config = Plugin.Instance!.Configuration;
        var includedLibraryIds = config.IncludedLibraryIds;

        var selectedGuids = includedLibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var matchingLibraries = _libraryManager.GetVirtualFolders()
            .Where(lf => selectedGuids.Contains(Guid.Parse(lf.ItemId.ToString())))
            .ToList();

        _logger.LogDebug("Metadata refresh: selected libraries ({Count}): {Libraries}",
            matchingLibraries.Count,
            string.Join(", ", matchingLibraries.Select(lf => $"{lf.Name} ({lf.ItemId})")));

        if (matchingLibraries.Count == 0 && includedLibraryIds.Count > 0)
        {
            _logger.LogWarning("Metadata refresh: no libraries found matching config IDs. Available: {Libraries}",
                string.Join(", ", _libraryManager.GetVirtualFolders().Select(lf => $"{lf.Name} ({lf.ItemId})")));
        }

        var items = new List<BaseItem>();

        foreach (var lib in matchingLibraries)
        {
            var folder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => f.Name == lib.Name);

            if (folder == null)
            {
                _logger.LogWarning("Metadata refresh: could not find folder for library {Name}", lib.Name);
                continue;
            }

            var folderId = Guid.Parse(folder.ItemId.ToString());

            var libQuery = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
                Recursive = true,
                ParentId = folderId,
                MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Book, Jellyfin.Data.Enums.MediaType.Audio }
            };

            var libItems = _libraryManager.GetItemList(libQuery);
            items.AddRange(libItems);
            _logger.LogDebug("Metadata refresh: library '{Name}' (folderId: {FolderId}) returned {Count} items", lib.Name, folderId, libItems.Count);
        }

        if (items.Count == 0)
        {
            _logger.LogDebug("Metadata refresh: no ABS-linked books found");
            progress.Report(100);
            return Task.CompletedTask;
        }

        int total = items.Count;
        int queued = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _providerManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = false
                },
                RefreshPriority.Normal);

            queued++;
            progress.Report((double)queued / total * 100.0);
        }

        LogQueuedRefreshes(_logger, queued);
        progress.Report(100);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued metadata refresh for {Count} ABS-linked books")]
    private static partial void LogQueuedRefreshes(ILogger logger, int count);
}
