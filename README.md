# AnimeThemes for Jellyfin

[![Latest release](https://img.shields.io/github/v/release/MrGameVlogger/jellyfin-plugin-animethemes)](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/releases)
[![Build](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/actions/workflows/build.yaml/badge.svg)](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/actions/workflows/build.yaml)
[![CodeQL](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/actions/workflows/scan-codeql.yaml/badge.svg)](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/actions/workflows/scan-codeql.yaml)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.x-blue)](https://jellyfin.org/)

Fetches anime opening and ending themes from [animethemes.moe](https://animethemes.moe) and turns
them into Jellyfin's native **theme songs** and **theme videos** — the music that plays while you
browse, and the clips shown on a show's page.

- 🎵 **Theme songs** (`theme-music/*.mp3`) and 🎬 **theme videos** (`backdrops/*.webm`)
- 🗂️ Kept **in sync automatically** — new themes appear, outdated ones disappear, replaced files refresh
- 📺 **Multi-season aware** — all seasons of a show, and *only* seasons of that show
- 🔊 Optional **loudness normalization** so themes sit at a sensible level next to dialogue

> **Requires the [AniDB plugin](https://github.com/jellyfin/jellyfin-plugin-anidb)** — themes are
> matched by each series' AniDB ID.

## Compatibility

| Jellyfin | Use |
|----------|-----|
| **12.0 or newer** | [v7+](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/releases) |
| 10.11.x | [v6](https://github.com/EusthEnoptEron/jellyfin-plugin-animethemes/releases/tag/6) (final 10.11 release) |

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:

   ```
   https://raw.githubusercontent.com/MrGameVlogger/jellyfin-plugin-animethemes/main/manifest.json
   ```

2. **Plugins → Catalog** → install **AnimeThemes** and restart.

Prefer manual installs? [Grab the latest build](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/actions/workflows/build.yaml)
and drop it in your [plugins folder](https://jellyfin.org/docs/general/server/plugins/). Also make
sure theme media is enabled in your **display settings**.

## How it works

The plugin adds a scheduled task — **Search For Anime Themes** — that runs every **12 hours** by
default (changeable in Dashboard → Scheduled Tasks). Each run keeps your themes in sync with
AnimeThemes:

- Newly published themes are downloaded within one interval.
- Themes that no longer match your configuration, or that were removed upstream, are deleted.
- Files whose content changed upstream are re-downloaded automatically.
- **Multi-season shows are fully covered.** AnimeThemes groups whole franchises, so each candidate
  is verified to be an actual season of *your* show — by format, title and premiere year, then
  confirmed against AniList's prequel/sequel relations. Spin-offs, movies and remakes stay out.
- Files the plugin did not create are never touched.

Setting a fetch type to *Don't download* removes existing themes of that type again.

## Playback order

Theme files are numbered so openings sort first:

```
0-01_…  0-02_…   ← openings
1-01_…           ← insert songs
2-01_…  2-02_…   ← endings
```

That is the order you get in theme **lists** and in clients that keep list order. Jellyfin's
*alphanumeric sorting* setting makes the numbers compare numerically (`0-02` before `0-10`).

> **Note:** the Jellyfin **web client shuffles background themes on purpose** — it requests theme
> media with `sortBy=Random`, and with more endings than openings in a library, endings win the
> shuffle more often. A server plugin cannot change that request.

If you want ordered OP-first playback in the web client, either:

- **Install the userscript** — [jellyfin-ordered-theme-playback.user.js](https://raw.githubusercontent.com/MrGameVlogger/jellyfin-plugin-animethemes/main/jellyfin-ordered-theme-playback.user.js)
  (open the link with a userscript manager such as Tampermonkey installed; it updates itself).
  It strips the shuffle parameter from theme requests only — see its header comment to restrict it
  to your own host.

- **Or keep it server-side simple**: enable *Ignore ED themes* for **videos** only — the web client
  prefers background theme videos when they exist, so the random pool becomes openings only.

## Configuration

Audio and video are configured separately — and again separately for series and for movies.

| Setting (as shown in the UI) | What it does |
|---------|--------------|
| Degree of Parallelism | Items processed at once. Default `1` goes easy on the servers; higher values can trip the CDN's rate limit (retries and the next run recover). |
| Included / Excluded Libraries (Regex) | Case-insensitive library name filters. |
| Fetch type | *All themes* (every theme and each of its versions — shows can use several cuts of one OP/ED), *Single theme*, or *Don't download* (which also removes existing themes of that type). |
| Ignore themes that overlap with episode | Drops themes that blend into episode footage. |
| Ignore themes that contain credits | Drops themes with credits in them — useful for background video. |
| Ignore OP themes / Ignore ED themes | Limit which kinds of themes are considered. |
| Normalize loudness | Keeps themes at a consistent loudness relative to dialogue (ffmpeg `loudnorm`) instead of a fixed volume. |
| Volume | Fixed volume baked into the files. `0` mutes videos entirely. Ignored when loudness normalization is on. |

## Where the files live

Theme files live next to the media they belong to — there is no configurable location:

- theme songs — `<series or movie folder>/theme-music/*.mp3`
- theme videos — `<series or movie folder>/backdrops/*.webm`

File names encode the theme order, the processing settings and the upstream content size
(e.g. `0-01_Show-OP1__50__12345678-other.webm`) — that is how the plugin knows what to keep, refresh
or remove. The `-other` suffix marks the files as extras for tools like Sonarr and Radarr, so they
never mistake theme media for episodes or movies. Files placed in these folders by hand are never
touched.

## Troubleshooting

**Nothing downloads** — themes are matched by the *AniDB* provider id on each series/movie. If the
task log says `Skipping N of M item(s) without an AniDB provider id`, install the
[AniDB plugin](https://github.com/jellyfin/jellyfin-plugin-anidb) and refresh your metadata so the
IDs appear (check the item's metadata IDs in the dashboard).

**"AnimeThemes has no themes for this anime yet"** — the show exists on AnimeThemes but has no
published themes yet; they are picked up automatically within one task interval.

**Many `Download failed … 503` errors** — the AnimeThemes CDN rate-limits bursts. Downloads are
retried automatically and anything missed is fetched on the next run; keep the degree of
parallelism low for big first-time syncs.

**Themes play in the wrong order in the web client** — see [Playback order](#playback-order); the
web client shuffles background themes by design.

## Development & releasing

Versions follow `major.minor.patch.0` (e.g. `7.0.1.0`), and the git tag matches the version exactly.
Everything lands through a pull request — `main` requires one (admins can bypass).

1. Bump `Version`, `AssemblyVersion` and `FileVersion` in `Directory.Build.props`, `version` in
   `build.yaml`, and set `build.yaml`'s `changelog` to the release notes. Keep `targetAbi` at
   `12.0.0.0` so the plugin stays installable on every Jellyfin 12.x server. Open a PR and merge it.
2. Create a GitHub release tagged with the version (e.g. `7.0.2.0`) and paste the changelog as the
   release notes.
3. The publish workflow builds the plugin, attaches the zip to the release, and opens a PR with the
   `manifest.json` update (checksum included) — merge it. Never edit `manifest.json` by hand.

More detail for agents and contributors: [AGENTS.md](AGENTS.md) · session history: [HANDOFF.md](HANDOFF.md)

## Other notes

Want theme videos to cover the whole screen? Try this custom CSS:

```css
body:not(.hide-scroll) > .videoPlayerContainer > video {
    object-fit: cover;
}
```

---

Originally created by [EusthEnoptEron](https://github.com/EusthEnoptEron).
