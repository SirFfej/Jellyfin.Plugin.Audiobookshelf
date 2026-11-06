using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Audiobookshelf.Tests;

public class MetadataReaderTests
{
    [Theory]
    [InlineData("\"authors\":[\"Example Author\"]", "Example Author")]
    [InlineData("\"author\":\"Legacy Author\"", "Legacy Author")]
    [InlineData("\"authors\":[]", null)]
    public void ReadsAbsAuthorsWithoutUsingBookDirectoryAsAuthor(string authorField, string? expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "abs-reader-" + Guid.NewGuid(), "Example Book");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "metadata.json"), "{\"title\":\"Example Book\"," + authorField + "}");
            var reader = new JellyfinMetadataReader(NullLogger.Instance);
            Assert.Equal(expected, reader.ReadMetadata(new AudioBook { Path = directory })?.Author);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }
}
