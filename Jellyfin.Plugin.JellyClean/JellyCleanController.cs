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

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyCleanController"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="remover">Remover.</param>
    /// <param name="arr">Radarr and Sonarr client.</param>
    /// <param name="leavingSoon">Leaving soon list.</param>
    /// <param name="userManager">User manager.</param>
    public JellyCleanController(Catalog catalog, Remover remover, ArrClient arr, LeavingSoon leavingSoon, IUserManager userManager)
    {
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
            Ids = allIds ? matches.Select(i => new { i.Id, i.Size }) : null,
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
    public async Task<ActionResult> Remove([FromBody] Selection request)
    {
        var userId = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

        // Not tied to the request: closing the page must never stop a removal halfway through an item.
        return Ok(Describe(await _remover.RemoveAsync(request.Ids, userId, CancellationToken.None).ConfigureAwait(false)));
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
    /// Runs the daily check now: saved views marked "check daily" add their matches to Leaving soon.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("LeavingSoon/Run")]
    public async Task<ActionResult> RunLeavingSoon(CancellationToken cancellationToken)
    {
        await _leavingSoon.RunAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Saves a filter under a name, replacing one with the same name.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns>No content.</returns>
    [HttpPost("Views")]
    public ActionResult SaveView([FromBody] SavedView view)
    {
        if (string.IsNullOrWhiteSpace(view.Name) || LeavingSoon.FilterOf(view) is null)
        {
            return BadRequest("A view needs a name and a filter.");
        }

        var plugin = Plugin.Instance!;
        view.Name = view.Name.Trim();
        var old = plugin.Configuration.Views.FirstOrDefault(v => v.Name == view.Name);
        view.CheckDaily = old?.CheckDaily ?? false;
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
    public ActionResult Keep([FromRoute] Guid itemId, [FromQuery] bool keep = true)
    {
        var plugin = Plugin.Instance!;
        var kept = plugin.Configuration.KeptIds.Where(i => i != itemId);
        plugin.Configuration.KeptIds = (keep ? kept.Append(itemId) : kept).ToArray();
        plugin.SaveConfiguration();
        return NoContent();
    }

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            return Ok(new { Ok = false, Message = ex.Message });
        }
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
