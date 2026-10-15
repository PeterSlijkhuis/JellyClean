using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// The Leaving soon list: items that will be removed after a notice period, once you approve
/// (or on their own, only when that is switched on). Optionally shown to users as a collection.
/// </summary>
public class LeavingSoon
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private readonly Catalog _catalog;
    private readonly Remover _remover;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LeavingSoon> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeavingSoon"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="remover">Remover.</param>
    /// <param name="collectionManager">Collection manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public LeavingSoon(Catalog catalog, Remover remover, ICollectionManager collectionManager, ILibraryManager libraryManager, ILogger<LeavingSoon> logger)
    {
        _catalog = catalog;
        _remover = remover;
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
        _logger = logger;

        // A favorite set on a listed item takes it off the list right away, not at the next run.
        _catalog.FavoritesChanged += (_, _) => Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update the Leaving soon list");
            }
        });
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Reads a saved view's filter.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns>The filter, or null when it can't be read.</returns>
    public static Filter? FilterOf(SavedView view)
    {
        try
        {
            return JsonSerializer.Deserialize<Filter>(view.Filter, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts items on the list. Protected items are left off, with why.
    /// </summary>
    /// <param name="ids">Item ids.</param>
    /// <param name="source">"you" or the name of a saved view.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="noticeDays">Days until due; 0 uses the general notice period.</param>
    /// <returns>The items added and the ones left off.</returns>
    public async Task<RemovalPlan> AddAsync(IEnumerable<Guid> ids, string source, CancellationToken cancellationToken, int noticeDays = 0)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var listed = Config.LeavingSoon.Select(l => l.Id).ToHashSet();
            var safeguards = _remover.Safeguards();
            safeguards.MaxItems = 0;
            var plan = RemovalPlan.Build(ids.Where(id => !listed.Contains(id)), _catalog.Get(), safeguards);

            var now = DateTime.UtcNow;
            var due = now.AddDays(noticeDays > 0 ? noticeDays : Math.Max(0, Config.NoticeDays));
            Config.LeavingSoon = Config.LeavingSoon
                .Concat(plan.Remove.Select(p => new LeavingItem { Id = p.Item.Id, Title = p.Item.Title, Added = now, Due = due, Source = source }))
                .ToArray();
            Plugin.Instance!.SaveConfiguration();

            if (plan.Remove.Count > 0)
            {
                Activity.Info(_logger, "{Count} items are leaving soon ({Source}), due {Due:d}", plan.Remove.Count, source, due);
            }

            await SyncCollectionAsync().ConfigureAwait(false);
            return plan;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Takes items off the list that should no longer leave: ones a safeguard now keeps (a favorite, keep, someone
    /// watching it), and ones a scheduled view put there that no longer match that view.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Config.LeavingSoon.Length == 0)
            {
                return;
            }

            var items = _catalog.Get();
            var safeguards = _remover.Safeguards();
            safeguards.MaxItems = 0;
            var reasons = RemovalPlan.Build(Config.LeavingSoon.Select(l => l.Id), items, safeguards).Skip
                .Where(p => items.ContainsKey(p.Item.Id))
                .ToDictionary(p => p.Item.Id, p => p.Reason);

            foreach (var view in Config.Views)
            {
                var listed = Config.LeavingSoon.Where(l => l.Source == view.Name && !reasons.ContainsKey(l.Id)).ToList();
                if (listed.Count == 0 || FilterOf(view) is not { } filter)
                {
                    continue;
                }

                var matches = _catalog.Find(filter).Select(i => i.Id).ToHashSet();
                foreach (var l in listed.Where(l => items.ContainsKey(l.Id) && !matches.Contains(l.Id)))
                {
                    reasons[l.Id] = $"It no longer matches {view.Name}.";
                }
            }

            if (reasons.Count == 0)
            {
                return;
            }

            foreach (var l in Config.LeavingSoon.Where(l => reasons.ContainsKey(l.Id)))
            {
                Activity.Info(_logger, "{Title} left the Leaving soon list. {Reason}", l.Title, reasons[l.Id]);
            }

            Config.LeavingSoon = Config.LeavingSoon.Where(l => !reasons.ContainsKey(l.Id)).ToArray();
            Plugin.Instance!.SaveConfiguration();
            await SyncCollectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Takes items off the list.
    /// </summary>
    /// <param name="ids">Item ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task CancelAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancel = ids.ToHashSet();
            Config.LeavingSoon = Config.LeavingSoon.Where(l => !cancel.Contains(l.Id)).ToArray();
            Plugin.Instance!.SaveConfiguration();
            await SyncCollectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The list, with each item's current state: how big it is, and whether a safeguard now keeps it.
    /// </summary>
    /// <returns>The items, due first.</returns>
    public IEnumerable<object> List()
    {
        var items = _catalog.Get();
        var safeguards = _remover.Safeguards();
        safeguards.MaxItems = 0;
        var plan = RemovalPlan.Build(Config.LeavingSoon.Select(l => l.Id), items, safeguards);
        var reasons = plan.Skip.ToDictionary(p => p.Item.Id, p => p.Reason);

        return Config.LeavingSoon
            .OrderBy(l => l.Due)
            .Select(l => new
            {
                l.Id,
                Title = items.GetValueOrDefault(l.Id)?.Title ?? l.Title,
                items.GetValueOrDefault(l.Id)?.Type,
                items.GetValueOrDefault(l.Id)?.SeriesId,
                Size = items.GetValueOrDefault(l.Id)?.Size ?? 0,
                l.Added,
                l.Due,
                l.Source,
                IsDue = l.Due <= DateTime.UtcNow,
                AutoRemove = RemovesOnItsOwn(l),
                Protected = reasons.GetValueOrDefault(l.Id),
                Item = items.GetValueOrDefault(l.Id),
            });
    }

    /// <summary>
    /// Whether an item is removed without asking once due: when that is on for everything, or for the view that added it.
    /// </summary>
    /// <param name="item">The listed item.</param>
    /// <returns>True when it goes on its own.</returns>
    public static bool RemovesOnItsOwn(LeavingItem item)
        => Config.AutoRemoveWhenDue || Config.Views.Any(v => v.Name == item.Source && v.AutoRemove && v.Runs().Length > 0);

    /// <summary>
    /// The scheduled run: views whose time has come add their matches, items that are gone leave the list,
    /// and due items are removed where that is switched on.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="views">Run these views now, whatever their schedule. Null: the ones that are due.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(CancellationToken cancellationToken, IReadOnlyCollection<string>? views = null)
    {
        var items = _catalog.Get(refresh: true);
        var now = DateTime.Now;

        foreach (var view in Config.Views.Where(v => views is null ? v.NextRun(now) <= now : views.Contains(v.Name)).ToList())
        {
            if (FilterOf(view) is not { } filter)
            {
                Activity.Log(_logger, LogLevel.Warning, null, "Saved view {View} could not be read", view.Name);
                continue;
            }

            var plan = await AddAsync(_catalog.Find(filter).Select(i => i.Id), view.Name, cancellationToken, view.NoticeDays).ConfigureAwait(false);
            (view.LastRun, view.LastAdded) = (now, plan.Remove.Count);
            Plugin.Instance!.SaveConfiguration();
        }

        var missing = Config.LeavingSoon.Where(l => !items.ContainsKey(l.Id)).Select(l => l.Id).ToList();
        if (missing.Count > 0)
        {
            await CancelAsync(missing, cancellationToken).ConfigureAwait(false);
        }

        await ReconcileAsync(cancellationToken).ConfigureAwait(false);

        var due = Config.LeavingSoon.Where(l => l.Due <= DateTime.UtcNow && RemovesOnItsOwn(l)).Select(l => l.Id).ToList();
        if (due.Count > 0)
        {
            Activity.Info(_logger, "Removing {Count} items that are due and set to go on their own", due.Count);
            // Never straight off the disk: automatic removals always wait in the recycle bin, so a mistake can be undone.
            var days = Math.Max(Math.Max(1, Config.AutoRecycleDays), Config.RecycleDays);
            await _remover.RemoveAsync(due, Guid.Empty, cancellationToken, days, "Leaving soon").ConfigureAwait(false);
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncCollectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Makes the collection hold exactly the listed items, or deletes it when switched off.
    private async Task SyncCollectionAsync()
    {
        try
        {
            var existing = Config.CollectionId == Guid.Empty ? null : _libraryManager.GetItemById(Config.CollectionId) as BoxSet;
            var wanted = Config.LeavingSoon.Select(l => l.Id).Where(id => _libraryManager.GetItemById(id) is not null).ToHashSet();

            if (!Config.UseCollection || wanted.Count == 0)
            {
                if (existing is not null)
                {
                    _libraryManager.DeleteItem(existing, new DeleteOptions { DeleteFileLocation = true });
                    Config.CollectionId = Guid.Empty;
                    Plugin.Instance!.SaveConfiguration();
                }

                return;
            }

            if (existing is null)
            {
                var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = string.IsNullOrWhiteSpace(Config.CollectionName) ? "Leaving soon" : Config.CollectionName.Trim(),
                    ItemIdList = wanted.Select(id => id.ToString("N")).ToArray(),
                    IsLocked = true,
                }).ConfigureAwait(false);
                Config.CollectionId = created.Id;
                Plugin.Instance!.SaveConfiguration();
                return;
            }

            var current = existing.GetLinkedChildren().Select(c => c.Id).ToHashSet();
            var add = wanted.Except(current).ToList();
            var remove = current.Except(wanted).ToList();
            if (add.Count > 0)
            {
                await _collectionManager.AddToCollectionAsync(existing.Id, add).ConfigureAwait(false);
            }

            if (remove.Count > 0)
            {
                await _collectionManager.RemoveFromCollectionAsync(existing.Id, remove).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The collection is a courtesy for users; never let it stop the list itself.
            Activity.Log(_logger, LogLevel.Warning, ex, "Could not update the {Name} collection", Config.CollectionName);
        }
    }
}
