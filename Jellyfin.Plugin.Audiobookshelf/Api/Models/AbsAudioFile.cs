using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// Represents a raw audio file stored in an ABS library item.
/// </summary>
public class AbsAudioFile
{
    /// <summary>Gets or sets the inode identifier used in file URLs.</summary>
    [JsonPropertyName("ino")]
    public string Ino { get; set; } = string.Empty;

    /// <summary>Gets or sets the metadata of this file.</summary>
    [JsonPropertyName("metadata")]
    public AbsAudioFileMetadata? Metadata { get; set; }

    /// <summary>Gets or sets the track index within the book.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Gets or sets the audio codec, e.g. <c>"mp3"</c>.</summary>
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    /// <summary>Gets or sets the duration of this file in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }
}

/// <summary>File-level metadata for an audio file.</summary>
public class AbsAudioFileMetadata
{
    /// <summary>Gets or sets the file name (with extension).</summary>
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>Gets or sets the file extension including the dot, e.g. <c>".mp3"</c>.</summary>
    [JsonPropertyName("ext")]
    public string Ext { get; set; } = string.Empty;
}
