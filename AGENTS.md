# AGENTS.md

Jellyfin plugin (C#/.NET 10) that downloads anime OP/ED themes from AnimeThemes into
`theme-music/` / `backdrops/` next to each series/movie. Fork of `EusthEnoptEron/jellyfin-plugin-animethemes`;
issues/PRs from upstream are a useful source of feature ideas (many already implemented here).

## Commands

```sh
dotnet build Jellyfin.Plugin.AnimeThemes.sln -c Release     # 0 warnings: TreatWarningsAsErrors + AllEnabledByDefault
dotnet format Jellyfin.Plugin.AnimeThemes.sln --verify-no-changes
```

- Requires the **.NET 10 SDK** (`net10.0`). On macOS it may live in `~/.dotnet` and not be on `PATH`.
- There is **no unit test suite** (`test.yaml` only builds). Verify behavior against the live
  AnimeThemes GraphQL API (`https://graphql.animethemes.moe/`, POST to `/`) with a scratch console
  app that references the plugin project. Useful probes: introspect `__type(name: "...")` for schema
  details; call `findAnimeByExternalSite` with real AniDB ids (e.g. `69` = One Piece, a worst-case
  franchise) and assert deserialization, series expansion and `size` binding.
- **Before pushing**: build + `dotnet format` clean, then let CodeQL run on the push and confirm
  **0 open code-scanning alerts** (GitHub MCP tools can list them from the session). We have been
  bitten by `cs/catch-of-all-exceptions` reappearing after refactorings — re-check after any change
  to catch blocks.
- Note: build/test/CodeQL workflows `paths-ignore` `**/*.md`, so docs-only pushes are CI-silent.

## Architecture

Single project (`Jellyfin.Plugin.AnimeThemes`), no test project. Execution flow:

1. `Tasks/ScheduledThemeSearchTask` (`IScheduledTask`, auto-discovered by Jellyfin — no DI
   registration needed; 12 h interval trigger) → `BaseThemeSearchTask.FindAndProcessAsync`.
2. Libraries are filtered by include/exclude **regexes** from config against library names
   (`GetApplicableItems`), then `Series`+`Movie` items are queried (`InternalItemsQuery`,
   `IsVirtualItem = false`, `Recursive = true`, scoped to the matched libraries).
3. Items are chunked (50) into `AnimeThemesDownloader.ResolveItems`:
   - collects the item's AniDB provider id (`TryGetProviderId("AniDB", …)`, **case-insensitive**
     dictionary — any casing works) and any **child seasons'** AniDB ids
   - `AnimeThemesApi.FindByAniDbId` batches ids (25/request) and expands each match with its
     AnimeThemes `series → anime` siblings that are **seasons of the same show**. The series
     grouping is franchise-wide (spin-offs, movies, remakes — remakes share the exact title, only
     the year separates them), so siblings pass two gates: `IsSameShow` (same format + title match
     after stripping season markers + premiere year within 10) and `AnimeRelations` (AniList
     prequel/sequel reachability — seasons chain through these; `SPIN_OFF`/`ALTERNATIVE` do not).
     AniList ids come from the AnimeThemes resources; relations are cached in
     `animethemes-relations.json` beside the plugin config and unknown data falls back to the
     title heuristic
   - yields per-item theme pools (`ItemWithAnime`) covering all matched anime
4. `AnimeThemesDownloader.HandleAsync` processes items through `Parallel.ForEachAsync`
   (parallelism config, clamped ≥ 1): theme selection (`GetBestThemes` filters + `Rate()`
   scoring, `DistinctBy(Theme.Id)`), then per media type the file lifecycle — rename-migration,
   download (ffmpeg), cleanup — and `RefreshMetadata` when anything changed.

Other pieces:

- `AnimeThemesApi.cs` — GraphQL client. `findAnimeByExternalSite(site: ANIDB, id: [Int!])` is
  purpose-built for this plugin; the JSON:API it used to call is **deprecated and will be
  removed** — do not use it. GraphQL errors arrive as HTTP 200 + `errors[]`.
- `AnimeRelations.cs` — AniList relation lookups (prequel/sequel graph) used to tell seasons from
  spin-offs; batched 15 ids/request with 500 ms pacing because AniList rate-limits by query cost.
- `PluginServiceRegistrator.cs` — DI; two named HttpClients (`AnimeThemes` for the API,
  `AnimeThemesMedia` for CDNs), both behind `PollyResilienceHandler` (retries 408/429/5xx,
  honors `Retry-After`). `AnimeThemesDownloader` is a **singleton** (it is captured by the
  singleton scheduled task).
- `Models/` — GraphQL DTOs, **one type per file** (StyleCop SA1402). Everything binds via
  `JsonPropertyName`; GraphQL connections arrive as `{ nodes: [...] }` wrappers
  (`ResourceConnection`, `VideoConnection`, `SeriesConnection`, `AnimeConnection`).
- `Configuration/configPage.html` — legacy Jellyfin web config page (emby-* elements, `ApiClient`
  globals). This is still the supported mechanism on Jellyfin 12; keep that style.

## Models & GraphQL shape gotchas

- `Anime.title` is a nested `AnimeTitle`; display name = `title.romaji` (see `Anime.Name`).
- Enums use `[JsonConverter(typeof(JsonStringEnumConverter))]` on the enum type itself. The API's
  `VideoOverlap` values are `NONE`/`TRANS`/`OVER` — `OverlapType.Transition` is aliased to `TRANS`
  via `[JsonStringEnumMemberName("TRANS")]`; `ThemeType` gained `IN` (insert songs).
- Nullable per schema even though C# types say otherwise: `Video.Overlap`, `Resource.ExternalId`,
  `Video.Audio` — handle nulls (a missing `audio` means the theme is skipped, not crashed).
