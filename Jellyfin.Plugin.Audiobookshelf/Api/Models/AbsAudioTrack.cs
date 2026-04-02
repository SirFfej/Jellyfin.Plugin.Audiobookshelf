using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A playable audio track returned inside a playback session or expanded library item.
/// Each track corresponds to one audio file in a multi-file audiobook.
/// </summary>
public class AbsAudioTrack
{
    /// <summary>Gets or sets the index within the book (1-based).</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Gets or sets the track duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>
    /// Gets or sets the start offset of this track within the full book timeline, in seconds.
    /// </summary>
    [JsonPropertyName("startOffset")]
    public double StartOffset { get; set; }

    /// <summary>
    /// Gets or sets the relative content URL, e.g. <c>/api/items/:id/file/:ino</c>.
    /// Append <c>?token=&lt;absToken&gt;</c> before passing to Jellyfin.
    /// </summary>
    [JsonPropertyName("contentUrl")]
    public string ContentUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the track title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the MIME type, e.g. <c>"audio/mpeg"</c>.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec string, e.g. <c>"mp3"</c> or <c>"aac"</c>.</summary>
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }
}
