using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Scheduled task that pulls ABS listening progress into Jellyfin for all mapped users.
/// Uses "last-write-wins" based on the ABS <c>lastUpdate</c> timestamp.
/// </summary>
public partial class InboundSyncTask : IScheduledTask
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<InboundSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboundSyncTask"/> class.
    /// </summary>
    public InboundSyncTask(
        AbsApiClientFactory clientFactory,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<InboundSyncTask> logger)
    {
        _clientFactory = clientFactory;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf: Pull Progress from ABS";

    /// <inheritdoc />
    public string Key => "AbsInboundProgressSync";

    /// <inheritdoc />
    public string Description => "Pulls listening progress from Audiobookshelf for all mapped users.";

    /// <inheritdoc />
    public string Category => "Audiobookshelf";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableInboundSync != true)
        {
            _logger.LogDebug("ABS inbound sync is disabled — skipping");
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
            _logger.LogInformation("ABS inbound sync: no user tokens configured");
            progress.Report(100);
            return;
        }

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
            _logger.LogWarning("ABS inbound sync completed with {FailedCount} user(s) failed", failed);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        int intervalMinutes = Plugin.Instance?.Configuration.ProgressSyncIntervalMinutes ?? 10;

        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks
            },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerStartup
            }
        ];
    }

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

        User? jellyfinUser = _userManager.GetUserById(jellyfinGuid);
        if (jellyfinUser is null)
        {
            LogUserNotFound(_logger, jellyfinUserId);
            return;
        }

        var absClient = _clientFactory.GetClientForToken(absToken);

        // Find all Jellyfin items that have an ABS provider ID
        var absItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Audiobookshelf"] = string.Empty },
            Recursive = true
        });

        foreach (var item in absItems)
        {
            ct.ThrowIfCancellationRequested();

            if (!item.TryGetProviderId("Audiobookshelf", out string? absItemId)
                || string.IsNullOrWhiteSpace(absItemId))
            {
                continue;
            }

            var absProgress = await absClient.GetProgressAsync(absItemId!, ct).ConfigureAwait(false);
            if (absProgress is null)
            {
                continue;
            }

            var jellyfinUserData = _userDataManager.GetUserData(jellyfinUser, item);
            if (jellyfinUserData is null)
            {
                continue;
            }

            DateTime absLastUpdate = TimeHelper.FromUnixMs(absProgress.LastUpdate);

            // Last-write-wins: only update Jellyfin if ABS is newer
            if (jellyfinUserData.LastPlayedDate.HasValue
                && jellyfinUserData.LastPlayedDate.Value >= absLastUpdate)
            {
                continue;
            }

            jellyfinUserData.PlaybackPositionTicks = TimeHelper.SecondsToTicks(absProgress.CurrentTime);
            jellyfinUserData.Played = absProgress.IsFinished;
            jellyfinUserData.LastPlayedDate = absProgress.IsFinished && absProgress.FinishedAt.HasValue
                ? TimeHelper.FromUnixMs(absProgress.FinishedAt.Value)
                : absLastUpdate;

            _userDataManager.SaveUserData(jellyfinUser, item, jellyfinUserData,
                UserDataSaveReason.Import, ct);

            LogProgressUpdated(_logger, absItemId!, absProgress.CurrentTime);
        }
    }

    // ── Source-generated log methods ──────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping sync: invalid Jellyfin user ID '{UserId}'")]
    private static partial void LogInvalidUserId(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Jellyfin user '{UserId}' not found — skipping ABS sync")]
    private static partial void LogUserNotFound(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated Jellyfin progress for ABS item '{ItemId}' → {CurrentTime:F1}s")]
    private static partial void LogProgressUpdated(ILogger logger, string itemId, double currentTime);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound sync failed for user '{UserId}'")]
    private static partial void LogSyncFailed(ILogger logger, Exception ex, string userId);
}
