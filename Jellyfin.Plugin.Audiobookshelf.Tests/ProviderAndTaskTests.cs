using System.Net;
using Jellyfin.Plugin.Audiobookshelf.Providers;
using Jellyfin.Plugin.Audiobookshelf.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Audiobookshelf.Tests;

public class ProviderAndTaskTests
{
    private const string LibraryId = "11111111222233334444555555555555";
    private const string Compact = "{\"id\":\"abs-book\",\"mediaType\":\"book\",\"media\":{\"numAudioFiles\":1,\"numTracks\":1,\"metadata\":{\"title\":\"Example Book\",\"authorName\":\"Example Author\",\"asin\":\"B000EXAMPLE\"}}}";
    private const string Full = "{\"id\":\"abs-book\",\"mediaType\":\"book\",\"media\":{\"audioFiles\":[{}],\"metadata\":{\"title\":\"Example Book\",\"description\":\"Full description\",\"asin\":\"B000EXAMPLE\",\"authors\":[{\"id\":\"author\",\"name\":\"Example Author\"}],\"narrators\":[\"Example Narrator\"]}}}";

    [Fact]
    public async Task AudioProviderUsesJellyfinScopeButSearchesUnrelatedAbsLibraryIdsAndHydratesMatch()
    {
        using var context = new PluginTestContext(Respond);
        context.Plugin.Configuration.IncludedLibraryIds = [LibraryId];
        var manager = Manager();
        var provider = new AbsBookMetadataProvider(context.ClientFactory, context.HttpFactory.Object,
            NullLogger<AbsBookMetadataProvider>.Instance, manager.Object);
        var audioProvider = (IRemoteMetadataProvider<AudioBook, SongInfo>)provider;
        var lookup = new SongInfo { Name = "Example Book", Path = "/media/books/Example.m4b" };
        var search = await audioProvider.GetSearchResults(lookup, CancellationToken.None);
        Assert.Single(search);
        var result = await audioProvider.GetMetadata(lookup, CancellationToken.None);
        Assert.True(result.HasMetadata);
        Assert.Equal("abs-book", result.Item.ProviderIds["Audiobookshelf"]);
        Assert.Equal("Full description", result.Item.Overview);
        Assert.Contains(result.People, person => person.Name == "Example Narrator");
        Assert.Contains(context.Requests, uri => uri.StartsWith("/api/libraries/abs-library/items"));
        Assert.Contains("/api/items/abs-book?expanded=1", context.Requests);
        Assert.DoesNotContain(context.Requests, uri => uri.Contains(LibraryId));
        var requests = context.Requests.Count;
        var outside = await audioProvider.GetMetadata(new SongInfo { Name = "Example Book", Path = "/media/excluded/Example.m4b" }, CancellationToken.None);
        Assert.False(outside.HasMetadata);
        Assert.Equal(requests, context.Requests.Count);
    }

    [Fact]
    public async Task EbookProviderKeepsEbooksSeparateFromAudio()
    {
        using var context = new PluginTestContext(request =>
        {
            var response = Respond(request);
            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                .Replace("\"numAudioFiles\":1,\"numTracks\":1", "\"ebookFormat\":\"epub\"")
                .Replace("\"audioFiles\":[{}]", "\"ebookFile\":{\"format\":\"epub\"}");
            return PluginTestContext.Json(content);
        });
        var provider = new AbsBookMetadataProvider(context.ClientFactory, context.HttpFactory.Object,
            NullLogger<AbsBookMetadataProvider>.Instance, Manager().Object);
        var result = await provider.GetMetadata(new BookInfo { Name = "Example Book", Path = "/media/books/Example.epub" }, CancellationToken.None);
        Assert.True(result.HasMetadata);
        var audioProvider = (IRemoteMetadataProvider<AudioBook, SongInfo>)provider;
        var audio = await audioProvider.GetMetadata(new SongInfo { Name = "Example Book", Path = "/media/books/Example.m4b" }, CancellationToken.None);
        Assert.False(audio.HasMetadata);
    }

