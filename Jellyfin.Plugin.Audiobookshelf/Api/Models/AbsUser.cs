using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

/// <summary>
/// An ABS user returned by <c>GET /api/me</c> or <c>GET /api/users</c>.
/// </summary>
public class AbsUser
{
    /// <summary>Gets or sets the user UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the username.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Gets or sets the user's email address.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>Gets or sets the user type: <c>"root"</c>, <c>"admin"</c>, <c>"user"</c>, or <c>"guest"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the account is active.</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>Gets or sets a value indicating whether the account is locked.</summary>
    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }

    /// <summary>Gets or sets the last seen timestamp.</summary>
    [JsonPropertyName("lastSeen")]
    public long? LastSeen { get; set; }

    /// <summary>Gets or sets the user's permissions object.</summary>
    [JsonPropertyName("permissions")]
    public AbsUserPermissions? Permissions { get; set; }

    /// <summary>Gets or sets the user's bookmarks.</summary>
    [JsonPropertyName("bookmarks")]
    public AbsBookmark[] Bookmarks { get; set; } = [];

    /// <summary>Gets or sets extra data.</summary>
    [JsonPropertyName("extraData")]
    public Dictionary<string, object>? ExtraData { get; set; }

    /// <summary>Gets or sets the user's API token (admin only for non-own users).</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Gets or sets when the user was created.</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    /// <summary>Gets or sets when the user was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }
}

/// <summary>
/// User permissions from Audiobookshelf.
/// </summary>
public class AbsUserPermissions
{
    [JsonPropertyName("download")]
    public bool CanDownload { get; set; }

    [JsonPropertyName("update")]
    public bool CanUpdate { get; set; }

    [JsonPropertyName("delete")]
    public bool CanDelete { get; set; }

    [JsonPropertyName("upload")]
    public bool CanUpload { get; set; }

    [JsonPropertyName("accessExplicitContent")]
    public bool CanAccessExplicitContent { get; set; }

    [JsonPropertyName("accessAllLibraries")]
    public bool CanAccessAllLibraries { get; set; }

    [JsonPropertyName("accessAllTags")]
    public bool CanAccessAllTags { get; set; }

    [JsonPropertyName("createEreader")]
    public bool CanCreateEReader { get; set; }

    [JsonPropertyName("librariesAccessible")]
    public string[]? LibrariesAccessible { get; set; }

    [JsonPropertyName("itemTagsSelected")]
    public string[]? ItemTagsSelected { get; set; }
}

/// <summary>
/// An audiobook bookmark.
/// </summary>
public class AbsBookmark
{
    /// <summary>Gets or sets the library item ID.</summary>
    [JsonPropertyName("libraryItemId")]
    public string LibraryItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the bookmark title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the bookmark time in seconds.</summary>
    [JsonPropertyName("time")]
    public double Time { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }
}
