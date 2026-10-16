using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyClean.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>One month on the space chart.</summary>
/// <param name="Month">The month, like 2026-10.</param>
/// <param name="Size">Space taken at the end of the month by what is in the library now.</param>
/// <param name="Added">Space added that month, by what is still here.</param>
/// <param name="Freed">Space JellyClean freed that month.</param>
/// <param name="FreedCount">Items JellyClean removed that month.</param>
public sealed record MonthPoint(string Month, long Size, long Added, long Freed, int FreedCount);

/// <summary>One user's year in the library.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Name">The user name.</param>
/// <param name="Movies">Movies watched to the end.</param>
/// <param name="Episodes">Episodes watched to the end.</param>
/// <param name="Hours">Hours of what they watched.</param>
/// <param name="ThisYear">Movies and episodes last played this year.</param>
/// <param name="HoursThisYear">Hours of those.</param>
/// <param name="Genres">Their top genres, by how many they watched.</param>
/// <param name="TopSeries">The series they watched most episodes of.</param>
/// <param name="TopSeriesEpisodes">How many episodes of it.</param>
/// <param name="Oldest">The oldest thing they watched.</param>
/// <param name="OldestYear">Its year.</param>
/// <param name="Last">What they played last.</param>
/// <param name="LastAt">When.</param>
/// <param name="Favorites">Movies and series they made a favorite.</param>
/// <param name="MovieHours">Hours of the movies they watched.</param>
/// <param name="EpisodeHours">Hours of the episodes they watched.</param>
/// <param name="Series">Series they watched at least one episode of.</param>
/// <param name="SeriesFinished">Series they watched every episode of that is here.</param>
public sealed record UserYear(
    Guid Id, string Name, int Movies, int Episodes, double Hours, int ThisYear, double HoursThisYear, Slice[] Genres,
    string? TopSeries, int TopSeriesEpisodes, string? Oldest, int? OldestYear, string? Last, DateTime? LastAt, int Favorites,
    double MovieHours, double EpisodeHours, int Series, int SeriesFinished);

/// <summary>Everything on the Stats tab.</summary>
/// <param name="Months">The last 24 months.</param>
/// <param name="FreedThisYear">Bytes freed this calendar year.</param>
/// <param name="RemovedThisYear">Items removed this calendar year.</param>
/// <param name="Hours">Hours of everything in the library.</param>
/// <param name="NeverPlayedHours">Hours nobody ever started.</param>
/// <param name="Shame">The biggest movies and series nobody ever played.</param>
/// <param name="Users">Per user.</param>
/// <param name="Library">What the library is made of.</param>
public sealed record StatsResult(MonthPoint[] Months, long FreedThisYear, int RemovedThisYear, double Hours, double NeverPlayedHours, CatalogItem[] Shame, UserYear[] Users, LibraryInsights Library)
{
    /// <summary>Gets the disks the libraries are on.</summary>
    public DiskSpace[] Disks { get; init; } = [];
}

/// <summary>A disk a library is on.</summary>
/// <param name="Name">Where it is mounted.</param>
/// <param name="Total">Its size in bytes.</param>
/// <param name="Free">Free bytes.</param>
/// <param name="Libraries">The libraries with files on it.</param>
/// <param name="AddedPerMonth">Bytes added to it per month, on average over the last 12 months, by what is still there.</param>
public sealed record DiskSpace(string Name, long Total, long Free, string[] Libraries, long AddedPerMonth);

/// <summary>A genre and how much of it nobody played.</summary>
/// <param name="Name">The genre.</param>
/// <param name="Count">Movies and series.</param>
/// <param name="Size">Bytes.</param>
/// <param name="NeverPlayed">Movies and series nobody played.</param>
/// <param name="NeverPlayedSize">Their bytes.</param>
public sealed record GenreInsight(string Name, int Count, long Size, int NeverPlayed, long NeverPlayedSize);

/// <summary>What the library is made of, for the Insights tab and the digest.</summary>
/// <param name="Movies">Movies.</param>
/// <param name="Series">Series.</param>
/// <param name="Episodes">Episodes.</param>
/// <param name="PlayedShare">Share of the space taken by movies and series someone played, 0 to 1.</param>
/// <param name="Decades">Movies and series per decade they came out.</param>
/// <param name="Genres">The genres taking the most space.</param>
/// <param name="Resolutions">Space per resolution.</param>
/// <param name="Codecs">Space per video codec.</param>
/// <param name="Gems">The best rated movies and series nobody played.</param>
/// <param name="Hogs">Movies taking the most space per hour.</param>
/// <param name="Abandoned">Series people started, never finished and left for half a year.</param>
/// <param name="AbandonedTotal">All of those.</param>
/// <param name="Stale">Played once, but not in two years.</param>
/// <param name="Untouched">Added over a year ago and never played.</param>
/// <param name="Longest">The longest movie.</param>
/// <param name="Oldest">The oldest movie or series.</param>
/// <param name="AverageRating">The average community rating.</param>
public sealed record LibraryInsights(
    int Movies, int Series, int Episodes, double PlayedShare, Slice[] Decades, GenreInsight[] Genres, Slice[] Resolutions, Slice[] Codecs,
    CatalogItem[] Gems, CatalogItem[] Hogs, CatalogItem[] Abandoned, Slice AbandonedTotal, Slice Stale, Slice Untouched,
    CatalogItem? Longest, CatalogItem? Oldest, double AverageRating);

