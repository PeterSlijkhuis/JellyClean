using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// What the Library page asks for. Every part is optional; an empty filter matches everything.
/// </summary>
public class Filter
{
    /// <summary>Gets or sets the types to show: Movie, Series, Season, Episode.</summary>
    public string[]? Types { get; set; }

    /// <summary>Gets or sets the library name.</summary>
    public string? Library { get; set; }

    /// <summary>Gets or sets genres; an item matches when it has any of them.</summary>
    public string[]? Genres { get; set; }

    /// <summary>
    /// Gets or sets the watched state: "none" (nobody played any of it), "any" (someone watched it to the end),
    /// "all" (every user did), or a user id (that user did).
    /// </summary>
    public string? Watched { get; set; }

    /// <summary>Gets or sets a date: only items added before it.</summary>
    public DateTime? AddedBefore { get; set; }

    /// <summary>Gets or sets a date: only items added after it.</summary>
    public DateTime? AddedAfter { get; set; }

    /// <summary>Gets or sets a date: only items nobody played since, including items never played.</summary>
    public DateTime? NotWatchedSince { get; set; }

    /// <summary>Gets or sets the smallest size in bytes.</summary>
    public long? MinSize { get; set; }

    /// <summary>Gets or sets text to look for in the title or path.</summary>
    public string? Search { get; set; }

    /// <summary>Gets or sets a value indicating whether to leave out items marked keep and favorites.</summary>
    public bool HideProtected { get; set; }

    /// <summary>Gets or sets the sort: added, name, size, watched, year or rating.</summary>
    public string Sort { get; set; } = "added";

    /// <summary>Gets or sets a value indicating whether to sort high to low (newest, biggest first).</summary>
    public bool Descending { get; set; }

    /// <summary>Gets or sets how many to skip, for "Show more".</summary>
    public int Offset { get; set; }

    /// <summary>Gets or sets how many to return.</summary>
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Returns the matching items, sorted.
    /// </summary>
    /// <param name="items">All items, with <see cref="CatalogItem.Kept"/> filled in.</param>
    /// <param name="userCount">How many users there are, for "watched by everyone".</param>
    /// <returns>The matches.</returns>
    public IEnumerable<CatalogItem> Apply(IEnumerable<CatalogItem> items, int userCount)
    {
        var watchedBy = Guid.TryParse(Watched, out var user) ? user : (Guid?)null;
        var matches = items.Where(i =>
            (Types is not { Length: > 0 } || Types.Contains(i.Type))
            && (string.IsNullOrEmpty(Library) || i.Library == Library)
            && (Genres is not { Length: > 0 } || i.Genres.Any(g => Genres.Contains(g, StringComparer.OrdinalIgnoreCase)))
            && (Watched switch
            {
                null or "" => true,
                "none" => !i.AnyPlayed,
                "any" => i.WatchedBy.Length > 0,
                "all" => userCount > 0 && i.WatchedBy.Length >= userCount,
                _ => watchedBy is not null && i.WatchedBy.Contains(watchedBy.Value),
            })
            && (AddedBefore is null || i.Added < AddedBefore)
            && (AddedAfter is null || i.Added > AddedAfter)
            && (NotWatchedSince is null || i.LastWatched is null || i.LastWatched < NotWatchedSince)
            && (MinSize is null || i.Size >= MinSize)
            && (string.IsNullOrWhiteSpace(Search)
                || i.Title.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)
                || i.Path.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))
            && !(HideProtected && (i.Kept || i.Favorite)));

        Func<CatalogItem, object?> key = Sort switch
        {
            "name" => i => i.Title,
            "size" => i => i.Size,
            "watched" => i => i.LastWatched,
            "year" => i => i.Year,
            "rating" => i => i.Rating,
            _ => i => i.Added,
        };

        return Descending
            ? matches.OrderByDescending(key).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            : matches.OrderBy(key).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
    }
}
