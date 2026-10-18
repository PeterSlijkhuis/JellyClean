using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyClean.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyClean.Tests;

public class LibraryTests
{
    private static readonly Guid _anna = Guid.NewGuid();
    private static readonly Guid _ben = Guid.NewGuid();
    private static readonly DateTime _now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CatalogItem Movie(string name, int daysOld = 10, long gb = 1, string[]? genres = null, Guid[]? watchedBy = null, bool played = false, int? lastPlayedDaysAgo = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = "Movie",
            Added = _now.AddDays(-daysOld),
            Size = gb * 1_000_000_000,
            Genres = genres ?? [],
            WatchedBy = watchedBy ?? [],
            AnyPlayed = played || watchedBy is { Length: > 0 },
            LastWatched = lastPlayedDaysAgo is null ? null : _now.AddDays(-lastPlayedDaysAgo.Value),
            Path = "/media/movies/" + name,
        };

    private static List<string> Names(Filter filter, params CatalogItem[] items) => filter.Apply(items, 2, _now).Select(i => i.Name).ToList();

    [Fact]
    public void Watched_none_means_nobody_played_any_of_it()
    {
        var never = Movie("Never");
        var partly = Movie("Partly", played: true);
        var done = Movie("Done", watchedBy: [_anna]);

        Assert.Equal(["Never"], Names(new Filter { Watched = "none" }, never, partly, done));
        Assert.Equal(["Done"], Names(new Filter { Watched = "any" }, never, partly, done));
    }

    [Fact]
    public void Watched_by_everyone_and_by_one_user()
    {
        var anna = Movie("Anna", watchedBy: [_anna]);
        var both = Movie("Both", watchedBy: [_anna, _ben]);

        Assert.Equal(["Both"], Names(new Filter { Watched = "all" }, anna, both));
        Assert.Equal(["Both"], Names(new Filter { Watched = _ben.ToString() }, anna, both));
    }

    [Fact]
    public void Unwatched_horror_added_long_ago()
    {
        var match = Movie("Old horror", daysOld: 800, genres: ["Horror"]);
        var recent = Movie("New horror", daysOld: 20, genres: ["Horror"]);
        var watched = Movie("Watched horror", daysOld: 800, genres: ["Horror"], watchedBy: [_anna]);
        var comedy = Movie("Old comedy", daysOld: 800, genres: ["Comedy"]);

        var filter = new Filter { Genres = ["horror"], Watched = "none", AddedMoreThanDays = 730 };
        Assert.Equal(["Old horror"], Names(filter, match, recent, watched, comedy));
    }

    [Fact]
    public void Max_size_zero_finds_empty_items()
    {
        Assert.Equal(["Empty"], Names(new Filter { MaxSize = 0 }, Movie("Empty", gb: 0), Movie("Full")));
    }

    [Fact]
    public void Not_watched_since_includes_never_played()
    {
        var never = Movie("Never");
        var longAgo = Movie("Long ago", played: true, lastPlayedDaysAgo: 400);
        var lately = Movie("Lately", played: true, lastPlayedDaysAgo: 5);

        Assert.Equal(["Long ago", "Never"], Names(new Filter { NotPlayedForDays = 365, Sort = "name" }, never, longAgo, lately));
        Assert.Equal(["Never"], Names(new Filter { NeverPlayed = true }, never, longAgo, lately));
    }

    [Fact]
    public void Size_search_protection_and_sort()
    {
        var small = Movie("Small", gb: 1);
        var big = Movie("Big", gb: 40);
        var huge = Movie("Huge", gb: 80);
        huge.Kept = true;

        Assert.Equal(["Huge", "Big"], Names(new Filter { MinSize = 10_000_000_000, Sort = "size", Descending = true }, small, big, huge));
        Assert.Equal(["Big"], Names(new Filter { MinSize = 10_000_000_000, HideProtected = true }, small, big, huge));
        Assert.Equal(["Small"], Names(new Filter { Search = "SMA" }, small, big, huge));
    }

    [Fact]
    public void Series_and_seasons_add_up_their_episodes()
    {
        var series = new CatalogItem { Id = Guid.NewGuid(), Name = "Dark", Type = "Series", Genres = ["Drama"], Ended = true, Library = "Shows" };
        var season = new CatalogItem { Id = Guid.NewGuid(), Name = "Season 1", Type = "Season", SeriesId = series.Id, Season = 1 };
        CatalogItem Episode(int n, Guid[] watchedBy, int? lastDaysAgo) => new()
        {
            Id = Guid.NewGuid(), Name = "E" + n, Type = "Episode", SeriesId = series.Id, SeriesName = "Dark", SeasonId = season.Id, Season = 1, Episode = n,
            Size = 2_000_000_000, WatchedBy = watchedBy, AnyPlayed = watchedBy.Length > 0,
            LastWatched = lastDaysAgo is null ? null : _now.AddDays(-lastDaysAgo.Value),
        };
        var e1 = Episode(1, [_anna, _ben], 30);
        var e2 = Episode(2, [_anna], 10);

        Catalog.AddUpFolders([series, season, e1, e2]);

        Assert.Equal(4_000_000_000, series.Size);
        Assert.Equal(4_000_000_000, season.Size);
        Assert.Equal([_anna], series.WatchedBy);
        Assert.True(series.AnyPlayed);
        Assert.Equal(_now.AddDays(-10), season.LastWatched);
        Assert.Equal(["Drama"], e1.Genres);
        Assert.Equal("Shows", season.Library);
        Assert.Equal("Dark S01E02 E2", e2.Title);
        Assert.True(e2.Ended);
        Assert.Equal(2, new Filter { EndedOnly = true, Types = ["Series", "Season"] }.Apply([series, season, e1, new CatalogItem { Name = "Running", Type = "Series" }], 2).Count());
    }

