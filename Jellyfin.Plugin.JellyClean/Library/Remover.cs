using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyClean.Configuration;
using MediaBrowser.Controller.Entities;
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
    private static readonly object _progressLock = new();
    private static readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly Catalog _catalog;
    private readonly ArrClient _arr;
    private readonly RecycleBin _bin;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IActivityManager _activityManager;
    private readonly ILogger<Remover> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Remover"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="arr">Radarr and Sonarr client.</param>
    /// <param name="bin">Recycle bin.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="activityManager">Activity manager.</param>
    /// <param name="logger">Logger.</param>
    public Remover(Catalog catalog, ArrClient arr, RecycleBin bin, ILibraryManager libraryManager, ISessionManager sessionManager, IActivityManager activityManager, ILogger<Remover> logger)
    {
        _catalog = catalog;
        _arr = arr;
        _bin = bin;
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
            planned.Paths = _libraryManager.GetItemById(planned.Item.Id) is { } item
                ? FileItems(item).SelectMany(i => i.GetDeletePaths()).Select(p => p.FullName).Where(p => !string.IsNullOrEmpty(p)).ToArray()
                : [];
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

    // What holds the files: the item itself, or for a season without its own folder (episodes straight in the
    // series folder, so the season has no path) its episodes.
    private BaseItem[] FileItems(BaseItem item)
        => !string.IsNullOrEmpty(item.Path) ? [item]
            : _catalog.Get().Values.Where(e => e.SeasonId == item.Id).Select(e => _libraryManager.GetItemById(e.Id)).OfType<BaseItem>().ToArray();

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
            NextUp = config.ProtectNextUp,
            AddedAfter = config.ProtectAddedDays > 0 ? now.AddDays(-config.ProtectAddedDays) : null,
            PlayedAfter = config.ProtectPlayedDays > 0 ? now.AddDays(-config.ProtectPlayedDays) : null,
            Tag = config.KeepTag,
            Libraries = config.ProtectedLibraries.ToHashSet(),
            MaxItems = config.MaxItemsPerRemoval,
        };
    }

    /// <summary>
    /// Gets how the current or last removal started with <see cref="Start"/> is going.
    /// </summary>
    public RemovalProgress Progress { get; private set; } = new();

    /// <summary>
    /// Starts a removal in the background, so a slow one is not cut off with the web request (a proxy or tunnel may stop waiting).
    /// </summary>
    /// <param name="ids">Selected item ids.</param>
    /// <param name="userId">The admin who asked.</param>
    /// <returns>False when another removal is still running.</returns>
    public bool Start(IEnumerable<Guid> ids, Guid userId)
    {
        var list = ids.ToList();
        lock (_progressLock)
        {
            if (Progress.Running)
            {
                return false;
            }

            Progress = new RemovalProgress { Running = true, Total = list.Count };
        }

        var progress = Progress;
        _ = Task.Run(async () =>
        {
            try
            {
                // Not tied to any request: closing the page must never stop a removal halfway through an item.
                progress.Result = await RemoveAsync(list, userId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "The removal stopped");
                progress.Error = ex.Message;
            }
            finally
            {
                progress.Running = false;
            }
        });
        return true;
    }

    /// <summary>
    /// Removes the items: from Radarr and Sonarr, then from Jellyfin and the disk.
    /// </summary>
    /// <param name="ids">Selected item ids.</param>
    /// <param name="userId">The admin who asked, for the Jellyfin activity log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="recycleDays">Days the files stay in the recycle bin; null uses the setting, 0 deletes right away.</param>
    /// <param name="source">What removed it, for the recycle bin: "you" or "Leaving soon".</param>
    /// <returns>The plan, with the items that failed moved to <see cref="RemovalPlan.Skip"/>.</returns>
    public async Task<RemovalPlan> RemoveAsync(IEnumerable<Guid> ids, Guid userId, CancellationToken cancellationToken, int? recycleDays = null, string source = "you")
    {
        var days = Math.Max(0, recycleDays ?? Plugin.Instance!.Configuration.RecycleDays);
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await PlanAsync(ids, cancellationToken).ConfigureAwait(false);
            var progress = Progress;
            progress.Total = plan.Remove.Count;
            var batch = Guid.NewGuid();
            foreach (var planned in plan.Remove.ToList())
            {
                progress.Current = planned.Item.Title;
                var error = await RemoveOneAsync(planned, days, source, batch, cancellationToken).ConfigureAwait(false);
                progress.Done++;
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
                if (days == 0)
                {
                    // With the recycle bin, the space counts as freed when the bin is emptied.
                    RecycleBin.CountFreed(plan.TotalSize, plan.Remove.Count);
                }
                Plugin.Instance.SaveConfiguration();

                var what = $"{plan.Remove.Count} {(plan.Remove.Count == 1 ? "item" : "items")}, {Bytes(plan.TotalSize)}";
                var summary = days > 0 ? $"JellyClean moved {what} to the recycle bin for {days} days" : $"JellyClean removed {what}";
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

    private async Task<string?> RemoveOneAsync(PlannedItem planned, int days, string source, Guid batch, CancellationToken cancellationToken)
    {
        var title = planned.Item.Title;
        var item = _libraryManager.GetItemById(planned.Item.Id);
        if (item is null)
        {
            return "No longer in the library.";
        }

        // With the recycle bin on, the files move first: that can be undone if Radarr or Sonarr then fails.
        RecycledItem? recycled = null;
        if (days > 0)
        {
            try
            {
                recycled = _bin.Move(planned.Item, planned.Paths);
                (recycled.Days, recycled.Source, recycled.Type, recycled.Batch) = (days, source, planned.Item.Type, batch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "{Title}: files kept, because they could not go to the recycle bin", title);
                return "Could not move it to the recycle bin, so it was kept: " + ex.Message;
            }
        }

        var undone = new List<ArrUndo>();
        foreach (var step in planned.Arr)
        {
            try
            {
                // With the recycle bin, Radarr and Sonarr only stop monitoring it until the bin is emptied, so a restore is clean.
                undone.Add(recycled is not null
                    ? await _arr.HoldAsync(step, cancellationToken).ConfigureAwait(false)
                    : await _arr.ApplyAsync(step, cancellationToken).ConfigureAwait(false));
                Activity.Info(_logger, "{Title}: {Step}", title, step.Description);
            }
            catch (Exception ex)
            {
                // Anything at all: the files may already be in the recycle bin and have to go back before this returns.
                Activity.Log(_logger, LogLevel.Error, ex, "{Title}: files kept, because this failed: {Step}", title, step.Description);

                // Put back what the other servers already did, so the item is whole again.
                foreach (var undo in undone)
                {
                    try
                    {
                        await _arr.UndoAsync(undo, Plugin.Instance!.Configuration.Servers, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception undoEx)
                    {
                        Activity.Log(_logger, LogLevel.Error, undoEx, "{Title}: could not undo the change in {Kind}", title, undo.Kind);
                    }
                }

                if (recycled is not null)
                {
                    RecycleBin.Add(recycled);
                    _bin.Restore(recycled.Id);
                }

                return $"Failed: {step.Description}. Files kept.";
            }
        }

        try
        {
            if (recycled is not null)
            {
                // The files are in the bin already; Jellyfin only forgets the item. Listed first, so they can always be found.
                recycled.Arr = undone.ToArray();
                RecycleBin.Add(recycled);
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                Activity.Info(_logger, "Moved {Title} ({Size}) to the recycle bin: {Paths}", title, Bytes(planned.Item.Size), string.Join(", ", planned.Paths));
                return null;
            }

            foreach (var episode in FileItems(item).Where(i => i != item))
            {
                _libraryManager.DeleteItem(episode, new DeleteOptions { DeleteFileLocation = true });
            }

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

/// <summary>
/// How a removal is going.
/// </summary>
public class RemovalProgress
{
    /// <summary>Gets or sets a value indicating whether it is still running.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets how many items are done.</summary>
    public int Done { get; set; }

    /// <summary>Gets or sets how many items will be removed.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the item being removed now.</summary>
    public string? Current { get; set; }

    /// <summary>Gets or sets the result, once done.</summary>
    public RemovalPlan? Result { get; set; }

    /// <summary>Gets or sets why it stopped, when it failed.</summary>
    public string? Error { get; set; }
}
