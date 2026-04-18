using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Api;

/// <summary>
/// Creates and caches <see cref="AbsApiClient"/> instances keyed by API token.
/// </summary>
/// <remarks>
/// Token-scoped clients are cached so we don't recreate the underlying <see cref="HttpClient"/>
/// on every request.  The cache is cleared when the plugin configuration changes.
/// </remarks>
public class AbsApiClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<AbsApiClient> _clientLogger;
    private readonly ConcurrentDictionary<string, AbsApiClient> _clients = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsApiClientFactory"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating named HTTP clients.</param>
    /// <param name="memoryCache">Shared memory cache passed to each client.</param>
    /// <param name="clientLogger">Logger passed to each created client.</param>
    public AbsApiClientFactory(
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        ILogger<AbsApiClient> clientLogger)
    {
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _clientLogger = clientLogger;
    }

    /// <summary>
    /// Returns a client configured for the given token, creating one if necessary.
    /// </summary>
    /// <param name="token">ABS API bearer token.</param>
    /// <param name="explicitBaseUrl">
    /// Optional server URL override. When provided this takes priority over the saved
    /// plugin configuration, allowing callers (e.g. user-discovery) to target the URL
    /// currently in the admin form before it has been saved.
    /// </param>
    /// <returns>A configured <see cref="AbsApiClient"/>.</returns>
    public AbsApiClient GetClientForToken(string token, string? explicitBaseUrl = null)
    {
        var config = Plugin.Instance?.Configuration;
        string baseUrl = !string.IsNullOrWhiteSpace(explicitBaseUrl)
            ? explicitBaseUrl.TrimEnd('/')
            : config?.NormalizedServerUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Audiobookshelf server URL and API token must be configured before making API requests.");
        }

        // Invalidate cached clients if the server URL has changed
        if (_clients.Count > 0)
        {
            string currentPrefix = baseUrl + ":";
            bool hasMismatchedKeys = _clients.Keys.Any(k => !k.StartsWith(currentPrefix, StringComparison.Ordinal));
            if (hasMismatchedKeys)
            {
                InvalidateAll();
            }
        }

        string cacheKey = $"{baseUrl}:{token}";

        return _clients.GetOrAdd(cacheKey, _ =>
        {
            var httpClient = _httpClientFactory.CreateClient(AbsApiClient.HttpClientName);
            return new AbsApiClient(httpClient, _memoryCache, _clientLogger, baseUrl, token);
        });
    }

    /// <summary>
    /// Returns a client using the configured admin API token.
    /// </summary>
    public AbsApiClient GetAdminClient()
    {
        var token = Plugin.Instance?.Configuration.AdminApiToken ?? string.Empty;
        return GetClientForToken(token);
    }

    /// <summary>
    /// Returns the best available client for a Jellyfin user ID:
    /// the per-user token if configured, otherwise the admin token.
    /// </summary>
    /// <param name="jellyfinUserId">Jellyfin user GUID as string.</param>
    public AbsApiClient GetClientForUser(string jellyfinUserId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.UserTokenMap.TryGetValue(jellyfinUserId, out var userToken) == true
            && !string.IsNullOrWhiteSpace(userToken))
        {
            return GetClientForToken(userToken);
        }

        return GetAdminClient();
    }

    /// <summary>
    /// Clears all cached client instances (e.g. after a configuration change).
    /// </summary>
    public void InvalidateAll() => _clients.Clear();

    /// <summary>
    /// Gets cached ABS libraries with items for the admin client.
    /// Uses a 10-minute cache to avoid reloading on each task run.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of ABS library items.</returns>
    public async Task<List<AbsLibraryItem>> GetCachedLibraryItemsAsync(CancellationToken ct = default)
    {
        const string CacheKey = "abs:library-items:all";
        const int CacheMinutes = 10;

        if (!_memoryCache.TryGetValue(CacheKey, out List<AbsLibraryItem>? cachedItems))
        {
            var client = GetAdminClient();
            _clientLogger.LogInformation("ABS library cache MISS — loading from server...");

            var libraries = await client.GetLibrariesAsync(ct).ConfigureAwait(false);
            cachedItems = new List<AbsLibraryItem>();

            foreach (var lib in libraries)
            {
                if (!string.Equals(lib.MediaType, "book", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int page = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var pageResponse = await client.GetLibraryItemsAsync(lib.Id, page, 100, ct).ConfigureAwait(false);
                    cachedItems.AddRange(pageResponse.Results);
                    if (pageResponse.Results.Length < 100)
                    {
                        break;
                    }

                    page++;
                }
            }

            _clientLogger.LogInformation("ABS library cache POPULATED — {Count} items cached for {Minutes} minutes",
                cachedItems.Count, CacheMinutes);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheMinutes));

            _memoryCache.Set(CacheKey, cachedItems, cacheOptions);
        }
        else
        {
            _clientLogger.LogDebug("ABS library cache HIT — {Count} items from cache", cachedItems?.Count ?? 0);
        }

        return cachedItems ?? [];
    }
}