    [Fact]
    public void Next_up_keeps_what_a_viewer_has_yet_to_see()
    {
        var show = Guid.NewGuid();
        CatalogItem Ep(int season, int number, params Guid[] watched) => new()
        {
            Id = Guid.NewGuid(), Type = "Episode", SeriesId = show, Season = season, Episode = number, WatchedBy = watched,
            PlayedOn = watched.ToDictionary(u => u, _ => _now.AddDays(-3)),
        };
        var special = Ep(0, 1);
        var e1 = Ep(1, 1, _anna);
        var e2 = Ep(1, 2);
        var e3 = Ep(2, 1);
        var stale = Ep(1, 1, _ben);
        stale.SeriesId = Guid.NewGuid();
        stale.PlayedOn[_ben] = _now.AddYears(-2);
        var staleNext = Ep(1, 2);
        staleNext.SeriesId = stale.SeriesId;

        Catalog.MarkNextUp([e3, special, e2, e1, stale, staleNext], _now.AddDays(-365));

        Assert.False(e1.NextUp);
        Assert.True(e2.NextUp);
        Assert.True(e3.NextUp);
        Assert.False(special.NextUp);
        Assert.False(staleNext.NextUp);
        Assert.Contains("Next Up", new Safeguards().Check(e2, "It "), StringComparison.Ordinal);
        Assert.Null(new Safeguards { NextUp = false }.Check(e2, "It "));
    }

    [Fact]
    public void Breakdown_puts_each_item_in_one_status_and_adds_up()
    {
        CatalogItem Movie(long size) => new() { Id = Guid.NewGuid(), Type = "Movie", Library = "Movies", Size = size, Added = _now.AddYears(-2) };
        var fav = Movie(10);
        fav.Favorite = true;
        var everyone = Movie(20);
        everyone.WatchedBy = [_anna, _ben];
        everyone.AnyPlayed = true;
        var some = Movie(30);
        some.WatchedBy = [_anna];
        some.AnyPlayed = true;
        var never = Movie(40);
        var kept = Movie(5);
        kept.AnyPlayed = true;
        kept.InProgress = true;

        var lib = Assert.Single(Breakdown.Build([fav, everyone, some, never, kept], new HashSet<Guid> { kept.Id }, 2, _now));

        Assert.Equal(105, lib.Size);
        Assert.Equal(lib.Size, lib.Status.Sum(s => s.Size));
        Assert.Equal(lib.Count, lib.Status.Sum(s => s.Count));
        Assert.Equal(new Slice("Favorite or keep", 2, 15), lib.Status[0]);
        Assert.Equal(new Slice("Never played", 1, 40), lib.Status[^1]);
        Assert.Contains(new Slice("Never played", 2, 50), lib.Facts);
        Assert.Contains(new Slice("Favorites", 1, 10), lib.Facts);
        Assert.Equal("Movie", lib.Kind);

        // Each part of the bar lists exactly what it counts.
        kept.Kept = true;
        foreach (var slice in lib.Status)
        {
            var found = new Filter { Library = "Movies", Status = slice.Name }.Apply([fav, everyone, some, never, kept], 2, _now).ToList();
            Assert.Equal(slice.Count, found.Count);
            Assert.Equal(slice.Size, found.Sum(i => i.Size));
        }
    }

    [Fact]
    public void Copies_keep_the_best_or_the_smallest_and_never_a_favorite()
    {
        CatalogItem Copy(string res, long size, string lib) => new() { Id = Guid.NewGuid(), Name = "Alien", Type = "Movie", Resolution = res, Size = size, Library = lib, CopyGroup = "Movie tmdb348" };
        var hd = Copy("1080p", 10, "Movies");
        var uhd = Copy("4K", 50, "Movies 4K");
        var single = new CatalogItem { Id = Guid.NewGuid(), Type = "Movie", Size = 5 };

        var best = Assert.Single(Catalog_copies([hd, uhd, single], "best"));
        Assert.Same(uhd, best.Keep);
        Assert.Equal([hd], best.Others);
        Assert.Same(hd, Assert.Single(Catalog_copies([hd, uhd], "smallest")).Keep);

        uhd.Favorite = true;
        Assert.Same(uhd, Assert.Single(Catalog_copies([hd, uhd], "smallest")).Keep);

        // A copy on a drive that is offline looks empty: keeping it would offer the real one for removal.
        uhd.Favorite = false;
        var offline = Copy("4K", 0, "Movies 4K offline");
        Assert.Same(hd, Assert.Single(Catalog_copies([hd, offline], "smallest")).Keep);
        Assert.Same(hd, Assert.Single(Catalog_copies([hd, offline], "best")).Keep);
    }

