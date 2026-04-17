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

        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
            Recursive = true,
            MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Book, Jellyfin.Data.Enums.MediaType.Audio }
        };

        if (includedLibraryIds.Count > 0)
        {
            var topParentGuids = includedLibraryIds
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToArray();

            if (topParentGuids.Length > 0)
            {
                query.TopParentIds = topParentGuids;
            }
        }

        var items = _libraryManager.GetItemList(query);

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
