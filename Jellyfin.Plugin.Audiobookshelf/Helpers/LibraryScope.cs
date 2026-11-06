using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Audiobookshelf.Helpers;

/// <summary>Resolves the Jellyfin library selection used by providers and scheduled tasks.</summary>
public static class LibraryScope
{
    /// <summary>Tests book/music libraries by ID; an empty selection includes all eligible libraries.</summary>
    public static bool IsIncluded(VirtualFolderInfo library, IReadOnlyCollection<string> includedLibraryIds)
    {
        if (library.CollectionType is not (CollectionTypeOptions.books or CollectionTypeOptions.music)
            || !Guid.TryParse(library.ItemId, out var libraryId))
        {
            return false;
        }

        return includedLibraryIds.Count == 0
            || includedLibraryIds.Any(id => Guid.TryParse(id, out var selectedId) && selectedId == libraryId);
    }

    /// <summary>Tests the lookup path against selected Jellyfin library locations.</summary>
    public static bool ContainsPath(
        IEnumerable<VirtualFolderInfo> libraries,
        IReadOnlyCollection<string> includedLibraryIds,
        string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        return libraries.Where(library => IsIncluded(library, includedLibraryIds))
            .SelectMany(library => library.Locations)
            .Any(location => IsWithin(location, itemPath));
    }

    private static bool IsWithin(string location, string itemPath)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var relative = Path.GetRelativePath(location, itemPath);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
