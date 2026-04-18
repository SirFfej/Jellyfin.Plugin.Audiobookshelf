using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// The media object of an ABS book-type library item.
/// </summary>
public class AbsBook
{
    /// <summary>Gets or sets the book UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the rich metadata for this book.</summary>
    [JsonPropertyName("metadata")]
    public AbsBookMetadata Metadata { get; set; } = new();

    /// <summary>Gets or sets the path to the cover image on the ABS host (may be null if no cover).</summary>
    [JsonPropertyName("coverPath")]
    public string? CoverPath { get; set; }

    /// <summary>Gets or sets the user-defined tags.</summary>
    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = [];

    /// <summary>Gets or sets the raw audio files in this book.</summary>
    [JsonPropertyName("audioFiles")]
    public AbsAudioFile[] AudioFiles { get; set; } = [];

    /// <summary>Gets or sets the chapter list (full book timeline, all files merged).</summary>
    [JsonPropertyName("chapters")]
    public AbsChapter[] Chapters { get; set; } = [];

    /// <summary>Gets or sets the total duration of the audiobook in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>
    /// Gets or sets the playable audio tracks (populated when <c>?expanded=1</c> is used or
    /// returned by <c>POST /api/items/:id/play</c>).
    /// </summary>
    [JsonPropertyName("tracks")]
    public AbsAudioTrack[] Tracks { get; set; } = [];

    /// <summary>Gets or sets the ebook file if present (for ebook-type books).</summary>
    [JsonPropertyName("ebookFile")]
    public AbsEbookFile? EbookFile { get; set; }
}
