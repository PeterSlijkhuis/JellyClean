using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// One movie, series, season or episode as the Library page shows it.
/// </summary>
public sealed class CatalogItem
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets "Movie", "Series", "Season" or "Episode".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the series of a season or episode.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the season of an episode.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>Gets or sets the series name of a season or episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the season number of a season or episode.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets the last episode number of a file with several episodes.</summary>
    public int? EpisodeEnd { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the library name.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the genres; seasons and episodes get those of their series.</summary>
    public string[] Genres { get; set; } = [];

    /// <summary>Gets or sets when it was added to Jellyfin.</summary>
    public DateTime Added { get; set; }

    /// <summary>Gets or sets the bytes that removing it frees. Series and seasons add up their episodes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the file or folder path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolution: 4K, 1080p, 720p or SD. Series and seasons show their most common one.</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Gets or sets the video codec, like HEVC.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the HDR format, like Dolby Vision, or empty for SDR.</summary>
    public string Hdr { get; set; } = string.Empty;

    /// <summary>Gets or sets the libraries of other copies of the same movie or series, found by TMDb, TVDb or IMDb id.</summary>
    public string[] CopiesIn { get; set; } = [];

    /// <summary>Gets or sets the community rating.</summary>
    public float? Rating { get; set; }

    /// <summary>Gets or sets the users who watched it to the end. For series and seasons: every episode.</summary>
    public Guid[] WatchedBy { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether anyone played any of it, even one episode.</summary>
    public bool AnyPlayed { get; set; }

    /// <summary>Gets or sets a value indicating whether someone stopped halfway, so it is in their Continue Watching.</summary>
    public bool InProgress { get; set; }

    /// <summary>Gets or sets a value indicating whether someone is watching the series and has not seen this yet, so it is in their Next Up or comes after it.</summary>
    public bool NextUp { get; set; }

    /// <summary>Gets or sets when each user last played it. Only used to build the catalog.</summary>
    internal Dictionary<Guid, DateTime> PlayedOn { get; set; } = [];

    /// <summary>Gets or sets the Jellyfin tags.</summary>
    public string[] Tags { get; set; } = [];

    /// <summary>Gets or sets when anyone last played it.</summary>
    public DateTime? LastWatched { get; set; }

    /// <summary>Gets or sets a value indicating whether any user made it a favorite.</summary>
    public bool Favorite { get; set; }

    /// <summary>Gets or sets a value indicating whether the series it is (in) has ended, so no new episodes come.</summary>
    public bool Ended { get; set; }

    /// <summary>Gets or sets the users who made it a favorite.</summary>
    public Guid[] FavoriteBy { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether it is marked keep. Filled in per request.</summary>
    public bool Kept { get; set; }

    /// <summary>
    /// Gets the name to show in lists and logs, like "Dark S01E02 Secrets".
    /// </summary>
    public string Title => Type switch
    {
        "Season" => $"{SeriesName} {Name}",
        "Episode" => $"{SeriesName} S{Season:00}E{Episode:00}{(EpisodeEnd > Episode ? $"-E{EpisodeEnd:00}" : string.Empty)} {Name}",
        _ => Year is null ? Name : $"{Name} ({Year})",
    };

    /// <summary>
    /// Returns a copy, so per-request flags never leak into the shared catalog.
    /// </summary>
    /// <returns>The copy.</returns>
    public CatalogItem Copy() => (CatalogItem)MemberwiseClone();
}