- `Models.Video` vs `MediaBrowser.Model.Entities.Video` collide — the downloader aliases
  `Video = Jellyfin.Plugin.AnimeThemes.Models.Video` (and `MediaType`). Do not remove the aliases.

## Theme file naming is the state machine

`{order}_{name}__{marker}__{size}.ext` in `theme-music/` (`.mp3`) or `backdrops/` (`.webm`):

- `order` = theme-type rank + sequence (`0-01_` = first OP … `2-03_` = third ED) so Jellyfin's
  name-sorted theme playback runs openings first
- `marker` = `n` (loudnorm mode) or volume×100
- `size` = upstream byte size → a changed file is a *new* file (auto-refresh)

Existence of the wanted name means "current"; `CleanDirectory` deletes only names matching
`PluginThemeFilePattern` (`^.+__(?:n|\d+)(?:__\d+)?(?:\.tmp)?\.(?:mp3|webm)$`) — hand-made files
are never touched. **Any naming change must update the regex, `MigrateLegacyName` (rename-based
migration, keyed on the `\d+-\d+_` prefix), and `PluginThemeFilePattern` together**, or upgrades
will re-download or mis-attribute files. ffmpeg writes to a `*.tmp.*` name and is `File.Move`d
into place on success only — a partial file must never count as valid.

## Config surface (PluginConfiguration)

- `DegreeOfParallelism` (1–8, clamped ≥ 1), `IncludeLibraries` / `ExcludeLibraries` (regexes),
  and per-media-type `MediaTypeConfiguration`: `FetchType` (None/Single/All), ignore flags,
  `Volume` (baked in), `NormalizeLoudness` (ffmpeg `loudnorm` instead of fixed volume).
- Series use the **global** Audio/Video settings; Movies use `MovieSettings` (a separate
  `CollectionTypeConfiguration`) — see `HandleAsync`.
- The config page posts numbers as strings; Jellyfin's `JsonDefaults` (`AllowReadingFromString`,
  string enums) coerces them. `FetchType` values are the enum names (`None`/`Single`/`All`).

## API etiquette (AnimeThemes)

- **90 requests/min**; query depth ≤ 13; complexity ≤ 10 000 (their words: experimental). Series
  expansion makes queries heavier — keep batch sizes sane and verify worst cases (One Piece, AniDB
  `69`) against the live API when changing query shape.
- A **User-Agent is required** (403 without one) — use the product token set in
  `PluginServiceRegistrator`.
- The media CDNs (`v./a.animethemes.moe`) shed parallel load with **503 bursts** — downloads are
  retried (Polly, honors `Retry-After`) and paced through a global `DownloadGate` (500 ms).

## Patterns that are load-bearing (learned the hard way)

- Never `Path.Combine` with untrusted relative paths — use `CombineRelative` (CodeQL
  `cs/path-combine`; rooted input must throw).
- Never an unfiltered `catch (Exception)` — CodeQL `cs/catch-of-all-exceptions` flags it. The
  house pattern distinguishes user cancellation from timeouts (a `TaskCanceledException`!):

  ```csharp
  catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
  ```
- Cleanup runs **after** downloads (a failed download must not delete existing themes) and its
  deletions count as "changes" so metadata refreshes.
- Disposables get `using` (`cs/local-not-disposed`): ffmpeg `Process`, `SemaphoreSlim`.
- Workflow `uses:` pins are commit SHAs with version comments (CodeQL `actions/unpinned-tag`);
  every job declares `permissions:` (`actions/missing-workflow-permissions`).

## CI & GitHub

7 workflows (`build`, `test`, `scan-codeql` C#, `scan-codeql-actions`, `dependency-review`,
`sync-labels`, `publish`) plus Dependabot (`.github/dependabot.yml`: weekly nuget +
github-actions, `dependencies`/`nuget`/`github-actions` labels). Renovate was removed to avoid
duplicate PRs. Workflows are fork-aware (`${{ github.repository }}` gates) and call reusable
`jellyfin/jellyfin-meta-plugins` workflows at SHA pins.

`publish.yaml` runs on GitHub release: JPRM builds the zip → assets (+md5/sha256) go to the
release → `jprm repo add ./manifest.json <zip> --plugin-url …` updates `manifest.json`
(checksum, newest-first order) and commits it. `manifest.json` is machine-maintained: never
hand-edit entries.

## Releasing (see README "Releasing" for the full flow)

- Version = `major.minor.patch.0`, set in **both** `Directory.Build.props` (3 fields) and
  `build.yaml` (`version:`). Git tag == version exactly (`7.3.1.0`).
- Keep `targetAbi: "12.0.0.0"` — it is the minimum server version, not the current one.
- `build.yaml` `changelog:` becomes the manifest entry text at release time.
- Release = `gh release create <version> --target main --title <version> --notes '...'`; the
  publish workflow does the rest. Never re-cut a published version — ship a patch bump.
- jprm gotchas when dry-running locally: run **from the repo root** (it finds `build.yaml`
  there), `python -m jprm plugin build . --dotnet-framework net10.0 --output <existing dir>`,
  and `repo add` needs `./manifest.json` (a bare `manifest.json` fails its path check).

## Log vocabulary (users quote these in bug reports)

- `Skipping N of M item(s) without an AniDB provider id` → AniDB plugin missing / metadata not refreshed.
- `{Type}: AnimeThemes has no themes for this anime yet` → series exists upstream, no OP/EDs published.
- `{Type}: none of the N available theme video(s) match the configured filters` → relax ignore flags.
- `Download failed … 503` → CDN rate limiting; retries + next run recover (keep parallelism low).
