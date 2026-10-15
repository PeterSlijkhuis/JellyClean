using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyClean.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyClean.Tests;

public class RemovalPlanTests
{
    private readonly CatalogItem _movie = new() { Id = Guid.NewGuid(), Name = "Movie", Type = "Movie", Size = 5 };
    private readonly CatalogItem _series = new() { Id = Guid.NewGuid(), Name = "Show", Type = "Series", Size = 20 };
    private readonly CatalogItem _season;
    private readonly CatalogItem _episode;

    public RemovalPlanTests()
    {
        _season = new() { Id = Guid.NewGuid(), Name = "Season 1", Type = "Season", SeriesId = _series.Id, Season = 1, Size = 20 };
        _episode = new() { Id = Guid.NewGuid(), Name = "Pilot", Type = "Episode", SeriesId = _series.Id, SeasonId = _season.Id, Season = 1, Episode = 1, Size = 20 };
    }

    private Dictionary<Guid, CatalogItem> Items => new[] { _movie, _series, _season, _episode }.ToDictionary(i => i.Id);

    private RemovalPlan Plan(Guid[] ids, Guid[]? kept = null, bool protectFavorites = true, Guid[]? playing = null)
        => RemovalPlan.Build(ids, Items, (kept ?? []).ToHashSet(), protectFavorites, (playing ?? []).ToHashSet());

    [Fact]
    public void Removes_what_was_selected()
    {
        var plan = Plan([_movie.Id, _series.Id]);
        Assert.Equal([_movie.Id, _series.Id], plan.Remove.Select(p => p.Item.Id));
        Assert.Equal(25, plan.TotalSize);
        Assert.Empty(plan.Skip);
    }

    [Fact]
    public void Kept_items_stay()
    {
        var plan = Plan([_movie.Id], kept: [_movie.Id]);
        Assert.Empty(plan.Remove);
        Assert.Equal("It is marked keep.", plan.Skip.Single().Reason);
    }

    [Fact]
    public void A_kept_episode_keeps_its_series_and_season()
    {
        var plan = Plan([_series.Id, _season.Id], kept: [_episode.Id]);
        Assert.Empty(plan.Remove);
        Assert.Equal(["An episode in it is marked keep."], plan.Skip.Select(p => p.Reason).Distinct());
    }

    [Fact]
    public void Favorites_stay_unless_switched_off()
    {
        _episode.Favorite = true;
        Assert.Empty(Plan([_episode.Id]).Remove);
        Assert.Single(Plan([_episode.Id], protectFavorites: false).Remove);
    }

    [Fact]
    public void Something_being_played_stays()
    {
        // The session's playing set holds the episode, its season and its series.
        var plan = Plan([_series.Id, _movie.Id], playing: [_episode.Id, _season.Id, _series.Id]);
        Assert.Equal([_movie.Id], plan.Remove.Select(p => p.Item.Id));
        Assert.Equal("It is being played right now.", plan.Skip.Single().Reason);
    }

    [Fact]
    public void Episodes_go_with_their_selected_series()
    {
        var plan = Plan([_episode.Id, _series.Id, _season.Id]);
        Assert.Equal([_series.Id], plan.Remove.Select(p => p.Item.Id));
    }

    [Fact]
    public void Unknown_ids_are_skipped()
    {
        var plan = Plan([Guid.NewGuid()]);
        Assert.Empty(plan.Remove);
        Assert.Equal("No longer in the library.", plan.Skip.Single().Reason);
    }
}
