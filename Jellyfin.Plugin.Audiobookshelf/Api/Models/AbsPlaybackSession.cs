using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A playback session returned by <c>POST /api/items/:id/play</c>.
/// </summary>
public class AbsPlaybackSession
{
    /// <summary>Gets or sets the session UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the library item ID being played.</summary>
    [JsonPropertyName("libraryItemId")]
    public string LibraryItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the current playback position in seconds.</summary>
    [JsonPropertyName("currentTime")]
    public double CurrentTime { get; set; }

    /// <summary>Gets or sets the total duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>
    /// Gets or sets the audio tracks for this session.
    /// Each track has a <c>contentUrl</c> and <c>startOffset</c>.
    /// </summary>
    [JsonPropertyName("audioTracks")]
    public AbsAudioTrack[] AudioTracks { get; set; } = [];

    /// <summary>Gets or sets the chapters for the full book timeline.</summary>
    [JsonPropertyName("chapters")]
    public AbsChapter[] Chapters { get; set; } = [];

    /// <summary>
    /// Gets or sets the play method: 0 = DirectPlay, 1 = DirectStream, 2 = Transcode.
    /// </summary>
    [JsonPropertyName("playMethod")]
    public int PlayMethod { get; set; }

    /// <summary>Gets or sets the media player identifier sent during session creation.</summary>
    [JsonPropertyName("mediaPlayer")]
    public string? MediaPlayer { get; set; }

    /// <summary>Gets or sets the item display title.</summary>
    [JsonPropertyName("displayTitle")]
    public string? DisplayTitle { get; set; }

    /// <summary>Gets or sets the item display author.</summary>
    [JsonPropertyName("displayAuthor")]
    public string? DisplayAuthor { get; set; }

    /// <summary>Gets or sets the cover URL for this session's item.</summary>
    [JsonPropertyName("coverPath")]
    public string? CoverPath { get; set; }
}
