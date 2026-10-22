# Session handoff — 2026-10-11/12

Everything one long session did to this repo (a Jellyfin plugin), so a new session can pick up
without re-deriving anything. **Standing rules live in `AGENTS.md`, user docs in `README.md`** —
this file is the session record: what changed, what was discovered, what is deliberately left.

## Current state

- **`7.3.6.0` released** and verified (release assets + `manifest.json` checksum match, automation
  green). `7.3.5.0`/`7.3.6.0` landed after the 2026-10-11/12 session: failure-mode hardening — one
  bad item, unreadable folder, failed download or unusable API payload can no longer abort a run or
  delete existing themes.
- `main` == remote, all workflows green, **CodeQL: 0 open alerts**, dependencies current (Dependabot).
- Plugin: Jellyfin **12.x** only (`targetAbi 12.0.0.0`, `net10.0`, Jellyfin.Controller/Model **12.2.0**).
  Jellyfin 10.11 users stay on upstream's v6.
- This is a **fork**: `MrGameVlogger/jellyfin-plugin-animethemes` (upstream `EusthEnoptEron/…`).
  Upstream's open issues/PRs were triaged; most were solved here (see §6).

## 1. What the plugin does (5 lines)

A scheduled task (12 h) finds Series/Movie items with **AniDB** provider ids, queries the
**AnimeThemes GraphQL API** for each show's OP/ED themes (plus its seasons), downloads them via
ffmpeg into `theme-music/` (`*.mp3`) and `backdrops/` (`*.webm`) next to the media, and keeps those
files in sync with the API forever. Themes are Jellyfin's stock "theme music / theme videos" feature.

## 2. Releases made this session (oldest → newest)

| Release | What it did |
|---|---|
| `7` → re-cut as `7.0.0.0` | Jellyfin 12 / .NET 10 migration; first bug-fix batch (see §3) |
| `7.0.1.0` | Type-aware download log messages |
| `7.0.2.0` | "Enforce conformity" actually re-checks themed items; dropped Release Drafter drafts |
| `7.0.3.0` | File names carry the upstream **size** → replaced content is re-downloaded ("newest files") |
| `7.1.0.0` | **Sync always on** (option removed) + safety fixes (§3) + loudnorm + volume read-out + skip diagnostics + download pacing |
| `7.1.1.0` | Media downloads retried (CDN 503 bursts); timeouts no longer abort runs |
| `7.2.0.0` | Upstream issue batch: loudness normalization, numeric volume, skip diagnostics, pacing |
| `7.3.0.0` | Multi-season collection + OP-first ordering prefixes |
| `7.3.1.0` | Hardened ordered-name migration; lookup batching 25 ids/req |
| `7.3.2.0` | Franchise filtering: spin-offs/movies/remakes excluded (AniList relations) |
| `7.3.3.0` | Unique theme numbering; documented that the web client shuffles playback |
| `7.3.4.0` | `-other` extra suffix — Sonarr/Radarr no longer match theme media as episodes/movies |

Versioning: `major.minor.patch.0`, **git tag == version** (`7.3.4.0`). The old re-cut-forever habit
was replaced by patch releases at the user's request.

## 3. Bugs found & fixed (the full list)

**Pre-existing, found by review during migration:** ResolveItems dropped all but the first item per
AniDB id; progress bar never reached 100 %; `int.Parse` crash on malformed AniDB ids; ffmpeg stdout
redirected but never drained (deadlock risk); null-`audio` NRE; unused `IUserManager` param;
`DegreeOfParallelism = 0` crash (UI allowed it!); invalid include/exclude regex crash; null library
name crash; CRLF line endings (`.editorconfig` says LF); downloader registered scoped but captured
by a singleton task.

**Safety bugs found while making sync always-on:** cleanup ran *before* downloads (a failed download
deleted existing themes); ffmpeg wrote straight to the final path (a crash left a corrupt file that
counted as valid forever); deletions didn't trigger metadata refresh; cleanup deleted *any*
mp3/webm (hand-made files died); one locked file aborted the run; `RefreshMetadata` failure aborted
the run.

**Found in live logs (user pasted runs):** download timeouts surfaced as `TaskCanceledException`
and aborted whole runs (fixed while keeping real cancellation propagating); CDN 503 bursts killed
every download because the media client had **no retry policy** (only the API client did);
misleading `AniDB=` log label (printed the AnimeThemes id).

**Found by CodeQL (twice):** `cs/path-combine` (5×), `cs/local-not-disposed` (2×),
`cs/catch-of-all-exceptions` (1× then 3 regressions later), `actions/unpinned-tag` (5×),
`actions/missing-workflow-permissions` (2×). House patterns are in `AGENTS.md`; **always re-check
CodeQL alerts before/after pushing** — the catch-filter regression bit us twice.