/// <summary>
/// Works out the numbers for the Stats tab.
/// </summary>
public static class Stats
{
    /// <summary>
    /// Builds the stats.
    /// </summary>
    /// <param name="items">Every catalog item.</param>
    /// <param name="users">Every user, id and name.</param>
    /// <param name="freed">Space freed per month.</param>
    /// <param name="now">Now.</param>
    /// <returns>The stats.</returns>
    public static StatsResult Build(IReadOnlyCollection<CatalogItem> items, IEnumerable<(Guid Id, string Name)> users, IEnumerable<FreedMonth> freed, DateTime now)
    {
        var files = items.Where(i => i.Type is "Movie" or "Episode").ToList();
        var freedBy = freed.ToDictionary(f => f.Month);
        var first = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-23);
        var months = Enumerable.Range(0, 24).Select(n =>
        {
            var start = first.AddMonths(n);
            var end = start.AddMonths(1);
            var key = start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            freedBy.TryGetValue(key, out var f);
            return new MonthPoint(
                key,
                files.Where(i => i.Added < end).Sum(i => i.Size),
                files.Where(i => i.Added >= start && i.Added < end).Sum(i => i.Size),
                f?.Bytes ?? 0,
                f?.Count ?? 0);
        }).ToArray();

        var shows = files.Where(i => i.Type == "Episode").GroupBy(i => i.SeriesName).ToList();
        var thisYear = freedBy.Values.Where(f => f.Month.StartsWith(now.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList();
        static double Hours(IEnumerable<CatalogItem> part) => Math.Round(part.Sum(i => i.RunTimeTicks) / (double)TimeSpan.TicksPerHour, 1);

        var year = users.Select(u =>
        {
            var watched = files.Where(i => i.WatchedBy.Contains(u.Id)).ToList();
            var recent = files.Where(i => i.PlayedOn.TryGetValue(u.Id, out var at) && at.Year == now.Year).ToList();
            var top = watched.Where(i => i.Type == "Episode").GroupBy(i => i.SeriesName).MaxBy(g => g.Count());
            var oldest = watched.Where(i => i.Year > 0).MinBy(i => i.Year);
            var last = files.Where(i => i.PlayedOn.ContainsKey(u.Id)).MaxBy(i => i.PlayedOn[u.Id]);
            return new UserYear(
                u.Id,
                u.Name,
                watched.Count(i => i.Type == "Movie"),
                watched.Count(i => i.Type == "Episode"),
                Hours(watched),
                recent.Count,
                Hours(recent),
                watched.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new Slice(g.Key, g.Count(), 0)).OrderByDescending(g => g.Count).Take(3).ToArray(),
                top?.Key,
                top?.Count() ?? 0,
                oldest?.Type == "Episode" ? oldest.SeriesName : oldest?.Title,
                oldest?.Year,
                last?.Title,
                last?.PlayedOn[u.Id],
                items.Count(i => i.Type is "Movie" or "Series" && i.FavoriteBy.Contains(u.Id)),
                Hours(watched.Where(i => i.Type == "Movie")),
                Hours(watched.Where(i => i.Type == "Episode")),
                shows.Count(g => g.Any(i => i.WatchedBy.Contains(u.Id))),
                shows.Count(g => g.All(i => i.WatchedBy.Contains(u.Id))));
        }).OrderByDescending(u => u.HoursThisYear).ThenByDescending(u => u.Hours).ToArray();

        return new StatsResult(
            months,
            thisYear.Sum(f => f.Bytes),
            thisYear.Sum(f => f.Count),
            Hours(files),
            Hours(files.Where(i => !i.AnyPlayed)),
            items.Where(i => i.Type is "Movie" or "Series" && !i.AnyPlayed && i.Size > 0).OrderByDescending(i => i.Size).Take(6).ToArray(),
            year,
            Library(items, now));
    }

    /// <summary>
    /// Builds the insights for the server as it is now, disks included.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="users">The user manager.</param>
    /// <returns>The insights.</returns>
    public static StatsResult For(Catalog catalog, IUserManager users)
    {
        var items = catalog.Get().Values.ToList();
        var now = DateTime.UtcNow;
        return Build(items, users.GetUsers().Select(u => (u.Id, u.Username)), Plugin.Instance!.Configuration.FreedByMonth, now) with { Disks = Disks(items, now) };
    }

