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
    /// Gets or sets the items marked keep. They are never removed, and neither is the season or series they are in.
    /// </summary>
    public Guid[] KeptIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the Radarr and Sonarr servers to remove items from as well.
    /// </summary>
    public ArrServer[] Servers { get; set; } = [];
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
