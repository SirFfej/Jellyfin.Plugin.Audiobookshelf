#pragma warning disable SA1600, IDE0079

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Audiobookshelf.Helpers;

public class JellyfinMetadataReader
{
    private readonly ILogger _logger;

    public JellyfinMetadataReader(ILogger logger)
    {
        _logger = logger;
    }

    public JellyfinItemMetadata? ReadMetadata(BaseItem item)
    {
        try
        {
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string? metadataDir = null;

            if (Directory.Exists(path))
            {
                metadataDir = path;
            }
            else if (File.Exists(path))
            {
                metadataDir = Path.GetDirectoryName(path);
            }

            if (string.IsNullOrWhiteSpace(metadataDir))
            {
                return null;
            }

            var metadataPath = Path.Combine(metadataDir, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<JellyfinMetadataJson>(json);

            if (metadata is null)
            {
                return null;
            }

            return new JellyfinItemMetadata
            {
                Title = metadata.Title ?? item.Name,
                Author = metadata.Author ?? ExtractAuthorFromPath(metadataDir),
                Asin = metadata.Asin,
                Isbn = metadata.Isbn,
                Narrator = metadata.Narrator ?? metadata.NarratedBy
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read metadata.json for item {ItemName}", item.Name);
            return null;
        }
    }

    private static string? ExtractAuthorFromPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i].Trim();
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (part.Equals("Audiobooks", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Books", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Music", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (part.Contains('[') || part.Contains("m4b", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return part;
        }

        return null;
    }
}

public class JellyfinItemMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Asin { get; set; }
    public string? Isbn { get; set; }
    public string? Narrator { get; set; }
}

internal class JellyfinMetadataJson
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("authorSort")]
    public string? AuthorSort { get; set; }

    [JsonPropertyName("asin")]
    public string? Asin { get; set; }

    [JsonPropertyName("isbn")]
    public string? Isbn { get; set; }

    [JsonPropertyName("narrator")]
    public string? Narrator { get; set; }

    [JsonPropertyName("narratedBy")]
    public string? NarratedBy { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("publishDate")]
    public string? PublishDate { get; set; }
}
