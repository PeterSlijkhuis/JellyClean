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
    /// Works out what removing the selected items does. Safeguards always win: a protected item stays,
    /// and so does the season and series it is in. A kept, tagged or favorite series keeps its episodes too.
    /// </summary>
    /// <param name="ids">Selected item ids.</param>
    /// <param name="items">The catalog.</param>
    /// <param name="safeguards">What to protect.</param>
    /// <returns>The plan.</returns>
    public static RemovalPlan Build(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, CatalogItem> items, Safeguards safeguards)
    {
        var plan = new RemovalPlan();
        var selected = ids.Distinct().ToList();
        var chosen = selected.ToHashSet();
        var bySeries = items.Values.Where(i => i.SeriesId is not null).ToLookup(i => i.SeriesId);
        var bySeason = items.Values.Where(i => i.SeasonId is not null).ToLookup(i => i.SeasonId);

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

            var parents = new[] { item.SeasonId, item.SeriesId }
                .Select(p => p is { } pid ? items.GetValueOrDefault(pid) : null)
                .OfType<CatalogItem>();
            var children = item.Type switch { "Series" => bySeries[item.Id], "Season" => bySeason[item.Id], _ => [] };

            var reason = safeguards.Check(item, "It ")
                ?? parents.Select(p => safeguards.CheckParent(p, $"Its {p.Type.ToLowerInvariant()} ")).FirstOrDefault(r => r is not null)
                ?? children.Select(c => safeguards.Check(c, c.Type == "Season" ? "A season in it " : "An episode in it ")).FirstOrDefault(r => r is not null);

            (reason is null ? plan.Remove : plan.Skip).Add(new PlannedItem(item, reason));
        }

        if (safeguards.MaxItems > 0 && plan.Remove.Count > safeguards.MaxItems)
        {
            foreach (var over in plan.Remove.Skip(safeguards.MaxItems).ToList())
            {
                plan.Remove.Remove(over);
                plan.Skip.Add(over with { Reason = $"Over the limit of {safeguards.MaxItems} items per removal (Settings)." });
            }
        }

        return plan;
    }
}

/// <summary>
/// What is never removed. Built from the settings for each removal.
/// </summary>
public class Safeguards
{
    /// <summary>Gets or sets ids marked keep.</summary>
    public ISet<Guid> Kept { get; set; } = new HashSet<Guid>();

    /// <summary>Gets or sets ids being played now, with their season and series.</summary>
    public ISet<Guid> Playing { get; set; } = new HashSet<Guid>();

    /// <summary>Gets or sets a value indicating whether favorites stay.</summary>
    public bool Favorites { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether items someone is in the middle of (Continue Watching) stay.</summary>
    public bool InProgress { get; set; } = true;

    /// <summary>Gets or sets a date: items added after it stay.</summary>
    public DateTime? AddedAfter { get; set; }

    /// <summary>Gets or sets a date: items played after it stay.</summary>
    public DateTime? PlayedAfter { get; set; }

    /// <summary>Gets or sets a Jellyfin tag that protects an item, like "keep".</summary>
    public string? Tag { get; set; }

    /// <summary>Gets or sets libraries nothing is ever removed from.</summary>
    public ISet<string> Libraries { get; set; } = new HashSet<string>();

    /// <summary>Gets or sets the most items one removal may take, or 0 for no limit.</summary>
    public int MaxItems { get; set; }

    /// <summary>
    /// Why an item must stay, or null.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="who">How to start the reason, like "It " or "An episode in it ".</param>
    /// <returns>The reason, or null.</returns>
    public string? Check(CatalogItem item, string who)
        => CheckParent(item, who)
            ?? (Playing.Contains(item.Id) ? who + "is being played right now."
            : InProgress && item.InProgress ? who + "is in someone's Continue Watching."
            : AddedAfter is { } added && item.Added > added && item.Type != "Series" ? who + $"was added in the last {Days(added)} days."
            : PlayedAfter is { } played && item.LastWatched > played ? who + $"was played in the last {Days(played)} days."
            : null);

    /// <summary>
    /// Why a series or season keeps everything in it, or null. Only marks a person set count here, not recent activity.
    /// </summary>
    /// <param name="item">The series or season.</param>
    /// <param name="who">How to start the reason.</param>
    /// <returns>The reason, or null.</returns>
    public string? CheckParent(CatalogItem item, string who)
        => Kept.Contains(item.Id) ? who + "is marked keep."
            : !string.IsNullOrWhiteSpace(Tag) && item.Tags.Contains(Tag.Trim(), StringComparer.OrdinalIgnoreCase) ? who + $"has the tag \"{Tag.Trim()}\"."
            : Favorites && item.Favorite ? who + "is a favorite."
            : Libraries.Contains(item.Library) ? $"Nothing is removed from the {item.Library} library (Settings)."
            : null;

    private static int Days(DateTime since) => (int)Math.Round((DateTime.UtcNow - since).TotalDays);
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
