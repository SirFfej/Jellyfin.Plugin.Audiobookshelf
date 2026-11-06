using System.Text.Json;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;
using Jellyfin.Plugin.Audiobookshelf.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Audiobookshelf.Tests;

public class ItemMatcherTests
{
    [Theory]
    [InlineData("\"numAudioFiles\":1,\"numTracks\":1")]
    [InlineData("\"numTracks\":1")]
    [InlineData("\"audioFiles\":[{}]")]
    public void MatchesAudioFromCompactOrFullRecords(string mediaFields)
    {
        var item = Parse(mediaFields);
        Assert.Same(item, ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Unrelated title", null, [item]));
        Assert.Same(item, ItemMatcher.FindBestMatch(null, "9780000000000", "Unrelated title", null, [item]));
        Assert.Same(item, ItemMatcher.FindBestMatch(null, null, "Example Book", "Example Author", [item]));
    }

    [Theory]
    [InlineData("\"ebookFormat\":\"epub\"")]
    [InlineData("\"ebookFile\":{\"ebookFormat\":\"epub\"}")]
    public void MatchesEbooksWithoutMatchingThemAsAudio(string mediaFields)
    {
        var item = Parse(mediaFields);
        Assert.Same(item, ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item], preferEbook: true));
        Assert.Null(ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item]));
    }

    [Fact]
    public void AudioOnlyRecordsDoNotMatchEbooksEvenWithExactAsin()
    {
        var item = Parse("\"numAudioFiles\":1");
        Assert.Null(ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item], preferEbook: true));
    }

    [Fact]
    public void EmptyRecordsDoNotMatchEitherType()
    {
        var item = Parse("\"numAudioFiles\":0,\"numTracks\":0");
        Assert.Null(ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item]));
        Assert.Null(ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item], preferEbook: true));
    }

    [Fact]
    public void MixedFormatRecordsCanMatchBothTypes()
    {
        var item = Parse("\"numAudioFiles\":1,\"ebookFormat\":\"epub\"");
        Assert.Same(item, ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item]));
        Assert.Same(item, ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item], preferEbook: true));
    }

    [Fact]
    public void MissingBooksAreNotMatchedEvenWithExactAsin()
    {
        var item = Parse("\"numAudioFiles\":1");
        item.IsMissing = true;
        Assert.Null(ItemMatcher.FindBestMatch("B000EXAMPLE", null, "Example Book", null, [item]));
    }

    private static AbsLibraryItem Parse(string mediaFields) => JsonSerializer.Deserialize<AbsLibraryItem>(
        "{\"id\":\"abs-item\",\"mediaType\":\"book\",\"media\":{" + mediaFields +
        ",\"metadata\":{\"title\":\"Example Book\",\"authorName\":\"Example Author\",\"asin\":\"B000EXAMPLE\",\"isbn\":\"9780000000000\"}}}")!;
}
