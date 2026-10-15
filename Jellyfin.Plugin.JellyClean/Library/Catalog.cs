using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// Reads every movie, series, season and episode with its size and watched state. Only reads: nothing is written anywhere.
/// </summary>
public class Catalog
{
    private static readonly TimeSpan _maxAge = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<Catalog> _logger;
    private readonly object _gate = new();

    // Walking folders for sizes is the slow part, so sizes are kept until the item changes.
    private readonly ConcurrentDictionary<Guid, (DateTime Modified, long Size)> _sizes = new();

    private IReadOnlyDictionary<Guid, CatalogItem> _items = new Dictionary<Guid, CatalogItem>();
    private DateTime _builtAt = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="Catalog"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="logger">Logger.</param>
    public Catalog(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, ILogger<Catalog> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the catalog, rebuilt when it is older than a few minutes or when asked.
    /// </summary>
    /// <param name="refresh">Rebuild now.</param>
    /// <returns>Every item by id.</returns>
    public IReadOnlyDictionary<Guid, CatalogItem> Get(bool refresh = false)
    {
        lock (_gate)
        {
            if (refresh || DateTime.UtcNow - _builtAt > _maxAge)
            {
                var started = DateTime.UtcNow;
                _items = Build();
                _builtAt = DateTime.UtcNow;
                _logger.LogInformation("Read {Count} items in {Seconds:0.0} s", _items.Count, (_builtAt - started).TotalSeconds);
            }

            return _items;
        }
    }

    /// <summary>
    /// Makes the next <see cref="Get"/> read the library again.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _builtAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Fills in series and seasons from their episodes: size, who watched all of it, last played and genres.
    /// </summary>
    /// <param name="rows">Every row, episodes already filled in.</param>
    internal static void AddUpFolders(IReadOnlyCollection<CatalogItem> rows)
    {
        var episodes = rows.Where(r => r.Type == "Episode").ToList();
        var bySeries = episodes.ToLookup(e => e.SeriesId);
        var bySeason = episodes.ToLookup(e => e.SeasonId);
        var series = rows.Where(r => r.Type == "Series").ToDictionary(r => r.Id);

        foreach (var folder in rows.Where(r => r.Type is "Series" or "Season"))
        {
            var children = (folder.Type == "Series" ? bySeries[folder.Id] : bySeason[folder.Id]).ToList();
            folder.Size = children.Sum(e => e.Size);
            folder.AnyPlayed = children.Any(e => e.AnyPlayed);
            folder.LastWatched = children.Max(e => e.LastWatched);
            folder.WatchedBy = children.Count == 0 ? [] : children.Select(e => (IEnumerable<Guid>)e.WatchedBy).Aggregate((a, b) => a.Intersect(b)).ToArray();
        }

        foreach (var row in rows.Where(r => r.Genres.Length == 0 && r.SeriesId is not null))
        {
            row.Genres = series.TryGetValue(row.SeriesId!.Value, out var s) ? s.Genres : [];
        }
    }

    private Dictionary<Guid, CatalogItem> Build()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true,
        });

        var userData = _userManager.GetUsers().Select(u => (u.Id, Data: _userDataManager.GetUserDataBatch(items, u))).ToList();
        var libraries = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations.Select(l => (Location: l.TrimEnd('/', '\\'), f.Name)))
            .OrderByDescending(l => l.Location.Length)
            .ToList();

        var rows = items.Select(item =>
        {
            var row = new CatalogItem
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.GetBaseItemKind().ToString(),
                Year = item.ProductionYear,
                Genres = item.Genres ?? [],
                Added = item.DateCreated,
                Path = item.Path ?? string.Empty,
                Rating = item.CommunityRating,
                Library = libraries.FirstOrDefault(l => IsUnder(item.Path, l.Location)).Name ?? string.Empty,
            };

            switch (item)
            {
                case Episode e:
                    (row.SeriesId, row.SeasonId, row.SeriesName) = (e.SeriesId, e.SeasonId, e.SeriesName);
                    (row.Season, row.Episode, row.EpisodeEnd) = (e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd);
                    break;
                case Season s:
                    (row.SeriesId, row.SeriesName, row.Season) = (s.SeriesId, s.SeriesName, s.IndexNumber);
                    break;
            }

            var data = userData.Select(u => (u.Id, Data: u.Data.GetValueOrDefault(item.Id))).Where(u => u.Data is not null).ToList();
            row.Favorite = data.Any(u => u.Data!.IsFavorite);

            if (item is not Folder)
            {
                row.Size = SizeOf(item);
                row.WatchedBy = data.Where(u => u.Data!.Played).Select(u => u.Id).ToArray();
                row.AnyPlayed = row.WatchedBy.Length > 0 || data.Any(u => u.Data!.PlayCount > 0 || u.Data.PlaybackPositionTicks > 0);
                row.LastWatched = data.Max(u => u.Data!.LastPlayedDate);
            }

            return row;
        }).ToList();

        AddUpFolders(rows);
        return rows.ToDictionary(r => r.Id);
    }

    private static bool IsUnder(string? path, string folder)
        => path is not null && path.StartsWith(folder, StringComparison.Ordinal)
            && (path.Length == folder.Length || path[folder.Length] is '/' or '\\');

    // What Jellyfin's own delete removes: the movie's folder when it has one, else the file and its sidecar files.
    private long SizeOf(BaseItem item)
    {
        if (_sizes.TryGetValue(item.Id, out var cached) && cached.Modified == item.DateModified)
        {
            return cached.Size;
        }

        long size = 0;
        try
        {
            foreach (var path in item.GetDeletePaths())
            {
                size += path.IsDirectory
                    ? new DirectoryInfo(path.FullName).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : File.Exists(path.FullName) ? new FileInfo(path.FullName).Length : 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to what Jellyfin recorded for the file.
            _logger.LogDebug(ex, "Could not measure {Path}", item.Path);
            size = item.Size ?? 0;
        }

        _sizes[item.Id] = (item.DateModified, size);
        return size;
    }
}