**Found in the naming/sorting work:** per-anime `sequence` in the order prefix produced duplicate
numbers across seasons and `0-00` for null sequences; the ordered-name migration could in a corner
case steal another theme's file (now identity-based + wanted-set guard).

**User-reported (Sonarr/Radarr):** theme videos in `backdrops/` entered *arr disk scans and matched
as episodes/movies. Fixed with the `-other` extra suffix (their `ExcludedExtraFilesRegex` skips
`-(other|trailer|…)` names); Jellyfin is unaffected because `ExtraRuleResolver` classifies by folder
first. Existing files are renamed in place via the identity migration.

## 4. Major discoveries (the "why" behind the design)

1. **AnimeThemes JSON:API is deprecated ("will be removed")** → plugin uses the **GraphQL** API at
   `https://graphql.animethemes.moe/` (`POST /`). `findAnimeByExternalSite(site: ANIDB, id: [Int!])`
   is purpose-built for this use case. Limits: 90 req/min, depth ≤ 13, complexity ≤ 10 000.
   User-Agent required (403 without). The media CDN (`v./a.animethemes.moe`) sheds parallel load
   with **503s** — hence Polly retries + a global download gate.
2. **AniDB's own relation API is unusable** (client registration → `error 302`; 1 req/5 s; UDP needs
   user credentials). **AniList** has the same relation graph and its ids ride along in the
   AnimeThemes `resources` → `AnimeRelations.cs` uses it (15 ids/req, 500 ms pacing — it rate-limits
   by query *cost*, big batched queries 429 quickly), cached in `animethemes-relations.json`.
3. **AnimeThemes `series` groups are franchise-wide**, not seasons — `.hack//Sign` pulled in
   `.hack//Roots`, One Piece its movies. Fix = **relations AND heuristic** (AniList calls
   franchise-sequels `SEQUEL` too, so title/format/year matching is still needed as a first gate).
   Remakes (Fruits Basket 2001/2019) share the exact title — only the year separates them.
4. **jellyfin-web shuffles background themes on purpose**: `themeMediaPlayer.js` requests
   `sortBy: [ItemSortBy.Random]` and the server shuffles (`RandomComparer`). No supported
   server-side lever exists (shadowing the Random comparer via DI would break Shuffle everywhere),
   and the core hook that would let plugins patch served web files — PR jellyfin/jellyfin#9095
   (`IWebFileTransformationWriteService`) — was **closed unmerged**. (The FileTransformation plugin
   works around it by mutating web files on disk; too invasive for this plugin.)
   "Favors the ending" = pools contain more EDs than OPs. `AGENTS.md`/`README.md` document a
   installable userscript (`jellyfin-ordered-theme-playback.user.js` in the repo root) + the
   `IgnoreEDs`-on-video trick.
5. Theme extras from `theme-music/`/`backdrops/` **keep their filename as their Name** (folder-rule
   extras are not renamed to "Theme Song 2"), so name sorting works — Jellyfin's *alphanumeric
   sorting* zero-pads digit chunks (so `0-02` < `0-10`).
6. **`jprm`** maintains `manifest.json` (checksum from the actual zip, newest-first). Gotchas: run
   from repo root (`build.yaml` discovery), `--output` dir must pre-exist, `repo add` needs
   `./manifest.json` (bare name fails `RepoPathParam`) — that last one failed release 7's first
   publish run.
7. **Fork traps**: reusable workflows were gated to the *upstream* repo name (silently no-op'd),
   CodeQL pointed at jellyfin-plugin-template (never ran), slash-command workflows ship a broken
   `token: .` (deleted). Default `GITHUB_TOKEN` is read-only → workflows needed `permissions:`.
8. **`System.Linq.Async` clashes with .NET 10** (built-in `System.Linq.AsyncEnumerable`) — removed;
   async LINQ is now BCL. Polly floor is 8.7.0+ via Jellyfin 12.2's dependency graph.

## 5. The theme file name is the state machine

`{order}_{name}__{marker}__{size}-other.ext` in `theme-music/` (`.mp3`) or `backdrops/` (`.webm`):

- `order` = per-type 1-based pool counter in (year, sequence, name) order — `0-01`, `0-02` …
  openings, `1-…` insert songs, `2-…` endings (unique; never the raw per-anime sequence)
- `marker` = `n` (ffmpeg `loudnorm`) or volume×100
- `size` = upstream byte size → replaced content is a *new* file
- `-other` = extra-file suffix for Sonarr/Radarr (they skip `-(other|trailer|…)` files in disk
  scans); Jellyfin classifies by folder, so theme detection is unaffected

Existence = "current"; `CleanDirectory` deletes only `PluginThemeFilePattern` matches (hand-made
files survive); `MigrateLegacyName` renames old prefixes by **identity** (name+marker+size, with
the `\d+-\d+_` prefix and the `-other` suffix stripped).
**Any naming change must update the regex, `MigrateLegacyName` and `PluginThemeFilePattern`
together.** ffmpeg output goes to `*.tmp.*` and is renamed on success only.

## 6. Upstream issue triage (EusthEnoptEron/jellyfin-plugin-animethemes)

Solved here: **#47** (`System.Linq.Async` crash), **#46** (checksum mismatch — structurally fixed by
jprm; its "gentler schedule" ask is covered by batching+pacing), **#22/#19** (silent no-ops — the
`Skipping N items without AniDB id` / `no themes yet` diagnostics), **#20** (volume precision +
loudnorm), **#4** (loudnorm instead of fixed volume), **#16** (OP-before-ED numbering), **#15**
(where files live — documented), **#8** (no full scans — per-item refresh). **PR #48** (JF12 compat)
is superseded by our work.

