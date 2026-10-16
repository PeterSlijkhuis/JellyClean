using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>Copies of one movie or series, with the one to keep picked out.</summary>
/// <param name="Title">The title.</param>
/// <param name="Keep">The copy to keep.</param>
/// <param name="Others">The copies that can go.</param>
public sealed record CopySet(string Title, CatalogItem Keep, CatalogItem[] Others);

/// <summary>
/// Picks which copy of a movie or series to keep when it is in Jellyfin more than once.
/// </summary>
public static class Copies
{
    private static readonly string[] _resolutions = ["SD", "720p", "1080p", "4K"];

    /// <summary>
    /// Groups the copies and picks one to keep in each group. A favorite or kept copy always wins; then the best
    /// quality (resolution, then HDR, then the biggest file) or the smallest file.
    /// </summary>
    /// <param name="items">Every catalog item, with keep marks filled in.</param>
    /// <param name="prefer">"best" or "smallest".</param>
    /// <returns>One set per movie or series that has copies, biggest saving first.</returns>
    public static IEnumerable<CopySet> Resolve(IEnumerable<CatalogItem> items, string prefer)
        => items.Where(i => i.CopyGroup is not null && i.Type is "Movie" or "Series")
            .GroupBy(i => i.CopyGroup)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var ranked = g.OrderByDescending(i => i.Favorite || i.Kept);
                ranked = prefer == "smallest"
                    ? ranked.ThenBy(i => i.Size)
                    : ranked.ThenByDescending(i => Array.IndexOf(_resolutions, i.Resolution)).ThenByDescending(i => i.Hdr.Length > 0).ThenByDescending(i => i.Size);
                var keep = ranked.First();
                return new CopySet(keep.Title, keep, g.Where(i => i != keep).ToArray());
            })
            .OrderByDescending(s => s.Others.Sum(i => i.Size));
}
