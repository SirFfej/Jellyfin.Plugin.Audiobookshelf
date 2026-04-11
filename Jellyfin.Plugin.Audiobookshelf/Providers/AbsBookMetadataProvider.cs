using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Providers;

/// <summary>
/// Enriches locally-scanned Jellyfin audiobooks with metadata from Audiobookshelf.
/// </summary>
/// <remarks>
/// Only active when <see cref="PluginConfiguration.EnableMetadataProvider"/> is <c>true</c>.
/// Disable this when using the channel mode exclusively to avoid items appearing twice.
/// </remarks>
public class AbsBookMetadataProvider : IRemoteMetadataProvider<Book, BookInfo>, IHasOrder
{
    private static readonly Regex YearRegex = new(@"\b(\d{4})\b", RegexOptions.Compiled);
    private static readonly Regex HtmlStripRegex = new("<[^>]*>", RegexOptions.Compiled);

    private readonly AbsApiClientFactory _clientFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AbsBookMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsBookMetadataProvider"/> class.
    /// </summary>
    public AbsBookMetadataProvider(
        AbsApiClientFactory clientFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<AbsBookMetadataProvider> logger)
    {
        _clientFactory = clientFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf";

    /// <summary>
    /// Run before the default providers (order 0) so ABS metadata takes precedence
    /// for audiobooks that are matched by this provider.
    /// </summary>
    public int Order => -1;

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        BookInfo searchInfo,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            return [];
        }

        var candidates = await FindCandidatesAsync(cancellationToken).ConfigureAwait(false);
        string baseUrl = Plugin.Instance!.Configuration.NormalizedServerUrl;

        var results = new List<RemoteSearchResult>();

        foreach (var item in candidates)
        {
            double score = ScoreMatch(searchInfo, item);
            if (score < 0.5)
            {
                continue;
            }

            results.Add(new RemoteSearchResult
            {
                Name = item.Media.Metadata.Title,
                Overview = StripHtml(item.Media.Metadata.Description),
                ProductionYear = ParseYear(item.Media.Metadata.PublishedYear),
                ImageUrl = $"{baseUrl}/api/items/{item.Id}/cover",
                ProviderIds = BuildProviderIds(item),
                SearchProviderName = Name
            });
        }

