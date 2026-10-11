# AGENTS.md

Jellyfin plugin (C#/.NET 10) that downloads anime OP/ED themes from AnimeThemes into
`theme-music/` / `backdrops/` next to each series/movie. Fork of `EusthEnoptEron/jellyfin-plugin-animethemes`.

## Commands

```sh
dotnet build Jellyfin.Plugin.AnimeThemes.sln -c Release     # 0 warnings: TreatWarningsAsErrors + AllEnabledByDefault
dotnet format Jellyfin.Plugin.AnimeThemes.sln --verify-no-changes
```

- Requires the **.NET 10 SDK** (`net10.0`). On macOS it may live in `~/.dotnet` and not be on `PATH`.
- There is **no unit test suite**. Verify behavior against the live AnimeThemes GraphQL API
  (`https://graphql.animethemes.moe/`, POST to `/`) with a scratch console app referencing the
  plugin project. `test.yaml` CI only builds.
- **Before pushing**: build + `dotnet format` clean, then let CodeQL run on the push and confirm
  **0 open code-scanning alerts**. GitHub MCP tools can list alerts without leaving the session.

## Architecture

- `AnimeThemesApi.cs` — GraphQL client. `findAnimeByExternalSite(site: ANIDB, id: [...])` batches
  lookups (25 ids/request) and expands each match with its AnimeThemes `series → anime` siblings
  (a show's seasons). The old JSON:API is **deprecated** — do not use it.
- `AnimeThemesDownloader.cs` — the whole file lifecycle: item/season AniDB id harvesting, theme
  selection (filters + `Rate()` scoring), download via ffmpeg, rename-migration, cleanup.
- `Tasks/` — `IScheduledTask`, 12 h interval, chunks of 50 items through `Parallel.ForEachAsync`.
- `PluginServiceRegistrator.cs` — DI; two named HttpClients (`AnimeThemes`, `AnimeThemesMedia`),
  both behind `PollyResilienceHandler`.
- `Models/` — GraphQL DTOs (one type per file; SA1402). Enums use `JsonStringEnumConverter`;
  `OverlapType.Transition` maps to API value `TRANS` via `JsonStringEnumMemberName`.
- `Configuration/configPage.html` — legacy Jellyfin web config page (emby-* elements, `ApiClient`
  globals). This is still the supported mechanism on Jellyfin 12; keep that style.

## Theme file naming is the state machine

`{order}_{name}__{marker}__{size}.ext` in `theme-music/` (`.mp3`) or `backdrops/` (`.webm`):

- `order` = theme-type rank + sequence (`0-01_` = first OP … `2-03_` = third ED) so Jellyfin's
  name-sorted theme playback runs openings first
- `marker` = `n` (loudnorm mode) or volume×100
- `size` = upstream byte size → a changed file is a *new* file (auto-refresh)

Existence of the wanted name means "current"; `CleanDirectory` deletes only names matching
`PluginThemeFilePattern` (`^.+__(?:n|\d+)(?:__\d+)?(?:\.tmp)?\.(?:mp3|webm)$`) — hand-made files
are never touched. **Any naming change must update the regex and the rename migration
(`MigrateLegacyName`) together**, or upgrades will re-download or mis-attribute files.

## API etiquette (AnimeThemes)

- **90 requests/min**; query depth ≤ 13; complexity ≤ 10 000 (their words: experimental).
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
- ffmpeg output goes to a temp name and is `File.Move`d into place on success only — a partial
  file must never be mistaken for a valid theme.
- Cleanup runs **after** downloads (a failed download must not delete existing themes) and its
  deletions count as "changes" so metadata refreshes.
- Workflow `uses:` pins are commit SHAs with version comments (CodeQL `actions/unpinned-tag`);
  every job declares `permissions:` (`actions/missing-workflow-permissions`).

## Releasing (see README "Releasing" for the full flow)

- Version = `major.minor.patch.0`, set in **both** `Directory.Build.props` (3 fields) and
  `build.yaml` (`version:`). Git tag == version exactly (`7.3.1.0`).
- Keep `targetAbi: "12.0.0.0"` — it is the minimum server version, not the current one.
- `build.yaml` `changelog:` becomes the manifest entry text at release time.
- Release = `gh release create <version> --target main --title <version> --notes '...'`; the
  publish workflow then builds (JPRM), attaches the zip, and **auto-updates `manifest.json`**
  (checksum + newest-first order) via `jprm repo add`. Never hand-edit manifest entries; never
  re-cut a published version — ship a patch bump instead.
