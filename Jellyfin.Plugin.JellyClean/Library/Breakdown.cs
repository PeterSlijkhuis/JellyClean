using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>A count and the space it takes.</summary>
/// <param name="Name">What is counted, like "Never played" or "4K".</param>
/// <param name="Count">How many movies or series.</param>
/// <param name="Size">Bytes on disk.</param>
public sealed record Slice(string Name, int Count, long Size);

/// <summary>One library at a glance, as the Library tab draws it.</summary>
/// <param name="Name">The library.</param>
/// <param name="Kind">"Movie" or "Series", whichever it mostly holds.</param>
/// <param name="Count">Movies and series in it.</param>
/// <param name="Episodes">Episodes in it.</param>
/// <param name="Size">Bytes on disk.</param>
/// <param name="Status">Watch status, each item in exactly one slice, adding up to the whole library.</param>
/// <param name="Facts">Things worth knowing that may overlap, like favorites and never played.</param>
/// <param name="Quality">Per resolution.</param>
/// <param name="Genres">The genres taking the most space.</param>
/// <param name="Biggest">The biggest movie or series.</param>
public sealed record LibraryBreakdown(string Name, string Kind, int Count, int Episodes, long Size, Slice[] Status, Slice[] Facts, Slice[] Quality, Slice[] Genres, Slice? Biggest);

/// <summary>
/// Works out what is in each library, for the overview on the Library tab.
/// </summary>
public static class Breakdown
{
    /// <summary>The watch status slices, in the order the bar draws them.</summary>
    public static readonly string[] StatusNames = ["Favorite or keep", "Being watched", "Watched by everyone", "Watched by some", "Never played"];

    /// <summary>
    /// Builds the breakdown of every library.
    /// </summary>
    /// <param name="items">Every catalog item.</param>
    /// <param name="kept">Ids marked keep.</param>
    /// <param name="users">How many users there are.</param>
    /// <param name="now">Now, for "added in the last 30 days".</param>
    /// <returns>One breakdown per library, biggest first.</returns>
    public static IEnumerable<LibraryBreakdown> Build(IEnumerable<CatalogItem> items, ISet<Guid> kept, int users, DateTime now)
    {
        var episodes = items.Where(i => i.Type == "Episode").ToLookup(i => i.Library);
        return items.Where(i => i.Type is "Movie" or "Series" && i.Library.Length > 0)
            .GroupBy(i => i.Library)
            .Select(g =>
            {
                var all = g.ToList();
                var status = all.ToLookup(i => Status(i, kept.Contains(i.Id), users));
                Slice Of(string name, IEnumerable<CatalogItem> part) => new(name, part.Count(), part.Sum(i => i.Size));
                return new LibraryBreakdown(
                    g.Key,
                    all.Count(i => i.Type == "Series") > all.Count(i => i.Type == "Movie") ? "Series" : "Movie",
                    all.Count,
                    episodes[g.Key].Count(),
                    all.Sum(i => i.Size),
                    StatusNames.Select(n => Of(n, status[n])).ToArray(),
                    new[]
                    {
                        Of("Favorites", all.Where(i => i.Favorite)),
                        Of("Marked keep", all.Where(i => kept.Contains(i.Id))),
                        Of("Never played", all.Where(i => !i.AnyPlayed)),
                        Of("Added in the last 30 days", all.Where(i => i.Added > now.AddDays(-30))),
                        Of("Not played in a year", all.Where(i => i.AnyPlayed && i.LastWatched < now.AddYears(-1))),
                        Of("Ended series", all.Where(i => i.Type == "Series" && i.Ended)),
                        Of("HDR", all.Where(i => i.Hdr.Length > 0 && i.Hdr != "SDR")),
                        Of("Also in another library", all.Where(i => i.CopiesIn.Length > 0)),
                        Of("Empty (0 B)", all.Where(i => i.Size == 0)),
                    }.Where(s => s.Count > 0).ToArray(),
                    all.GroupBy(i => i.Resolution.Length > 0 ? i.Resolution : "Unknown").Select(q => Of(q.Key, q)).OrderByDescending(q => q.Size).ToArray(),
                    all.SelectMany(i => i.Genres.Select(x => (Genre: x, Item: i))).GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
                        .Select(x => Of(x.Key, x.Select(y => y.Item))).OrderByDescending(x => x.Size).Take(6).ToArray(),
                    all.Where(i => i.Size > 0).MaxBy(i => i.Size) is { } big ? new Slice(big.Title, 1, big.Size) : null);
            })
            .OrderByDescending(l => l.Size);
    }

    /// <summary>
    /// The one watch status slice an item falls in. Protection comes first, because that is what decides whether it can go.
    /// </summary>
    /// <param name="item">A movie or series.</param>
    /// <param name="kept">Whether it is marked keep.</param>
    /// <param name="users">How many users there are.</param>
    /// <returns>One of <see cref="StatusNames"/>.</returns>
    internal static string Status(CatalogItem item, bool kept, int users)
        => item.Favorite || kept ? StatusNames[0]
            : item.InProgress || item.NextUp ? StatusNames[1]
            : users > 0 && item.WatchedBy.Length >= users ? StatusNames[2]
            : item.AnyPlayed ? StatusNames[3]
            : StatusNames[4];
}