        return results.OrderByDescending(r => r.ProductionYear);
    }

    // -------------------------------------------------------------------------
    // Metadata
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<MetadataResult<Book>> GetMetadata(
        BookInfo info,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book> { HasMetadata = false };

        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            return result;
        }

        AbsLibraryItem? absItem = null;

        // Direct match via existing provider ID
        if (info.TryGetProviderId("Audiobookshelf", out string? absId) && !string.IsNullOrWhiteSpace(absId))
        {
            var client = _clientFactory.GetAdminClient();
            absItem = await client.GetItemAsync(absId!, cancellationToken).ConfigureAwait(false);
        }

        // Fallback: search all libraries
        if (absItem is null)
        {
            var config = Plugin.Instance!.Configuration;
            var candidates = await FindCandidatesAsync(cancellationToken).ConfigureAwait(false);

            info.TryGetProviderId("Asin", out string? asin);
            info.TryGetProviderId("Isbn", out string? isbn);
            // BookInfo does not expose author in Jellyfin 10.10 — pass null so ItemMatcher
            // falls back to title-only scoring. Author matching can be added if a future
            // Jellyfin version adds author to ItemLookupInfo.
            string? authorName = null;

            absItem = ItemMatcher.FindBestMatch(
                asin,
                isbn,
                info.Name,
                authorName,
                candidates,
                config.TitleMatchConfidenceThreshold);
        }

        if (absItem is null)
        {
            _logger.LogDebug("No ABS match found for '{Title}'", info.Name);
            return result;
        }

        var meta = absItem.Media.Metadata;
        var book = new Book
        {
            Name = meta.Title,
            Overview = StripHtml(meta.Description),
            ProductionYear = ParseYear(meta.PublishedYear)
        };

        if (!string.IsNullOrWhiteSpace(meta.Publisher))
        {
            book.Studios = [meta.Publisher];
        }

        book.Tags = absItem.Media.Tags;
        book.Genres = meta.Genres;
        book.SetProviderId("Audiobookshelf", absItem.Id);

        if (!string.IsNullOrWhiteSpace(meta.Asin))
        {
            book.SetProviderId("Asin", meta.Asin);
        }

        if (!string.IsNullOrWhiteSpace(meta.Isbn))
        {
            book.SetProviderId("Isbn", meta.Isbn);
        }

        // Series
        if (meta.Series.Length > 0)
        {
            book.SeriesName = meta.Series[0].Name;
            if (float.TryParse(meta.Series[0].Sequence, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float seq))
            {
                book.IndexNumber = (int)Math.Round(seq);
            }
        }

        result.Item = book;
        result.HasMetadata = true;
        result.QueriedById = info.TryGetProviderId("Audiobookshelf", out _);
        result.ResultLanguage = info.MetadataLanguage;

        // People: authors + narrators
        foreach (var author in meta.Authors)
        {
            result.AddPerson(new PersonInfo
            {
                Name = author.Name,
                Type = PersonKind.Author
            });
        }

        foreach (var narrator in meta.Narrators)
        {
            result.AddPerson(new PersonInfo
            {
                Name = narrator,
                Type = PersonKind.Unknown,
                Role = "Narrator"
            });
        }

        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        // Cover image URL is public — no auth needed
        var httpClient = _httpClientFactory.CreateClient(AbsApiClient.HttpClientName);
        return httpClient.GetAsync(url, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<List<AbsLibraryItem>> FindCandidatesAsync(CancellationToken ct)
    {
        var client = _clientFactory.GetAdminClient();
        var libraries = await client.GetLibrariesAsync(ct).ConfigureAwait(false);
        var config = Plugin.Instance!.Configuration;

        IEnumerable<AbsLibrary> included;
        if (config.IncludedLibraryIds.Count > 0)
        {
            included = libraries.Where(l => config.IncludedLibraryIds.Contains(l.Id));
        }
        else
        {
            included = config.EnablePodcastLibraries
                ? libraries.Where(l => l.MediaType == "book" || l.MediaType == "podcast")
                : libraries.Where(l => l.MediaType == "book");
        }

        var all = new List<AbsLibraryItem>();
        foreach (var lib in included)
        {
            int page = 0;
            while (true)
            {
                var response = await client.GetLibraryItemsAsync(lib.Id, page, 100, ct).ConfigureAwait(false);
                // Always filter to book items here — podcast metadata enrichment requires
                // a dedicated provider not yet implemented. This prevents podcast items from
                // polluting book matching when a podcast library is included.
                all.AddRange(response.Results.Where(i => !i.IsMissing && i.MediaType == "book"));
                if (all.Count >= response.Total || response.Results.Length == 0)
                {
                    break;
                }

                page++;
            }
        }

        return all;
    }

    private static double ScoreMatch(BookInfo info, AbsLibraryItem item)
    {
        // Quick score to surface relevant search results
        double titleScore = ItemMatcher.FuzzyScorePublic(info.Name, item.Media.Metadata.Title);
        return titleScore;
    }

    private static int? ParseYear(string? yearStr)
    {
        if (string.IsNullOrWhiteSpace(yearStr))
        {
            return null;
        }

        if (int.TryParse(yearStr, out int year))
        {
            return year;
        }

        var m = YearRegex.Match(yearStr);
        return m.Success && int.TryParse(m.Groups[1].Value, out int extracted) ? extracted : null;
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        string stripped = HtmlStripRegex.Replace(html, string.Empty);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    private static Dictionary<string, string> BuildProviderIds(AbsLibraryItem item)
    {
        var ids = new Dictionary<string, string> { ["Audiobookshelf"] = item.Id };
        if (!string.IsNullOrWhiteSpace(item.Media.Metadata.Asin))
        {
            ids["Asin"] = item.Media.Metadata.Asin!;
        }

        return ids;
    }
}
