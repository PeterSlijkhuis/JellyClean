using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private readonly ILogger<RecycleBin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecycleBin"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public RecycleBin(ILibraryManager libraryManager, ILogger<RecycleBin> logger)
    {
        _libraryManager = libraryManager;
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
            Config.Recycled = Config.Recycled.Append(item).ToArray();
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
    /// Deletes items for good: the ones older than the set number of days, or all of them.
    /// </summary>
    /// <param name="all">Empty the whole bin.</param>
    /// <returns>How many items were deleted.</returns>
    public int Purge(bool all)
    {
        lock (_lock)
        {
            var before = DateTime.UtcNow.AddDays(-Math.Max(0, Config.RecycleDays));
            var due = Config.Recycled.Where(r => all || r.Removed < before).ToList();
            foreach (var item in due)
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
                    Activity.Info(_logger, "Deleted {Title} from the recycle bin for good", item.Title);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Activity.Log(_logger, LogLevel.Error, ex, "Could not empty {Title} from the recycle bin", item.Title);
                }
            }

            return due.Count;
        }
    }

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
