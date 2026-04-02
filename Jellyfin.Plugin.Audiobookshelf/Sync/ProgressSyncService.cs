using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Pushes Jellyfin playback progress to Audiobookshelf in real time.
/// Subscribes to <see cref="IUserDataManager.UserDataSaved"/> and debounces
/// updates per-item (10 s) to avoid hammering the ABS server.
/// </summary>
public sealed class ProgressSyncService : IDisposable
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ProgressSyncService> _logger;

    // Debounce: one pending CTS per (userId, absItemId)
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressSyncService"/> class.
    /// </summary>
    public ProgressSyncService(
        AbsApiClientFactory clientFactory,
        IUserDataManager userDataManager,
        ILogger<ProgressSyncService> logger)
    {
        _clientFactory = clientFactory;
        _userDataManager = userDataManager;
        _logger = logger;

        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("ABS ProgressSyncService started");
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (Plugin.Instance?.Configuration.EnableOutboundSync != true)
        {
            return;
        }

        // Only sync playback events
        if (e.SaveReason != UserDataSaveReason.PlaybackProgress
            && e.SaveReason != UserDataSaveReason.PlaybackFinished)
        {
            return;
        }

        // Only items that have been matched/browsed via ABS
        if (!e.Item.TryGetProviderId("Audiobookshelf", out string? absItemId)
            || string.IsNullOrWhiteSpace(absItemId))
        {
            return;
        }

        string userId = e.UserId.ToString("N");
        double currentSecs = TimeHelper.TicksToSeconds(e.UserData.PlaybackPositionTicks);
        bool isFinished = e.UserData.Played;

        // Duration: try to read from provider IDs or fall back to 0 (ABS will ignore it)
        double duration = 0;

        // Debounce: cancel any existing pending update for this user+item pair
        string debounceKey = $"{userId}:{absItemId}";
        if (_pending.TryRemove(debounceKey, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pending[debounceKey] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, cts.Token).ConfigureAwait(false);
                _pending.TryRemove(debounceKey, out _);

                var client = _clientFactory.GetClientForUser(userId);
                bool ok = await client.UpdateProgressAsync(absItemId, currentSecs, duration, isFinished, CancellationToken.None)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    _logger.LogWarning("ABS progress update failed for item {ItemId}, user {UserId}", absItemId, userId);
                }
            }
            catch (OperationCanceledException)
            {
                // Debounced away — a newer update supersedes this one
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error syncing progress for item {ItemId}", absItemId);
            }
        }, cts.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;

        foreach (var cts in _pending.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _pending.Clear();
    }
}
