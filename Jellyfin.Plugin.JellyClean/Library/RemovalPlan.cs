using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// What a removal will do: the items that go and the ones that stay, with why.
/// Preview and the real removal build it the same way.
/// </summary>
public class RemovalPlan
{
    /// <summary>Gets the items that will be removed.</summary>
    public List<PlannedItem> Remove { get; } = [];

    /// <summary>Gets the items that stay, with the reason.</summary>
    public List<PlannedItem> Skip { get; } = [];

    /// <summary>Gets the bytes freed.</summary>
    public long TotalSize => Remove.Sum(r => r.Item.Size);

    /// <summary>
    /// Works out what removing the selected items does. Protection always wins: an item that is kept, a favorite
    /// or being played stays, and so does the season and series it is in.
    /// </summary>
    /// <param name="ids">Selected item ids.</param>
    /// <param name="items">The catalog.</param>
    /// <param name="kept">Ids marked keep.</param>
    /// <param name="protectFavorites">Keep favorites.</param>
    /// <param name="playing">Ids being played now, with their season and series.</param>
    /// <returns>The plan.</returns>
    public static RemovalPlan Build(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, CatalogItem> items, ISet<Guid> kept, bool protectFavorites, ISet<Guid> playing)
    {
        var plan = new RemovalPlan();
        var selected = ids.Distinct().ToList();
        var chosen = selected.ToHashSet();

        foreach (var id in selected)
        {
            if (!items.TryGetValue(id, out var item))
            {
                plan.Skip.Add(new PlannedItem(new CatalogItem { Id = id, Name = "Unknown item" }, "No longer in the library."));
                continue;
            }

            // An episode whose season or series is also selected goes with it.
            if ((item.SeasonId is { } season && chosen.Contains(season)) || (item.SeriesId is { } series && chosen.Contains(series)))
            {
                continue;
            }

            var reason = Protection(item, kept, protectFavorites, playing, "")
                ?? (item.Type is "Series" or "Season"
                    ? items.Values
                        .Where(c => item.Type == "Series" ? c.SeriesId == item.Id : c.SeasonId == item.Id)
                        .Select(c => Protection(c, kept, protectFavorites, playing, c.Type == "Season" ? "A season in it " : "An episode in it "))
                        .FirstOrDefault(r => r is not null)
                    : null);

            (reason is null ? plan.Remove : plan.Skip).Add(new PlannedItem(item, reason));
        }

        return plan;
    }

    private static string? Protection(CatalogItem item, ISet<Guid> kept, bool protectFavorites, ISet<Guid> playing, string prefix)
    {
        var what = prefix.Length == 0 ? "It " : prefix;
        return kept.Contains(item.Id) ? what + "is marked keep."
            : protectFavorites && item.Favorite ? what + "is a favorite."
            : playing.Contains(item.Id) ? what + "is being played right now."
            : null;
    }
}

/// <summary>
/// One item in a <see cref="RemovalPlan"/>.
/// </summary>
/// <param name="Item">The item.</param>
/// <param name="Reason">Why it stays, or null when it goes.</param>
public record PlannedItem(CatalogItem Item, string? Reason)
{
    /// <summary>Gets or sets the files and folders that will be deleted.</summary>
    public string[] Paths { get; set; } = [];

    /// <summary>Gets or sets what happens in Radarr and Sonarr.</summary>
    public List<ArrStep> Arr { get; set; } = [];
}
