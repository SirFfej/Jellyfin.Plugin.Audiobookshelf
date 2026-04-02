using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A user's listening progress for a library item or podcast episode,
/// returned by <c>GET /api/me/progress/:libraryItemId</c>.
/// </summary>
public class AbsMediaProgress
{
    /// <summary>Gets or sets the progress UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the library item ID this progress applies to.</summary>
    [JsonPropertyName("libraryItemId")]
    public string LibraryItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the total duration in seconds (from item metadata).</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Gets or sets the current playback position in seconds.</summary>
    [JsonPropertyName("currentTime")]
    public double CurrentTime { get; set; }

    /// <summary>Gets or sets the fractional progress (0.0–1.0).</summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>Gets or sets a value indicating whether the item has been marked as finished.</summary>
    [JsonPropertyName("isFinished")]
    public bool IsFinished { get; set; }

    /// <summary>Gets or sets the Unix epoch milliseconds of the last update.</summary>
    [JsonPropertyName("lastUpdate")]
    public long LastUpdate { get; set; }

    /// <summary>Gets or sets the Unix epoch milliseconds when the item was finished (null if not finished).</summary>
    [JsonPropertyName("finishedAt")]
    public long? FinishedAt { get; set; }
}
