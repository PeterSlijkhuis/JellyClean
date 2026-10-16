using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyClean;

/// <summary>
/// Endpoints for the JellyClean pages. Admin only.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyClean")]
public class JellyCleanController : ControllerBase
{
    private readonly Catalog _catalog;
    private readonly Remover _remover;
    private readonly ArrClient _arr;
    private readonly LeavingSoon _leavingSoon;
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _http;
    private readonly RecycleBin _bin;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyCleanController"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="remover">Remover.</param>
    /// <param name="arr">Radarr and Sonarr client.</param>
    /// <param name="leavingSoon">Leaving soon list.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="bin">Recycle bin.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="http">HTTP client factory, for the digest.</param>
    public JellyCleanController(Catalog catalog, Remover remover, ArrClient arr, LeavingSoon leavingSoon, IUserManager userManager, RecycleBin bin, ILibraryManager libraryManager, IHttpClientFactory http)
    {
        _http = http;
        _bin = bin;
        _libraryManager = libraryManager;
        _catalog = catalog;
        _remover = remover;
        _arr = arr;
        _leavingSoon = leavingSoon;
        _userManager = userManager;
    }

    /// <summary>
    /// What there is to filter on, and totals for the header.
    /// </summary>
    /// <param name="refresh">Read the library again first.</param>
    /// <returns>Libraries, genres, users and totals.</returns>
    [HttpGet("Overview")]
    public ActionResult Overview([FromQuery] bool refresh = false)
    {
        var items = _catalog.Get(refresh).Values;

        // Movies and episodes hold the files; series and seasons only add them up.
        var files = items.Where(i => i.Type is "Movie" or "Episode").ToList();
        return Ok(new
        {
            Libraries = items.Select(i => i.Library).Where(l => l.Length > 0).Distinct().Order(),
            Genres = items.SelectMany(i => i.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
            Codecs = files.Select(i => i.Codec).Where(c => c.Length > 0).Distinct().Order(),
            Copies = items.Count(i => i.CopiesIn.Length > 0 && i.Type == "Movie"),
            Users = _userManager.GetUsers().Select(u => new { u.Id, Name = u.Username }).OrderBy(u => u.Name),
            Movies = items.Count(i => i.Type == "Movie"),
            Series = items.Count(i => i.Type == "Series"),
            Episodes = items.Count(i => i.Type == "Episode"),
            Size = files.Sum(i => i.Size),
            NeverWatchedSize = files.Where(i => !i.AnyPlayed).Sum(i => i.Size),
            LibrarySizes = files.GroupBy(i => i.Library)
                .Select(g => new { Name = g.Key, Size = g.Sum(i => i.Size), NeverWatchedSize = g.Where(i => !i.AnyPlayed).Sum(i => i.Size) })
                .OrderByDescending(l => l.Size),
            Breakdown = Breakdown.Build(items, Plugin.Instance!.Configuration.KeptIds.ToHashSet(), _userManager.GetUsers().Count(), DateTime.UtcNow),
            Plugin.Instance.Configuration.FreedBytes,
            Plugin.Instance.Configuration.RemovedCount,
        });
    }

    /// <summary>
    /// The items that match a filter, one page at a time.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="allIds">Also return the id and size of every match, for "Select all".</param>
    /// <returns>The page, the count and size of all matches.</returns>
    [HttpPost("Items")]
    public ActionResult Items([FromBody] Filter filter, [FromQuery] bool allIds = false)
    {
        var matches = _catalog.Find(filter);
        return Ok(new
        {
            Total = matches.Count,
            TotalSize = matches.Sum(i => i.Size),
            Items = matches.Skip(Math.Max(0, filter.Offset)).Take(Math.Clamp(filter.Limit, 1, 500)),
            Ids = allIds ? matches.Select(i => new { i.Id, i.Size, i.SeriesId, i.SeasonId, Protected = i.Kept || i.Favorite || i.InProgress || i.NextUp }) : null,
        });
    }

    /// <summary>
    /// Every item that matches a filter, as a spreadsheet.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <returns>A CSV file.</returns>
    [HttpPost("Export")]
    public ActionResult Export([FromBody] Filter filter)
    {
        var users = _userManager.GetUsers().ToDictionary(u => u.Id, u => u.Username);
        var csv = new StringBuilder("Title,Type,Library,Year,Genres,Added,Watched by,Last played,Size (GB),Resolution,Codec,HDR,Also in,Keep,Favorite,Path\n");
        foreach (var i in _catalog.Find(filter))
        {
            string[] cells =
            [
                i.Title, i.Type, i.Library, i.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, string.Join("; ", i.Genres),
                i.Added.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                string.Join("; ", i.WatchedBy.Select(u => users.GetValueOrDefault(u, "?"))),
                i.LastWatched?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                (i.Size / 1e9).ToString("0.00", CultureInfo.InvariantCulture), i.Resolution, i.Codec, i.Hdr, string.Join("; ", i.CopiesIn),
                i.Kept ? "yes" : string.Empty, i.Favorite ? "yes" : string.Empty, i.Path,
            ];
            csv.AppendJoin(',', cells.Select(Cell)).Append('\n');
        }

        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", $"jellyclean-{DateTime.Now:yyyy-MM-dd}.csv");
    }

    /// <summary>
    /// What removing the items would do. Changes nothing.
    /// </summary>
    /// <param name="request">The selected ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan.</returns>
    [HttpPost("Preview")]
    public async Task<ActionResult> Preview([FromBody] Selection request, CancellationToken cancellationToken)
        => Ok(Describe(await _remover.PlanAsync(request.Ids, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Removes the items from Radarr and Sonarr, then deletes them from Jellyfin and the disk.
    /// </summary>
    /// <param name="request">The selected ids.</param>
    /// <returns>What was removed and what was kept.</returns>
    [HttpPost("Remove")]
    public ActionResult Remove([FromBody] Selection request)
    {
        var userId = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;
        return _remover.Start(request.Ids, userId) ? NoContent() : Conflict("Another removal is still running.");
    }

    /// <summary>
    /// How the removal started with Remove is going, and its result once done.
    /// </summary>
    /// <returns>The progress.</returns>
    [HttpGet("Remove")]
    public ActionResult RemoveStatus()
    {
        var p = _remover.Progress;
        return Ok(new { p.Running, p.Done, p.Total, p.Current, p.Error, Result = p.Result is null ? null : Describe(p.Result) });
    }

    /// <summary>
    /// The Leaving soon list.
    /// </summary>
    /// <returns>The items, due first, with a reason when a safeguard now keeps one.</returns>
    [HttpGet("LeavingSoon")]
    public ActionResult GetLeavingSoon() => Ok(_leavingSoon.List());

    /// <summary>
    /// Puts items on the Leaving soon list. Nothing is removed.
    /// </summary>
    /// <param name="request">The selected ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was added and what was left off.</returns>
    [HttpPost("LeavingSoon")]
    public async Task<ActionResult> AddLeavingSoon([FromBody] Selection request, CancellationToken cancellationToken)
        => Ok(Describe(await _leavingSoon.AddAsync(request.Ids, "you", cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Takes items off the Leaving soon list.
    /// </summary>
    /// <param name="request">The ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("LeavingSoon/Cancel")]
    public async Task<ActionResult> CancelLeavingSoon([FromBody] Selection request, CancellationToken cancellationToken)
    {
        await _leavingSoon.CancelAsync(request.Ids, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Runs scheduled views now, whatever their time: all of them, or the one named. Their matches go on the Leaving soon list.
    /// </summary>
    /// <param name="view">One view to run, by name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="dry">Only show what it would add, without changing anything.</param>
    /// <returns>What each view added, or would add.</returns>
    [HttpPost("LeavingSoon/Run")]
    public async Task<ActionResult> RunLeavingSoon([FromQuery] string? view, CancellationToken cancellationToken, [FromQuery] bool dry = false)
    {
        var views = view is null
            ? Plugin.Instance!.Configuration.Views.Where(v => v.Runs().Length > 0).Select(v => v.Name).ToList()
            : [view];
        return Ok(await _leavingSoon.RunAsync(cancellationToken, views, dry).ConfigureAwait(false));
    }

    /// <summary>
    /// Movies and series that are in Jellyfin more than once, with the copy to keep picked out.
    /// </summary>
    /// <param name="prefer">"best" keeps the best quality, "smallest" the smallest file.</param>
    /// <returns>One set per movie or series.</returns>
    [HttpGet("Copies")]
    public ActionResult GetCopies([FromQuery] string prefer = "best")
        => Ok(Copies.Resolve(_catalog.Find(new Filter { Limit = int.MaxValue }), prefer));

    /// <summary>
    /// The numbers for the Stats tab.
    /// </summary>
    /// <returns>Space over time, the biggest unplayed items and a year per user.</returns>
    [HttpGet("Stats")]
    public ActionResult GetStats()
        => Ok(Stats.Build(_catalog.Get().Values.ToList(), _userManager.GetUsers().Select(u => (u.Id, u.Username)), Plugin.Instance!.Configuration.FreedByMonth, DateTime.UtcNow));

    /// <summary>
    /// Sends the digest now, to the address given or the saved one.
    /// </summary>
    /// <param name="url">The webhook address; empty uses the saved one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it worked, and the text.</returns>
    [HttpPost("Digest/Test")]
    public async Task<ActionResult> TestDigest([FromQuery] string? url, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var text = Digest.Text(config, _catalog.Get(), DateTime.UtcNow);
        var to = string.IsNullOrWhiteSpace(url) ? config.DigestUrl : url;
        if (string.IsNullOrWhiteSpace(to))
        {
            return Ok(new { Ok = false, Message = "Fill in a webhook address first.", Text = text });
        }

        try
        {
            await Digest.SendAsync(_http.CreateClient(), to.Trim(), text, cancellationToken).ConfigureAwait(false);
            return Ok(new { Ok = true, Message = "Sent.", Text = text });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return Ok(new { Ok = false, Message = "Could not send it: " + ex.Message, Text = text });
        }
    }

    /// <summary>
    /// What is in the recycle bin, newest first.
    /// </summary>
    /// <returns>The items, with the days left before they are deleted for good.</returns>
    [HttpGet("RecycleBin")]
    public ActionResult GetRecycleBin()
    {
        var config = Plugin.Instance!.Configuration;
        return Ok(config.Recycled.OrderByDescending(r => r.Removed).Select(r => new
        {
            r.Id,
            r.Title,
            r.Type,
            r.Size,
            r.Removed,
            r.Source,
            r.Batch,
            DeleteAt = RecycleBin.DeleteAt(r),
            Arr = r.Arr.Select(a => a.Kind).Distinct(),
        }));
    }

    /// <summary>
    /// Puts an item from the recycle bin back, and scans the library so Jellyfin shows it again.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Why it could not (Error), or what did not work in Radarr or Sonarr (Warning).</returns>
    [HttpPost("RecycleBin/Restore/{itemId}")]
    public async Task<ActionResult> RestoreRecycled([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var (error, warning) = await _bin.RestoreAsync(itemId, cancellationToken).ConfigureAwait(false);
        return Ok(new { Error = error, Warning = warning });
    }

    /// <summary>
    /// Undoes the last removal: everything of it that is still in the recycle bin comes back.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many came back, and what went wrong.</returns>
    [HttpPost("RecycleBin/UndoLast")]
    public async Task<ActionResult> UndoLastRemoval(CancellationToken cancellationToken)
    {
        var (restored, problems) = await _bin.UndoLastAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { Restored = restored, Problems = problems });
    }

    /// <summary>
    /// Deletes everything in the recycle bin for good, removing held movies and series from Radarr and Sonarr.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were deleted.</returns>
    [HttpPost("RecycleBin/Empty")]
    public async Task<ActionResult> EmptyRecycleBin(CancellationToken cancellationToken)
        => Ok(new { Count = await _bin.PurgeAsync(all: true, cancellationToken).ConfigureAwait(false) });

    /// <summary>
    /// Saves a filter under a name, replacing one with the same name.
    /// </summary>
    /// <param name="view">The view, with its schedule.</param>
    /// <param name="keepSchedule">Keep the schedule of the view saved under this name, and only change the filter.</param>
    /// <returns>No content.</returns>
    [HttpPost("Views")]
    public ActionResult SaveView([FromBody] SavedView view, [FromQuery] bool keepSchedule = false)
    {
        if (string.IsNullOrWhiteSpace(view.Name) || LeavingSoon.FilterOf(view) is null)
        {
            return BadRequest("A view needs a name and a filter.");
        }

        var plugin = Plugin.Instance!;
        view.Name = view.Name.Trim();
        var old = plugin.Configuration.Views.FirstOrDefault(v => v.Name == view.Name);
        view.CheckDaily = false;
        if (old is not null && keepSchedule)
        {
            (view.CheckDaily, view.Schedule, view.Day, view.Hour, view.NoticeDays, view.AutoRemove) = (old.CheckDaily, old.Schedule, old.Day, old.Hour, old.NoticeDays, old.AutoRemove);
        }

        if (view.Schedule is not ("" or "daily" or "weekly") || view.Hour is < 0 or > 23 || view.NoticeDays < 0)
        {
            return BadRequest("The schedule is not valid.");
        }

        (view.LastRun, view.LastAdded) = (old?.LastRun, old?.LastAdded ?? 0);
        plugin.Configuration.Views = plugin.Configuration.Views.Where(v => v.Name != view.Name).Append(view).ToArray();
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Deletes a saved view.
    /// </summary>
    /// <param name="name">Its name.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Views")]
    public ActionResult DeleteView([FromQuery] string name)
    {
        var plugin = Plugin.Instance!;
        plugin.Configuration.Views = plugin.Configuration.Views.Where(v => v.Name != name).ToArray();
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Marks an item keep, or takes the mark off.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="keep">Keep it.</param>
    /// <returns>No content.</returns>
    [HttpPost("Keep/{itemId}")]
    public async Task<ActionResult> Keep([FromRoute] Guid itemId, [FromQuery] bool keep = true)
    {
        var plugin = Plugin.Instance!;
        var kept = plugin.Configuration.KeptIds.Where(i => i != itemId);
        plugin.Configuration.KeptIds = (keep ? kept.Append(itemId) : kept).ToArray();
        plugin.SaveConfiguration();
        if (keep)
        {
            await _leavingSoon.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Takes one user's favorite mark off an item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="userId">The user whose favorite it is.</param>
    /// <returns>No content, or not found.</returns>
    [HttpPost("Unfavorite/{itemId}")]
    public ActionResult Unfavorite([FromRoute] Guid itemId, [FromQuery] Guid userId)
        => _catalog.Unfavorite(itemId, userId) ? NoContent() : NotFound();

    /// <summary>
    /// Checks a Radarr or Sonarr address and API key.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it worked, and the version or the error.</returns>
    [HttpPost("TestServer")]
    public async Task<ActionResult> TestServer([FromBody] ArrServer server, CancellationToken cancellationToken)
    {
        try
        {
            var version = await _arr.TestAsync(server, cancellationToken).ConfigureAwait(false);
            var expected = server.IsSonarr ? "Sonarr" : "Radarr";
            return Ok(version.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                ? new { Ok = true, Message = "Connected to " + version }
                : new { Ok = false, Message = $"This is {version}, not {expected}." });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The Jellyfin server makes this call, not the browser, so the address has to work from the server.
            return Ok(new { Ok = false, Message = $"No answer from {server.Url} within 30 seconds. JellyClean connects from the Jellyfin server, not from your browser, so use an address that works on the server itself, like its local IP and port, or the container name if both run in Docker." });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            return Ok(new { Ok = false, Message = ex.Message });
        }
    }

    /// <summary>
    /// A number that goes up when items are added or removed. Asking also starts a new read in the background
    /// when Jellyfin reported a change, so the page can poll this and reload when the number moves on.
    /// </summary>
    /// <returns>The version.</returns>
    [HttpGet("Status")]
    public ActionResult Status()
    {
        _catalog.Get();
        return Ok(new { _catalog.Version });
    }

    /// <summary>
    /// What JellyClean did recently, newest first.
    /// </summary>
    /// <returns>Tab separated lines: time, kind, message.</returns>
    [HttpGet("Activity")]
    public ActionResult<IReadOnlyList<string>> GetActivity() => Ok(Activity.Read());

    /// <summary>
    /// Empties the activity list.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Activity")]
    public ActionResult ClearActivity()
    {
        Activity.Clear();
        return NoContent();
    }

    // Quotes a CSV cell, and keeps spreadsheet apps from running a title that starts with = as a formula.
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static object Describe(RemovalPlan plan) => new
    {
        plan.Remove,
        plan.Skip,
        plan.TotalSize,
        Size = Remover.Bytes(plan.TotalSize),
    };

    /// <summary>
    /// Selected item ids.
    /// </summary>
    public class Selection
    {
        /// <summary>
        /// Gets or sets the ids.
        /// </summary>
        public Guid[] Ids { get; set; } = [];
    }
}
