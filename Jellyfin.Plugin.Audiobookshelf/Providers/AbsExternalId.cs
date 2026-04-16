using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Audiobookshelf.Providers;

/// <summary>
/// Registers the Audiobookshelf item ID as an external identifier so it appears
/// in the "External IDs" section of Jellyfin's Edit Metadata dialog for Book and Audio items.
/// This allows administrators to view, set, or clear the ABS link manually.
/// </summary>
public class AbsExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Audiobookshelf";

    /// <inheritdoc />
    public string Key => "Audiobookshelf";

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public string? UrlFormatString => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Book or Audio;
}
