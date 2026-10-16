using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyClean.Configuration;

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
public sealed record StatsResult(MonthPoint[] Months, long FreedThisYear, int RemovedThisYear, double Hours, double NeverPlayedHours, CatalogItem[] Shame, UserYear[] Users);

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
            year);
    }
}
