using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Audiobookshelf.Api.Models;

namespace Jellyfin.Plugin.Audiobookshelf.Helpers;

/// <summary>
/// Matches a Jellyfin item to an ABS library item using a prioritised cascade.
/// Used by the metadata provider when enriching locally-scanned audiobooks.
/// </summary>
public static class ItemMatcher
{
    // Strips series annotations ABS appends to titles, e.g.:
    //   "The Bourne Identity (Jason Bourne Book #1)" → "The Bourne Identity"
    //   "Dune: Part One"                            → "Dune"  (subtitle after colon)
    // Only strips trailing content so leading subtitles are preserved.
    private static readonly Regex SeriesAnnotationRegex = new(
        @"\s*[\(\[].*?[\)\]]\s*$|\s*:\s*.+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>
    /// Finds the best matching ABS item for the given search parameters.
    /// Returns <c>null</c> if no confident match is found.
    /// </summary>
    /// <param name="asin">ASIN from Jellyfin provider IDs (may be null).</param>
    /// <param name="isbn">ISBN from Jellyfin provider IDs (may be null).</param>
    /// <param name="title">Item title (used for fuzzy fallback).</param>
    /// <param name="authorName">Author name (used for fuzzy fallback).</param>
    /// <param name="absItems">Candidate ABS items to search.</param>
    /// <param name="confidenceThreshold">Minimum fuzzy score (0–1) to accept a title+author match.</param>
    public static AbsLibraryItem? FindBestMatch(
        string? asin,
        string? isbn,
        string title,
        string? authorName,
        IReadOnlyList<AbsLibraryItem> absItems,
        double confidenceThreshold = 0.85)
    {
        // Priority 1: ASIN exact match
        if (!string.IsNullOrWhiteSpace(asin))
        {
            var match = absItems.FirstOrDefault(i =>
                !i.IsMissing &&
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
                !i.IsMissing &&
                string.Equals(i.Media.Metadata.Isbn, isbn, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Priority 3: Fuzzy title + author match
        // ABS often appends series/subtitle info to the stored title, e.g.:
        //   ABS: "The Bourne Identity (Jason Bourne Book #1)"
        //   Jellyfin: "The Bourne Identity"
        // We score both the raw ABS title and a normalised version (series annotation
        // stripped) and take the best, then additionally check containment so that
        // a short Jellyfin title that is fully contained within a longer ABS title
        // still scores high.
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        AbsLibraryItem? bestCandidate = null;
        double bestScore = 0;

        string normalisedQuery = NormaliseTitle(title);

        foreach (var item in absItems.Where(i => !i.IsMissing))
        {
            string absRawTitle    = item.Media.Metadata.Title;
            string absNormTitle   = NormaliseTitle(absRawTitle);
            string absStrippedTitle = NormaliseTitle(StripSeriesAnnotation(absRawTitle));

            // Score against both the raw normalised title and the stripped title; take best
            double titleScore = Math.Max(
                FuzzyScore(normalisedQuery, absNormTitle),
                FuzzyScore(normalisedQuery, absStrippedTitle));

            // Containment bonus: if the query is entirely contained within the ABS title
            // (or vice versa) and the shorter string is at least 6 chars, it's a strong signal
            if (titleScore < 0.95 && normalisedQuery.Length >= 6)
            {
                if (absNormTitle.Contains(normalisedQuery, StringComparison.OrdinalIgnoreCase)
                    || absStrippedTitle.Contains(normalisedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    titleScore = Math.Max(titleScore, 0.95);
                }
            }

            if (titleScore <= 0)
            {
                continue;
            }

            double authorScore = string.IsNullOrWhiteSpace(authorName)
                ? 1.0
                : FuzzyScore(authorName, item.Media.Metadata.AuthorName ?? string.Empty);

            double combined = (titleScore * 0.7) + (authorScore * 0.3);
            if (combined > bestScore)
            {
                bestScore = combined;
                bestCandidate = item;
                if (bestScore >= 1.0)
                {
                    break;
                }
            }
        }

        return bestScore >= confidenceThreshold ? bestCandidate : null;
    }

    private static string StripSeriesAnnotation(string title)
        => SeriesAnnotationRegex.Replace(title, string.Empty).Trim();

    /// <summary>
    /// Normalises a title for comparison: lowercase, trim, collapse whitespace.
    /// Articles ("the", "a", "an") are NOT stripped — they are part of audiobook identity.
    /// </summary>
    private static string NormaliseTitle(string title)
        => string.Join(' ', title.ToLowerInvariant().Trim()
               .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // -------------------------------------------------------------------------
    // Levenshtein-based similarity (0.0 – 1.0)
    // -------------------------------------------------------------------------

    /// <summary>Public wrapper for use by search result scoring outside this class.</summary>
    public static double FuzzyScorePublic(string a, string b)
        => FuzzyScore(NormaliseTitle(a), NormaliseTitle(b));

    private static double FuzzyScore(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
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

}
