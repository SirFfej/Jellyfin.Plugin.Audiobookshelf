using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// An Audiobookshelf library returned by <c>GET /api/libraries</c>.
/// </summary>
public class AbsLibrary
{
    /// <summary>Gets or sets the library UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the media type: <c>"book"</c> or <c>"podcast"</c>.</summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the library icon identifier.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

/// <summary>
/// Wrapper returned by <c>GET /api/libraries</c>.
/// </summary>
public class AbsLibrariesResponse
{
    /// <summary>Gets or sets the list of libraries.</summary>
    [JsonPropertyName("libraries")]
    public AbsLibrary[] Libraries { get; set; } = [];
}
