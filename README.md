<p align="center">
  <img src="docs/jellyclean.png" alt="JellyClean" width="100%" />
</p>

<p align="center">
  <a href="https://github.com/PeterSlijkhuis/JellyClean/releases/latest"><img src="https://img.shields.io/github/v/release/PeterSlijkhuis/JellyClean?style=flat-square&label=release&color=aa5cc3" alt="Latest release" /></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.1%2B-00a4dc?style=flat-square" alt="Jellyfin 12.1 or newer" />
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
- [FAQ](#faq)
- [For developers](#for-developers)
- [License](#license)

## What you get

- **Everything in one list.** Title, library, year, genres, date added, who watched it, when it was last played, rating, size on disk and video quality (resolution, codec and HDR).
- **Filters that add up.** Type (movies, series, seasons, episodes), library, genres, watched state (never played, watched by someone, by everyone, or by one user), added before or after, nobody played it in a while, size, resolution, codec, HDR, and search on title or path.
- **One-click suggestions.** *Never played, added over a year ago*, *Biggest movies*, *Watched by everyone*, *Series nobody played in 2 years*, *Movies you have twice* and *Old H.264 below 1080p*.
- **Copies.** Spots movies and series that are in Jellyfin more than once, like a 1080p copy next to a 4K one, so you can drop the one you don't need.
- **Export.** Download the current list as a spreadsheet (CSV).
- **Real sizes.** The size is what removing frees: the movie's folder with its extras and artwork, or the episode file with its subtitles. Series and seasons add up their episodes.
- **Preview first.** See exactly which files and folders go, what happens in Radarr and Sonarr, and what stays and why. Removing 20 or more items asks you to type the number.
- **Keep what matters.** Mark anything **keep** with the lock button and it is never removed, nor the season or series it is in. Favorites and anything being played are left alone too.
- **Radarr and Sonarr.** Removed movies leave Radarr, removed series leave Sonarr, and removed seasons or episodes stop being monitored, so new episodes still come in. Several of each are fine, like a separate 4K one.
- **Activity log.** Every removal, every item that was kept and why, and every error.

## Install

JellyClean needs **Jellyfin 12.1 or newer**. No other plugins are needed.

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
3. Back on **Library**, pick a suggestion or set your filters.
4. Tick what you want gone, or press **Select all**, then **Preview removal**.
5. Check the preview and press **Delete from disk**.

## How removing works

For each item, in this order:

1. **Radarr or Sonarr.** A movie is removed from Radarr, a whole series from Sonarr, both with *delete files* off and optionally added to the exclusion list. For seasons and episodes, Sonarr keeps the series and only stops monitoring those episodes. Items are found by their TMDb, TVDb or IMDb id.
2. **Jellyfin and the disk.** Jellyfin's own delete removes the files, the item and its artwork, exactly like the Delete button in the Jellyfin app.

If Radarr or Sonarr can't be reached, or a step there fails, that item is skipped and its files stay, because Radarr or Sonarr would only download it again. The Activity tab says why.

These always stay:

- items marked **keep**, and the seasons and series they are in;
- favorites of any user, and the seasons and series they are in (switch this off under **Settings** if you like);
- anything someone is playing right now.

## FAQ

**Does JellyClean change my libraries while I browse?**
No. It only reads. Its own settings and the list of items marked keep are stored in the plugin's settings, never on your items or in your media folders.

**Does it use Jellyfin's connection to Radarr and Sonarr?**
Jellyfin has no connection to Radarr or Sonarr of its own, so JellyClean needs the address and API key of each one. Add them under **Settings**. Without them, removing still works, but Radarr and Sonarr may download the items again.

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

- Targets .NET 10 and Jellyfin 12.1.
- Filtering (`Library/Filter.cs`), the removal plan (`Library/RemovalPlan.cs`) and the Radarr and Sonarr calls (`Library/ArrClient.cs`) are covered by unit tests. No test touches a real file or server.
- **Releasing.** Open **Actions > Release > Run workflow**, fill in the version and release notes, and run it. It builds, tests and publishes a release with the plugin zip and `manifest.json`.

## License

JellyClean is licensed under the [GNU General Public License v3.0](LICENSE), the same as Jellyfin.
