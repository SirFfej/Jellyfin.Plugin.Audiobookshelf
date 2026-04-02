using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// Metadata fields for an ABS audiobook.
/// </summary>
public class AbsBookMetadata
{
    /// <summary>Gets or sets the book title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the list of authors.</summary>
    [JsonPropertyName("authors")]
    public AbsAuthorMinified[] Authors { get; set; } = [];

    /// <summary>Gets or sets the list of narrator names.</summary>
    [JsonPropertyName("narrators")]
    public string[] Narrators { get; set; } = [];

    /// <summary>Gets or sets the series associations (with sequence).</summary>
    [JsonPropertyName("series")]
    public AbsSeriesMinified[] Series { get; set; } = [];

    /// <summary>Gets or sets the genre list.</summary>
    [JsonPropertyName("genres")]
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the published year as a string (ABS stores it as STRING, not integer).
    /// May be null, empty, or non-numeric — always use <c>int.TryParse</c>.
    /// </summary>
    [JsonPropertyName("publishedYear")]
    public string? PublishedYear { get; set; }

    /// <summary>Gets or sets the publisher name.</summary>
    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    /// <summary>Gets or sets the book description (may contain HTML).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the ISBN.</summary>
    [JsonPropertyName("isbn")]
    public string? Isbn { get; set; }

    /// <summary>Gets or sets the ASIN (Amazon Standard Identification Number).</summary>
    [JsonPropertyName("asin")]
    public string? Asin { get; set; }

    /// <summary>Gets or sets the language code.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Gets or sets a value indicating whether the content is explicit.</summary>
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }

    /// <summary>Gets or sets a value indicating whether this is an abridged edition.</summary>
    [JsonPropertyName("abridged")]
    public bool Abridged { get; set; }

    // Expanded-only convenience fields (populated when ?expanded=1)

    /// <summary>Gets or sets the comma-joined author name string (expanded only).</summary>
    [JsonPropertyName("authorName")]
    public string? AuthorName { get; set; }

    /// <summary>Gets or sets the comma-joined narrator name string (expanded only).</summary>
    [JsonPropertyName("narratorName")]
    public string? NarratorName { get; set; }

    /// <summary>Gets or sets the series name + sequence string (expanded only).</summary>
    [JsonPropertyName("seriesName")]
    public string? SeriesName { get; set; }
}
