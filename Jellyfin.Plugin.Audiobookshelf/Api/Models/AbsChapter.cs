using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A chapter entry within an ABS audiobook.
/// <c>start</c> and <c>end</c> are floating-point seconds relative to the full book timeline.
/// </summary>
public class AbsChapter
{
    /// <summary>Gets or sets the chapter index.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the start position in seconds.</summary>
    [JsonPropertyName("start")]
    public double Start { get; set; }

    /// <summary>Gets or sets the end position in seconds.</summary>
    [JsonPropertyName("end")]
    public double End { get; set; }

    /// <summary>Gets or sets the chapter title (may be empty).</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}
