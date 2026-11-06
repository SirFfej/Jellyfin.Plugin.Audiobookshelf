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

    /// <summary>Gets or sets the audio-file count returned by compact library records.</summary>
    [JsonPropertyName("numAudioFiles")]
    public int NumAudioFiles { get; set; }

    /// <summary>Gets or sets the playable-track count returned by compact library records.</summary>
    [JsonPropertyName("numTracks")]
    public int NumTracks { get; set; }

    /// <summary>Gets or sets the ebook format returned by compact library records.</summary>
    [JsonPropertyName("ebookFormat")]
    public string? EbookFormat { get; set; }

    /// <summary>Gets a value indicating whether a compact or full record contains audio.</summary>
    [JsonIgnore]
    public bool HasAudio => AudioFiles.Length > 0 || NumAudioFiles > 0 || NumTracks > 0;

    /// <summary>Gets a value indicating whether a compact or full record contains an ebook.</summary>
    [JsonIgnore]
    public bool HasEbook => EbookFile is not null || !string.IsNullOrWhiteSpace(EbookFormat);

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
