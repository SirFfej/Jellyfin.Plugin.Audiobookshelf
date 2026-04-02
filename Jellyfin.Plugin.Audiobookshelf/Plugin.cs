using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf;

/// <summary>
/// The Audiobookshelf Jellyfin plugin entry point.
/// </summary>
/// <remarks>
/// Exposes ABS libraries as a Jellyfin channel, provides metadata/image enrichment
/// for locally-scanned audiobooks, and syncs playback progress bidirectionally.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Gets the singleton instance of this plugin, set during construction.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Audiobookshelf";

    /// <inheritdoc />
    public override string Description =>
        "Browse and play audiobooks from Audiobookshelf with bidirectional progress sync.";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application path provider.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer used for config persistence.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Audiobookshelf",
                DisplayName = "Audiobookshelf",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