    [Fact]
    public async Task ProviderPaginatesEachAbsLibraryIndependently()
    {
        using var context = new PluginTestContext(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/libraries")
            {
                return PluginTestContext.Json("{\"libraries\":[{\"id\":\"first\",\"mediaType\":\"book\"},{\"id\":\"second\",\"mediaType\":\"book\"}]}");
            }

            if (path.StartsWith("/api/libraries/"))
            {
                var targetPage = path.Contains("second") && request.RequestUri.Query.Contains("page=1");
                var results = targetPage ? Compact : string.Join(",", Enumerable.Repeat(Compact.Replace("Example Book", "Unrelated"), 100));
                return PluginTestContext.Json("{\"total\":" + (path.Contains("second") ? "101" : "100") + ",\"results\":[" + results + "]}");
            }

            return Respond(request);
        });
        var provider = new AbsBookMetadataProvider(context.ClientFactory, context.HttpFactory.Object,
            NullLogger<AbsBookMetadataProvider>.Instance, Manager().Object);
        var audioProvider = (IRemoteMetadataProvider<AudioBook, SongInfo>)provider;
        var results = await audioProvider.GetSearchResults(new SongInfo { Name = "Example Book", Path = "/media/books/Example.m4b" }, CancellationToken.None);
        Assert.Single(results);
        Assert.Contains("/api/libraries/second/items?expanded=1&limit=100&page=1", context.Requests);
    }

    [Fact]
    public async Task EmptySelectionLinksAudiobooksAndSkipsAuthorFolders()
    {
        using var context = new PluginTestContext(Respond);
        var manager = Manager();
        var book = new AudioBook { Id = Guid.NewGuid(), Name = "Example Book", Path = "/media/books/Example.m4b" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Example Book" };
        manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([book, folder]);
        var task = new AbsLinkRetryTask(context.ClientFactory, manager.Object, Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(), context.Paths.Object, NullLogger<AbsLinkRetryTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal("abs-book", book.ProviderIds["Audiobookshelf"]);
        Assert.False(folder.ProviderIds.ContainsKey("Audiobookshelf"));
        manager.Verify(m => m.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == Guid.Parse(LibraryId))), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPreservesSelectedLibraryLinksAndOnlyRemovesExcludedOnes(bool staleSelection)
    {
        using var context = new PluginTestContext(Respond);
        context.Plugin.Configuration.IncludedLibraryIds = [staleSelection ? Guid.NewGuid().ToString() : LibraryId];
        var manager = Manager();
        var included = new AudioBook { Id = Guid.NewGuid(), Name = "Included" };
        var excluded = new AudioBook { Id = Guid.NewGuid(), Name = "Excluded" };
        included.ProviderIds["Audiobookshelf"] = "included";
        excluded.ProviderIds["Audiobookshelf"] = "excluded";
        manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => q.ParentId == Guid.Parse(LibraryId) ? [included] : [included, excluded]);
        var task = new AbsCleanupInvalidLinksTask(manager.Object, context.Paths.Object, NullLogger<AbsCleanupInvalidLinksTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.True(included.ProviderIds.ContainsKey("Audiobookshelf"));
        Assert.Equal(staleSelection, excluded.ProviderIds.ContainsKey("Audiobookshelf"));
    }

    [Fact]
    public async Task TypeValidationKeepsLinksWhenAbsIsUnavailable()
    {
        using var context = new PluginTestContext(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var manager = Manager();
        var book = new AudioBook { Id = Guid.NewGuid(), Name = "Example", Container = "m4b" };
        book.ProviderIds["Audiobookshelf"] = "abs-book";
        manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([book]);
        var task = new AbsValidateLinkTypesTask(context.ClientFactory, manager.Object, context.Paths.Object, NullLogger<AbsValidateLinkTypesTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal("abs-book", book.ProviderIds["Audiobookshelf"]);
    }

    private static Mock<ILibraryManager> Manager()
    {
        var manager = new Mock<ILibraryManager>();
        manager.Setup(m => m.GetVirtualFolders()).Returns([new VirtualFolderInfo
        {
            Name = "Audiobooks", ItemId = LibraryId, CollectionType = CollectionTypeOptions.books, Locations = ["/media/books"]
        }]);
        return manager;
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/api/me" => PluginTestContext.Json("{\"id\":\"test-user\",\"username\":\"test\"}"),
        "/api/libraries" => PluginTestContext.Json("{\"libraries\":[{\"id\":\"abs-library\",\"mediaType\":\"book\"}]}"),
        "/api/libraries/abs-library/items" => PluginTestContext.Json("{\"results\":[" + Compact + "],\"total\":1}"),
        "/api/items/abs-book" => PluginTestContext.Json(Full),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
    };
}
