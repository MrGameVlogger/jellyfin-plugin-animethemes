# AnimeThemes for Jellyfin

Plugin for [Jellyfin](https://jellyfin.org/) that fetches anime themes (OPs and EDs)
from [animethemes.moe](https://animethemes.moe). Both audio files and video files are supported.

**An AniDB ID is used to find matching themes, so you'll need to install the
[AniDB plugin](https://github.com/jellyfin/jellyfin-plugin-anidb). All other series are ignored.**

## Compatibility

| Jellyfin | Plugin |
|----------|--------|
| 12.0 or newer | v7 or newer |
| 10.11.x | [v6](https://github.com/EusthEnoptEron/jellyfin-plugin-animethemes/releases/tag/6) (final release for 10.11) |

## How to use

The plugin creates a new scheduled task ("Search For Anime Themes") that runs **every 12 hours** by
default; you can change the interval in Dashboard → Scheduled Tasks. Every run keeps your themes in
sync with AnimeThemes:

- Newly added themes are downloaded, within one interval of appearing on AnimeThemes.
- Themes that no longer match your configuration — or that were removed upstream — are deleted.
- Files whose content changed upstream are re-downloaded (the source size is part of the file name,
  so a changed file counts as a new file).
- **Multi-season shows are fully covered**: themes are collected from every season of the series and
  any Jellyfin seasons that carry their own AniDB id. AnimeThemes series groups are franchise-wide,
  so siblings are filtered to the same show (format/title/year) and then verified against AniList
  relations — seasons are prequel/sequel-linked to the show, while spin-offs, movies and remakes
  are not.
- **Openings play first**: file names are prefixed with the theme order (e.g. `0-01_` for the first
  OP, `2-03_` for the third ED), so Jellyfin's alphabetical theme playback runs OPs before EDs.
- Files the plugin did not create are never touched.

Setting a fetch type to *Don't download* removes existing themes of that type again. Also make sure
to enable theme music / videos in your display settings.

## How to install

### Plugin Manager

1. Add the following repository to your plugin repositories:
   https://raw.githubusercontent.com/MrGameVlogger/jellyfin-plugin-animethemes/main/manifest.json
2. Find the plugin in the catalog and install it.

### Manually

1. [Grab the latest build.](https://github.com/MrGameVlogger/jellyfin-plugin-animethemes/actions/workflows/build.yaml)
2. Download it into your plugins folder [as described here](https://jellyfin.org/docs/general/server/plugins/).

## Configuration

Audio and video settings are configured separately, and again separately for series and movies.

| Configuration                           | Description                                                                                               |
|-----------------------------------------|-----------------------------------------------------------------------------------------------------------|
| Degree of parallelism                   | How many items should be downloaded in parallel. Default is set to 1 to go easy on the servers. Higher values can hit the AnimeThemes CDN's rate limit; failed downloads are retried automatically and picked up on the next run. |
| Included libraries (regex)              | Only libraries matching this regex are searched for anime themes. Case-insensitive.                       |
| Excluded libraries (regex)              | Matching libraries are ignored even if they match the include regex. Leave empty to ignore.               |
| Ignore themes that overlap with episode | Some shows transition into their themes. Use this setting to filter those themes out.                     |
| Ignore themes with credits              | Whether to skip themes with credits in them. Useful for video downloads.                                  |
| Ignore OP themes                        | Use this to control whether OPs are considered.                                                           |
| Ignore ED themes                        | Use this to control whether EDs are considered.                                                           |
| Fetch type                              | Determines if you want all distinct themes, only the best match, or none at all.                          |
| Normalize loudness                      | Keeps themes at a consistent loudness relative to dialogue (ffmpeg loudnorm) instead of a fixed volume.   |
| Volume                                  | The desired volume, which will be baked in. For videos, 0 will result in the audio channel being removed. Ignored when loudness normalization is enabled. |

## Where the files are stored

Theme files live next to the media they belong to — there is no configurable location:

- theme songs: `<series or movie folder>/theme-music/*.mp3`
- theme videos: `<series or movie folder>/backdrops/*.webm`

The file names encode the theme order, the processing settings and the upstream content size
(e.g. `0-01_Show-OP1__50__12345678.webm`), which is how the plugin knows what to keep, refresh or
remove. Files placed in these folders by hand are never touched.

## Troubleshooting

- **Nothing downloads**: the plugin matches themes by the *AniDB* provider id on each series/movie. If
  the task log says `Skipping N of M item(s) without an AniDB provider id`, install the
  [AniDB plugin](https://github.com/jellyfin/jellyfin-plugin-anidb) and refresh your metadata so the
  ids appear (check the item's metadata ids in the dashboard).
- **"AnimeThemes has no themes for this anime yet"**: the show has an AnimeThemes entry but no
  published themes yet — they are picked up automatically within one task interval.
- **Many `Download failed ... 503` errors**: the AnimeThemes CDN rate-limits bursts; downloads are
  retried automatically and anything missed is fetched on the next run. Keep the degree of
  parallelism low for large first-time syncs.

## Releasing

Versions follow `major.minor.patch.0` (e.g. `7.0.1.0`); the git tag matches the version exactly.

1. Bump `Version`, `AssemblyVersion` and `FileVersion` in `Directory.Build.props` and `version` in
   `build.yaml`, and set `build.yaml`'s `changelog` to the release notes bullets.
   Keep `targetAbi` at `12.0.0.0` so the plugin stays installable on all Jellyfin 12.x servers.
2. Create a GitHub release with the version as the tag (e.g. `7.0.2.0`) and paste the changelog
   bullets as the release notes.
3. The publish workflow builds the plugin, attaches the zip to the release and updates `manifest.json`
   (checksum included) automatically.

## Other notes

If you want the videos to cover the whole screen, try the following CSS:

```css
body:not(.hide-scroll) > .videoPlayerContainer > video {
    object-fit: cover;
}
```

---

Originally created by [EusthEnoptEron](https://github.com/EusthEnoptEron).
