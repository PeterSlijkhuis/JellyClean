using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// The recycle bin: removed files are moved to a hidden folder in their own library folder, so moving is instant
/// and a mistake can be undone, and deleted for good after the set number of days.
/// </summary>
public class RecycleBin
{
    /// <summary>
    /// The name of the bin folder in each library folder. Hidden, so Jellyfin does not scan it.
    /// </summary>
    public const string FolderName = ".jellyclean-trash";

    private static readonly object _lock = new();
    private readonly ILibraryManager _libraryManager;
    private readonly ArrClient _arr;
    private readonly ILogger<RecycleBin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecycleBin"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="arr">Radarr and Sonarr client.</param>
    /// <param name="logger">Logger.</param>
    public RecycleBin(ILibraryManager libraryManager, ArrClient arr, ILogger<RecycleBin> logger)
    {
        _libraryManager = libraryManager;
        _arr = arr;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Moves the files of an item into the bin. When one can't be moved, the ones already moved go back and it throws,
    /// so the item is either fully in the bin or untouched.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="paths">Its files and folders.</param>
    /// <returns>What was moved where.</returns>
    public RecycledItem Move(CatalogItem item, IEnumerable<string> paths)
    {
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Select(l => l.TrimEnd('/', '\\'))
            .OrderByDescending(l => l.Length)
            .ToList();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + item.Id.ToString("N")[..8];
        var moved = new List<RecycledPath>();
        try
        {
            foreach (var path in paths)
            {
                if (!Directory.Exists(path) && !File.Exists(path))
                {
                    continue;
                }

                var root = roots.FirstOrDefault(r => IsUnder(path, r))
                    ?? throw new IOException($"{path} is not in a library folder");
                var to = Path.Combine(root, FolderName, stamp, Path.GetRelativePath(root, path));
                Move(path, to);
                moved.Add(new RecycledPath { From = path, To = to });
            }
        }
        catch
        {
            foreach (var m in Enumerable.Reverse(moved))
            {
                Move(m.To, m.From);
            }

            throw;
        }

        return new RecycledItem { Id = item.Id, Title = item.Title, Size = item.Size, Removed = DateTime.UtcNow, Paths = moved.ToArray() };
    }

    /// <summary>
    /// Adds a moved item to the list in the settings.
    /// </summary>
    /// <param name="item">The item.</param>
    public static void Add(RecycledItem item)
    {
        lock (_lock)
        {
            Config.Recycled = Config.Recycled.Where(r => r.Id != item.Id).Append(item).ToArray();
            Plugin.Instance!.SaveConfiguration();
        }
    }

    /// <summary>
    /// Puts an item's files back where they were. Jellyfin finds them again at its next scan.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <returns>Why it could not, or null.</returns>
    public string? Restore(Guid id)
    {
        lock (_lock)
        {
            var item = Config.Recycled.FirstOrDefault(r => r.Id == id);
            if (item is null)
            {
                return "It is no longer in the recycle bin.";
            }

            var blocked = item.Paths.FirstOrDefault(p => Directory.Exists(p.From) || File.Exists(p.From));
            if (blocked is not null)
            {
                return $"Something is already at {blocked.From}.";
            }

            try
            {
                foreach (var p in item.Paths)
                {
                    Move(p.To, p.From);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "Could not restore {Title}", item.Title);
                return ex.Message;
            }

            Forget(item);
            Activity.Info(_logger, "Restored {Title} from the recycle bin", item.Title);
            return null;
        }
    }

    /// <summary>
    /// Restores an item completely: the files go back, Jellyfin scans them in, and Radarr and Sonarr get the movie
    /// or series back (or monitor the episodes again), as they were.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Why the files could not go back (Error), or what did not work in Radarr or Sonarr (Warning).</returns>
    public async Task<(string? Error, string? Warning)> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = Config.Recycled.FirstOrDefault(r => r.Id == id);
        var error = Restore(id);
        if (error is not null || item is null)
        {
            return (error, null);
        }

        _libraryManager.QueueLibraryScan();
        var problems = new List<string>();
        foreach (var undo in item.Arr)
        {
            try
            {
                await _arr.UndoAsync(undo, Config.Servers, cancellationToken).ConfigureAwait(false);
                Activity.Info(_logger, "{Title} is back in {Kind}", item.Title, undo.Kind);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "{Title} is back in Jellyfin, but not in {Kind}", item.Title, undo.Kind);
                problems.Add($"{undo.Kind}: {ex.Message}");
            }
        }

        return (null, problems.Count == 0 ? null : "Back in Jellyfin, but add it again by hand in " + string.Join("; ", problems));
    }

    /// <summary>
    /// Deletes items for good: the ones whose days are over, or all of them. A held movie or series is removed from
    /// Radarr or Sonarr first; when that fails the item stays in the bin and is tried again at the next run.
    /// </summary>
    /// <param name="all">Empty the whole bin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were deleted.</returns>
    public async Task<int> PurgeAsync(bool all, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var deleted = 0;
        foreach (var item in Config.Recycled.Where(r => all || DeleteAt(r) < now).ToList())
        {
            try
            {
                foreach (var undo in item.Arr)
                {
                    await _arr.FinishAsync(undo, Config.Servers, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "{Title} stays in the recycle bin: Radarr or Sonarr could not remove it yet", item.Title);
                continue;
            }

            lock (_lock)
            {
                try
                {
                    foreach (var p in item.Paths)
                    {
                        if (Directory.Exists(p.To))
                        {
                            Directory.Delete(p.To, true);
                        }
                        else if (File.Exists(p.To))
                        {
                            File.Delete(p.To);
                        }

                        DeleteEmptyParents(p.To);
                    }

                    Forget(item);
                    deleted++;
                    Activity.Info(_logger, "Deleted {Title} from the recycle bin for good", item.Title);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Activity.Log(_logger, LogLevel.Error, ex, "Could not empty {Title} from the recycle bin", item.Title);
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// When an item is deleted for good.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The time.</returns>
    public static DateTime DeleteAt(RecycledItem item) => item.Removed.AddDays(item.Days > 0 ? item.Days : Math.Max(0, Config.RecycleDays));

    private static void Forget(RecycledItem item)
    {
        Config.Recycled = Config.Recycled.Where(r => r != item).ToArray();
        Plugin.Instance!.SaveConfiguration();
    }

    private static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    // Removes the stamp folders left empty, up to the bin folder itself.
    private static void DeleteEmptyParents(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && Path.GetFileName(dir) != FolderName && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static bool IsUnder(string path, string folder)
        => path.StartsWith(folder, StringComparison.Ordinal) && path.Length > folder.Length && path[folder.Length] is '/' or '\\';
}
