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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that audits every Jellyfin <see cref="Book"/> whose stored
/// <c>Audiobookshelf</c> provider ID points to a missing or deleted ABS item,
/// attempts to re-match it against the current ABS library, and queues a metadata
/// refresh for any book that was successfully re-linked.
///
/// A dedicated report is written to
/// <c>{LogDirectory}/audiobookshelf-cleanup-yyyyMMddHHmmss.log</c> each run so
/// administrators can review exactly what was re-linked, what couldn't be matched,
/// and what was skipped.
/// </summary>
public partial class AbsLinkCleanupTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<AbsLinkCleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsLinkCleanupTask"/> class.
    /// </summary>
    public AbsLinkCleanupTask(
        AbsApiClientFactory clientFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        ILogger<AbsLinkCleanupTask> logger)
    {
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf: Repair Broken Links";

    /// <inheritdoc />
    public string Key => "AbsLinkCleanup";

    /// <inheritdoc />
    public string Description =>
        "Checks every audiobook whose Audiobookshelf ID points to a missing or deleted ABS item and " +
        "attempts to re-match it against the current ABS library. A detailed report is written to the " +
        "Jellyfin log directory each run.";

    /// <inheritdoc />
    public string Category => "Audiobookshelf";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly on Sunday at 03:00 — low-traffic time, not time-critical
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            _logger.LogDebug("ABS metadata provider is disabled — skipping link cleanup");
            progress.Report(100);
            return;
        }

        string reportPath = Path.Combine(
            _appPaths.LogDirectoryPath,
            $"audiobookshelf-cleanup-{DateTime.Now:yyyyMMddHHmmss}.log");

        await using var report = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        report.AutoFlush = false;

        await report.WriteLineAsync($"Audiobookshelf Link Cleanup — {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        // ── Step 1: collect all Jellyfin books with an ABS provider ID ────────
        var config = Plugin.Instance!.Configuration;
        var includedLibraryIds = config.IncludedLibraryIds;

        var linkedBooksQuery = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
            Recursive = true
        };

        if (includedLibraryIds.Count > 0)
        {
            var topParentGuids = includedLibraryIds
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToArray();

            if (topParentGuids.Length > 0)
            {
                linkedBooksQuery.TopParentIds = topParentGuids;
            }
        }

        var linkedBooks = _libraryManager.GetItemList(linkedBooksQuery)
            .OfType<Book>()
            .ToList();

        if (linkedBooks.Count == 0)
        {
            await report.WriteLineAsync("No books with an Audiobookshelf provider ID found. Nothing to check.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        await report.WriteLineAsync($"Books with stored ABS ID: {linkedBooks.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        AbsApiClientFactory clientFactory;
        try
        {
            clientFactory = _clientFactory;
            _ = clientFactory.GetAdminClient(); // validates config
        }
        catch (InvalidOperationException ex)
        {
            await report.WriteLineAsync($"ERROR: ABS not configured — {ex.Message}").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("ABS link cleanup skipped — plugin not configured");
            progress.Report(100);
            return;
        }

        var adminClient = _clientFactory.GetAdminClient();

        // ── Step 2: identify broken links ─────────────────────────────────────
        var brokenBooks = new List<(Book Book, string OldAbsId)>();
        int checkedCount = 0;

        await report.WriteLineAsync("--- Checking existing links ---").ConfigureAwait(false);

        foreach (var book in linkedBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)checkedCount / linkedBooks.Count * 50.0);

            if (!book.TryGetProviderId("Audiobookshelf", out string? absId) || string.IsNullOrWhiteSpace(absId))
            {
                checkedCount++;
                continue;
            }

            var absItem = await adminClient.GetItemAsync(absId!, cancellationToken).ConfigureAwait(false);

            if (absItem is null)
            {
                // 404 — item was deleted from ABS
                await report.WriteLineAsync($"  BROKEN (deleted) : \"{book.Name}\"  [{absId}]").ConfigureAwait(false);
                brokenBooks.Add((book, absId!));
            }
            else if (absItem.IsMissing)
            {
                // Still in ABS but files are gone
                await report.WriteLineAsync($"  BROKEN (missing)  : \"{book.Name}\"  [{absId}]").ConfigureAwait(false);
                brokenBooks.Add((book, absId!));
            }
            else
            {
                await report.WriteLineAsync($"  OK                : \"{book.Name}\"  [{absId}]").ConfigureAwait(false);
            }

            checkedCount++;
        }

        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync($"Broken links found: {brokenBooks.Count}").ConfigureAwait(false);
        await report.WriteLineAsync().ConfigureAwait(false);

        if (brokenBooks.Count == 0)
        {
            await report.WriteLineAsync("All links are healthy. No re-matching required.").ConfigureAwait(false);
            await report.FlushAsync(cancellationToken).ConfigureAwait(false);
            LogCleanupComplete(_logger, 0, 0, linkedBooks.Count);
            progress.Report(100);
            return;
        }

        // ── Step 3: load full ABS library for re-matching ─────────────────────
        await report.WriteLineAsync("--- Loading ABS library for re-matching ---").ConfigureAwait(false);

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

        // ── Step 4: attempt re-match for each broken book ─────────────────────
        await report.WriteLineAsync("--- Re-matching broken links ---").ConfigureAwait(false);

        int relinked = 0;
        int unmatched = 0;
        int bookIndex = 0;

        foreach (var (book, oldAbsId) in brokenBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(50.0 + ((double)bookIndex / brokenBooks.Count * 50.0));

            // Book doesn't expose author directly on the entity — pass null so
            // ItemMatcher falls back to title-only scoring (same as metadata provider).
            var newMatch = ItemMatcher.FindBestMatch(
                asin: null,
                isbn: null,
                title: book.Name ?? string.Empty,
                authorName: null,
                absItems: allAbsItems);

            if (newMatch is null)
            {
                await report.WriteLineAsync(
                    $"  NO MATCH : \"{book.Name}\"  (old: {oldAbsId})").ConfigureAwait(false);
                LogNoMatch(_logger, book.Name ?? string.Empty, oldAbsId);
                unmatched++;
            }
            else if (string.Equals(newMatch.Id, oldAbsId, StringComparison.OrdinalIgnoreCase))
            {
                // Matched to the same (still-missing) item — leave it, don't re-queue
                await report.WriteLineAsync(
                    $"  SAME ID  : \"{book.Name}\"  [{newMatch.Id}] — re-matched to same missing item, skipping").ConfigureAwait(false);
                unmatched++;
            }
            else
            {
                // Found a different, non-missing item
                book.ProviderIds["Audiobookshelf"] = newMatch.Id;
                await _libraryManager.UpdateItemAsync(book, book.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                _providerManager.QueueRefresh(
                    book.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false
                    },
                    RefreshPriority.Normal);

                await report.WriteLineAsync(
                    $"  RELINKED : \"{book.Name}\"  {oldAbsId} → {newMatch.Id}  (\"{newMatch.Media.Metadata.Title}\")").ConfigureAwait(false);
                LogRelinked(_logger, book.Name ?? string.Empty, oldAbsId, newMatch.Id);
                relinked++;
            }

            bookIndex++;
        }

        // ── Summary ───────────────────────────────────────────────────────────
        await report.WriteLineAsync().ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);
        await report.WriteLineAsync($"Summary").ConfigureAwait(false);
        await report.WriteLineAsync($"  Total checked  : {linkedBooks.Count}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Broken links   : {brokenBooks.Count}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Re-linked      : {relinked}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Could not match: {unmatched}").ConfigureAwait(false);
        await report.WriteLineAsync($"  Report written : {reportPath}").ConfigureAwait(false);
        await report.WriteLineAsync(new string('=', 60)).ConfigureAwait(false);

        await report.FlushAsync(cancellationToken).ConfigureAwait(false);

        LogCleanupComplete(_logger, relinked, unmatched, linkedBooks.Count);
        _logger.LogInformation("ABS link cleanup report written to {ReportPath}", reportPath);

        progress.Report(100);
    }

    // ── Source-generated log methods ──────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ABS link cleanup complete — {Relinked} re-linked, {Unmatched} unmatched, {Total} total checked")]
    private static partial void LogCleanupComplete(ILogger logger, int relinked, int unmatched, int total);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ABS link cleanup: no re-match found for \"{BookName}\" (old ID: {OldId})")]
    private static partial void LogNoMatch(ILogger logger, string bookName, string oldId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ABS link cleanup: re-linked \"{BookName}\" from {OldId} → {NewId}")]
    private static partial void LogRelinked(ILogger logger, string bookName, string oldId, string newId);
}