Still open as future candidates: **#18** theme videos as playable "extras" (hardlinks into
`extras/`, needs matching cleanup rules), **PR #49** (Shoko/Shokofin theme placement — worth porting
if the user ever uses Shoko; it is built against a diverged codebase), **#12** (song-title hover —
web-UI feature, out of scope).

## 7. Verification methodology (how claims were/are tested)

- **No unit tests exist.** A throwaway console app (`/tmp/gqltest`, recreate if gone) references the
  plugin project and runs against **live APIs**. It needs `PackageReference Jellyfin.Common` (the
  plugin declares `ExcludeAssets=runtime`, so Jellyfin DLLs aren't in outputs) and a fake
  `IApplicationPaths` + name-routing `IHttpClientFactory` ("AnimeThemes" vs "AniList").
- Current suite covers: enum aliases (`TRANS`, `IN`), live `findAnimeByExternalSite` round-trip,
  **franchise filtering cases** (.hack 4→1, One Piece 5→3, Re:Zero 10→5, Fruits Basket remake out,
  Mushoku 6 incl. its same-AniDB-id special, **SAO spin-off *Alternative GGO* out** via AniList
  `SPIN_OFF`, Log Horizon subtitle season kept via `SEQUEL` chain), `size` binding, unknown-id
  handling, and Polly retry with a replayable POST body.
- API probes: write query JSON to a file (avoids shell-escaping bugs) and `curl --data @file` with
  the product UA. Worst cases used as probes: **One Piece (AniDB 69)** for complexity, Fruits Basket
  `14490` vs `34` for remakes.
- **CodeQL must show 0 open alerts** after every push (GitHub MCP tools can check from the session).

## 8. Known limitations / accepted trade-offs

- Background playback in the **web client is shuffled by design** — documented; the installable
  `jellyfin-ordered-theme-playback.user.js` (repo root) strips `sortBy=Random` in the browser for
  those who want ordered playback. The plugin itself cannot change it.
- AniList lookups **fall back to the title heuristic** when data is missing (no AniList id, API
  down) — siblings are then kept if they pass format/title/year.
- Sibling filter uses `|year gap| ≤ 10` — a 11+ year gap between seasons of one show would be
  dropped (none observed; chains through intermediate seasons still connect).
- Secret-scanning **validity checks** toggle could not be enabled via API (GHAS-gated; one click in
  Settings → Code security).
- `has_projects` could not be turned off via API (classic Projects is read-only now; harmless).
- The `cs/catch-of-all-exceptions` filtered-catch pattern (`e is not OperationCanceledException ||
  !cancellationToken.IsCancellationRequested`) is load-bearing for both CodeQL and timeout
  semantics — do not "simplify" it.

## 9. If you're continuing work

1. Read `AGENTS.md` (commands, patterns, release flow) and `README.md` (user behavior).
2. Repo state check: `git status`, `gh run list`, `gh api …/code-scanning/alerts?state=open`.
3. Test harness: rebuild `/tmp/gqltest` per §7 if needed; run before/after API-touching changes.
4. Everything lands through pull requests (`main` is ruleset-protected: PR required, no force-push
   or deletion, admins may bypass). Releasing = version-bump PR (`Directory.Build.props` ×3 +
   `build.yaml` version & changelog) → merge → `gh release create <version> --target main`. Watch
   the `🚀 Publish Plugin` workflow; it opens a `manifest.json` PR to merge (never hand-edit
   entries).
5. Likely next work: §6 candidates (#18 extras, Shoko port), or user-driven fixes from their library
   logs (they paste run logs — they are gold; every real bug this session came from one).
