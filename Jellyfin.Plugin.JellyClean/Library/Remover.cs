using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// Previews and removes items: Radarr and Sonarr first, then Jellyfin deletes the files from disk.
/// </summary>
public class Remover
{
    private static readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly Catalog _catalog;
    private readonly ArrClient _arr;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IActivityManager _activityManager;
    private readonly ILogger<Remover> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Remover"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="arr">Radarr and Sonarr client.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="activityManager">Activity manager.</param>
    /// <param name="logger">Logger.</param>
    public Remover(Catalog catalog, ArrClient arr, ILibraryManager libraryManager, ISessionManager sessionManager, IActivityManager activityManager, ILogger<Remover> logger)
    {
        _catalog = catalog;
        _arr = arr;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _activityManager = activityManager;
        _logger = logger;
    }

    /// <summary>
    /// Formats a size like "186 GB".
    /// </summary>
    /// <param name="bytes">Bytes.</param>
    /// <returns>The text.</returns>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(value < 10 && unit > 0 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>
    /// Works out what removing the items would do, without changing anything.
    /// </summary>
    /// <param name="ids">Selected item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan.</returns>
    public async Task<RemovalPlan> PlanAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var plan = RemovalPlan.Build(ids, _catalog.Get(), Safeguards());
        foreach (var planned in plan.Remove)
        {
            planned.Paths = _libraryManager.GetItemById(planned.Item.Id)?.GetDeletePaths().Select(p => p.FullName).ToArray() ?? [];
        }

        await _arr.ResolveAsync(plan.Remove, config.Servers, ProviderIds, cancellationToken).ConfigureAwait(false);

        // An empty item on a drive that is offline only looks empty: leave it, or Radarr and Sonarr would drop it too.
        var offline = OfflineLibraries();
        foreach (var planned in plan.Remove.Where(p => p.Item.Size == 0 && offline.Contains(p.Item.Library)).ToList())
        {
            plan.Remove.Remove(planned);
            plan.Skip.Add(planned with { Reason = $"The {planned.Item.Library} folder can't be reached, so it may only look empty." });
        }

        // Radarr or Sonarr could not be asked: the files stay, or they would just be downloaded again.
        foreach (var planned in plan.Remove.Where(p => p.Arr.Any(s => s.Failed)).ToList())
        {
            plan.Remove.Remove(planned);
            plan.Skip.Add(planned with { Reason = planned.Arr.First(s => s.Failed).Description });
        }

        return plan;
    }

    // Libraries none of whose folders exist or hold anything, like an unmounted network share.
    private HashSet<string> OfflineLibraries()
        => _libraryManager.GetVirtualFolders()
            .Where(f => !f.Locations.Any(l => Directory.Exists(l) && Directory.EnumerateFileSystemEntries(l).Any()))
            .Select(f => f.Name)
            .ToHashSet();

    /// <summary>
    /// The safeguards as set in the settings, with what is being played right now.
    /// </summary>
    /// <returns>The safeguards.</returns>
    public Safeguards Safeguards()
    {
        var config = Plugin.Instance!.Configuration;
        var now = DateTime.UtcNow;
        return new Safeguards
        {
            Kept = config.KeptIds.ToHashSet(),
            Playing = _sessionManager.Sessions
                .Select(s => s.NowPlayingItem)
                .Where(i => i is not null)
                .SelectMany(i => new[] { i!.Id, i.SeriesId ?? Guid.Empty, i.SeasonId ?? Guid.Empty })
                .ToHashSet(),
            Favorites = config.ProtectFavorites,
            InProgress = config.ProtectInProgress,
            AddedAfter = config.ProtectAddedDays > 0 ? now.AddDays(-config.ProtectAddedDays) : null,
            PlayedAfter = config.ProtectPlayedDays > 0 ? now.AddDays(-config.ProtectPlayedDays) : null,
            Tag = config.KeepTag,
            Libraries = config.ProtectedLibraries.ToHashSet(),
            MaxItems = config.MaxItemsPerRemoval,
        };
    }

    /// <summary>
    /// Removes the items: from Radarr and Sonarr, then from Jellyfin and the disk.
    /// </summary>
    /// <param name="ids">Selected item ids.</param>
    /// <param name="userId">The admin who asked, for the Jellyfin activity log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan, with the items that failed moved to <see cref="RemovalPlan.Skip"/>.</returns>
    public async Task<RemovalPlan> RemoveAsync(IEnumerable<Guid> ids, Guid userId, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await PlanAsync(ids, cancellationToken).ConfigureAwait(false);
            foreach (var planned in plan.Remove.ToList())
            {
                var error = await RemoveOneAsync(planned, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    plan.Remove.Remove(planned);
                    plan.Skip.Add(planned with { Reason = error });
                }
            }

            foreach (var skipped in plan.Skip)
            {
                Activity.Log(_logger, LogLevel.Warning, null, "Kept {Title}: {Reason}", skipped.Item.Title, skipped.Reason);
            }

            _catalog.Invalidate();
            if (plan.Remove.Count > 0)
            {
                // Whatever was removed is no longer leaving soon.
                var config = Plugin.Instance!.Configuration;
                var gone = plan.Remove.Select(p => p.Item.Id).ToHashSet();
                config.LeavingSoon = config.LeavingSoon.Where(l => !gone.Contains(l.Id)).ToArray();
                config.FreedBytes += plan.TotalSize;
                config.RemovedCount += plan.Remove.Count;
                Plugin.Instance.SaveConfiguration();

                var summary = $"JellyClean removed {plan.Remove.Count} {(plan.Remove.Count == 1 ? "item" : "items")}, {Bytes(plan.TotalSize)}";
                Activity.Info(_logger, "{Summary}", summary);
                await _activityManager.CreateAsync(new ActivityLog(summary, "JellyClean", userId)
                {
                    ShortOverview = string.Join(", ", plan.Remove.Take(10).Select(p => p.Item.Title)) + (plan.Remove.Count > 10 ? " and more" : string.Empty),
                }).ConfigureAwait(false);
            }

            return plan;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<string?> RemoveOneAsync(PlannedItem planned, CancellationToken cancellationToken)
    {
        var title = planned.Item.Title;
        foreach (var step in planned.Arr)
        {
            try
            {
                await _arr.ApplyAsync(step, cancellationToken).ConfigureAwait(false);
                Activity.Info(_logger, "{Title}: {Step}", title, step.Description);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "{Title}: files kept, because this failed: {Step}", title, step.Description);
                return $"Failed: {step.Description}. Files kept.";
            }
        }

        var item = _libraryManager.GetItemById(planned.Item.Id);
        if (item is null)
        {
            return "No longer in the library.";
        }

        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
            Activity.Info(_logger, "Deleted {Title} ({Size}) from disk: {Paths}", title, Bytes(planned.Item.Size), string.Join(", ", planned.Paths));
            return null;
        }
        catch (Exception ex)
        {
            Activity.Log(_logger, LogLevel.Error, ex, "Could not delete {Title}", title);
            return "Jellyfin could not delete it: " + ex.Message;
        }
    }

    private IReadOnlyDictionary<string, string> ProviderIds(Guid id)
        => new Dictionary<string, string>(_libraryManager.GetItemById(id)?.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
}
