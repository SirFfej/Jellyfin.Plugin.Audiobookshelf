using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A minified series reference embedded in book metadata.
/// </summary>
public class AbsSeriesMinified
{
    /// <summary>Gets or sets the series UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the book's position within this series (may be "1", "1.5", etc.).</summary>
    [JsonPropertyName("sequence")]
    public string? Sequence { get; set; }
}
