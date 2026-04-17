using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that populates Jellyfin chapter markers from Audiobookshelf chapter data.
/// Run this manually after a metadata refresh, or on a schedule, to keep chapter lists in sync.
/// </summary>
public partial class ChapterSyncTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterRepository _chapterRepository;
    private readonly ILogger<ChapterSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterSyncTask"/> class.
    /// </summary>
    public ChapterSyncTask(
        AbsApiClientFactory clientFactory,
        ILibraryManager libraryManager,
        IChapterRepository chapterRepository,
        ILogger<ChapterSyncTask> logger)
    {
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _chapterRepository = chapterRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf: Sync Chapters";

    /// <inheritdoc />
    public string Key => "AbsChapterSync";

    /// <inheritdoc />
    public string Description => "Populates Jellyfin chapter markers from Audiobookshelf chapter data for all matched books.";

    /// <inheritdoc />
    public string Category => "Audiobookshelf";

    /// <inheritdoc />
    /// <remarks>
    /// No default triggers — run manually after a metadata refresh or on demand.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping chapter sync");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(config?.AbsServerUrl) || string.IsNullOrWhiteSpace(config?.AdminApiToken))
        {
            _logger.LogDebug("ABS server URL or admin token not configured — skipping chapter sync");
            progress.Report(100);
            return;
        }

        var adminClient = _clientFactory.GetAdminClient();

        var includedLibraryIds = config.IncludedLibraryIds;

        var selectedGuids = includedLibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var matchingLibraries = _libraryManager.GetVirtualFolders()
            .Where(lf => selectedGuids.Contains(Guid.Parse(lf.ItemId.ToString())))
            .ToList();

        _logger.LogDebug("Chapter sync: selected libraries ({Count}): {Libraries}",
            matchingLibraries.Count,
            string.Join(", ", matchingLibraries.Select(lf => $"{lf.Name} ({lf.ItemId})")));

        if (matchingLibraries.Count == 0 && includedLibraryIds.Count > 0)
        {
            _logger.LogWarning("Chapter sync: no libraries found matching config IDs. Available: {Libraries}",
                string.Join(", ", _libraryManager.GetVirtualFolders().Select(lf => $"{lf.Name} ({lf.ItemId})")));
        }

        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
            Recursive = true
        };

        if (selectedGuids.Count > 0)
        {
            query.TopParentIds = selectedGuids.ToArray();
        }

        var items = _libraryManager.GetItemList(query);

        if (items.Count == 0)
        {
            _logger.LogDebug("Chapter sync: no ABS-tagged items found");
            progress.Report(100);
            return;
        }

        int total = items.Count;
        int completed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!item.TryGetProviderId("Audiobookshelf", out string? absItemId)
                || string.IsNullOrWhiteSpace(absItemId))
            {
                progress.Report((double)Interlocked.Increment(ref completed) / total * 100.0);
                continue;
            }

            try
            {
                var absItem = await adminClient.GetItemAsync(absItemId!, cancellationToken).ConfigureAwait(false);
                if (absItem?.Media.Chapters.Length > 0)
                {
                    var chapters = absItem.Media.Chapters
                        .OrderBy(c => c.Start)
                        .Select(c => new ChapterInfo
                        {
                            Name = string.IsNullOrWhiteSpace(c.Title) ? $"Chapter {c.Id + 1}" : c.Title,
                            StartPositionTicks = TimeHelper.SecondsToTicks(c.Start)
                        })
                        .ToList();

                    _chapterRepository.SaveChapters(item.Id, chapters);
                    LogChaptersSaved(_logger, absItemId!, chapters.Count);
                }
            }
            catch (Exception ex)
            {
                LogChapterSyncError(_logger, ex, absItemId!);
            }
            finally
            {
                progress.Report((double)Interlocked.Increment(ref completed) / total * 100.0);
            }
        }

        progress.Report(100);
    }

    // ── Source-generated log methods ──────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Saved {Count} chapters for ABS item '{ItemId}'")]
    private static partial void LogChaptersSaved(ILogger logger, string itemId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error syncing chapters for ABS item '{ItemId}'")]
    private static partial void LogChapterSyncError(ILogger logger, Exception ex, string itemId);
}