    /// <summary>
    /// Finds the disks the movies and episodes are on, by where their files are, and how fast each fills up.
    /// Library folders are not used: a library like Collections only keeps its metadata in Jellyfin's own folder.
    /// </summary>
    /// <param name="items">Every catalog item.</param>
    /// <param name="now">Now (UTC).</param>
    /// <returns>One entry per disk, biggest first.</returns>
    public static DiskSpace[] Disks(IEnumerable<CatalogItem> items, DateTime now)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.TotalSize > 0).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var byFolder = new Dictionary<string, DriveInfo?>(StringComparer.Ordinal);
        DriveInfo? DriveOf(string path)
        {
            var folder = Path.GetDirectoryName(path) ?? path;
            if (!byFolder.TryGetValue(folder, out var drive))
            {
                byFolder[folder] = drive = drives.Where(d => Under(path, d.RootDirectory.FullName)).MaxBy(d => d.RootDirectory.FullName.Length);
            }

            return drive;
        }

        return items.Where(i => i.Type is "Movie" or "Episode" && i.Path.Length > 0)
            .Select(i => (Item: i, Drive: DriveOf(i.Path)))
            .Where(x => x.Drive is not null)
            .GroupBy(x => x.Drive!.RootDirectory.FullName)
            .Select(g =>
            {
                var drive = g.First().Drive!;
                return new DiskSpace(
                    g.Key,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    g.Select(x => x.Item.Library).Where(l => l.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    g.Where(x => x.Item.Added > now.AddYears(-1)).Sum(x => x.Item.Size) / 12);
            })
            .OrderByDescending(d => d.Total)
            .ToArray();
    }

    private static bool Under(string path, string root)
    {
        var folder = root.TrimEnd('/', '\\');
        return path.Equals(folder, StringComparison.Ordinal) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || folder.Length == 0;
    }

    private static LibraryInsights Library(IReadOnlyCollection<CatalogItem> items, DateTime now)
    {
        var titles = items.Where(i => i.Type is "Movie" or "Series").ToList();
        var movies = titles.Where(i => i.Type == "Movie").ToList();
        static Slice Of(string name, IEnumerable<CatalogItem> part) => new(name, part.Count(), part.Sum(i => i.Size));
        var total = Math.Max(1, titles.Sum(i => i.Size));
        var abandoned = titles.Where(i => i.Type == "Series" && i.AnyPlayed && i.WatchedBy.Length == 0 && !i.InProgress && !i.NextUp && i.LastWatched < now.AddDays(-180))
            .OrderByDescending(i => i.Size).ToList();
        var rated = titles.Where(i => i.Rating > 0).ToList();

        return new LibraryInsights(
            movies.Count,
            titles.Count - movies.Count,
            items.Count(i => i.Type == "Episode"),
            titles.Where(i => i.AnyPlayed).Sum(i => i.Size) / (double)total,
            titles.Where(i => i.Year > 1800).GroupBy(i => i.Year!.Value / 10 * 10).OrderBy(g => g.Key)
                .Select(g => Of(g.Key.ToString(CultureInfo.InvariantCulture) + "s", g)).ToArray(),
            Genres(titles),
            titles.GroupBy(i => i.Resolution.Length > 0 ? i.Resolution : "Unknown").Select(g => Of(g.Key, g)).OrderByDescending(g => g.Size).ToArray(),
            items.Where(i => i.Type is "Movie" or "Episode").GroupBy(i => i.Codec.Length > 0 ? i.Codec : "Unknown").Select(g => Of(g.Key, g)).OrderByDescending(g => g.Size).ToArray(),
            titles.Where(i => !i.AnyPlayed && i.Rating > 0).OrderByDescending(i => i.Rating).ThenByDescending(i => i.Size).Take(6).ToArray(),
            movies.Where(i => i.RunTimeTicks >= TimeSpan.FromMinutes(30).Ticks && i.Size > 0).OrderByDescending(i => i.Size / (double)i.RunTimeTicks).Take(5).ToArray(),
            abandoned.Take(6).ToArray(),
            Of("Left halfway", abandoned),
            Of("Not played in two years", titles.Where(i => i.AnyPlayed && i.LastWatched < now.AddYears(-2))),
            Of("Never played, added over a year ago", titles.Where(i => !i.AnyPlayed && i.Added < now.AddYears(-1))),
            movies.Where(i => i.RunTimeTicks > 0).MaxBy(i => i.RunTimeTicks),
            titles.Where(i => i.Year > 1800).MinBy(i => i.Year),
            rated.Count > 0 ? Math.Round(rated.Average(i => (double)i.Rating!.Value), 1) : 0);
    }

    private static GenreInsight[] Genres(List<CatalogItem> titles)
        => titles.SelectMany(i => i.Genres.Select(g => (Genre: g, Item: i)))
            .GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var part = g.Select(x => x.Item).ToList();
                var never = part.Where(i => !i.AnyPlayed).ToList();
                return new GenreInsight(g.Key, part.Count, part.Sum(i => i.Size), never.Count, never.Sum(i => i.Size));
            })
            .OrderByDescending(g => g.Size).Take(10).ToArray();
}
