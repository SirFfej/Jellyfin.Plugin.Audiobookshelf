using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// The current ABS user returned by <c>GET /api/me</c>.
/// Used for connection validation.
/// </summary>
public class AbsUser
{
    /// <summary>Gets or sets the user UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the username.</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the user type: <c>"root"</c>, <c>"admin"</c>, <c>"user"</c>, or <c>"guest"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the account is active.</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
