using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Providers;

/// <summary>
/// Provides cover art for audiobooks from Audiobookshelf.
/// </summary>
/// <remarks>
/// The ABS cover endpoint (<c>/api/items/:id/cover</c>) does not require authentication,
/// so the image URL can be passed directly to Jellyfin without embedding a token.
/// </remarks>
public class AbsImageProvider : IRemoteImageProvider
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AbsImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsImageProvider"/> class.
    /// </summary>
    public AbsImageProvider(
        AbsApiClientFactory clientFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<AbsImageProvider> logger)
    {
        _clientFactory = clientFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf";

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Book;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            return Task.FromResult<IEnumerable<RemoteImageInfo>>([]);
        }

        if (!item.TryGetProviderId("Audiobookshelf", out string? absId) || string.IsNullOrWhiteSpace(absId))
        {
            return Task.FromResult<IEnumerable<RemoteImageInfo>>([]);
        }

        string baseUrl = Plugin.Instance!.Configuration.NormalizedServerUrl;
        string coverUrl = $"{baseUrl}/api/items/{absId}/cover";

        _logger.LogDebug("Returning ABS cover URL for item {AbsId}: {Url}", absId, coverUrl);

        return Task.FromResult<IEnumerable<RemoteImageInfo>>(
        [
            new RemoteImageInfo
            {
                Url = coverUrl,
                Type = ImageType.Primary,
                ProviderName = Name
            }
        ]);
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        // Cover images are public — no auth header required
        var httpClient = _httpClientFactory.CreateClient(AbsApiClient.HttpClientName);
        return httpClient.GetAsync(url, cancellationToken);
    }
}
