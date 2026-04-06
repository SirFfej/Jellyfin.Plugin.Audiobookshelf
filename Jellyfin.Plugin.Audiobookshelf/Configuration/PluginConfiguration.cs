using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Audiobookshelf;

/// <summary>
/// Serializable key-value pair for user→token mappings.
/// <see cref="Dictionary{TKey,TValue}"/> cannot be XML-serialized by Jellyfin's internal
/// XmlSerializer because it implements IDictionary.
/// </summary>
public class UserTokenEntry
{
    /// <summary>Gets or sets the Jellyfin user ID (GUID string).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the ABS API token (JWT).</summary>
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Plugin configuration persisted as XML by <see cref="MediaBrowser.Common.Plugins.BasePlugin{T}"/>.
/// </summary>
/// <remarks>
/// Do NOT add JSON attributes to this class — Jellyfin serialises it with its internal
/// <c>IXmlSerializer</c>, not <c>System.Text.Json</c>.
/// Do NOT use <see cref="Dictionary{TKey,TValue}"/> — it implements IDictionary and cannot
/// be XML-serialized. Use <see cref="List{T}"/> of a custom entry class instead.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Audiobookshelf server base URL (no trailing slash).
    /// Example: <c>http://192.168.1.10:13378</c> or <c>https://abs.example.com</c>.
    /// </summary>
    public string AbsServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the admin-level ABS API token used for metadata and library browsing
    /// when no per-user token is configured.
    /// </summary>
    public string AdminApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the per-user ABS API token entries (XML-serializable backing store).
    /// Use <see cref="UserTokenMap"/> for dictionary-style access in code.
    /// </summary>
    public List<UserTokenEntry> UserTokenEntries { get; set; } = new();

    /// <summary>
    /// Gets a dictionary view of <see cref="UserTokenEntries"/> for read-only lookup.
    /// Mutations must go through <see cref="UserTokenEntries"/> directly.
    /// </summary>
    [XmlIgnore]
    public Dictionary<string, string> UserTokenMap =>
        UserTokenEntries.ToDictionary(e => e.UserId, e => e.Token);

    /// <summary>
    /// Gets or sets the ABS library IDs to expose in Jellyfin.
    /// Empty list means all libraries are included.
    /// </summary>
    public List<string> IncludedLibraryIds { get; set; } = new();

    /// <summary>
    /// Gets or sets how often (in minutes) the inbound progress sync task runs.
    /// Default: 10 minutes.
    /// </summary>
    public int ProgressSyncIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether to push Jellyfin playback progress to ABS.
    /// </summary>
    public bool EnableOutboundSync { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to pull ABS progress into Jellyfin on schedule.
    /// </summary>
    public bool EnableInboundSync { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the metadata provider path is active.
    /// Disable when using the channel path exclusively to avoid double-display.
    /// </summary>
    public bool EnableMetadataProvider { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether ABS podcast libraries are included when
    /// auto-discovering libraries. When <c>false</c> (default), only book libraries are considered.
    /// Full podcast metadata enrichment requires a dedicated podcast provider (not yet implemented).
    /// </summary>
    public bool EnablePodcastLibraries { get; set; } = false;

    /// <summary>
    /// Gets or sets the minimum title + author match score (0.0–1.0) required to accept a
    /// fuzzy match in the metadata provider's fallback matching logic.
    /// </summary>
    public double TitleMatchConfidenceThreshold { get; set; } = 0.85;

    /// <summary>
    /// Returns the server URL with no trailing slash, safe for URL construction.
    /// </summary>
    public string NormalizedServerUrl => AbsServerUrl.TrimEnd('/');

}
