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
    /// Gets or sets a value indicating whether the daily task puts new matches on the Leaving soon list. Nothing is removed by this.
    /// </summary>
    public bool CheckDaily { get; set; }
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
