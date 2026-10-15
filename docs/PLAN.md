# JellyClean plan

A Jellyfin plugin that gives admins one dashboard page to see everything in their libraries, filter it, and remove what they no longer want, without touching the libraries until you press delete.

## Goals

- **Read only by default.** Browsing, filtering and sorting never write to a library, a metadata file or the database. The plugin keeps its own index and its own settings, nothing more.
- **See what matters per item:** title, type, library, genres, year, date added, watched or not (and by whom, and when last), file size, file path, video quality.
- **Filter and combine:** type (movie, series, season, episode), library, genre, watched state, added before or after, last watched before, size, rating, year.
- **Bulk remove** the result of a filter, for example *unwatched horror movies added more than 2 years ago*, behind a preview and a confirmation. Removing deletes the files from disk and removes the item from Jellyfin, Radarr and Sonarr, so it doesn't come back.
- **Same look as JellyBadge and JellyTheme.**

## Not in scope (for now)

- Automatic, scheduled deletion by rules. That is what [media-cleaner](https://github.com/shemanaev/jellyfin-plugin-media-cleaner) does well. JellyClean is a manual tool you look at. A rule runner can come later on top of the same filters (see phase 5).
- Anything in the normal Jellyfin home screen or item pages for regular users.
- Moving, renaming or transcoding files.

## Do we need File Transformation?

**No.** File Transformation exists to change the files of the Jellyfin web client itself (`index.html`, its scripts), which plugins need when they want to add buttons or styles to the normal browsing UI. JellyClean lives entirely in its own dashboard page, served by the plugin through `IHasWebPages`, exactly like JellyBadge's settings and activity pages and JellyTheme's Missing themes page. Neither of those uses File Transformation, and JellyClean has no reason to either.

It would only become relevant if we later want something inside the normal item pages, like a "Keep forever" button on a movie's detail page (media-cleaner's "Keep until watched" uses it for that). That is optional and not planned.

## What we take from media-cleaner

Ideas, not code.

- Be clear about what "watched" means. JellyClean uses what Jellyfin shows as watched per user, and *never played* means nobody even started it.
- Never delete what someone is watching right now.
- A dry run that goes through the exact same code path as the real delete.
- Protection always wins over a delete: a "keep" mark on an item stops it from being removed, and for a series also stops the series from being removed.

## How it works

### Stack

Same as JellyBadge: `net10.0`, `Jellyfin.Controller` and `Jellyfin.Model` 12.1.0 with runtime assets excluded, `TreatWarningsAsErrors`, nullable on, an `.slnx`, a test project, a GitHub Actions build that runs `dotnet test`, and a release workflow with a `manifest.json` repository like JellyTheme's.

```
JellyClean.slnx
Directory.Build.props
manifest.json
Jellyfin.Plugin.JellyClean/
  Plugin.cs                    plugin, pages, service registration
  JellyCleanController.cs      admin-only API for the page
  Library/Catalog.cs           builds the item list from Jellyfin
  Library/CatalogItem.cs       one row: what the page shows
  Library/Filter.cs            filter model and matching
  Library/Remover.cs           preview and delete
  Configuration/PluginConfiguration.cs
  Configuration/cleanPage.html the main page
  Configuration/activityPage.html what was removed, when, by whom
Jellyfin.Plugin.JellyClean.Tests/
.github/workflows/build.yaml, release.yaml
```

### Reading the library (no writes)

- Items come from `ILibraryManager.GetItemList` with an `InternalItemsQuery` for `Movie`, `Series`, `Season` and `Episode`, skipping virtual items (missing episodes).
- **Date added** is `BaseItem.DateCreated`.
- **Watched** comes from `IUserDataManager.GetUserData(user, item)` for every user from `IUserManager`: `Played`, `PlayCount`, `LastPlayedDate`. The page shows "watched by 2 of 3 users, last on 4 Mar 2025". The filter offers *watched by nobody*, *watched by anyone*, *watched by everyone*, and *by user X*.
- **Size** comes from the item's media sources (`GetMediaSources`), summed over versions. A season or series sums its episodes. When Jellyfin has no size, the plugin reads it from the file (`FileInfo.Length`) and caches it.
- **Genres, year, rating, library** from the item and its top parent.
- The catalog is built in memory when the page asks for it and cached for a few minutes. It is rebuilt on `ItemAdded`, `ItemRemoved` and `ItemUpdated`. No database of its own: there is nothing to migrate or get out of sync.

### Filtering

Filtering runs on the server, so a library with 20 000 episodes stays fast on a phone. The page sends one filter object; the server returns a page of rows plus totals (count and total size of everything that matches). Sorting by name, date added, last watched, size, year and rating.

### Removing

1. **Select** rows by hand, or press *Select all matching*.
2. **Preview.** The server works out exactly what would go: every item, every file, the total size, and what is skipped and why (marked keep, being watched now, in a library where deleting is not allowed).
3. **Confirm** in a dialog that shows the count and size, for example *Delete 37 movies, 412 GB, from disk*. Large removals ask you to type the number.
4. **Remove from Radarr and Sonarr first**, when they are connected (see below), so they don't download it again.
5. **Delete from disk** through Jellyfin's own `ILibraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true })`, one item at a time, so Jellyfin removes the files, its metadata and images, and its database rows the same way as the Delete button in the web app does.
6. **Log** every removal (title, path, size, user, time, and what Radarr or Sonarr said) on the activity page, also to Jellyfin's activity log.

