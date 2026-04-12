using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Audiobookshelf.Api;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Providers;

/// <summary>
/// Exposes Audiobookshelf chapter markers as Jellyfin <see cref="MediaSegmentDto"/> entries
/// so chapter navigation is available in the Jellyfin player.
/// </summary>
public class AbsChapterSegmentProvider : IMediaSegmentProvider
{
    private readonly AbsApiClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AbsChapterSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbsChapterSegmentProvider"/> class.
    /// </summary>
    public AbsChapterSegmentProvider(
        AbsApiClientFactory clientFactory,
        ILibraryManager libraryManager,
        ILogger<AbsChapterSegmentProvider> logger)
    {
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audiobookshelf";

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Book);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableMetadataProvider != true)
        {
            return [];
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is not Book book)
        {
            return [];
        }

        if (!book.TryGetProviderId("Audiobookshelf", out string? absId) || string.IsNullOrWhiteSpace(absId))
        {
            return [];
        }

        AbsApiClient client;
        try
        {
            client = _clientFactory.GetAdminClient();
        }
        catch (InvalidOperationException)
        {
            // ABS not yet configured
            return [];
        }

        var absItem = await client.GetItemAsync(absId!, cancellationToken).ConfigureAwait(false);
        var chapters = absItem?.Media?.Chapters;
        if (chapters is null || chapters.Length == 0)
        {
            return [];
        }

        _logger.LogDebug("Building {Count} chapter segments for item {AbsId}", chapters.Length, absId);

        var segments = new List<MediaSegmentDto>(chapters.Length);
        foreach (var chapter in chapters)
        {
            segments.Add(new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = request.ItemId,
                // Type defaults to Unknown (0) — no direct reference to Jellyfin.Database.Implementations.Enums
                StartTicks = TimeHelper.SecondsToTicks(chapter.Start),
                EndTicks = TimeHelper.SecondsToTicks(chapter.End)
            });
        }

        return segments;
    }
}
