using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Sync;

/// <summary>
/// Pushes Jellyfin playback progress to Audiobookshelf in real time.
/// Subscribes to <see cref="IUserDataManager.UserDataSaved"/> and debounces
/// updates per-item (10 s) to avoid hammering the ABS server.
/// Also subscribes to <see cref="ISessionManager.PlaybackStopped"/> and
/// <see cref="ISessionManager.PlaybackProgress"/> (for pause detection) to
/// push progress immediately on stop or pause without waiting for the debounce.
/// </summary>
public sealed partial class ProgressSyncService : IDisposable
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
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
        ISessionManager sessionManager,
        ILogger<ProgressSyncService> logger)
    {
        _clientFactory = clientFactory;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _logger = logger;

        _userDataManager.UserDataSaved += OnUserDataSaved;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
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

                // Only proceed if this CTS is still the current one for the key.
                // If a newer update arrived and replaced it, TryRemove returns false
                // and we bail out to avoid sending a duplicate request.
                if (!_pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(debounceKey, cts)))
                {
                    return;
                }

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
                    cts.Token)
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

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        // Push final position to ABS immediately when playback stops,
        // without waiting for the 10 s debounce.
        PushImmediately(e);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        // Push current position immediately when the user pauses.
        if (!e.IsPaused)
        {
            return;
        }

        PushImmediately(e);
    }

    private void PushImmediately(PlaybackProgressEventArgs e)
    {
        if (Plugin.Instance?.Configuration.EnableOutboundSync != true)
        {
            return;
        }

        if (e.Item is null || e.Session is null)
        {
            return;
        }

        if (!e.Item.TryGetProviderId("Audiobookshelf", out string? absItemId)
            || string.IsNullOrWhiteSpace(absItemId))
        {
            return;
        }

        string userId = e.Session.UserId.ToString("N");
        double currentSecs = e.PlaybackPositionTicks.HasValue
            ? TimeHelper.TicksToSeconds(e.PlaybackPositionTicks.Value)
            : 0;
        bool isFinished = e is PlaybackStopEventArgs stop && stop.PlayedToCompletion;
        double duration = e.Item.RunTimeTicks.HasValue
            ? TimeHelper.TicksToSeconds(e.Item.RunTimeTicks.Value)
            : 0;

        string debounceKey = $"{userId}:{absItemId}";

        // Cancel any pending debounced update — this immediate push supersedes it.
        if (_pending.TryRemove(debounceKey, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        _ = Task.Run(async () =>
        {
            try
            {
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
            catch (Exception ex)
            {
                LogProgressSyncError(_logger, ex, absItemId);
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;

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
