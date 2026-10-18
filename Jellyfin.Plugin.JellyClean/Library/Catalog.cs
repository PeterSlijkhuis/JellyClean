using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
    private readonly object _build = new();

    // Walking folders and reading streams is the slow part, so both are kept until the item changes,
    // and saved to disk so a restart doesn't have to walk everything again.
    private ConcurrentDictionary<Guid, Measured>? _files;

    private IReadOnlyDictionary<Guid, CatalogItem>? _items;
    private DateTime _builtAt = DateTime.MinValue;
    private Task? _rebuilding;
    private volatile bool _changed;

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

        // A scan that finds or drops items makes the list out of date; the next look reads it again.
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
        _userDataManager.UserDataSaved += OnUserDataSaved;
    }

    /// <summary>
    /// Gets a number that goes up whenever items were added or removed, so the page can tell when there is something new to show.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Raised when a favorite is set or cleared, so lists that depend on it can follow at once.
    /// </summary>
    public event EventHandler? FavoritesChanged;

    /// <summary>
    /// Gets the catalog, rebuilt when it is older than a few minutes or when asked.
    /// </summary>
    /// <param name="refresh">Rebuild now.</param>
    /// <returns>Every item by id.</returns>
    public IReadOnlyDictionary<Guid, CatalogItem> Get(bool refresh = false)
    {
        lock (_gate)
        {
            // After a change, wait a little so a scan that adds many items is read once.
            var age = DateTime.UtcNow - _builtAt;
            if (_items is not null && !refresh && _builtAt != DateTime.MinValue && (age > _maxAge || (_changed && age > TimeSpan.FromSeconds(30))))
            {
                // Old but usable: answer now and read the library again in the background.
                _rebuilding ??= Task.Run(() =>
                {
                    try
                    {
                        Rebuild();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read the library");
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _rebuilding = null;
                        }
                    }
                });
                return _items;
            }
        }

        if (_items is null || refresh || _builtAt == DateTime.MinValue)
        {
            Rebuild();
        }

        return _items!;
    }

    private void Rebuild()
    {
        // One build at a time; a second caller waits for it and gets its result.
        lock (_build)
        {
            var started = DateTime.UtcNow;
            if (_items is not null && _builtAt > started.AddSeconds(-2))
            {
                return;
            }

            _changed = false;
            var items = Build();
            lock (_gate)
            {
                if (_items is null || _items.Count != items.Count || !_items.Keys.All(items.ContainsKey))
                {
                    Version++;
                }

                _items = items;
                _builtAt = DateTime.UtcNow;
            }

            _logger.LogInformation("Read {Count} items in {Seconds:0.0} s", items.Count, (DateTime.UtcNow - started).TotalSeconds);
            SaveSizes();
        }
    }

    /// <summary>
    /// The items that match a filter, with their keep marks.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The matches, sorted.</returns>
    public List<CatalogItem> Find(Filter filter)
    {
        var kept = Plugin.Instance!.Configuration.KeptIds.ToHashSet();
        var items = Get().Values.Select(i =>
        {
            var copy = i.Copy();
            copy.Kept = kept.Contains(i.Id);
            return copy;
        });

        return filter.Apply(items, _userManager.GetUsers().Count()).ToList();
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

        // The next Get() rebuilds while the caller waits; fine after a removal, which already took a while.
    }

    // Jellyfin drops a series from Next Up a year after it was last played (its default).
    private const int NextUpDays = 365;

    /// <summary>
    /// Marks the episodes someone has yet to see in a series they are watching: everything after the last episode they watched,
    /// when they played any of it in the last <paramref name="since"/>. Specials don't count, like in Jellyfin's Next Up.
    /// </summary>
    /// <param name="episodes">Every episode.</param>
    /// <param name="since">Played after this counts as still watching.</param>
    internal static void MarkNextUp(IEnumerable<CatalogItem> episodes, DateTime since)
    {
        foreach (var show in episodes.Where(e => e.Season > 0).GroupBy(e => e.SeriesId))
        {
            var order = show.OrderBy(e => e.Season).ThenBy(e => e.Episode).ToList();
            foreach (var user in order.SelectMany(e => e.WatchedBy).Distinct())
            {
                if (order.Any(e => e.PlayedOn.TryGetValue(user, out var at) && at > since))
                {
                    var last = order.FindLastIndex(e => e.WatchedBy.Contains(user));
                    order.Skip(last + 1).ToList().ForEach(e => e.NextUp = true);
                }
            }
        }
    }

    /// <summary>
    /// Fills in series and seasons from their episodes: size, who watched all of it, last played and genres.
    /// </summary>
    /// <param name="rows">Every row, episodes already filled in.</param>
    internal static void AddUpFolders(IReadOnlyCollection<CatalogItem> rows)
    {
        var episodes = rows.Where(r => r.Type == "Episode").ToList();
        MarkNextUp(episodes, DateTime.UtcNow.AddDays(-NextUpDays));
        var bySeries = episodes.ToLookup(e => e.SeriesId);
        var bySeason = episodes.ToLookup(e => e.SeasonId);
        var series = rows.Where(r => r.Type == "Series").ToDictionary(r => r.Id);

        foreach (var folder in rows.Where(r => r.Type is "Series" or "Season"))
        {
            var children = (folder.Type == "Series" ? bySeries[folder.Id] : bySeason[folder.Id]).ToList();
            folder.Size = children.Sum(e => e.Size);
            folder.RunTimeTicks = children.Sum(e => e.RunTimeTicks);
            folder.AnyPlayed = children.Any(e => e.AnyPlayed);
            folder.InProgress = children.Any(e => e.InProgress);
            folder.NextUp = children.Any(e => e.NextUp);
            folder.LastWatched = children.Max(e => e.LastWatched);
            folder.WatchedBy = children.Count == 0 ? [] : children.Select(e => (IEnumerable<Guid>)e.WatchedBy).Aggregate((a, b) => a.Intersect(b)).ToArray();
            (folder.Resolution, folder.Codec, folder.Hdr) = Quality.MostCommon(children);
        }

        foreach (var row in rows.Where(r => r.SeriesId is not null))
        {
            series.TryGetValue(row.SeriesId!.Value, out var s);
            row.Ended = s?.Ended ?? false;

            // A season without its own folder has no path, so it takes the library of its series.
            if (row.Library.Length == 0)
            {
                row.Library = s?.Library ?? string.Empty;
            }

            if (row.Genres.Length == 0)
            {
                row.Genres = s?.Genres ?? [];
            }
        }
    }

    /// <summary>
    /// Finds movies and series that are in Jellyfin more than once, for example in a 1080p and a 4K library.
    /// </summary>
    /// <param name="rows">Movies and series with the key they are matched on: a TMDb, TVDb or IMDb id.</param>
    internal static void FindCopies(IEnumerable<(CatalogItem Item, string? Key)> rows)
    {
        foreach (var group in rows.Where(r => r.Key is not null).GroupBy(r => (r.Item.Type, r.Key)).Where(g => g.Count() > 1))
        {
            foreach (var (item, _) in group)
            {
                item.CopiesIn = group.Where(g => g.Item != item).Select(g => g.Item.Library).ToArray();
                item.CopyGroup = group.Key.Type + " " + group.Key.Key;
            }
        }
    }

    private static string? CopyKey(BaseItem item)
        => item is not (MediaBrowser.Controller.Entities.Movies.Movie or Series) ? null
            : item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && tmdb.Length > 0 ? "tmdb" + tmdb
            : item.ProviderIds.TryGetValue("Tvdb", out var tvdb) && tvdb.Length > 0 ? "tvdb" + tvdb
            : item.ProviderIds.TryGetValue("Imdb", out var imdb) && imdb.Length > 0 ? "imdb" + imdb
            : null;

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

        // Measure files in parallel: on a network share most of the time is spent waiting on the disk.
        var files = Sizes();
        var measured = new ConcurrentDictionary<Guid, Measured>();
        Parallel.ForEach(items.Where(i => i is not Folder), new ParallelOptions { MaxDegreeOfParallelism = 8 }, item => measured[item.Id] = Measure(files, item));

        var collections = FavoriteCollections();
        var keys = new List<(CatalogItem, string?)>();
        var rows = items.Select(item =>
        {
            var row = new CatalogItem
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.GetBaseItemKind().ToString(),
                Year = item.ProductionYear,
                Genres = item.Genres ?? [],
                Tags = item.Tags ?? [],
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
                case Series s:
                    row.Ended = s.Status == MediaBrowser.Model.Entities.SeriesStatus.Ended;
                    break;
            }

            var data = userData.Select(u => (u.Id, Data: u.Data.GetValueOrDefault(item.Id))).Where(u => u.Data is not null).ToList();
            row.FavoriteBy = data.Where(u => u.Data!.IsFavorite).Select(u => u.Id).ToArray();
            row.FavoriteCollections = collections.GetValueOrDefault(item.Id) ?? [];
            row.Favorite = row.FavoriteBy.Length > 0 || row.FavoriteCollections.Length > 0;

            keys.Add((row, CopyKey(item)));
            if (item is not Folder)
            {
                var m = measured[item.Id];
                row.Size = m.Size;
                row.RunTimeTicks = item.RunTimeTicks ?? 0;
                (row.Resolution, row.Codec, row.Hdr) = m.Quality;
                row.WatchedBy = data.Where(u => u.Data!.Played).Select(u => u.Id).ToArray();
                row.AnyPlayed = row.WatchedBy.Length > 0 || data.Any(u => u.Data!.PlayCount > 0 || u.Data.PlaybackPositionTicks > 0);
                row.LastWatched = data.Max(u => u.Data!.LastPlayedDate);
                row.InProgress = data.Any(u => !u.Data!.Played && u.Data.PlaybackPositionTicks > 0);
                row.PlayedOn = data.Where(u => u.Data!.LastPlayedDate is not null).ToDictionary(u => u.Id, u => u.Data!.LastPlayedDate!.Value);
            }

            return row;
        }).ToList();

        AddUpFolders(rows);
        FindCopies(keys);

        // Forget items that are gone, so the saved sizes don't grow forever.
        foreach (var id in files.Keys.Where(id => !measured.ContainsKey(id)))
        {
            files.TryRemove(id, out _);
        }

        return rows.ToDictionary(r => r.Id);
    }

    private static bool IsUnder(string? path, string folder)
        => path is not null && path.StartsWith(folder, StringComparison.Ordinal)
            && (path.Length == folder.Length || path[folder.Length] is '/' or '\\');

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is MediaBrowser.Controller.Entities.Movies.Movie or Series or Season or Episode)
        {
            _changed = true;
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.Item is BoxSet)
        {
            OnCollectionFavorite();
            return;
        }

        if (e.Item is not (MediaBrowser.Controller.Entities.Movies.Movie or Series or Season or Episode))
        {
            return;
        }

        // A favorite changes what may be removed, so it shows at once; watched changes come with the next read.
        _changed = true;
        lock (_gate)
        {
            if (_items is not null && _items.TryGetValue(e.Item.Id, out var row))
            {
                var by = FavoriteBy(e.Item);
                if (!row.FavoriteBy.SequenceEqual(by))
                {
                    (row.FavoriteBy, row.Favorite) = (by, by.Length > 0 || row.FavoriteCollections.Length > 0);
                    Version++;
                }
                else
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }

        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    // A favorite collection keeps everything in it, so it changes what may be removed just like a favorite title.
    private void OnCollectionFavorite()
    {
        var collections = FavoriteCollections();
        var changed = false;
        lock (_gate)
        {
            foreach (var row in _items?.Values ?? [])
            {
                var now = collections.GetValueOrDefault(row.Id) ?? [];
                if (!row.FavoriteCollections.SequenceEqual(now))
                {
                    (row.FavoriteCollections, row.Favorite, changed) = (now, row.FavoriteBy.Length > 0 || now.Length > 0, true);
                }
            }

            if (changed)
            {
                Version++;
            }
        }

        if (changed)
        {
            FavoritesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The titles in collections someone made a favorite, with the names of those collections.
    /// The Leaving soon collection does not count: a heart on it would empty the list.
    /// </summary>
    private Dictionary<Guid, string[]> FavoriteCollections()
    {
        var users = _userManager.GetUsers().ToList();
        var leaving = Plugin.Instance?.Configuration.CollectionId ?? Guid.Empty;
        return _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true })
            .OfType<BoxSet>()
            .Where(b => b.Id != leaving && users.Any(u => _userDataManager.GetUserData(u, b)?.IsFavorite == true))
            .SelectMany(b => b.GetLinkedChildren().Select(c => (c.Id, b.Name)))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Name).Distinct().ToArray());
    }

    private Guid[] FavoriteBy(BaseItem item)
        => _userManager.GetUsers().Where(u => _userDataManager.GetUserData(u, item)?.IsFavorite == true).Select(u => u.Id).ToArray();

    /// <summary>
    /// Takes one user's favorite mark off an item.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="userId">The user.</param>
    /// <returns>Whether the item was found.</returns>
    public bool Unfavorite(Guid id, Guid userId)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return false;
        }

        foreach (var user in _userManager.GetUsers().Where(u => u.Id == userId))
        {
            var data = _userDataManager.GetUserData(user, item);
            if (data?.IsFavorite == true)
            {
                data.IsFavorite = false;
                _userDataManager.SaveUserData(user, item, data, MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating, System.Threading.CancellationToken.None);
            }
        }

        return true;
    }

    private static string SizesPath => Path.Combine(Plugin.Instance!.DataFolderPath, "sizes.json");

    private ConcurrentDictionary<Guid, Measured> Sizes()
    {
        if (_files is not null)
        {
            return _files;
        }

        try
        {
            if (File.Exists(SizesPath))
            {
                using var stream = File.OpenRead(SizesPath);
                _files = JsonSerializer.Deserialize<ConcurrentDictionary<Guid, Measured>>(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the saved sizes, measuring again");
        }

        return _files ??= new ConcurrentDictionary<Guid, Measured>();
    }

    private void SaveSizes()
    {
        try
        {
            Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
            var temp = SizesPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_files));
            File.Move(temp, SizesPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the sizes");
        }
    }

    // Size is what Jellyfin's own delete removes: the movie's folder when it has one, else the file and its sidecar files.
    private Measured Measure(ConcurrentDictionary<Guid, Measured> files, BaseItem item)
    {
        if (files.TryGetValue(item.Id, out var cached) && cached.Modified == item.DateModified)
        {
            return cached;
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

        var quality = item is Video video ? Quality.Of(video.GetDefaultVideoStream()) : Quality.None;
        return files[item.Id] = new Measured(item.DateModified, size, quality);
    }
}

/// <summary>
/// A measured item: its size and quality, and when the item last changed.
/// </summary>
/// <param name="Modified">When the item last changed in Jellyfin.</param>
/// <param name="Size">Bytes on disk.</param>
/// <param name="Quality">Video quality.</param>
public record Measured(DateTime Modified, long Size, Quality Quality);
