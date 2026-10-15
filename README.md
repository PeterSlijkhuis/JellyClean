<p align="center">
  <img src="docs/jellyclean.png" alt="JellyClean" width="100%" />
</p>

<p align="center">
  <a href="https://github.com/PeterSlijkhuis/JellyClean/releases/latest"><img src="https://img.shields.io/github/v/release/PeterSlijkhuis/JellyClean?style=flat-square&label=release&color=aa5cc3" alt="Latest release" /></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.2%2B-00a4dc?style=flat-square" alt="Jellyfin 12.2 or newer" />
  <a href="https://github.com/PeterSlijkhuis/JellyClean/actions/workflows/build.yaml"><img src="https://img.shields.io/github/actions/workflow/status/PeterSlijkhuis/JellyClean/build.yaml?style=flat-square&label=build" alt="Build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-555?style=flat-square" alt="GPL-3.0" /></a>
</p>

<p align="center"><b>See what is in your libraries, find what nobody watches, and free up space in bulk.</b></p>

JellyClean adds a **JellyClean** page to the Jellyfin dashboard. It lists every movie, series, season and episode with when it was added, who watched it, when it was last played and how much space it takes. Filter it down, for example to *horror movies nobody played that were added over two years ago*, check the preview, and remove them all at once. Removed items are deleted from disk and from Jellyfin, and also from Radarr and Sonarr, so they are not downloaded again.

Nothing changes until you remove something. Browsing and filtering only read.

## Contents

