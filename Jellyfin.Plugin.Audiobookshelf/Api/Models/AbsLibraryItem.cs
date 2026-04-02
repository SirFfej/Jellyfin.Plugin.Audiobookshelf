using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A library item (book or podcast) returned by ABS library and item endpoints.
/// </summary>
public class AbsLibraryItem
{
    /// <summary>Gets or sets the library item UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the inode of the item's folder/file.</summary>
    [JsonPropertyName("ino")]
    public string Ino { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute file system path on the ABS host.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the path relative to the library folder.</summary>
    [JsonPropertyName("relPath")]
    public string RelPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the media type: <c>"book"</c> or <c>"podcast"</c>.</summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the library ID this item belongs to.</summary>
    [JsonPropertyName("libraryId")]
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the book media object (present when mediaType is "book").</summary>
    [JsonPropertyName("media")]
    public AbsBook Media { get; set; } = new();

    /// <summary>Gets or sets the Unix epoch milliseconds when the item was added.</summary>
    [JsonPropertyName("addedAt")]
    public long AddedAt { get; set; }

    /// <summary>Gets or sets the Unix epoch milliseconds of the last update.</summary>
    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the item's files are missing from disk.</summary>
    [JsonPropertyName("isMissing")]
    public bool IsMissing { get; set; }
}

/// <summary>
/// Paged response from <c>GET /api/libraries/:id/items</c>.
/// </summary>
public class AbsLibraryItemsResponse
{
    /// <summary>Gets or sets the items on the current page.</summary>
    [JsonPropertyName("results")]
    public AbsLibraryItem[] Results { get; set; } = [];

    /// <summary>Gets or sets the total number of items across all pages.</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>Gets or sets the current page index (0-based).</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the page size limit.</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; set; }
}
