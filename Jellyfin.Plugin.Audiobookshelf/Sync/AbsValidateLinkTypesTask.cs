using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

public sealed partial class AbsValidateLinkTypesTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<AbsValidateLinkTypesTask> _logger;

    public AbsValidateLinkTypesTask(
        AbsApiClientFactory clientFactory,
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        ILogger<AbsValidateLinkTypesTask> logger)
    {
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    public string Name => "Audiobookshelf: Validate Link Types";

    public string Key => "AbsValidateLinkTypes";

    public string Description =>
        "Validates that linked items match file types (audiobook to audio, ebook to ebook). " +
        "Removes links where file type has changed in ABS. A report is written to the log directory.";

    public string Category => "Audiobookshelf";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping link validation");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.AbsServerUrl) || string.IsNullOrWhiteSpace(config.AdminApiToken))
        {
            _logger.LogDebug("ABS not configured — skipping link validation");
            progress.Report(100);
            return;
        }

        var reportPath = Path.Combine(
            _appPaths.LogDirectoryPath,
            $"audiobookshelf-link-validation-{DateTime.Now:yyyyMMddHHmmss}.log");

        await using var report = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        report.AutoFlush = false;

        await report.WriteLineAsync($"Audiobookshelf Link Validation — {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        var includedLibraryIds = config.IncludedLibraryIds;

        var selectedGuids = includedLibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var matchingLibraries = _libraryManager.GetVirtualFolders()
            .Where(lf => selectedGuids.Contains(Guid.Parse(lf.ItemId.ToString())))
            .ToList();

        var linkedItems = new List<BaseItem>();

        foreach (var lib in matchingLibraries)
        {
            var folder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => f.Name == lib.Name);

            if (folder == null)
            {
                continue;
            }

            var folderId = Guid.Parse(folder.ItemId.ToString());

            var libQuery = new InternalItemsQuery
            {
                Recursive = true
            };

            var libFolders = _libraryManager.GetItemList(libQuery)
                .Where(i => i.ParentId == folderId)
                .ToList();

            var linked = libFolders
                .Where(i => i.ProviderIds.ContainsKey("Audiobookshelf"))
                .ToList();

            linkedItems.AddRange(linked);
            await report.WriteLineAsync($"Library '{lib.Name}': {linked.Count} linked items").ConfigureAwait(false);
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync($"Total linked items: {linkedItems.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        if (linkedItems.Count == 0)
        {
            await report.WriteLineAsync("No linked items found.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        var adminClient = _clientFactory.GetAdminClient();

        var mismatched = new List<(BaseItem Item, string AbsId, string Reason)>();
        int checkedCount = 0;

        await report.WriteLineAsync("--- Validating links ---").ConfigureAwait(false);

        foreach (var item in linkedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)checkedCount / linkedItems.Count * 100.0);

            string? absId = item.ProviderIds.GetValueOrDefault("Audiobookshelf");
            if (string.IsNullOrWhiteSpace(absId))
            {
                checkedCount++;
                continue;
            }

            string? container = item.Container;
            bool jellyfinIsEbook = !string.IsNullOrWhiteSpace(container) &&
                (container.EndsWith("epub", StringComparison.OrdinalIgnoreCase) ||
                 container.EndsWith("pdf", StringComparison.OrdinalIgnoreCase));

            try
            {
                var absItem = await adminClient.GetItemAsync(absId!, cancellationToken).ConfigureAwait(false);

                if (absItem is null)
                {
                    await report.WriteLineAsync($"  MISSING      : \"{item.Name}\"  [{absId}] — ABS item deleted").ConfigureAwait(false);
                    mismatched.Add((item, absId!, "ABS item deleted"));
                }
                else
                {
                    bool absHasEbook = absItem.Media.EbookFile is not null;
                    bool absHasAudio = absItem.Media.AudioFiles.Length > 0;

                    if (jellyfinIsEbook && !absHasEbook)
                    {
                        await report.WriteLineAsync($"  MISMATCH    : \"{item.Name}\"  [{absId}] — Jellyfin ebook, ABS has no ebook file").ConfigureAwait(false);
                        mismatched.Add((item, absId!, "Jellyfin ebook, ABS has no ebook file"));
                    }
                    else if (!jellyfinIsEbook && !absHasAudio)
                    {
                        await report.WriteLineAsync($"  MISMATCH    : \"{item.Name}\"  [{absId}] — Jellyfin audio, ABS has no audio file").ConfigureAwait(false);
                        mismatched.Add((item, absId!, "Jellyfin audio, ABS has no audio file"));
                    }
                    else
                    {
                        await report.WriteLineAsync($"  OK          : \"{item.Name}\"  [{absId}]").ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                await report.WriteLineAsync($"  ERROR       : \"{item.Name}\"  [{absId}] — {ex.Message}").ConfigureAwait(false);
                mismatched.Add((item, absId!, $"Error: {ex.Message}"));
            }

            checkedCount++;
        }

        await report.WriteLineAsync().ConfigureAwait(false);

        int removed = 0;
        if (mismatched.Count > 0)
        {
            await report.WriteLineAsync($"--- Removing {mismatched.Count} mismatched links ---").ConfigureAwait(false);

            foreach (var (item, absId, reason) in mismatched)
            {
                cancellationToken.ThrowIfCancellationRequested();

                item.ProviderIds.Remove("Audiobookshelf");
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                await report.WriteLineAsync($"  REMOVED    : \"{item.Name}\"  ({reason})").ConfigureAwait(false);
                removed++;
            }
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync("Summary").ConfigureAwait(false);
        await report.WriteLineAsync($"  Items checked: {checkedCount}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Mismatched: {mismatched.Count}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Links removed: {removed}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Report: {reportPath}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);

        await report.FlushAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("ABS link validation complete — {Checked} checked, {Mismatched} mismatched, {Removed} removed",
            checkedCount, mismatched.Count, removed);

        progress.Report(100);
    }
}