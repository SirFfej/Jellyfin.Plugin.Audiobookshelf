using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// A minified author reference embedded in book metadata.
/// </summary>
public class AbsAuthorMinified
{
    /// <summary>Gets or sets the author UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the author's full name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Full author detail returned by <c>GET /api/authors/:id</c>.
/// </summary>
public class AbsAuthor : AbsAuthorMinified
{
    /// <summary>Gets or sets the author biography.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the ASIN for the author page.</summary>
    [JsonPropertyName("asin")]
    public string? Asin { get; set; }

    /// <summary>Gets or sets the path to the author's image on the ABS host.</summary>
    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; set; }
}
