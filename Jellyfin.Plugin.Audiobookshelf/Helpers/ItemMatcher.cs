using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;

namespace Jellyfin.Plugin.Audiobookshelf.Helpers;

/// <summary>
/// Matches a Jellyfin item to an ABS library item using a prioritised cascade.
/// Used by the metadata provider when enriching locally-scanned audiobooks.
/// </summary>
public static class ItemMatcher
{
    /// <summary>
    /// Finds the best matching ABS item for the given search parameters.
    /// Returns <c>null</c> if no confident match is found.
    /// </summary>
    /// <param name="asin">ASIN from Jellyfin provider IDs (may be null).</param>
    /// <param name="isbn">ISBN from Jellyfin provider IDs (may be null).</param>
    /// <param name="filePath">Absolute file path of the Jellyfin item (may be null).</param>
    /// <param name="title">Item title (used for fuzzy fallback).</param>
    /// <param name="authorName">Author name (used for fuzzy fallback).</param>
    /// <param name="absItems">Candidate ABS items to search.</param>
    /// <param name="confidenceThreshold">Minimum fuzzy score (0–1) to accept a title+author match.</param>
    public static AbsLibraryItem? FindBestMatch(
        string? asin,
        string? isbn,
        string? filePath,
        string title,
        string? authorName,
        IReadOnlyList<AbsLibraryItem> absItems,
        double confidenceThreshold = 0.85)
    {
        // Priority 1: ASIN exact match
        if (!string.IsNullOrWhiteSpace(asin))
        {
            var match = absItems.FirstOrDefault(i =>
                string.Equals(i.Media.Metadata.Asin, asin, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Priority 2: ISBN exact match
        if (!string.IsNullOrWhiteSpace(isbn))
        {
            var match = absItems.FirstOrDefault(i =>
                string.Equals(i.Media.Metadata.Isbn, isbn, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Priority 3: File path comparison (normalise separators)
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string normalised = NormalisePath(filePath);
            var match = absItems.FirstOrDefault(i =>
                NormalisePath(i.Path).Equals(normalised, StringComparison.OrdinalIgnoreCase)
                || normalised.StartsWith(NormalisePath(i.Path), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Priority 4: Fuzzy title + author match
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        AbsLibraryItem? bestCandidate = null;
        double bestScore = 0;

        foreach (var item in absItems)
        {
            double titleScore = FuzzyScore(title, item.Media.Metadata.Title);
            double authorScore = string.IsNullOrWhiteSpace(authorName)
                ? 1.0
                : FuzzyScore(authorName, item.Media.Metadata.AuthorName ?? string.Empty);

            double combined = (titleScore * 0.7) + (authorScore * 0.3);
            if (combined > bestScore)
            {
                bestScore = combined;
                bestCandidate = item;
            }
        }

        return bestScore >= confidenceThreshold ? bestCandidate : null;
    }

    // -------------------------------------------------------------------------
    // Levenshtein-based similarity (0.0 – 1.0)
    // -------------------------------------------------------------------------

    /// <summary>Public wrapper for use by search result scoring outside this class.</summary>
    public static double FuzzyScorePublic(string a, string b) => FuzzyScore(a, b);

    private static double FuzzyScore(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0;
        }

        a = a.ToLowerInvariant().Trim();
        b = b.ToLowerInvariant().Trim();

        if (a == b)
        {
            return 1.0;
        }

        int distance = LevenshteinDistance(a, b);
        int maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 1.0 : 1.0 - ((double)distance / maxLen);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;

        if (n > m)
        {
            (s, t) = (t, s);
            (n, m) = (m, n);
        }

        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int i = 0; i <= n; i++)
        {
            prev[i] = i;
        }

        for (int j = 1; j <= m; j++)
        {
            curr[0] = j;
            for (int i = 1; i <= n; i++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[i] = Math.Min(
                    Math.Min(prev[i] + 1, curr[i - 1] + 1),
                    prev[i - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }

    private static string NormalisePath(string path)
        => path.Replace('\\', '/');
}
