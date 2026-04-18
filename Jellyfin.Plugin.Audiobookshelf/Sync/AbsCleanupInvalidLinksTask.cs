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
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

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

    public string Name => "Audiobookshelf: Cleanup - Remove Invalid Links";

    public string Key => "AbsCleanupInvalidLinks";

    public string Description =>
        "Removes Audiobookshelf provider IDs from items that are not in selected libraries. " +
        "Use this when library selection changes to unlink items from removed libraries.";

    public string Category => "Audiobookshelf";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping invalid link cleanup");
            progress.Report(100);
            return;
        }

        var reportPath = Path.Combine(
            _appPaths.LogDirectoryPath,
            $"audiobookshelf-invalid-links-cleanup-{DateTime.Now:yyyyMMddHHmmss}.log");

        await using var report = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        report.AutoFlush = false;

        await report.WriteLineAsync($"Audiobookshelf Invalid Links Cleanup — {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        var includedLibraryIds = config.IncludedLibraryIds;

        if (includedLibraryIds.Count == 0)
        {
            await report.WriteLineAsync("No libraries selected — nothing to clean up.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ABS invalid links cleanup: no libraries selected");
            progress.Report(100);
            return;
        }

        await report.WriteLineAsync("Raw config IncludedLibraryIds:").ConfigureAwait(false);
        foreach (var libId in includedLibraryIds)
        {
            await report.WriteLineAsync($"  - \"{libId}\"").ConfigureAwait(false);
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        var selectedGuids = includedLibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        await report.WriteLineAsync($"Parsed GUIDs: {selectedGuids.Count}").ConfigureAwait(false);
        foreach (var guid in selectedGuids)
        {
            await report.WriteLineAsync($"  - {guid}").ConfigureAwait(false);
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        await report.WriteLineAsync("Available libraries from GetVirtualFolders:").ConfigureAwait(false);
        foreach (var lf in _libraryManager.GetVirtualFolders())
        {
            await report.WriteLineAsync($"  - {lf.Name} (ItemId: {lf.ItemId}, Type: {lf.CollectionType})").ConfigureAwait(false);
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        var matchingLibraries = _libraryManager.GetVirtualFolders()
            .Where(lf => selectedGuids.Contains(Guid.Parse(lf.ItemId.ToString())))
            .Select(lf => $"{lf.Name} ({lf.ItemId})")
            .ToList();

        await report.WriteLineAsync($"Matching libraries: {matchingLibraries.Count}").ConfigureAwait(false);
        foreach (var name in matchingLibraries)
        {
            await report.WriteLineAsync($"  - {name}").ConfigureAwait(false);
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        if (matchingLibraries.Count == 0)
        {
            await report.WriteLineAsync("WARNING: No libraries found matching selected IDs!").ConfigureAwait(false);
            await report.WriteLineAsync("This means ALL items with ABS IDs will be cleaned up.").ConfigureAwait(false);
        }

        var inScopeItems = new List<BaseItem>();

        foreach (var lib in matchingLibraries)
        {
            var folder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => f.Name == lib);

            if (folder == null)
            {
                continue;
            }

            var folderId = Guid.Parse(folder.ItemId.ToString());

            var libQuery = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
                Recursive = true,
                ParentId = folderId,
                MediaTypes = new[] { MediaType.Book, MediaType.Audio }
            };

            var libItems = _libraryManager.GetItemList(libQuery);
            inScopeItems.AddRange(libItems);
        }

        var inScopeIds = inScopeItems.Select(i => i.Id).ToHashSet();

        await report.WriteLineAsync($"Items with ABS ID in selected libraries: {inScopeIds.Count}").ConfigureAwait(false);
        if (inScopeIds.Count > 0 && inScopeIds.Count <= 20)
        {
            foreach (var item in inScopeItems)
            {
                item.TryGetProviderId("Audiobookshelf", out string? absId);
                await report.WriteLineAsync($"  - \"{item.Name}\" [{item.Id}] ABS: {absId}").ConfigureAwait(false);
            }
        }
        await report.WriteLineAsync().ConfigureAwait(false);

        var allLinkedQuery = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
            Recursive = true,
            MediaTypes = new[] { MediaType.Book, MediaType.Audio }
        };

        var outOfScopeItems = _libraryManager.GetItemList(allLinkedQuery)
            .Where(item => !inScopeIds.Contains(item.Id))
            .ToList();

        await report.WriteLineAsync($"Items with ABS ID outside selected libraries: {outOfScopeItems.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        int cleaned = 0;
        int total = outOfScopeItems.Count;

        if (total > 0)
        {
            await report.WriteLineAsync("--- Removing invalid links ---").ConfigureAwait(false);

            foreach (var item in outOfScopeItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report((double)cleaned / total * 100.0);

                if (!item.TryGetProviderId("Audiobookshelf", out string? absId) || string.IsNullOrWhiteSpace(absId))
                {
                    continue;
                }

                item.ProviderIds.Remove("Audiobookshelf");
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                await report.WriteLineAsync($"  CLEANED      : \"{item.Name}\"  (removed: {absId})").ConfigureAwait(false);
                cleaned++;
            }
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync("Summary").ConfigureAwait(false);
        await report.WriteLineAsync($"  In-scope items : {inScopeIds.Count}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Cleaned up     : {cleaned}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Report written : {reportPath}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);

        await report.FlushAsync(cancellationToken).ConfigureAwait(false);

        LogCleanupComplete(_logger, cleaned, inScopeIds.Count);
        _logger.LogInformation("ABS invalid links cleanup complete — {Cleaned} removed, {InScope} in-scope", cleaned, inScopeIds.Count);

        progress.Report(100);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ABS invalid links cleanup complete — {Cleaned} removed, {InScope} in-scope")]
    private static partial void LogCleanupComplete(ILogger logger, int cleaned, int inScope);
}
