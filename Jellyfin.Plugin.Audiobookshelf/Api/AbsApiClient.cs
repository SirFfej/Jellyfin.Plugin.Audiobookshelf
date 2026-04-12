using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Api;

/// <summary>
/// Thin HTTP wrapper around the Audiobookshelf REST API.
/// One instance per ABS API token — obtain instances via <see cref="AbsApiClientFactory"/>.
/// </summary>
public class AbsApiClient
{
    /// <summary>Named client key registered with <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "AbsClient";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AbsApiClient> _logger;
    private readonly string _token;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">Pre-configured named HTTP client.</param>
    /// <param name="cache">Shared memory cache.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="baseUrl">ABS server base URL (no trailing slash).</param>
    /// <param name="token">ABS API bearer token for this client instance.</param>
    public AbsApiClient(
        HttpClient httpClient,
        IMemoryCache cache,
        ILogger<AbsApiClient> logger,
        string baseUrl,
        string token)
    {
        _http = httpClient;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        _cache = cache;
        _logger = logger;
        _token = token;
    }

    // -------------------------------------------------------------------------
    // Library browsing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all ABS libraries the token has access to. Cached for 5 minutes.
    /// </summary>
    public async Task<AbsLibrary[]> GetLibrariesAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrCreateAsync("abs:libraries", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var response = await GetAsync<AbsLibrariesResponse>("api/libraries", ct).ConfigureAwait(false);
            return response?.Libraries ?? [];
        }).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Returns a page of items from a library. Cached per (libraryId, page, limit) for 5 minutes.
    /// </summary>
    public async Task<AbsLibraryItemsResponse> GetLibraryItemsAsync(
        string libraryId,
        int page = 0,
        int limit = 100,
        CancellationToken ct = default)
    {
        string cacheKey = $"abs:items:{libraryId}:{page}:{limit}";
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await GetAsync<AbsLibraryItemsResponse>(
                $"api/libraries/{libraryId}/items?expanded=1&limit={limit}&page={page}",
                ct).ConfigureAwait(false) ?? new AbsLibraryItemsResponse();
        }).ConfigureAwait(false) ?? new AbsLibraryItemsResponse();
    }

    /// <summary>
    /// Returns full detail for a single library item. Cached for 5 minutes.
    /// </summary>
    public async Task<AbsLibraryItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        string cacheKey = $"abs:item:{itemId}";
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await GetAsync<AbsLibraryItem>($"api/items/{itemId}?expanded=1", ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Progress
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the current user's listening progress for a library item.
    /// Not cached — always fetches fresh data.
    /// </summary>
    /// <param name="libraryItemId">The ABS library item ID.</param>
    /// <param name="episodeId">Optional episode ID for podcasts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Progress data or null if not found.</returns>
    public Task<AbsMediaProgress?> GetProgressAsync(string libraryItemId, string? episodeId = null, CancellationToken ct = default)
    {
        var url = episodeId != null
            ? $"api/me/progress/{libraryItemId}/{episodeId}"
            : $"api/me/progress/{libraryItemId}";
        return GetAsync<AbsMediaProgress>(url, ct);
    }

    /// <summary>
    /// Updates the current user's listening progress for a library item.
    /// </summary>
    /// <param name="libraryItemId">The ABS library item ID.</param>
    /// <param name="currentTime">Current playback position in seconds.</param>
    /// <param name="duration">Total duration in seconds.</param>
    /// <param name="isFinished">Whether the item is marked as finished.</param>
    /// <param name="hideFromContinueListening">Whether to hide from continue listening.</param>
    /// <param name="markAsFinishedTimeRemaining">Seconds remaining to auto-mark as finished (default 10).</param>
    /// <param name="lastUpdate">Optional Unix ms timestamp for local sync.</param>
    /// <param name="episodeId">Optional episode ID for podcasts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if update succeeded.</returns>
    public async Task<bool> UpdateProgressAsync(
        string libraryItemId,
        double currentTime,
        double duration,
        bool isFinished,
        bool hideFromContinueListening = false,
        int markAsFinishedTimeRemaining = 10,
        long? lastUpdate = null,
        string? episodeId = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["currentTime"] = currentTime,
            ["duration"] = duration,
            ["isFinished"] = isFinished,
            ["progress"] = duration > 0 ? currentTime / duration : 0,
            ["hideFromContinueListening"] = hideFromContinueListening,
            ["markAsFinishedTimeRemaining"] = markAsFinishedTimeRemaining
        };

        if (lastUpdate.HasValue)
        {
            payload["lastUpdate"] = lastUpdate.Value;
        }

        var url = episodeId != null
            ? $"api/me/progress/{libraryItemId}/{episodeId}"
            : $"api/me/progress/{libraryItemId}";

        try
        {
            using var response = await _http
                .PatchAsJsonAsync(url, payload, JsonOptions, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ABS progress for item {ItemId}", libraryItemId);
            return false;
        }
    }

    /// <summary>
    /// Batch updates progress for multiple items.
    /// Used for local sync with lastUpdate timestamps.
    /// </summary>
    /// <param name="updates">Array of progress update payloads.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if batch update succeeded.</returns>
    public async Task<bool> BatchUpdateProgressAsync(
        IEnumerable<ProgressBatchItem> updates,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await _http
                .PatchAsJsonAsync("api/me/progress/batch/update", updates, JsonOptions, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to batch update ABS progress");
            return false;
        }
    }

    /// <summary>
    /// Gets all items currently in progress for the user.
    /// </summary>
    /// <param name="limit">Maximum number of items to return (default 25).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Items in progress response.</returns>
    public async Task<AbsItemsInProgressResponse?> GetItemsInProgressAsync(int limit = 25, CancellationToken ct = default)
        => await GetAsync<AbsItemsInProgressResponse>($"api/me/items-in-progress?limit={limit}", ct).ConfigureAwait(false);

    // -------------------------------------------------------------------------
    // Playback sessions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts an ABS playback session and returns the session data including audio track URLs.
    /// </summary>
    public async Task<AbsPlaybackSession?> StartPlaybackSessionAsync(
        string itemId,
        bool forceDirectPlay = true,
        CancellationToken ct = default)
    {
        var payload = new
        {
            forceDirectPlay,
            forceTranscode = false,
            mediaPlayer = "Jellyfin",
            supportedMimeTypes = new[] { "audio/mpeg", "audio/mp4", "audio/ogg", "audio/flac", "audio/x-m4b" }
        };

        try
        {
            using var response = await _http
                .PostAsJsonAsync($"api/items/{itemId}/play", payload, JsonOptions, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ABS play session returned {Status} for item {ItemId}",
                    response.StatusCode, itemId);
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<AbsPlaybackSession>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start ABS playback session for item {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// Syncs an open playback session with the current position.
    /// </summary>
    public async Task<bool> SyncSessionAsync(
        string sessionId,
        double currentTime,
        double timeListened,
        double duration = 0,
        CancellationToken ct = default)
    {
        var payload = new SessionSyncPayload
        {
            CurrentTime = currentTime,
            TimeListened = timeListened,
            Duration = duration
        };

        try
        {
            using var response = await _http
                .PostAsJsonAsync($"api/session/{sessionId}/sync", payload, JsonOptions, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync ABS session {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Closes an open playback session.
    /// </summary>
    public async Task<bool> CloseSessionAsync(
        string sessionId,
        double currentTime,
        double timeListened,
        CancellationToken ct = default)
    {
        var payload = new SessionClosePayload
        {
            CurrentTime = currentTime,
            TimeListened = timeListened
        };

        try
        {
            using var response = await _http
                .PostAsJsonAsync($"api/session/{sessionId}/close", payload, JsonOptions, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close ABS session {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Returns all currently open playback sessions (admin only).
    /// Used at startup to close orphaned Jellyfin sessions.
    /// </summary>
    public async Task<AbsOpenSessionsResponse?> GetOpenSessionsAsync(CancellationToken ct = default)
        => await GetAsync<AbsOpenSessionsResponse>("api/sessions/open", ct).ConfigureAwait(false);

    /// <summary>
    /// Syncs a local session (for mobile/offline sync).
    /// </summary>
    public async Task<bool> SyncLocalSessionAsync(
        string libraryItemId,
        double currentTime,
        double duration,
        long lastUpdate,
        CancellationToken ct = default)
    {
        var payload = new LocalSessionPayload
        {
            LibraryItemId = libraryItemId,
            CurrentTime = currentTime,
            Duration = duration,
            LastUpdate = lastUpdate
        };

        try
        {
            using var response = await _http
                .PostAsJsonAsync("api/session/local", payload, JsonOptions, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync local ABS session for item {ItemId}", libraryItemId);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Identity / connection test
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the ABS user associated with this client's token.
    /// Used for connection testing and user validation.
    /// </summary>
    public Task<AbsUser?> GetCurrentUserAsync(CancellationToken ct = default)
        => GetAsync<AbsUser>("api/me", ct);

    /// <summary>Gets the raw ABS API token used by this client (for URL construction).</summary>
    public string Token => _token;

    // -------------------------------------------------------------------------
    // User management (admin only)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all users from the ABS server (admin only).
    /// Each user includes their API token.
    /// </summary>
    public async Task<AbsAllUsersResponse?> GetAllUsersAsync(CancellationToken ct = default)
        => await GetAsync<AbsAllUsersResponse>("api/users", ct).ConfigureAwait(false);

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        const int maxRetries = 3;
        Exception? lastException = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await _http
                    .GetFromJsonAsync<T>(relativeUrl, JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 = resource genuinely doesn't exist (e.g. no progress record yet) — not an error
                _logger.LogDebug("ABS returned 404 for GET {Url} — treating as no data", relativeUrl);
                return null;
            }
            catch (HttpRequestException ex) when (attempt < maxRetries - 1)
            {
                lastException = ex;
                int delayMs = 100 * (1 << attempt);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ABS request error: GET {Url}", relativeUrl);
                return null;
            }
        }

        if (lastException is not null)
        {
            _logger.LogWarning(lastException, "ABS HTTP request failed after {MaxRetries} retries: GET {Url}", maxRetries, relativeUrl);
        }

        return null;
    }
}

/// <summary>
/// Wrapper for open sessions response from <c>GET /api/sessions/open</c>.
/// </summary>
public class AbsOpenSessionsResponse
{
    /// <summary>Gets or sets the open sessions.</summary>
    [JsonPropertyName("sessions")]
    public AbsPlaybackSession[] Sessions { get; set; } = [];
}

/// <summary>
/// Wrapper for all users response from <c>GET /api/users</c>.
/// </summary>
public class AbsAllUsersResponse
{
    /// <summary>Gets or sets the list of all users.</summary>
    [JsonPropertyName("users")]
    public AbsUser[] Users { get; set; } = [];
}

/// <summary>
/// Response from <c>GET /api/me/items-in-progress</c>.
/// </summary>
public class AbsItemsInProgressResponse
{
    /// <summary>Gets or sets the library items in progress.</summary>
    [JsonPropertyName("libraryItems")]
    public AbsLibraryItem[] LibraryItems { get; set; } = [];
}

/// <summary>
/// Payload for batch progress updates via <c>PATCH /api/me/progress/batch/update</c>.
/// </summary>
public class ProgressBatchItem
{
    /// <summary>Gets or sets the library item ID.</summary>
    [JsonPropertyName("libraryItemId")]
    public string LibraryItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode ID for podcasts (optional).</summary>
    [JsonPropertyName("episodeId")]
    public string? EpisodeId { get; set; }

    /// <summary>Gets or sets the current playback position in seconds.</summary>
    [JsonPropertyName("currentTime")]
    public double CurrentTime { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Gets or sets the fractional progress (0.0–1.0).</summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>Gets or sets whether the item is finished.</summary>
    [JsonPropertyName("isFinished")]
    public bool IsFinished { get; set; }

    /// <summary>Gets or sets the Unix ms timestamp for local sync.</summary>
    [JsonPropertyName("lastUpdate")]
    public long LastUpdate { get; set; }
}

/// <summary>
/// Payload for session sync via <c>POST /api/session/:id/sync</c>.
/// </summary>
public class SessionSyncPayload
{
    /// <summary>Gets or sets the current playback position in seconds.</summary>
    [JsonPropertyName("currentTime")]
    public double CurrentTime { get; set; }

    /// <summary>Gets or sets the total time listened in seconds.</summary>
    [JsonPropertyName("timeListened")]
    public double TimeListened { get; set; }

    /// <summary>Gets or sets the total duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Gets or sets the display title (optional).</summary>
    [JsonPropertyName("displayTitle")]
    public string? DisplayTitle { get; set; }

    /// <summary>Gets or sets the play method (optional).</summary>
    [JsonPropertyName("playMethod")]
    public string? PlayMethod { get; set; }
}

/// <summary>
/// Payload for session close via <c>POST /api/session/:id/close</c>.
/// </summary>
public class SessionClosePayload
{
    /// <summary>Gets or sets the current playback position in seconds.</summary>
    [JsonPropertyName("currentTime")]
    public double CurrentTime { get; set; }

    /// <summary>Gets or sets the total time listened in seconds.</summary>
    [JsonPropertyName("timeListened")]
    public double TimeListened { get; set; }
}

/// <summary>
/// Payload for local session sync via <c>POST /api/session/local</c>.
/// </summary>
public class LocalSessionPayload
{
    /// <summary>Gets or sets the library item ID.</summary>
    [JsonPropertyName("libraryItemId")]
    public string LibraryItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode ID for podcasts (optional).</summary>
    [JsonPropertyName("episodeId")]
    public string? EpisodeId { get; set; }

    /// <summary>Gets or sets the current playback position in seconds.</summary>
    [JsonPropertyName("currentTime")]
    public double CurrentTime { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Gets or sets the Unix ms timestamp of the update.</summary>
    [JsonPropertyName("lastUpdate")]
    public long LastUpdate { get; set; }

    /// <summary>Gets or sets whether the item is finished.</summary>
    [JsonPropertyName("isFinished")]
    public bool IsFinished { get; set; }
}
