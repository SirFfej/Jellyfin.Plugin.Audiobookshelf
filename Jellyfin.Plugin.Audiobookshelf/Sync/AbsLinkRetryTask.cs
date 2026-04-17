using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        var allItems = _libraryManager.GetItemList(new InternalItemsQuery { Recursive = true });

        var booksWithoutAbsId = allItems
            .OfType<Book>()
            .Where(b => !b.TryGetProviderId("Audiobookshelf", out _))
            .Cast<BaseItem>()
            .Concat(allItems.OfType<Audio>().Where(a => !a.TryGetProviderId("Audiobookshelf", out _)))
            .ToList();

        await report.WriteLineAsync($"Items without Audiobookshelf ID: {booksWithoutAbsId.Count}").ConfigureAwait(false);
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

            var match = ItemMatcher.FindBestMatch(
                asin,
                isbn,
                item.Name ?? string.Empty,
                null,
                allAbsItems,
                config.TitleMatchConfidenceThreshold);

            if (match is null)
            {
                await report.WriteLineAsync($"  NO MATCH : \"{item.Name}\"").ConfigureAwait(false);
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