    [Fact]
    public void Stats_count_per_month_and_per_user()
    {
        var now = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        var movie = new CatalogItem { Id = Guid.NewGuid(), Name = "Alien", Type = "Movie", Year = 1979, Size = 100, Added = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), WatchedBy = [_anna], AnyPlayed = true, RunTimeTicks = TimeSpan.FromHours(2).Ticks, Genres = ["Horror"] };
        movie.PlayedOn[_anna] = now.AddDays(-1);
        var unplayed = new CatalogItem { Id = Guid.NewGuid(), Name = "Dune", Type = "Movie", Size = 300, Added = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), RunTimeTicks = TimeSpan.FromHours(3).Ticks };

        CatalogItem Episode(string show, bool seen) => new() { Id = Guid.NewGuid(), Name = "E", Type = "Episode", SeriesName = show, Added = now, WatchedBy = seen ? [_anna] : [], AnyPlayed = seen, RunTimeTicks = TimeSpan.FromHours(1).Ticks };

        var stats = Stats.Build([movie, unplayed, Episode("Dark", true), Episode("Dark", true), Episode("Lost", true), Episode("Lost", false)], [(_anna, "Anna")], [new Jellyfin.Plugin.JellyClean.Configuration.FreedMonth { Month = "2026-10", Bytes = 50, Count = 2 }], now);

        Assert.Equal(24, stats.Months.Length);
        Assert.Equal(new MonthPoint("2026-09", 100, 100, 0, 0), stats.Months[^2]);
        Assert.Equal(new MonthPoint("2026-10", 400, 300, 50, 2), stats.Months[^1]);
        Assert.Equal(50, stats.FreedThisYear);
        Assert.Equal(4, stats.NeverPlayedHours);
        Assert.Same(unplayed, Assert.Single(stats.Shame));
        var anna = Assert.Single(stats.Users);
        Assert.Equal((1, 5.0, 1, "Alien (1979)", "Horror"), (anna.Movies, anna.Hours, anna.ThisYear, anna.Oldest, anna.Genres[0].Name));
        Assert.Equal((2.0, 3.0, 2, 1), (anna.MovieHours, anna.EpisodeHours, anna.Series, anna.SeriesFinished));
    }

    private static List<CopySet> Catalog_copies(CatalogItem[] items, string prefer) => Copies.Resolve(items, prefer).ToList();

    [Fact]
    public void Empty_series_counts_as_never_watched()
    {
        var series = new CatalogItem { Id = Guid.NewGuid(), Name = "Empty", Type = "Series" };
        Catalog.AddUpFolders([series]);
        Assert.Empty(series.WatchedBy);
        Assert.False(series.AnyPlayed);
        Assert.Equal(0, series.Size);
    }

    [Fact]
    public void Bytes_reads_like_a_person_would_write_it()
    {
        Assert.Equal("186 GB", Remover.Bytes(186_000_000_000));
        Assert.Equal("1.5 TB", Remover.Bytes(1_500_000_000_000));
        Assert.Equal("512 B", Remover.Bytes(512));
    }

    [Fact]
    public void Scheduled_views_run_at_their_hour_and_day()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0); // a Thursday
        var view = new Jellyfin.Plugin.JellyClean.Configuration.SavedView { Schedule = "daily", Hour = 4 };
        Assert.Equal(now, view.NextRun(now));

        view.LastRun = new DateTime(2026, 10, 15, 4, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 16, 4, 0, 0), view.NextRun(now));

        view.LastRun = new DateTime(2026, 10, 15, 3, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 15, 4, 0, 0), view.NextRun(now));

        (view.Schedule, view.Day) = ("weekly", DayOfWeek.Monday);
        Assert.Equal(new DateTime(2026, 10, 19, 4, 0, 0), view.NextRun(now));

        view.Schedule = string.Empty;
        Assert.Null(view.NextRun(now));
        view.CheckDaily = true;
        Assert.Equal("daily", view.Runs());
    }

    [Fact]
    public void A_favorite_collection_keeps_what_is_in_it()
    {
        var movie = new CatalogItem { Id = Guid.NewGuid(), Type = "Movie", Favorite = true, FavoriteCollections = ["Harry Potter"] };
        Assert.Equal("It is in Harry Potter, a favorite collection.", new Safeguards().Check(movie, "It "));
        Assert.Null(new Safeguards { Favorites = false }.Check(movie, "It "));
    }
}
