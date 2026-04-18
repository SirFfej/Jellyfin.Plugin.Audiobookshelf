using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Audiobookshelf.Api.Models;

public class AbsEbookFile
{
    [JsonPropertyName("ino")]
    public string Ino { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public AbsEbookFileMetadata? Metadata { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class AbsEbookFileMetadata
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("ext")]
    public string Ext { get; set; } = string.Empty;
}