### Radarr and Sonarr

Deleting a file is not enough on its own: Radarr and Sonarr still have the movie or episode as *monitored*, see it as missing, and download it again. So JellyClean tells them as well.

- **Settings:** a card per app with URL and API key, a **Test connection** button, and a switch *Add to the exclusion list* (on by default for Radarr) so a removed movie is never added again by a list.
- **Matching:** by the TMDb, TVDb or IMDb id from the Jellyfin item's provider ids. Paths are not used, because Radarr, Sonarr and Jellyfin often see the same files under different paths. The preview says when items were not found in Radarr or Sonarr.
- **Movies:** `DELETE /api/v3/movie/{id}?deleteFiles=false&addImportExclusion=true` on Radarr. The movie is gone from Radarr; Jellyfin then deletes the files.
- **Whole series:** `DELETE /api/v3/series/{id}?deleteFiles=false` on Sonarr, optionally with an import exclusion; Jellyfin then deletes the files.
- **Seasons or episodes:** Sonarr keeps the series. JellyClean unmonitors those episodes (`PUT /api/v3/episode/monitor`), Jellyfin deletes the files, and Sonarr sees them as unmonitored and missing, so they stay gone while new episodes still come in.
- **Order:** Radarr or Sonarr first, then Jellyfin deletes the files. Only one of them ever deletes a file, so nothing fails on a file that is already gone. If the call to Radarr or Sonarr fails, that item is skipped, nothing is deleted, and the activity log says why. That way an item is never deleted from disk while Radarr or Sonarr still wants it.
- Several Radarr or Sonarr instances (for example a separate 4K one) are allowed: each item is looked up in all of them.
- Without Radarr or Sonarr connected, removal still works, and the preview warns that they may download the item again.

Safety rules that always apply:

- Admin only (`Policies.RequiresElevation`) on every endpoint, like JellyBadge.
- Items being played right now (`ISessionManager` sessions) are skipped.
- **Keep** marks: a per-item "never remove" list stored in the plugin configuration, not on the item. Kept items show a lock and are skipped by every removal. Optional: treat favorites of any user as kept (on by default).
- Removing a series or season only happens when you chose the series or season row; selecting episodes never cascades upward.

### The page

One dashboard page, **JellyClean**, in the main menu (icon `cleaning_services`), with tabs like JellyBadge: **Library** and **Activity**.

Styling reuses JellyBadge's tokens and components, scoped to the page so nothing leaks into the dashboard:

- `--jc-accent: #00a4dc`, `--jc-accent-2: #aa5cc3`, the same line, card and radius values.
- The gradient **hero** with a logo tile and a summary: *2 431 items, 18.4 TB, 6.1 TB never watched*.
- **Cards** for the filter panel and the results.
- **Pill tabs** for Library and Activity, **chips** for genres and quality, the same **switch** for on and off settings.
- JellyTheme's poster rows: small poster, title linking to the item, a muted line with year, library and path.

Layout:

- **Filter bar** at the top: type, library, genre (multi-select chips), watched state, added before, last watched before, minimum size, search by name. Quick presets: *Never watched, added over a year ago*, *Biggest files*, *Watched by everyone*.
- **Results** as a list with a checkbox, poster, title, genres, added date, watched state, size. Sticky footer: *12 selected, 186 GB* with **Preview removal**.
- On a phone the filters fold into one card and rows drop the poster, like JellyTheme does.

## Phases

1. **Scaffold.** Project, build and release workflows, `manifest.json`, empty page with hero and tabs, README in the style of the other two.
2. **Browse.** Catalog, filters, sorting, totals, the Library tab. Read only. Shippable on its own.
3. **Remove.** Selection, preview, confirmation, delete from disk, keep marks, favorites protection, the Activity tab.
4. **Radarr and Sonarr.** Connection settings, matching, removal and unmonitoring as described above.
5. **Later, if wanted.** Saved filters, a scheduled task that runs a saved filter as a dry run and reports, then optionally deletes. A "Keep" button on item pages (this is the only part that might need File Transformation).

## Tests

- Filter matching: every filter on fixture items, including the edge cases (played without a date, played before added, series with partly watched episodes).
- Size: versions summed, series summed, missing size falls back to the file.
- Remover: kept items, favorites and items being played are never in the plan; preview and delete use the same plan.
- Radarr and Sonarr: matching by provider id, and a failed call skips the item instead of deleting it (against a fake HTTP handler).
- No test ever deletes a real file: the remover takes the delete action as a dependency.

## Open questions

- Should deleting a series also delete the series folder when it ends up empty? Jellyfin's own delete does this already for the item folder; the plan follows Jellyfin.
- Is one admin page enough, or should non-admin users see a read-only view? The plan says admin only.
