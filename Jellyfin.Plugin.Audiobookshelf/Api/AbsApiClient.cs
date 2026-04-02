using System;
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
    public Task<AbsMediaProgress?> GetProgressAsync(string libraryItemId, CancellationToken ct = default)
        => GetAsync<AbsMediaProgress>($"api/me/progress/{libraryItemId}", ct);

    /// <summary>
    /// Updates the current user's listening progress for a library item.
    /// </summary>
    public async Task<bool> UpdateProgressAsync(
        string libraryItemId,
        double currentTime,
        double duration,
        bool isFinished,
        CancellationToken ct = default)
    {
        var payload = new
        {
            currentTime,
            duration,
            isFinished,
            progress = duration > 0 ? currentTime / duration : 0
        };

        try
        {
            using var response = await _http
                .PatchAsJsonAsync($"api/me/progress/{libraryItemId}", payload, JsonOptions, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ABS progress for item {ItemId}", libraryItemId);
            return false;
        }
    }

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
        CancellationToken ct = default)
    {
        var payload = new { currentTime, timeListened, duration = 0 };
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
        var payload = new { currentTime, timeListened };
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
