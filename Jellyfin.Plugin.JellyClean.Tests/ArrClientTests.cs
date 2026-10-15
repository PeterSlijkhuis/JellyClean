using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;
using Jellyfin.Plugin.JellyClean.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyClean.Tests;

public class ArrClientTests
{
    private readonly ArrServer _radarr = new() { Kind = "Radarr", Url = "http://radarr:7878/", ApiKey = "r-key" };
    private readonly ArrServer _sonarr = new() { Kind = "Sonarr", Url = "http://sonarr:8989", ApiKey = "s-key", AddExclusion = false };
    private readonly List<(HttpMethod Method, string Url, string? Body, string? Key)> _calls = [];
    private readonly Dictionary<string, string> _answers = new()
    {
        ["http://radarr:7878/api/v3/movie"] = """[{"id":7,"title":"Alien","tmdbId":348,"imdbId":"tt0078748"},{"id":8,"tmdbId":1}]""",
        ["http://sonarr:8989/api/v3/series"] = """[{"id":3,"title":"Dark","tvdbId":334824}]""",
        ["http://sonarr:8989/api/v3/episode?seriesId=3"] = """[{"id":30,"seasonNumber":1,"episodeNumber":1},{"id":31,"seasonNumber":1,"episodeNumber":2},{"id":32,"seasonNumber":1,"episodeNumber":3},{"id":40,"seasonNumber":2,"episodeNumber":1}]""",
    };

    private ArrClient Client()
    {
        var handler = new Handler(async request =>
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            _calls.Add((request.Method, url, body, request.Headers.GetValues("X-Api-Key").Single()));
            if (url.StartsWith("http://down", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Connection refused");
            }

            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(_answers.ContainsKey(url) ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new StringContent(_answers.GetValueOrDefault(url, "{}"), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new ArrClient(factory.Object);
    }

    private static PlannedItem Planned(string type, Guid? seriesId = null, int? season = null, int? episode = null, int? episodeEnd = null)
        => new(new CatalogItem { Id = Guid.NewGuid(), Type = type, SeriesId = seriesId, Season = season, Episode = episode, EpisodeEnd = episodeEnd }, null);

    [Fact]
    public async Task Movie_found_by_tmdb_id_is_removed_without_its_files_and_excluded()
    {
        var movie = Planned("Movie");
        var client = Client();
        await client.ResolveAsync([movie], [_radarr, _sonarr], _ => new Dictionary<string, string> { ["Tmdb"] = "348" }, CancellationToken.None);

        var step = Assert.Single(movie.Arr);
        Assert.Equal(7, step.ArrId);
        Assert.False(step.Failed);

        await client.ApplyAsync(step, CancellationToken.None);
        var delete = _calls.Last();
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal("http://radarr:7878/api/v3/movie/7?deleteFiles=false&addImportExclusion=true", delete.Url);
        Assert.Equal("r-key", delete.Key);

        // Sonarr is not asked about movies.
        Assert.DoesNotContain(_calls, c => c.Url.Contains("sonarr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Movie_found_by_imdb_id_when_tmdb_is_missing()
    {
        var movie = Planned("Movie");
        await Client().ResolveAsync([movie], [_radarr], _ => new Dictionary<string, string> { ["Imdb"] = "TT0078748" }, CancellationToken.None);
        Assert.Equal(7, Assert.Single(movie.Arr).ArrId);
    }

    [Fact]
    public async Task Item_not_in_radarr_gets_no_step()
    {
        var movie = Planned("Movie");
        await Client().ResolveAsync([movie], [_radarr], _ => new Dictionary<string, string> { ["Tmdb"] = "999" }, CancellationToken.None);
        Assert.Empty(movie.Arr);
    }

    [Fact]
    public async Task Whole_series_is_removed_from_sonarr()
    {
        var series = Planned("Series");
        var client = Client();
        await client.ResolveAsync([series], [_sonarr], _ => new Dictionary<string, string> { ["Tvdb"] = "334824" }, CancellationToken.None);

        await client.ApplyAsync(Assert.Single(series.Arr), CancellationToken.None);
        Assert.Equal("http://sonarr:8989/api/v3/series/3?deleteFiles=false&addImportListExclusion=false", _calls.Last().Url);
    }

    [Fact]
    public async Task Seasons_and_episodes_are_unmonitored_and_the_series_stays()
    {
        var seriesId = Guid.NewGuid();
        var season = Planned("Season", seriesId, season: 1);
        var double_ = Planned("Episode", seriesId, season: 1, episode: 2, episodeEnd: 3);
        var client = Client();
        await client.ResolveAsync([season, double_], [_sonarr], id => id == seriesId ? new Dictionary<string, string> { ["Tvdb"] = "334824" } : [], CancellationToken.None);

        Assert.Equal([30, 31, 32], Assert.Single(season.Arr).EpisodeIds);
        Assert.Equal([31, 32], Assert.Single(double_.Arr).EpisodeIds);

        // The episode list is read once per series.
        Assert.Single(_calls, c => c.Url.Contains("episode?seriesId=3", StringComparison.Ordinal));

        await client.ApplyAsync(double_.Arr[0], CancellationToken.None);
        var put = _calls.Last();
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("http://sonarr:8989/api/v3/episode/monitor", put.Url);
        Assert.Equal("""{"episodeIds":[31,32],"monitored":false}""", put.Body);
    }

    [Fact]
    public async Task Unreachable_server_marks_items_failed_so_their_files_stay()
    {
        var movie = Planned("Movie");
        var down = new ArrServer { Kind = "Radarr", Name = "Radarr 4K", Url = "http://down:7878", ApiKey = "x" };
        await Client().ResolveAsync([movie], [_radarr, down], _ => new Dictionary<string, string> { ["Tmdb"] = "348" }, CancellationToken.None);

        Assert.Equal(2, movie.Arr.Count);
        var failed = Assert.Single(movie.Arr, s => s.Failed);
        Assert.StartsWith("Could not reach Radarr 4K", failed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_apply_throws()
    {
        var step = new ArrStep(new ArrServer { Kind = "Radarr", Url = "http://down:7878", ApiKey = "x" }, "Remove", 7, []);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client().ApplyAsync(step, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }
}
