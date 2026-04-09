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
public sealed partial class ProgressSyncService : IDisposable
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
        LogStarted(_logger);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (Plugin.Instance?.Configuration.EnableOutboundSync != true)
        {
            return;
        }

        if (e.SaveReason != UserDataSaveReason.PlaybackProgress
            && e.SaveReason != UserDataSaveReason.PlaybackFinished)
        {
            return;
        }

        if (!e.Item.TryGetProviderId("Audiobookshelf", out string? absItemId)
            || string.IsNullOrWhiteSpace(absItemId))
        {
            return;
        }

        string userId = e.UserId.ToString("N");
        double currentSecs = TimeHelper.TicksToSeconds(e.UserData.PlaybackPositionTicks);
        bool isFinished = e.UserData.Played;
        double duration = e.Item.RunTimeTicks.HasValue
            ? TimeHelper.TicksToSeconds(e.Item.RunTimeTicks.Value)
            : 0;

        string debounceKey = $"{userId}:{absItemId}";
        CancellationTokenSource? oldCts = null;
        var cts = new CancellationTokenSource();
        _pending.AddOrUpdate(debounceKey, cts, (_, existing) => { oldCts = existing; return cts; });
        oldCts?.Cancel();
        oldCts?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, cts.Token).ConfigureAwait(false);
                _pending.TryRemove(debounceKey, out _);

                var client = _clientFactory.GetClientForUser(userId);
                bool ok = await client.UpdateProgressAsync(
                    absItemId,
                    currentSecs,
                    duration,
                    isFinished,
                    hideFromContinueListening: false,
                    markAsFinishedTimeRemaining: 10,
                    lastUpdate: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    episodeId: null,
                    CancellationToken.None)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    LogProgressUpdateFailed(_logger, absItemId, userId);
                }
            }
            catch (OperationCanceledException)
            {
                // Debounced — a newer update supersedes this one
            }
            catch (Exception ex)
            {
                LogProgressSyncError(_logger, ex, absItemId);
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

    // ── Source-generated log methods (zero allocation on hot path) ────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Audiobookshelf progress sync service started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Progress update failed for item {ItemId}, user {UserId}")]
    private static partial void LogProgressUpdateFailed(ILogger logger, string itemId, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected error syncing progress for item {ItemId}")]
    private static partial void LogProgressSyncError(ILogger logger, Exception ex, string itemId);
}
