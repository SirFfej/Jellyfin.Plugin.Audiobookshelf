using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that attempts to match unlinked Jellyfin <see cref="Book"/> and <see cref="Audio"/>
/// items to Audiobookshelf library items using ASIN, ISBN, and fuzzy title+author matching.
/// Successfully matched items are linked and queued for metadata refresh.
/// </summary>
public sealed partial class AbsLinkRetryTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly JellyfinMetadataReader _metadataReader;
    private readonly ILogger<AbsLinkRetryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsLinkRetryTask"/> class.
    /// </summary>
    public AbsLinkRetryTask(
        AbsApiClientFactory clientFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        ILogger<AbsLinkRetryTask> logger)
    {
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _metadataReader = new JellyfinMetadataReader(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf: Link Unmatched Items";

    /// <inheritdoc />
    public string Key => "AbsLinkRetry";

    /// <inheritdoc />
    public string Description =>
        "Attempts to match unlinked audiobooks to Audiobookshelf using ASIN, ISBN, and title matching. " +
        "Successfully matched items are linked and queued for metadata refresh.";

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
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping link retry");
            progress.Report(100);
            return;
        }

        string reportPath = Path.Combine(
            _appPaths.LogDirectoryPath,
            $"audiobookshelf-link-retry-{DateTime.Now:yyyyMMddHHmmss}.log");

        await using var report = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        report.AutoFlush = false;

        await report.WriteLineAsync($"Audiobookshelf Link Retry — {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        AbsApiClient adminClient;
        try
        {
            adminClient = _clientFactory.GetAdminClient();
            _ = adminClient.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            await report.WriteLineAsync($"ERROR: ABS not configured — {ex.Message}").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("ABS link retry skipped — plugin not configured");
            progress.Report(100);
            return;
        }

        var config = Plugin.Instance!.Configuration;

        var includedLibraryIds = config.IncludedLibraryIds;
        _logger.LogInformation("ABS link retry: config IncludedLibraryIds = {Ids}", string.Join(", ", includedLibraryIds));

        var selectedGuids = includedLibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        _logger.LogInformation("ABS link retry: parsed selectedGuids = {Guids}", string.Join(", ", selectedGuids.Select(g => g.ToString())));

        var matchingLibraries = _libraryManager.GetVirtualFolders()
            .Where(lf => selectedGuids.Contains(Guid.Parse(lf.ItemId.ToString())))
            .ToList();

        _logger.LogInformation("ABS link retry: matchingLibraries count = {Count}", matchingLibraries.Count);

        await report.WriteLineAsync($"Selected libraries ({matchingLibraries.Count}):").ConfigureAwait(false);
        if (matchingLibraries.Count == 0 && includedLibraryIds.Count > 0)
        {
            await report.WriteLineAsync("  WARNING: No libraries found matching config IDs").ConfigureAwait(false);
            await report.WriteLineAsync("  Available libraries:").ConfigureAwait(false);
            foreach (var lf in _libraryManager.GetVirtualFolders())
            {
                await report.WriteLineAsync($"    - {lf.Name} ({lf.ItemId}) [{lf.CollectionType}]").ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var lf in matchingLibraries)
            {
                await report.WriteLineAsync($"  - {lf.Name} ({lf.ItemId}) [{lf.CollectionType}]").ConfigureAwait(false);
            }
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        var query = new InternalItemsQuery
        {
            Recursive = true,
            MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Book, Jellyfin.Data.Enums.MediaType.Audio }
        };

        if (selectedGuids.Count > 0)
        {
            query.TopParentIds = selectedGuids.ToArray();
            _logger.LogInformation("ABS link retry: query.TopParentIds set to {Ids}", string.Join(", ", selectedGuids.Select(g => g.ToString())));
        }

        List<BaseItem> allItemsInScope;
        try
        {
            allItemsInScope = _libraryManager.GetItemList(query).ToList();
            _logger.LogDebug("ABS link retry: query with TopParentIds returned {Count} items", allItemsInScope.Count);

            if (allItemsInScope.Count == 0 && selectedGuids.Count > 0)
            {
                var queryNoFilter = new InternalItemsQuery
                {
                    Recursive = true,
                    MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Book, Jellyfin.Data.Enums.MediaType.Audio }
                };
                var allItems = _libraryManager.GetItemList(queryNoFilter).ToList();
                _logger.LogDebug("ABS link retry: query without TopParentIds returned {Count} items", allItems.Count);

                allItemsInScope = allItems.Where(item =>
                    item.GetParent()?.GetParent() != null &&
                    selectedGuids.Contains(item.GetParent()!.GetParent()!.Id)).ToList();
                _logger.LogDebug("ABS link retry: filtered by parent hierarchy to {Count} items", allItemsInScope.Count);
            }
            _logger.LogInformation("ABS link retry: query returned {Count} items", allItemsInScope.Count);
            await report.WriteLineAsync($"Query returned {allItemsInScope.Count} items in scope").ConfigureAwait(false);
            foreach (var item in allItemsInScope.Take(10))
            {
                item.TryGetProviderId("Audiobookshelf", out string? absId);
                item.TryGetProviderId("Asin", out string? asin);
                await report.WriteLineAsync($"  - \"{item.Name}\" [{item.GetType().Name}] ABS:{absId ?? "(none)"} ASIN:{asin ?? "(none)"}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ABS link retry failed while querying items — {Reason}", ex.Message);
            await report.WriteLineAsync($"ERROR: Failed to query items — {ex.Message}").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        List<BaseItem> booksWithoutAbsId = allItemsInScope
            .Where(item => !item.TryGetProviderId("Audiobookshelf", out _))
            .ToList();

        await report.WriteLineAsync($"Items without Audiobookshelf ID: {booksWithoutAbsId.Count}").ConfigureAwait(false);
        await report.WriteLineAsync($"(from {allItemsInScope.Count} total items in selected libraries)").ConfigureAwait(false);
        await report.WriteLineAsync("  (showing first 20):").ConfigureAwait(false);
        foreach (var item in booksWithoutAbsId.Take(20))
        {
            await report.WriteLineAsync($"    - \"{item.Name}\"  [{item.Id}]").ConfigureAwait(false);
        }
        if (booksWithoutAbsId.Count > 20)
        {
            await report.WriteLineAsync($"    ... and {booksWithoutAbsId.Count - 20} more").ConfigureAwait(false);
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        if (booksWithoutAbsId.Count == 0)
        {
            await report.WriteLineAsync("No unlinked items found. Nothing to do.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        await report.WriteLineAsync("--- Loading ABS library items ---").ConfigureAwait(false);

        var libraries = await adminClient.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        var allAbsItems = new List<Api.Models.AbsLibraryItem>();

        foreach (var lib in libraries)
        {
            if (!string.Equals(lib.MediaType, "book", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int page = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageResponse = await adminClient.GetLibraryItemsAsync(lib.Id, page, 100, cancellationToken).ConfigureAwait(false);
                allAbsItems.AddRange(pageResponse.Results);
                if (pageResponse.Results.Length < 100)
                {
                    break;
                }

                page++;
            }
        }

        await report.WriteLineAsync($"ABS library items loaded: {allAbsItems.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        await report.WriteLineAsync("--- Matching unlinked items ---").ConfigureAwait(false);

        int linked = 0;
        int unmatched = 0;
        int itemIndex = 0;

        foreach (var item in booksWithoutAbsId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)itemIndex / booksWithoutAbsId.Count * 100.0);

            item.TryGetProviderId("Asin", out string? asin);
            item.TryGetProviderId("Isbn", out string? isbn);

            var title = item.Name ?? string.Empty;
            string? author = null;

            var enrichedMetadata = _metadataReader.ReadMetadata(item);
            if (enrichedMetadata is not null)
            {
                if (!string.IsNullOrWhiteSpace(enrichedMetadata.Asin) && string.IsNullOrWhiteSpace(asin))
                {
                    asin = enrichedMetadata.Asin;
                }

                if (!string.IsNullOrWhiteSpace(enrichedMetadata.Isbn) && string.IsNullOrWhiteSpace(isbn))
                {
                    isbn = enrichedMetadata.Isbn;
                }

                if (!string.IsNullOrWhiteSpace(enrichedMetadata.Author))
                {
                    author = enrichedMetadata.Author;
                }

                if (!string.IsNullOrWhiteSpace(enrichedMetadata.Title) && enrichedMetadata.Title != item.Name)
                {
                    title = enrichedMetadata.Title;
                }
            }

            var match = ItemMatcher.FindBestMatch(
                asin,
                isbn,
                title,
                author,
                allAbsItems,
                config.TitleMatchConfidenceThreshold);

            if (match is null)
            {
                var searchInfo = $"Title: \"{title}\", Author: \"{author ?? "(none)"}\", ASIN: \"{asin ?? "(none)"}\", ISBN: \"{isbn ?? "(none)"}\"";
                await report.WriteLineAsync($"  NO MATCH : \"{item.Name}\"  [{searchInfo}]").ConfigureAwait(false);
                unmatched++;
            }
            else
            {
                item.ProviderIds["Audiobookshelf"] = match.Id;
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                _providerManager.QueueRefresh(
                    item.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false
                    },
                    RefreshPriority.Normal);

                await report.WriteLineAsync(
                    $"  LINKED  : \"{item.Name}\" → \"{match.Media.Metadata.Title}\"  [{match.Id}]").ConfigureAwait(false);
                LogLinked(_logger, item.Name ?? string.Empty, match.Id);
                linked++;
            }

            itemIndex++;
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync($"Summary").ConfigureAwait(false);
        await report.WriteLineAsync($"  Total checked   : {booksWithoutAbsId.Count}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Successfully linked: {linked}").ConfigureAwait(false);
        await report.WriteLineAsync($"  No match found  : {unmatched}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Report written  : {reportPath}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);

        await report.FlushAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("ABS link retry complete — {Linked} linked, {Unmatched} unmatched", linked, unmatched);
        _logger.LogInformation("ABS link retry report written to {ReportPath}", reportPath);

        progress.Report(100);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ABS link retry: linked \"{ItemName}\" to {AbsId}")]
    private static partial void LogLinked(ILogger logger, string itemName, string absId);
}
