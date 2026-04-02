using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Audiobookshelf;

/// <summary>
/// Plugin configuration persisted as XML by <see cref="MediaBrowser.Common.Plugins.BasePlugin{T}"/>.
/// </summary>
/// <remarks>
/// Do NOT add JSON attributes to this class — Jellyfin serialises it with its internal
/// <c>IXmlSerializer</c>, not <c>System.Text.Json</c>.
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
    /// Gets or sets the per-user ABS API token map.
    /// Key: Jellyfin user ID (GUID string). Value: ABS API token (JWT).
    /// </summary>
    public Dictionary<string, string> UserTokenMap { get; set; } = new();

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
    /// Gets or sets the minimum title + author match score (0.0–1.0) required to accept a
    /// fuzzy match in the metadata provider's fallback matching logic.
    /// </summary>
    public double TitleMatchConfidenceThreshold { get; set; } = 0.85;

    /// <summary>
    /// Returns the server URL with no trailing slash, safe for URL construction.
    /// </summary>
    public string NormalizedServerUrl => AbsServerUrl.TrimEnd('/');
}
