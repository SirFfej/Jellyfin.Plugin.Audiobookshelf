using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that removes Audiobookshelf provider IDs from items that are not in selected libraries.
/// Use this when library selection changes to unlink items from removed libraries.
/// </summary>
public sealed partial class AbsCleanupInvalidLinksTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<AbsCleanupInvalidLinksTask> _logger;

    public AbsCleanupInvalidLinksTask(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        ILogger<AbsCleanupInvalidLinksTask> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    public string Name => "Audiobookshelf: Cleanup Invalid Links";

    public string Key => "AbsCleanupInvalidLinks";

    public string Description =>
        "Removes Audiobookshelf provider IDs from items that are not in selected libraries. " +
        "Use this when library selection changes to unlink items from removed libraries.";

    public string Category => "Audiobookshelf";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping invalid link cleanup");
            progress.Report(100);
            return;
        }

        string reportPath = Path.Combine(
            _appPaths.LogDirectoryPath,
            $"audiobookshelf-invalid-links-cleanup-{DateTime.Now:yyyyMMddHHmmss}.log");

        await using var report = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        report.AutoFlush = false;

        await report.WriteLineAsync($"Audiobookshelf Invalid Links Cleanup — {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        var includedLibraryIds = config.IncludedLibraryIds;
        var topParentGuids = includedLibraryIds.Count > 0
            ? includedLibraryIds
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToHashSet()
            : new HashSet<Guid>();

        await report.WriteLineAsync($"Selected libraries: {(topParentGuids.Count == 0 ? "All (none selected)" : string.Join(", ", topParentGuids))}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        if (topParentGuids.Count == 0)
        {
            await report.WriteLineAsync("No libraries selected — nothing to clean up.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ABS invalid links cleanup: no libraries selected, nothing to clean");
            progress.Report(100);
            return;
        }

        var linkedItemsQuery = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
            Recursive = true,
            MediaTypes = new[] { MediaType.Book, MediaType.Audio }
        };

        var linkedItems = _libraryManager.GetItemList(linkedItemsQuery).ToList();

        if (linkedItems.Count == 0)
        {
            await report.WriteLineAsync("No items with an Audiobookshelf provider ID found.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        await report.WriteLineAsync($"Items with ABS ID: {linkedItems.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        var cleanupList = new List<(BaseItem Item, string AbsId)>();
        int checkedCount = 0;

        await report.WriteLineAsync("--- Checking library membership ---").ConfigureAwait(false);

        foreach (var item in linkedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)checkedCount / linkedItems.Count * 50.0);

            if (!item.TryGetProviderId("Audiobookshelf", out string? absId) || string.IsNullOrWhiteSpace(absId))
            {
                checkedCount++;
                continue;
            }

            var itemLibraryId = GetTopParentId(item);

            if (itemLibraryId != Guid.Empty && !topParentGuids.Contains(itemLibraryId))
            {
                await report.WriteLineAsync($"  OUT-OF-SCOPE : \"{item.Name}\"  [ABS: {absId}]").ConfigureAwait(false);
                cleanupList.Add((item, absId!));
            }

            checkedCount++;
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync($"Items outside selected libraries: {cleanupList.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        int cleaned = 0;

        if (cleanupList.Count > 0)
        {
            await report.WriteLineAsync("--- Removing invalid links ---").ConfigureAwait(false);

            foreach (var (item, absId) in cleanupList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(50.0 + (double)cleaned / cleanupList.Count * 50.0);

                item.ProviderIds.Remove("Audiobookshelf");
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                await report.WriteLineAsync($"  CLEANED      : \"{item.Name}\"  (removed: {absId})").ConfigureAwait(false);
                cleaned++;
            }
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync("Summary").ConfigureAwait(false);
        await report.WriteLineAsync($"  Total checked  : {checkedCount}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Cleaned up     : {cleaned}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Report written : {reportPath}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);

        await report.FlushAsync(cancellationToken).ConfigureAwait(false);

        LogCleanupComplete(_logger, cleaned, checkedCount - cleanupList.Count);
        _logger.LogInformation("ABS invalid links cleanup report written to {ReportPath}", reportPath);

        progress.Report(100);
    }

    private static Guid GetTopParentId(BaseItem item)
    {
        var current = item;
        while (current != null)
        {
            var parent = current.GetParent();
            if (parent is AggregateFolder)
            {
                return current.Id;
            }

            current = parent;
        }

        return item.Id;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ABS invalid links cleanup complete — {Cleaned} removed, {InScope} in-scope")]
    private static partial void LogCleanupComplete(ILogger logger, int cleaned, int inScope);
}
