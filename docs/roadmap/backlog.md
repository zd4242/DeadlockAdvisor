# Backlog: evaluated and set aside

Ideas from the review that aren't scheduled. Each keeps its evidence so it can be promoted to a package (copy the template from
[README.md](README.md), give it the next WP number, and add it to the package list). Marks as in the README: **✔** verified, **(agent)** reported by an audit and not
re-checked, **(est.)** an estimate.

## B01 CI automation and workflow health

- **Patch-day game sync as a pull request.** Item, cost and stat changes from a game patch reach users only after the maintainer runs Data → Sync from Game API and publishes
  the model ✔ (README and `docs/scoring_model.md`, "run it after each patch"). New heroes already have a workflow (`new-heroes.yml`, `ModelPublisher.AddNewHeroesAsync`,
  `PublishModel --add-new-heroes`). A sibling `game-sync.yml` and `PublishModel --sync-game` would sync a temp copy of the seed and **open a pull request** with the
  `SyncReport` and a generated user-facing note ("Patch 10-07: 9 items changed …"); merging publishes (CI's `publish-model`). It must not push to `main`: a changed seed
  publishes to every install, which CLAUDE.md reserves for the user's decision.
- **Release notes.** Release bodies are only a "Full Changelog" link ✔ (`gh release view v0.3.0 --json body`), because `release.yml` uses `--generate-notes` and commits go straight
  to `main`; the in-app "What's new" and "Releases" lead there. Build notes from `git log` with `--notes-file`.
- **Workflow health.** GitHub disables scheduled workflows after 60 days without repo activity (the README says to re-enable by hand); if `match-data.yml` stops, installs fall back
  after 4 days (`MatchSnapshot.StaleAfter`) to about 560 API calls each ✔. Add a keep-alive and a watchdog that opens an issue when the snapshot's `checked_at` is stale. Consider whether
  a merely stale snapshot should keep being used, with its age shown, before an install hits the API.
- **match-data job.** `match-data.yml` turns any failed `gh release download` into "Nothing to restore: starting afresh", a full refetch ✔; retry, then fail loudly. It runs on Windows;
  the tool builds on Linux in CI already, so ubuntu is cheaper (agent). Scheduled runs started 5-8 hours after the 06:17 UTC cron (agent).
- **CI and release hygiene (agent).** `release.yml` gives `contents: write` to every job (test and build run package code); actions are tag-pinned, not SHA-pinned, and there is no
  Dependabot; the release job pushes the tag before the build succeeds, leaving an orphan tag when a build fails; the tag-push path has no on-main check. CI runs about 5 minutes with no
  TRX, hang timeout or artifacts: add `--blame-hang-timeout`, upload `TestResults` and `mockups` on failure, `paths-ignore` for docs.

## B02 Owned items and a next-purchase list

No notion of what you already have: the list is "best items for this line-up" at any point of a match ✔ (search the source for `owned`/`inventory`: nothing). Add per-match "already built"
and "hide" marks (saved beside `Focused` in `SavedMatch`) so the list answers "what next", and an optional souls-to-spend box that orders and filters by cost and tier. Net worth is
read for every hero, but not what you have spent. Presentation and filtering only: no flat bonuses in scoring.

## B03 Undo across the editors

Undo and redo exist only in Hero Traits ✔ (the buttons, `HeroTraitsViewModel`). In By Item, a rule's × deletes at once, "Copy rules from…" replaces a set, and Clear says "can't be undone";
a By Trait keystroke overwrites a value; "Clear" on the match has no confirm (agent: `ByItemViewModel`, `ByTraitViewModel`, `MatchBoardViewModel`). A shared `EditHistory` and a
"match replaced: Undo" toast.

## B04 Line-up read-out

The roster shows counts and net worth only, though scoring is trait-based. Show each side's standout traits (against `DataStore.TraitBaselines`) as chips; a chip filters the results to items
with rules on that trait. Display and filtering only.

## B05 Patch-change flags and a "what changed" page

`SyncReport.StatChanges` is shown once and discarded (agent). Persist it, mark items changed after the match data's patch (their win rates may be stale), and add a page of what moved.
Pairs with B01's generated notes.

## B06 Match history

Only `LastMatch` is kept ✔ (`AppSettings.LastMatch`). Keep about 20 matches to flip between; optionally list recent matches for the saved Steam account if the API offers it (not checked).

## B07 Item comparison

Pick two or three rows for a side-by-side of score, the per-trait "why", the data lifts and the item cards. The By Item card host is reusable (agent).

## B08 A "peek" window for in-game use

After an F9 pressed in the game (WP02 adds a sound), a non-focusable, click-through, topmost window could show the top picks for a few seconds, or a compact always-on-top window could stay up.
No hooking involved, but it doesn't show over exclusive fullscreen. Needs a setting and care not to take focus.

## B09 Structure and cleanliness

- `DataMenuViewModel` is 1,131 lines with 14 constructor dependencies ✔ (startup orchestration, match download, model update, art, sync, job runner). Split into coordinators and a shared
  `BackgroundJobHost` (`RunAsync`, `RunBehindModalAsync`, `RunInBackgroundAsync`, `Show`, `Remove`, `Succeeded`, `Failed`, `Launch`); start with partial classes. `DataMenuTests` (about 1,100
  lines of behaviour-named tests) is the safety net. Do after WP13/WP14 so the new surface isn't built twice.
- `MatchStatsMath` (1,114 lines) mixes records, API query parameters, statistics, patch-title parsing and user-facing prose built from a stringly-typed `JsonObject meta` ✔; split by concern and
  type the meta (golden risk). `GameSync` (1,004), `DataStore` (997, eleven load/save pairs), `ModelUpdateService` (479) mix roles (agent).
- `AppSettings` is 58 properties of preferences, UI state, update timestamps and caches with no schema version ✔.
- `MainWindowViewModel`: pages are index-based (adding one touches about eight places; a page registry fixes it), child view models are built with `new` beside DI, and "How scoring works"
  is a 50-line literal duplicating README and docs ✔.
- Smaller: `Launch` is `async void`, and `IsNetworkFailure` includes `InvalidOperationException` and `FormatException`, so bugs read as network failures ✔; relations are strings ("against", "as")
  in places; code used only by tests in `src` (`ItemScoring.FullMatchResults`, `TopHeroesForItem`, `MatchStatsMath.AnalyseFamily`, `DataStore.SaveAll`; agent); no analyzers or
  warnings-as-errors; `ReactiveUI.Fody` is end of life upstream (agent; check); the tools reference the whole WinExe project, so the match-data job builds the GUI.

## B10 Startup time and size

The release has no ReadyToRun (`release.pubxml`), loads settings synchronously, builds every page eagerly (agent), and carries the Excel stack (ClosedXML 1.6 MB, DocumentFormat.OpenXml
6.1 MB, SixLabors.Fonts 1.1 MB ✔ of assemblies) for a maintainer-only export. Add a stopwatch log first. Candidates: ReadyToRun, lazy page creation, moving the export to a tool.
Art decodes to full-size images that are never evicted, about 49 MB (agent; `ArtService`).

## B11 Detection: speed, coverage, unused screen data

- Warm read 324 ms (`mockups/detection_report.md`), a cold search about 5 s; per trial it copies a crop, rebuilds resampling tables, allocates and blurs in double precision; parallelism covers
  12 slots only, there is no cancel, and the bank of about 114 PNGs is reloaded on every F9 (`TemplateBank.Load`, "nothing is cached" ✔). Bit-exact optimizations validated by `VisionCorpusTests`.
- The corpus is 20 captures from one machine at 2560×1440 and one HUD scale; other scales are synthetic (agent), so "any resolution" is unproven. An "export my kept captures" flow
  (`CaptureArchive` already stores them) and ultrawide and tone-map tests would widen it. A slot's box size as a suggestion-only second cue for "you".
- Unused screen data: the game clock (the net-worth ink pipeline with a new glyph set), the lane partner (its half-brightness backplate is computed in `Detector` already), items owned
  (needs the bottom HUD and a new corpus).
- Corrupt art: `TemplateBank` catches only I/O and invalid-data errors while `Png` slices unvalidated lengths (agent); and a damaged local file is never repaired because the art refresh only asks
  "not modified".
- The top-bar vertical images (about 1.5 MB, 40 requests a week) are only a fallback for heroes with no cut portraits; dropping them needs `VisionCorpusTests` unchanged.

## B12 Self-update hardening and consent

The only check on a downloaded update is GitHub's digest from the same API response; the installer doesn't re-hash before copying, ignores a failed install and keeps no previous exe (agent;
`AppUpdateService`). An opt-in "download new versions automatically" (still installed on exit). The GitHub checks (formulas, app) run before the first-run dialog is answered ✔
(`DataMenuViewModel.OnStartup` starts the model check first): fold them into the first-run consent as a third box.

## B13 Test-suite time and flakes

About 44 cold grid searches probably dominate the suite (agent); seed `VisionGeometry` where the search isn't under test. Timing-based waits (the F9 shortcut test failed on CI twice;
`BoardMouseTests` waits 5 × 700 ms, `ShortcutTests` 500 ms, `InfoBadgeTests` sleeps). Untested: `ScreenCaptureService`, `ForegroundService`, corrupt PNGs, the stale-grid fallback
(`CacheTrustFit`) (agent). `VisionCorpusTests` writes `mockups/` on every run.

## B14 Light theme, high contrast, localization

Dark only ✔ (`App.axaml` `RequestedThemeVariant="Dark"`; painted controls read the static `Palette`). A theme means making `Palette` theme-aware. Strings are inline; localization would
need resources.

## B15 Net-worth timeline and a replay

`NetWorthHistory` keeps up to 120 readings per match but only the latest and one delta show ✔. A sparkline of the team lead in the match bar; with WP21's curve, a scrubber that replays the
advisor at minute N.
