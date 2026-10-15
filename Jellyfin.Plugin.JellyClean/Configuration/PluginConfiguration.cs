using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyClean.Configuration;

/// <summary>
/// JellyClean settings. Nothing here is ever written to a library.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether favorites of any user are never removed.
    /// </summary>
    public bool ProtectFavorites { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether items someone stopped halfway (Continue Watching) are never removed.
    /// </summary>
    public bool ProtectInProgress { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days new items are left alone, or 0 for none.
    /// </summary>
    public int ProtectAddedDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets how many days items stay safe after anyone played them, or 0 for none.
    /// </summary>
    public int ProtectPlayedDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets a Jellyfin tag that protects an item and everything in it, or empty for none.
    /// </summary>
    public string KeepTag { get; set; } = "keep";

    /// <summary>
    /// Gets or sets libraries nothing is ever removed from.
    /// </summary>
    public string[] ProtectedLibraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the most items one removal may take, or 0 for no limit.
    /// </summary>
    public int MaxItemsPerRemoval { get; set; } = 200;

    /// <summary>
    /// Gets or sets the items marked keep. They are never removed, and neither is the season or series they are in.
    /// </summary>
    public Guid[] KeptIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the Radarr and Sonarr servers to remove items from as well.
    /// </summary>
    public ArrServer[] Servers { get; set; } = [];

    /// <summary>
    /// Gets or sets the saved filters.
    /// </summary>
    public SavedView[] Views { get; set; } = [];

    /// <summary>
    /// Gets or sets the items waiting on the Leaving soon list.
    /// </summary>
    public LeavingItem[] LeavingSoon { get; set; } = [];

    /// <summary>
    /// Gets or sets how many days items stay on the Leaving soon list before they are due.
    /// </summary>
    public int NoticeDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets a value indicating whether due items are removed by the daily task without asking. Off: they wait for you.
    /// </summary>
    public bool AutoRemoveWhenDue { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Leaving soon list is also shown to users as a Jellyfin collection.
    /// </summary>
    public bool UseCollection { get; set; }

    /// <summary>
    /// Gets or sets the name of that collection.
    /// </summary>
    public string CollectionName { get; set; } = "Leaving soon";

    /// <summary>
    /// Gets or sets the id of that collection, once made.
    /// </summary>
    public Guid CollectionId { get; set; }

    /// <summary>
    /// Gets or sets how many days removed files stay in the recycle bin, or 0 to delete them right away.
    /// </summary>
    public int RecycleDays { get; set; }

    /// <summary>
    /// Gets or sets the items in the recycle bin.
    /// </summary>
    public RecycledItem[] Recycled { get; set; } = [];

    /// <summary>
    /// Gets or sets the bytes JellyClean has freed so far.
    /// </summary>
    public long FreedBytes { get; set; }

    /// <summary>
    /// Gets or sets how many items JellyClean has removed so far.
    /// </summary>
    public int RemovedCount { get; set; }
}

/// <summary>
/// A Radarr or Sonarr server.
/// </summary>
public class ArrServer
{
    /// <summary>
    /// Gets or sets "Radarr" or "Sonarr".
    /// </summary>
    public string Kind { get; set; } = "Radarr";

    /// <summary>
    /// Gets or sets the name shown in the activity log, like "Radarr 4K".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address, like http://192.168.1.10:7878.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key (Settings, General in Radarr or Sonarr).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether removed movies and series go on the exclusion list, so import lists never add them again.
    /// </summary>
    public bool AddExclusion { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether this is a Sonarr server.
    /// </summary>
    public bool IsSonarr => string.Equals(Kind, "Sonarr", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the name to show, falling back to the kind.
    /// </summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Kind : Name;
}

/// <summary>
/// A saved filter.
/// </summary>
public class SavedView
{
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filter, as the Library page sends it (JSON).
    /// </summary>
    public string Filter { get; set; } = "{}";

    /// <summary>
    /// Gets or sets a value indicating whether the view was checked daily. Read only for settings saved by older versions: see <see cref="Schedule"/>.
    /// </summary>
    public bool CheckDaily { get; set; }

    /// <summary>
    /// Gets or sets how often the view puts its matches on the Leaving soon list: empty (never), "daily" or "weekly".
    /// </summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the day a weekly view runs.
    /// </summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;

    /// <summary>
    /// Gets or sets the hour of the day it runs, server time.
    /// </summary>
    public int Hour { get; set; } = 4;

    /// <summary>
    /// Gets or sets how many days its items stay on the list before they are due. 0 uses the general notice period.
    /// </summary>
    public int NoticeDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether its items are removed on their own once due, instead of waiting for you.
    /// </summary>
    public bool AutoRemove { get; set; }

    /// <summary>
    /// Gets or sets when it last ran.
    /// </summary>
    public DateTime? LastRun { get; set; }

    /// <summary>
    /// Gets or sets how many items its last run put on the list.
    /// </summary>
    public int LastAdded { get; set; }

    /// <summary>
    /// Gets how often it runs, counting "check daily" from older versions as daily.
    /// </summary>
    /// <returns>Empty, "daily" or "weekly".</returns>
    public string Runs() => Schedule is "daily" or "weekly" ? Schedule : CheckDaily ? "daily" : string.Empty;

    /// <summary>
    /// Gets when it runs next: the first time at its hour (and day) after its last run. Never run: right away.
    /// </summary>
    /// <param name="now">The current server time.</param>
    /// <returns>The next run, or null when it has no schedule.</returns>
    public DateTime? NextRun(DateTime now)
    {
        var runs = Runs();
        if (runs.Length == 0)
        {
            return null;
        }

        if (LastRun is not { } last)
        {
            return now;
        }

        var next = last.Date.AddHours(Math.Clamp(Hour, 0, 23));
        while (next <= last || (runs == "weekly" && next.DayOfWeek != Day))
        {
            next = next.AddDays(1);
        }

        return next;
    }
}

/// <summary>
/// An item on the Leaving soon list.
/// </summary>
public class LeavingItem
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the title when it was added, for when the item is gone.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was put on the list.
    /// </summary>
    public DateTime Added { get; set; }

    /// <summary>
    /// Gets or sets when it is due for removal.
    /// </summary>
    public DateTime Due { get; set; }

    /// <summary>
    /// Gets or sets who put it there: "you" or the name of a saved view.
    /// </summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// An item in the recycle bin.
/// </summary>
public class RecycledItem
{
    /// <summary>Gets or sets the item id it had in Jellyfin.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets when it was removed.</summary>
    public DateTime Removed { get; set; }

    /// <summary>Gets or sets where each file or folder came from and where it is now.</summary>
    public RecycledPath[] Paths { get; set; } = [];
}

/// <summary>
/// A file or folder in the recycle bin.
/// </summary>
public class RecycledPath
{
    /// <summary>Gets or sets where it was.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets where it is now.</summary>
    public string To { get; set; } = string.Empty;
}
