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
public class InboundSyncTask : IScheduledTask
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

        double step = 100.0 / userTokenPairs.Count;
        int processed = 0;

        foreach (var (jellyfinUserId, absToken) in userTokenPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await SyncUserProgressAsync(jellyfinUserId, absToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ABS inbound sync failed for user {UserId}", jellyfinUserId);
            }

            processed++;
            progress.Report(processed * step);
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
            _logger.LogWarning("Skipping sync for invalid Jellyfin user ID: {Id}", jellyfinUserId);
            return;
        }

        User? jellyfinUser = _userManager.GetUserById(jellyfinGuid);
        if (jellyfinUser is null)
        {
            _logger.LogDebug("Jellyfin user {UserId} not found — skipping ABS sync", jellyfinUserId);
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

            _logger.LogDebug("Updated Jellyfin progress for item {ItemId} from ABS ({CurrentTime}s)",
                absItemId, absProgress.CurrentTime);
        }
    }
}
