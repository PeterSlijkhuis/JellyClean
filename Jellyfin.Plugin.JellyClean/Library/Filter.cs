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

    // Days rather than dates, so a saved view still means "over a year ago" next year.

    /// <summary>Gets or sets a number of days: only items added longer ago.</summary>
    public int? AddedMoreThanDays { get; set; }

    /// <summary>Gets or sets a number of days: only items added within that time.</summary>
    public int? AddedWithinDays { get; set; }

    /// <summary>Gets or sets a number of days: only items nobody played in that time, including items never played.</summary>
    public int? NotPlayedForDays { get; set; }

    /// <summary>Gets or sets a value indicating whether to show only items nobody ever started.</summary>
    public bool NeverPlayed { get; set; }

    /// <summary>Gets or sets the smallest size in bytes.</summary>
    public long? MinSize { get; set; }

    /// <summary>Gets or sets the largest size in bytes; 0 finds empty movies and series.</summary>
    public long? MaxSize { get; set; }

    /// <summary>Gets or sets resolutions: 4K, 1080p, 720p, SD.</summary>
    public string[]? Resolutions { get; set; }

    /// <summary>Gets or sets the video codec, like H.264.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets "hdr" for any HDR format, "sdr" for none, or one format like Dolby Vision.</summary>
    public string? Hdr { get; set; }

    /// <summary>Gets or sets a value indicating whether to show only movies and series that are in Jellyfin more than once.</summary>
    public bool CopiesOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether to show only series (and their seasons and episodes) that have ended.</summary>
    public bool EndedOnly { get; set; }

    /// <summary>Gets or sets text to look for in the title or path.</summary>
    public string? Search { get; set; }

    /// <summary>Gets or sets a value indicating whether to leave out items marked keep and favorites.</summary>
    public bool HideProtected { get; set; }

    /// <summary>Gets or sets the sort: added, name, size, watched, year, rating or quality.</summary>
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
    /// <param name="now">The time to count days from; now when left out.</param>
    /// <returns>The matches.</returns>
    public IEnumerable<CatalogItem> Apply(IEnumerable<CatalogItem> items, int userCount, DateTime? now = null)
    {
        var today = now ?? DateTime.UtcNow;
        DateTime? Ago(int? days) => days > 0 ? today.AddDays(-days.Value) : null;
        var addedBefore = Ago(AddedMoreThanDays);
        var addedAfter = Ago(AddedWithinDays);
        var playedBefore = Ago(NotPlayedForDays);
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
            && (addedBefore is null || i.Added < addedBefore)
            && (addedAfter is null || i.Added > addedAfter)
            && (playedBefore is null || i.LastWatched is null || i.LastWatched < playedBefore)
            && (!NeverPlayed || !i.AnyPlayed)
            && (MinSize is null || i.Size >= MinSize)
            && (MaxSize is null || i.Size <= MaxSize)
            && (Resolutions is not { Length: > 0 } || Resolutions.Contains(i.Resolution))
            && (string.IsNullOrEmpty(Codec) || i.Codec == Codec)
            && (Hdr switch
            {
                null or "" => true,
                "hdr" => i.Hdr.Length > 0,
                "sdr" => i.Hdr.Length == 0,
                _ => i.Hdr == Hdr,
            })
            && (!CopiesOnly || i.CopiesIn.Length > 0)
            && (!EndedOnly || i.Ended)
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
            "quality" => i => Array.IndexOf(["SD", "720p", "1080p", "4K"], i.Resolution),
            _ => i => i.Added,
        };

        return Descending
            ? matches.OrderByDescending(key).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            : matches.OrderBy(key).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
    }
}
