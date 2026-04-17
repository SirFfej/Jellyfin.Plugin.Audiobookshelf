using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that pushes Jellyfin playback progress to Audiobookshelf for all mapped users.
/// Complements <see cref="ProgressSyncService"/>, which handles real-time per-event pushes.
/// This task is useful for bulk re-sync after ABS was unreachable, or as a periodic backstop.
/// </summary>
public partial class OutboundSyncTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<OutboundSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundSyncTask"/> class.
    /// </summary>
    public OutboundSyncTask(
        AbsApiClientFactory clientFactory,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<OutboundSyncTask> logger)
    {
        _clientFactory = clientFactory;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf: Push Progress to ABS";

    /// <inheritdoc />
    public string Key => "AbsOutboundProgressSync";

    /// <inheritdoc />
    public string Description => "Pushes current Jellyfin playback positions to Audiobookshelf for all mapped users.";

    /// <inheritdoc />
    public string Category => "Audiobookshelf";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableOutboundSync != true)
        {
            _logger.LogDebug("ABS outbound sync is disabled — skipping");
            progress.Report(100);
            return;
        }

        // Build the set of user token pairs to sync
        var userTokenPairs = new List<(string JellyfinUserId, string AbsToken)>();

        foreach (var kv in config.UserTokenMap)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
            {
                userTokenPairs.Add((kv.Key, kv.Value));
            }
        }

        // Always include admin token if it isn't already in the map
        if (!string.IsNullOrWhiteSpace(config.AdminApiToken)
            && !userTokenPairs.Any(p => p.AbsToken == config.AdminApiToken))
        {
            var adminClient = _clientFactory.GetAdminClient();
            var adminAbsUser = await adminClient.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            if (adminAbsUser is not null)
            {
                userTokenPairs.Add((adminAbsUser.Id, config.AdminApiToken));
            }
        }

        if (userTokenPairs.Count == 0)
        {
            _logger.LogInformation("ABS outbound sync: no user tokens configured");
            progress.Report(100);
            return;
        }

        int total = userTokenPairs.Count;
        int completed = 0;

        var syncTasks = userTokenPairs.Select(async pair =>
        {
            try
            {
                await SyncUserProgressAsync(pair.JellyfinUserId, pair.AbsToken, cancellationToken).ConfigureAwait(false);
                return (pair.JellyfinUserId, Success: true, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (pair.JellyfinUserId, Success: false, Error: ex);
            }
            finally
            {
                int done = Interlocked.Increment(ref completed);
                progress.Report((double)done / total * 100.0);
            }
        });

        var results = await Task.WhenAll(syncTasks).ConfigureAwait(false);

        foreach (var (userId, success, error) in results)
        {
            if (!success && error is not null)
            {
                LogSyncFailed(_logger, error, userId);
            }
        }

        int failed = results.Count(r => !r.Success);
        if (failed > 0)
        {
            _logger.LogWarning("ABS outbound sync completed with {FailedCount} user(s) failed", failed);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    /// <remarks>
    /// No default triggers — the real-time <see cref="ProgressSyncService"/> handles day-to-day pushes.
    /// This task is intended for on-demand use or as a manual backstop.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    // -------------------------------------------------------------------------

    private async Task SyncUserProgressAsync(
        string jellyfinUserId,
        string absToken,
        CancellationToken ct)
    {
        if (!Guid.TryParse(jellyfinUserId, out var jellyfinGuid))
        {
            LogInvalidUserId(_logger, jellyfinUserId);
            return;
        }

        var jellyfinUser = _userManager.GetUserById(jellyfinGuid);
        if (jellyfinUser is null)
        {
            LogUserNotFound(_logger, jellyfinUserId);
            return;
        }

        var absClient = _clientFactory.GetClientForToken(absToken);

        var config = Plugin.Instance!.Configuration;
        var includedLibraryIds = config.IncludedLibraryIds;

        var query = new InternalItemsQuery
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
                query.TopParentIds = topParentGuids;
            }
        }

        // Find all Jellyfin items that have an ABS provider ID
        var absItems = _libraryManager.GetItemList(query);

        foreach (var item in absItems)
        {
            ct.ThrowIfCancellationRequested();

            if (!item.TryGetProviderId("Audiobookshelf", out string? absItemId)
                || string.IsNullOrWhiteSpace(absItemId))
            {
                continue;
            }

            var userData = _userDataManager.GetUserData(jellyfinUser, item);
            if (userData is null)
            {
                continue;
            }

            // Skip items with no meaningful playback data
            if (userData.PlaybackPositionTicks == 0 && !userData.Played)
            {
                continue;
            }

            double currentSecs = TimeHelper.TicksToSeconds(userData.PlaybackPositionTicks);
            double duration = item.RunTimeTicks.HasValue
                ? TimeHelper.TicksToSeconds(item.RunTimeTicks.Value)
                : 0;

            bool ok = await absClient.UpdateProgressAsync(
                absItemId!,
                currentSecs,
                duration,
                userData.Played,
                hideFromContinueListening: false,
                markAsFinishedTimeRemaining: 10,
                lastUpdate: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                episodeId: null,
                ct: ct).ConfigureAwait(false);

            if (ok)
            {
                LogProgressPushed(_logger, absItemId!, currentSecs);
            }
        }
    }

    // ── Source-generated log methods ──────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping outbound sync: invalid Jellyfin user ID '{UserId}'")]
    private static partial void LogInvalidUserId(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Jellyfin user '{UserId}' not found — skipping ABS outbound sync")]
    private static partial void LogUserNotFound(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pushed ABS progress for item '{ItemId}' → {CurrentTime:F1}s")]
    private static partial void LogProgressPushed(ILogger logger, string itemId, double currentTime);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbound sync failed for user '{UserId}'")]
    private static partial void LogSyncFailed(ILogger logger, Exception ex, string userId);
}
