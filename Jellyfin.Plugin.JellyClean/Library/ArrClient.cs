using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// Removes items from Radarr and Sonarr, so they are not downloaded again after the files are deleted.
/// </summary>
public class ArrClient
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public ArrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Checks the address and API key.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The app name and version, like "Radarr 5.26.2".</returns>
    public async Task<string> TestAsync(ArrServer server, CancellationToken cancellationToken)
    {
        var status = await GetAsync<ArrEntry>(server, "system/status", cancellationToken).ConfigureAwait(false);
        return $"{status.AppName} {status.Version}".Trim();
    }

    /// <summary>
    /// Works out what to do in each Radarr and Sonarr for the items in a plan, and adds it to each item.
    /// Only reads from Radarr and Sonarr. An item a server can't be asked about gets a step with an error, so it is skipped.
    /// </summary>
    /// <param name="items">Items that will be removed.</param>
    /// <param name="servers">Radarr and Sonarr servers.</param>
    /// <param name="providerIds">TMDb, IMDb and TVDb ids of a movie or series by Jellyfin id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ResolveAsync(IReadOnlyList<PlannedItem> items, IEnumerable<ArrServer> servers, Func<Guid, IReadOnlyDictionary<string, string>> providerIds, CancellationToken cancellationToken)
    {
        foreach (var server in servers.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            var relevant = items.Where(p => server.IsSonarr ? p.Item.Type != "Movie" : p.Item.Type == "Movie").ToList();
            if (relevant.Count == 0)
            {
                continue;
            }

            try
            {
                var library = await GetAsync<List<ArrEntry>>(server, server.IsSonarr ? "series" : "movie", cancellationToken).ConfigureAwait(false);
                var episodes = new Dictionary<int, List<ArrEntry>>();

                // What is downloading right now: removing it would cancel a download someone is waiting for.
                var queue = (await GetAsync<ArrQueue>(server, "queue?pageSize=1000", cancellationToken).ConfigureAwait(false)).Records;

                foreach (var planned in relevant)
                {
                    var item = planned.Item;
                    var ids = providerIds(item.SeriesId ?? item.Id);
                    var match = library.FirstOrDefault(e => e.Matches(ids));
                    if (match is null)
                    {
                        continue;
                    }

                    if (item.Type is "Movie" or "Series" && queue.Any(q => (server.IsSonarr ? q.SeriesId : q.MovieId) == match.Id))
                    {
                        planned.Arr.Add(new ArrStep(server, $"It is downloading in {server.Label} right now.", match.Id, []) { Failed = true });
                        continue;
                    }

                    if (item.Type is "Movie" or "Series")
                    {
                        var what = item.Type == "Movie" ? "movie" : "series";
                        planned.Arr.Add(new ArrStep(server, $"Remove the {what} from {server.Label}", match.Id, []));
                        continue;
                    }

                    if (!episodes.TryGetValue(match.Id, out var list))
                    {
                        list = await GetAsync<List<ArrEntry>>(server, "episode?seriesId=" + match.Id, cancellationToken).ConfigureAwait(false);
                        episodes[match.Id] = list;
                    }

                    var first = item.Episode ?? int.MinValue;
                    var last = item.EpisodeEnd ?? item.Episode ?? int.MaxValue;
                    var episodeIds = list
                        .Where(e => e.SeasonNumber == item.Season && (item.Type == "Season" || (e.EpisodeNumber >= first && e.EpisodeNumber <= last)))
                        .Select(e => e.Id)
                        .ToArray();
                    if (queue.Any(q => episodeIds.Contains(q.EpisodeId)))
                    {
                        planned.Arr.Add(new ArrStep(server, $"An episode in it is downloading in {server.Label} right now.", match.Id, []) { Failed = true });
                    }
                    else if (episodeIds.Length > 0)
                    {
                        planned.Arr.Add(new ArrStep(server, $"Stop monitoring {episodeIds.Length} {(episodeIds.Length == 1 ? "episode" : "episodes")} in {server.Label}", match.Id, episodeIds));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
            {
                foreach (var planned in relevant)
                {
                    planned.Arr.Add(new ArrStep(server, $"Could not reach {server.Label}: {ex.Message}", 0, []) { Failed = true });
                }
            }
        }
    }

    /// <summary>
    /// Does one step in Radarr or Sonarr. Throws when it fails, so the files stay.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What is needed to undo it.</returns>
    public async Task<ArrUndo> ApplyAsync(ArrStep step, CancellationToken cancellationToken)
    {
        var server = step.Server;
        var exclusion = server.AddExclusion ? "true" : "false";
        var undo = new ArrUndo { Kind = server.Kind, Url = server.Url, EpisodeIds = step.EpisodeIds, Excluded = server.AddExclusion };
        if (step.EpisodeIds.Length == 0)
        {
            // Keep the movie or series as it was, with its quality profile, folder and monitored seasons, to add it back.
            using var get = new HttpRequestMessage(HttpMethod.Get, Url(server, (server.IsSonarr ? "series/" : "movie/") + step.ArrId));
            using var got = await SendAsync(server, get, cancellationToken).ConfigureAwait(false);
            undo.Json = await got.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        using var request = step.EpisodeIds.Length > 0
            ? new HttpRequestMessage(HttpMethod.Put, Url(server, "episode/monitor")) { Content = JsonContent.Create(new { episodeIds = step.EpisodeIds, monitored = false }) }
            : server.IsSonarr
                ? new HttpRequestMessage(HttpMethod.Delete, Url(server, $"series/{step.ArrId}?deleteFiles=false&addImportListExclusion={exclusion}"))
                : new HttpRequestMessage(HttpMethod.Delete, Url(server, $"movie/{step.ArrId}?deleteFiles=false&addImportExclusion={exclusion}"));
        using var response = await SendAsync(server, request, cancellationToken).ConfigureAwait(false);
        return undo;
    }

    /// <summary>
    /// The recycle bin's first half of a step: the movie, series or episodes stop being monitored, so nothing is
    /// downloaded again, but stay in Radarr or Sonarr until the recycle bin is emptied.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What is needed to finish or undo it.</returns>
    public async Task<ArrUndo> HoldAsync(ArrStep step, CancellationToken cancellationToken)
    {
        var server = step.Server;
        await MonitorAsync(server, step.ArrId, step.EpisodeIds, false, cancellationToken).ConfigureAwait(false);
        return new ArrUndo { Kind = server.Kind, Url = server.Url, ArrId = step.ArrId, EpisodeIds = step.EpisodeIds, Excluded = server.AddExclusion, Held = true };
    }

    /// <summary>
    /// The second half, when the recycle bin is emptied: a held movie or series is removed from Radarr or Sonarr.
    /// Held episodes need nothing more: they stay unmonitored.
    /// </summary>
    /// <param name="undo">What was held.</param>
    /// <param name="servers">The servers set up now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task FinishAsync(ArrUndo undo, IEnumerable<ArrServer> servers, CancellationToken cancellationToken)
    {
        if (!undo.Held || undo.EpisodeIds.Length > 0)
        {
            return;
        }

        // A server that was taken out of the settings has nothing left to remove.
        if (!servers.Any(s => Same(s, undo)))
        {
            return;
        }

        var server = Find(undo, servers);
        var exclusion = undo.Excluded ? "true" : "false";
        using var request = server.IsSonarr
            ? new HttpRequestMessage(HttpMethod.Delete, Url(server, $"series/{undo.ArrId}?deleteFiles=false&addImportListExclusion={exclusion}"))
            : new HttpRequestMessage(HttpMethod.Delete, Url(server, $"movie/{undo.ArrId}?deleteFiles=false&addImportExclusion={exclusion}"));
        try
        {
            using var response = await SendAsync(server, request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already removed there by someone: done.
        }
    }

    /// <summary>
    /// Undoes a step: monitors the episodes again, or takes the movie or series off the exclusion list and adds it back
    /// as it was, without searching. Radarr or Sonarr then finds the files that are back in its folder.
    /// </summary>
    /// <param name="undo">What was done.</param>
    /// <param name="servers">The servers set up now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task UndoAsync(ArrUndo undo, IEnumerable<ArrServer> servers, CancellationToken cancellationToken)
    {
        var server = Find(undo, servers);
        if (undo.Held || undo.EpisodeIds.Length > 0)
        {
            await MonitorAsync(server, undo.ArrId, undo.EpisodeIds, true, cancellationToken).ConfigureAwait(false);

            // Look at the folder now, so the files that are back count as present right away.
            object command = server.IsSonarr ? new { name = "RescanSeries", seriesId = undo.ArrId } : new { name = "RefreshMovie", movieIds = new[] { undo.ArrId } };
            using var rescan = new HttpRequestMessage(HttpMethod.Post, Url(server, "command")) { Content = JsonContent.Create(command) };
            using var rescanned = await SendAsync(server, rescan, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (JsonNode.Parse(undo.Json ?? "null") is not JsonObject entry)
        {
            return;
        }

        var key = server.IsSonarr ? "tvdbId" : "tmdbId";
        var id = entry[key]?.GetValue<int>() ?? 0;
        if (undo.Excluded && id > 0)
        {
            var path = server.IsSonarr ? "importlistexclusion" : "exclusions";
            var exclusions = await GetAsync<List<JsonObject>>(server, path, cancellationToken).ConfigureAwait(false);
            foreach (var ex in exclusions.Where(e => e[key]?.GetValue<int>() == id))
            {
                using var delete = new HttpRequestMessage(HttpMethod.Delete, Url(server, $"{path}/{ex["id"]}"));
                using var deleted = await SendAsync(server, delete, cancellationToken).ConfigureAwait(false);
            }
        }

        entry.Remove("id");
        entry["addOptions"] = server.IsSonarr
            ? new JsonObject { ["searchForMissingEpisodes"] = false, ["searchForCutoffUnmetEpisodes"] = false }
            : new JsonObject { ["searchForMovie"] = false };
        using var add = new HttpRequestMessage(HttpMethod.Post, Url(server, server.IsSonarr ? "series" : "movie"))
        {
            Content = new StringContent(entry.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        using var added = await SendAsync(server, add, cancellationToken).ConfigureAwait(false);
    }

    private static bool Same(ArrServer server, ArrUndo undo)
        => server.Kind == undo.Kind && server.Url.Trim().TrimEnd('/') == undo.Url.Trim().TrimEnd('/');

    private static ArrServer Find(ArrUndo undo, IEnumerable<ArrServer> servers)
        => servers.FirstOrDefault(s => Same(s, undo))
            ?? throw new HttpRequestException($"{undo.Kind} at {undo.Url} is no longer set up");

    private async Task MonitorAsync(ArrServer server, int arrId, int[] episodeIds, bool monitored, CancellationToken cancellationToken)
    {
        object body = episodeIds.Length > 0 ? new { episodeIds, monitored }
            : server.IsSonarr ? new { seriesIds = new[] { arrId }, monitored }
            : new { movieIds = new[] { arrId }, monitored };
        var path = episodeIds.Length > 0 ? "episode/monitor" : server.IsSonarr ? "series/editor" : "movie/editor";
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(server, path)) { Content = JsonContent.Create(body) };
        using var response = await SendAsync(server, request, cancellationToken).ConfigureAwait(false);
    }

    private static Uri Url(ArrServer server, string path) => new(server.Url.Trim().TrimEnd('/') + "/api/v3/" + path);

    private async Task<T> GetAsync<T>(ArrServer server, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(server, path));
        using var response = await SendAsync(server, request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(_json, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Empty answer");
    }

    private async Task<HttpResponseMessage> SendAsync(ArrServer server, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("X-Api-Key", server.ApiKey.Trim());
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }

        return response;
    }

    private sealed class ArrQueue
    {
        public List<ArrQueueRecord> Records { get; set; } = [];
    }

    private sealed class ArrQueueRecord
    {
        public int MovieId { get; set; }

        public int SeriesId { get; set; }

        public int EpisodeId { get; set; }
    }

    private sealed class ArrEntry
    {
        public int Id { get; set; }

        public int TmdbId { get; set; }

        public int TvdbId { get; set; }

        public string? ImdbId { get; set; }

        public int SeasonNumber { get; set; }

        public int EpisodeNumber { get; set; }

        public string? AppName { get; set; }

        public string? Version { get; set; }

        public bool Matches(IReadOnlyDictionary<string, string> ids)
            => (TvdbId > 0 && ids.GetValueOrDefault("Tvdb") == TvdbId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                || (TmdbId > 0 && ids.GetValueOrDefault("Tmdb") == TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                || (!string.IsNullOrEmpty(ImdbId) && string.Equals(ids.GetValueOrDefault("Imdb"), ImdbId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// One thing to do in Radarr or Sonarr for an item.
/// </summary>
/// <param name="Server">The server.</param>
/// <param name="Description">What happens, for the preview and the log.</param>
/// <param name="ArrId">The movie or series id in Radarr or Sonarr.</param>
/// <param name="EpisodeIds">Episodes to stop monitoring; empty to remove the whole movie or series.</param>
public record ArrStep([property: JsonIgnore] ArrServer Server, string Description, int ArrId, int[] EpisodeIds)
{
    /// <summary>Gets a value indicating whether the server could not be asked, so the item must stay.</summary>
    public bool Failed { get; init; }
}
