using Jellyfin.Plugin.Audiobookshelf.Helpers;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Audiobookshelf.Tests;

public class LibraryScopeTests
{
    private static readonly Guid BooksId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(CollectionTypeOptions.books, true)]
    [InlineData(CollectionTypeOptions.music, true)]
    [InlineData(CollectionTypeOptions.movies, false)]
    [InlineData(CollectionTypeOptions.mixed, false)]
    public void EmptySelectionIncludesOnlyBookAndMusicLibraries(CollectionTypeOptions type, bool expected)
    {
        Assert.Equal(expected, LibraryScope.IsIncluded(Library(type), []));
    }

    [Fact]
    public void SelectionMatchesGuidAcrossFormatsAndRejectsOtherIds()
    {
        var library = Library();
        Assert.True(LibraryScope.IsIncluded(library, [BooksId.ToString("D").ToUpperInvariant()]));
        Assert.False(LibraryScope.IsIncluded(library, [Guid.NewGuid().ToString()]));
        Assert.False(LibraryScope.IsIncluded(library, ["not-a-guid"]));
        library.ItemId = "not-a-guid";
        Assert.False(LibraryScope.IsIncluded(library, []));
    }

    [Theory]
    [InlineData("/media/books/Author/Example.epub", true)]
    [InlineData("/media/books/Author/../Example.epub", true)]
    [InlineData("/media/books-other/Example.epub", false)]
    [InlineData("/media/books/../excluded/Example.epub", false)]
    [InlineData(null, false)]
    public void ProviderScopeUsesJellyfinLocationsAndPathBoundaries(string? path, bool expected)
    {
        Assert.Equal(expected, LibraryScope.ContainsPath([Library()], [BooksId.ToString()], path));
    }

    private static VirtualFolderInfo Library(CollectionTypeOptions type = CollectionTypeOptions.books) => new()
    {
        Name = "Audiobooks",
        ItemId = BooksId.ToString("N"),
        CollectionType = type,
        Locations = ["/media/books"]
    };
}