- [What you get](#what-you-get)
- [Install](#install)
- [Getting started](#getting-started)
- [How removing works](#how-removing-works)
- [Leaving soon](#leaving-soon)
- [FAQ](#faq)
- [For developers](#for-developers)
- [License](#license)

## What you get

- **See where the space goes.** A bar per library shows how much space it takes and how much of that nobody ever played. Click a library to filter on it. JellyClean also counts how much it has freed so far.
- **Everything in one list.** Title, library, year, genres, date added, who watched it, when it was last played, rating, size on disk and video quality (resolution, codec and HDR).
- **Filters that add up.** Type (movies, series, seasons, episodes), library, genres, watched state (never played, watched by someone, by everyone, or by one user), added before or after, nobody played it in a while, size, resolution, codec, HDR, and search on title or path.
- **Start with what is empty.** The Library page opens on movies and series that take 0 B: downloads that never finished, or series whose episodes are all gone. Removing them is the safest first clean-up. If a whole library folder can't be reached, like a network drive that is offline, its items are left alone, because they only look empty.
- **One-click suggestions.** *Empty movies and series*, *Never played, added over a year ago*, *Biggest movies*, *Watched by everyone*, *Series nobody played in 2 years*, *Movies you have twice* and *Old H.264 below 1080p*.
- **Copies.** Spots movies and series that are in Jellyfin more than once, like a 1080p copy next to a 4K one, so you can drop the one you don't need.
- **Export.** Download the current list as a spreadsheet (CSV).
- **Real sizes.** The size is what removing frees: the movie's folder with its extras and artwork, or the episode file with its subtitles. Series and seasons add up their episodes.
- **Preview first.** See exactly which files and folders go, what happens in Radarr and Sonarr, and what stays and why. Removing 20 or more items asks you to type the number.
- **Keep what matters.** Mark anything **keep** with the lock button and it is never removed, nor the season or series it is in. Safeguards you choose in **Settings** keep favorites, Continue Watching, recently added and recently played items, items with a tag like `keep`, and whole libraries. One removal is capped at 200 items unless you change it.
- **Saved views.** Save any filter under a name and open it with one click. Days are relative, so *added over 2 years ago* still means that next year.
- **Leaving soon.** Instead of deleting right away, put items on a list with a notice period (14 days by default). Users can see the list as a Jellyfin collection if you like. When items are due you review and remove them; safeguards are checked again at that moment.
- **Daily checks (optional).** A saved view can be checked every day, putting new matches on the Leaving soon list. Nothing is deleted unless you also switch on *Remove due items without asking*.
- **Radarr and Sonarr.** Removed movies leave Radarr, removed series leave Sonarr, and removed seasons or episodes stop being monitored, so new episodes still come in. Several of each are fine, like a separate 4K one.
- **Activity log.** Every removal, every item that was kept and why, and every error.

## Install

JellyClean needs **Jellyfin 12.2 or newer**. No other plugins are needed.

### Option 1: plugin repository (recommended)

1. Open **Dashboard > Plugins** and click **Manage Repositories**.
2. Click **+**, give it a name such as `JellyClean` and paste this URL:
   ```
   https://github.com/PeterSlijkhuis/JellyClean/releases/latest/download/manifest.json
   ```
3. Go back to **Plugins**, open **Available**, find **JellyClean** and click **Install**.
4. Restart Jellyfin.

### Option 2: manual install

1. Download `jellyclean_x.y.z.0.zip` from the [latest release](https://github.com/PeterSlijkhuis/JellyClean/releases/latest).
2. Unzip it into a folder called `JellyClean` inside your Jellyfin `plugins` folder. With Docker this is usually `/config/plugins/JellyClean`.
3. Restart Jellyfin.

## Getting started

1. Open **JellyClean** in the dashboard menu.
2. If you use Radarr or Sonarr, go to **Settings**, click **Add Radarr** or **Add Sonarr**, fill in the address and API key (in Radarr or Sonarr under **Settings > General**), press **Test connection** and **Save**.
3. Back on **Library**, start with the empty movies and series it opens on, then pick another suggestion or set your filters.
4. Tick what you want gone (hold Shift to tick a whole range), or press **Select all**, then **Preview removal**.
5. Check the preview and press **Delete from disk**.

## How removing works

For each item, in this order:

1. **Radarr or Sonarr.** A movie is removed from Radarr, a whole series from Sonarr, both with *delete files* off and optionally added to the exclusion list. For seasons and episodes, Sonarr keeps the series and only stops monitoring those episodes. Items are found by their TMDb, TVDb or IMDb id.
2. **Jellyfin and the disk.** Jellyfin's own delete removes the files, the item and its artwork, exactly like the Delete button in the Jellyfin app.

If Radarr or Sonarr can't be reached, or a step there fails, that item is skipped and its files stay, because Radarr or Sonarr would only download it again. The Activity tab says why.

These always stay:

- items marked **keep**, and the seasons and series they are in;
- anything someone is playing right now.

These stay unless you switch them off under **Settings**:

- favorites of any user, and the seasons and series they are in;
- anything in someone's Continue Watching, and the season and series it is in;
- items added in the last 30 days, and items anyone played in the last 30 days;
- items with the tag `keep` (add it in Jellyfin under **Edit metadata**);
- everything in libraries you mark as protected.

The preview lists every item that stays, with the reason.

## Leaving soon

1. In the preview, press **Leaving soon** instead of **Delete from disk**. Protected items are left off the list.
2. Optionally switch on **Show it to users as a collection** under **Settings**, so everyone sees what is leaving and can ask to keep it, or mark it a favorite.
3. When the notice period is over, the Library page shows the due items. Press **Review due items**, check the preview and remove them.

The daily task **Update Leaving soon** (under **Dashboard > Scheduled Tasks**) adds matches from saved views marked *Check daily*, drops items that are gone and keeps the collection up to date. It only deletes when **Remove due items without asking** is on.

## FAQ

**Does JellyClean change my libraries while I browse?**
No. It only reads. Its own settings and the list of items marked keep are stored in the plugin's settings, never on your items or in your media folders.

**Does it use Jellyfin's connection to Radarr and Sonarr?**
Jellyfin has no connection to Radarr or Sonarr of its own, so JellyClean needs the address and API key of each one. Add them under **Settings**. Without them, removing still works, but Radarr and Sonarr may download the items again.

**Can I skip the API keys?**
Yes. In Radarr switch on **Unmonitor Deleted Movies**, and in Sonarr **Unmonitor Deleted Episodes**, both under **Settings > Media Management**. They then stop monitoring what disappears from disk, so nothing comes back. The movie or series stays listed in Radarr or Sonarr, and an import list may still add a removed movie again; the API keys with the exclusion list prevent that.

**Do I need the File Transformation plugin?**
No. JellyClean lives in its own dashboard page and does not change the Jellyfin web app.

**What counts as watched?**
What Jellyfin shows as watched for each user. *Never played by anyone* means nobody started it at all; a series counts as watched by a user once they watched every episode.

**Why is a series folder on disk a bit bigger than shown?**
A series adds up its episode files. Artwork and extras in the series folder are not counted.

**The list is not up to date.**
JellyClean reads the library again every few minutes. Press **Refresh** to read it right away.

## For developers

```bash
dotnet test JellyClean.slnx
dotnet publish Jellyfin.Plugin.JellyClean -c Release -o out
```

- Targets .NET 10 and Jellyfin 12.2.
- Filtering (`Library/Filter.cs`), the removal plan (`Library/RemovalPlan.cs`) and the Radarr and Sonarr calls (`Library/ArrClient.cs`) are covered by unit tests. No test touches a real file or server.
- **Releasing.** Open **Actions > Release > Run workflow** on `main`. Leave the version empty to count up from the last release (0.1.0 becomes 0.1.1), or fill one in. The release notes default to "Bug fixes and feature updates". It builds, tests and publishes a release with the plugin zip and `manifest.json`.

## License

JellyClean is licensed under the [GNU General Public License v3.0](LICENSE), the same as Jellyfin.
