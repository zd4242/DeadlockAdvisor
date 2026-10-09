# Architecture: a map of the app

For anyone (human or agent) starting work here. It says where things are and how they fit; the other docs say
how the parts work. Read this first, then the doc for the area you're touching:

| Doc | Read it before |
|---|---|
| [README.md](../README.md) | anything user-facing: what the app does, as a person sees it |
| [scoring_model.md](scoring_model.md) | scoring, the formula CSVs, `GameSync`, match data, Model Health |
| [detection_model.md](detection_model.md) | `src/Vision`, the Detect flow, the art download |
| [publishing.md](publishing.md) | releases, the published model, shared match data, workflows |
| [roadmap/README.md](roadmap/README.md) | planned improvements, as work packages ("implement WP08") |

Last checked against the code: 2026-10-09. Names below are stable; line numbers aren't given on purpose. If you add a
service, page, setting, shortcut or update channel, update the matching section here in the same commit.

## What the app is

A Windows desktop app (C#, .NET 10, Avalonia) that recommends Deadlock items for the heroes in your match. Heroes are
rated on traits, items respond to traits through formulas, and a match's score for an item is a sum over its heroes
measured against the roster average. Detect reads the match off the screen; match data from deadlock-api.com gives a
second opinion. The "model" (hero ratings, item formulas, game data) and the match data are published by CI to GitHub
releases that installs download.

Words used throughout: **hero**, **item** (shop upgrade, tier 1-4), **trait** (a `categories.csv` row), **relation**
(`against` an enemy, `with` an ally, `as` you), **profiled hero** (at least one nonzero trait), **model** (the formula
files, see publishing.md), **seed** (`src/Assets/SeedData`, the starter and published copy of the model), **lift** (an
item's win-rate gain from match data), **segment** (one patch's raw match counts), **snapshot** (the shared download of
segments), **net worth** (a hero's souls, read by Detect), **focus** (enemies the list leans toward).

## 1. Stack and conventions

- .NET 10 (`net10.0`), Avalonia 11.3 (Fluent theme, dark only, compiled bindings on by default), ReactiveUI through
  Avalonia.ReactiveUI with `ReactiveUI.Fody` weaving `[Reactive]`, `Microsoft.Extensions.DependencyInjection`, ClosedXML
  (only the maintainer's Excel export). Nullable is on. The release is a single-file, self-contained, untrimmed exe
  (`Properties/PublishProfiles/release.pubxml`); Linux and macOS are built as previews only.
- **View models** derive from `ViewModelBase` (a `ReactiveObject` that is `IDisposable`, with a `Disposables` bag and
  `RequestViewAction(name)` / `ViewInteraction` for asking its view to do something, such as focus a box). State is
  `[Reactive] public T X { get; private set; }`; actions are `ReactiveCommand`s. `ViewLocator` finds a view by replacing
  `ViewModel` with `View` in the type name.
- **Views** are `.axaml` with thin code-behind. Custom painted controls live in `src/Controls` and read colours from
  `src/Theme/Palette.cs`; the XAML brushes and styles are in the root `Themes/` (`DarkTheme.axaml` exposes the palette,
  `Styles.axaml` holds the shared styles).
- **Services** have interfaces in `src/Services/Contracts` and are registered in `App.RegisterServices` (tests reuse it).
  A class that depends on time takes an `IScheduler` or a clock through an `internal` constructor that the public one
  delegates to, so tests drive time with a `TestScheduler` (`DataMenuViewModel`, `DataService`, `ConnectivityService`).
- **Style** is in `.claude/CLAUDE.md` ("Guidelines"): private fields are `_camelCase` (statics too), a single-statement
  `if` puts its statement on the next line, comments only where something isn't obvious. `GlobalUsings.cs` has
  `Avalonia`, `Avalonia.Controls`, `System`, `System.Collections.Generic`, `System.Linq`, `System.Threading.Tasks`.
- **Windows-only code** (the F9 hotkey, screen capture, foreground window, self-install) stays behind
  `OperatingSystem.IsWindows()`.
- **Files are written safely**: `AtomicFile` (temp file, then swap) and `BackedUpFile` (a timestamped copy of the old file
  under `.backups/`, then an atomic write). Per file it keeps the newest 12 copies, the newest of each hour for 24 hours and
  the newest of each day for 14 days (`BackedUpFile.Keep`, `HourlyWindow`, `DailyWindow`; CSV and JSON alike, timed by the
  stamp in the name).

## 2. Folder map

| Path | Holds |
|---|---|
| `Program.cs`, `App.axaml(.cs)`, `ViewLocator.cs` | entry point, DI wiring and startup, view lookup |
| `src/Core` | `ViewModelBase`, formatting, fuzzy match, zoom steps, shortcut keys, wiki links, small helpers |
| `src/Models` | `Hero`, `Item`, `Category`, `StatRule`, `MatchState` (who's in the match), `NetWorthHistory`, `AppSettings` |
| `src/Scoring` | pure scoring and statistics: `ItemScoring`, `WeightMatrix`, `BestTargets`, `FocusWeights`, `NetWorthWeights`, `ScoreScales`, `MatchStatsMath`, `MatchSegment`, `HeroItemTable`, `HeroFits`, `ModelHealth` |
| `src/Services` | data and I/O: `DataStore`, `DataService`, settings, logging, notifications, modals, API client, downloads and updates, screen capture, hotkey; `Contracts/` interfaces, `Formats/` CSV/JSON/number formats, `GameApi/` (`GameSync`, `SyncReport`, tooltip parsing) |
| `src/Vision` | detection: `Detector`, `Layout`, `TemplateBank`, `Matcher`, `NetWorthReader`, image ops and PNG codec |
| `src/Features/<Page>` | one folder per page or area, each with its view models and views: `Match` (`Board`, `Results`, `Explain`, `Detect`, `Import`), `HeroItems`, `HeroTraits`, `ItemFormulas` (`ByItem`, `ByTrait`), `Settings` (`General`, `Shortcuts`, `Detection`, `Data`), `MainWindow` (window, menus, status bar, `Welcome`, `MatchDownload`, `ModelUpdate`, `DataMenuViewModel`), `Shared` (`Modals`, `Notifications`, `BackgroundJobs`, `ItemCard`) |
| `src/Controls`, `src/Behaviors`, `src/Converters`, `src/Theme`, `src/Enums` | custom controls, behaviours (middle-click autoscroll), converters, palette and fonts, enums |
| `src/Assets/SeedData` | the starter and published model; the app embeds it |
| `Themes/` | XAML resource dictionaries (brushes, shared styles) |
| `tools/MatchSnapshot`, `tools/PublishModel` | the CI programs (publishing.md); both reference the app project |
| `tests/DeadlockAdvisor.Tests` | the xUnit suite (section 9) |
| `docs/`, `.github/workflows/` | these docs; `ci.yml`, `match-data.yml`, `new-heroes.yml`, `release.yml` |
| `mockups/` | git-ignored: screenshots and reports the tests write |

## 3. Startup and dependency injection

1. `Program.Main`: if started as an update installer (`--install`) it installs and exits (`AppUpdateService.InstallIfAsked`);
   otherwise, on Windows, it claims the data folder (`SingleInstance`: a named mutex whose name hashes
   `JsonSettingsService.AppDataPath`, so a copy with its own `DEADLOCK_ADVISOR_HOME` runs beside the real one). A second copy
   sets the first one's named event and exits; the first raises its window (`App` listens once the window exists, and posts
   `MainWindowViewModel.BringForward`). Then it starts Avalonia. After shutdown it lets go of the claim and installs a
   downloaded update (`AppUpdateService.InstallIfDownloaded`).
2. `App.Initialize` loads the XAML, builds the container (`App.RegisterServices`: services as singletons;
   `DetectAction`, `ImportMatchAction` and the page view models as transients; `NotificationOverlayViewModel` singleton) and
   installs the global exception handlers (Rx default handler, dispatcher, unobserved tasks, and
   `AppDomain.UnhandledException`, which only logs: the log line is written at once, so it survives the process dying).
3. `App.OnFrameworkInitializationCompleted`: load settings (blocking; a `settings.json` that can't be read is copied to
   `settings.bad-<yyyyMMdd-HHmmss>.json`, the newest 3 kept, and the user is told, before the defaults take over),
   `DataService.Initialize()` (seed the default folder on a
   first run, load the tables through `DataRecovery`, build the weight matrix), point the art service at the assets folder, create `MainWindow` with
   a `MainWindowViewModel`, attach the hotkey service. A startup failure writes `startup-error.log` and rethrows.
4. `MainWindow.OnOpened` calls `MainWindowViewModel.OnOpened`: `DataMenuViewModel.OnStartup()` (formula check, first-run
   offer, match-data and art checks) and `AppUpdateViewModel.OnStartupAsync()`. A reconnect repeats the startup checks
   (`OnReconnected`). `MainWindowViewModel` builds the page view models through DI and a few children with `new`
   (`DataStatusViewModel`, `AppUpdateViewModel`, `ConnectionViewModel`, `SettingsViewModel` and its pages).

Where the user's files live: `%AppData%\DeadlockAdvisor` (or the folder in `DEADLOCK_ADVISOR_HOME`) holds `settings.json` and
the logs (`deadlock-advisor.log`, `.previous.log`, `startup-error.log`); the **data root** (the same folder by default, or
Settings → Data's choice) holds `data/` (CSV tables, `.backups/`, `match_counts/`, `model.json`), `assets/` (`heroes`, `items`,
`ranks`, `topbar`, `art_manifest.json`) and `captures/` (kept Detect captures).

## 4. Data and scoring flow

- **`DataStore`** holds the tables in memory, no UI: heroes, items, categories, hero scores, item coefficients, trait weights,
  stat rules, item stats and tooltips, match lifts and their meta, per-patch segments. Load and save are per file, in
  insertion order. **`DataService`** owns the store and what is derived from it (`Matrix`: `WeightMatrix`, `Scales`:
  `ScoreScales`), and announces `StoreReplaced` (a different store: reload, sync, folder change) and `ScoresChanged` (the
  matrix was rebuilt after an edit). `MarkEdited(DataFiles)` schedules a save 600 ms after the last edit and a rescore at most
  every 100 ms.
- **A damaged file:** `DataStore.Load` throws `DataLoadException` (the file's name and the reason) for a row that doesn't
  parse, a malformed JSON file, or a base table (`heroes.csv`, `items.csv`, `categories.csv`) with no rows; a missing or
  locked file throws as it is. `DataService.Open` (startup) and `Reload` load through `DataRecovery`: the file is kept as
  `<file>.bad-<yyyyMMdd-HHmmss>` (the newest 3 per file stay), replaced by the newest of its last 10 backups that gets the load past it, else by the
  bundled copy (`DataService.BundledCopy`), and one warning names each file, why, and what it was restored from. Each file
  is repaired once per load, and a file with no bundled copy is just set aside. `ChangeDataRoot` loads strictly, so a
  folder the user picks is refused and left alone. `PruneOrphans` does nothing while a base table is empty
  (`DataStore.EmptyBaseTables`), and Sync New says so.
- **Match page:** `MatchViewModel` composes `MatchBoardViewModel` (the roster, picker, roles, focus), `ResultsViewModel` (the
  recommendation list), `ExplainViewModel` ("Why this item?"), `DataRanksViewModel` (rank filters), and the `DetectAction` and
  `ImportMatchAction`. `MatchState` is who is in the match (roles, top-bar slots, net-worth history, focused enemies); it is
  saved in `AppSettings.LastMatch`.
- **Match lookup:** `MatchLookupService.LookUpAsync` reads a finished match's `/v1/matches/{id}/metadata` into a
  `LookedUpMatch`: when it started, how long it ran, who won, and per player the account, hero, team and slot plus
  `Items` (`PurchasedItem`: the game's item id, when bought, when sold or null; ability upgrades that have no shop item are
  in there too and simply match no `Item.GameId`), `Worth` (the net-worth curve, one `NetWorthPoint` per `stats[]` entry) and
  the final `NetWorth`. Malformed entries are skipped. The import dialog uses only the roster; the rest is for a post-match
  review (roadmap WP21).
- **Scoring:** `ItemScoring.ScoreAll` scores every item for a `LineUp` (the match's heroes with their `NetWorthWeights` and
  `FocusWeights`) from the weight matrix; `ExplainItem` walks the same arithmetic; `DataScores` is the match-data second
  opinion; `BlendScale` puts the two on one scale. Details and rules: scoring_model.md.
- **Match data:** downloads bring segments (`data/match_counts/<patch>.json`); `MatchStatsMath.Analyse` turns them into lifts
  (`match_item_lift.csv` + `.meta.json`); `Hero Items` builds its table from the segments (`HeroItemTable`, `HeroFits`).
- **Game data:** `Data → Sync from Game API` (`GameApiService` → `GameSync`) rewrites heroes, items, item stats and tooltips and
  measures the max-HP and durability traits (scoring_model.md). Everyone else gets those files through the model update.

## 5. Threading and background jobs

- Services' `async` methods resume on the UI thread (there is no `ConfigureAwait(false)` outside `JsonSettingsService`), and
  `Task.Run` is used only for Model Health and Detect's capture, vision and archiving steps. CPU-heavy steps in a download
  therefore run on the UI thread today (roadmap WP11).
- `DataMenuViewModel` is the hub for long data work. A **background job** is a `BackgroundJobViewModel` shown as a status-bar
  chip with progress, a cancel, and a result to open (`RunInBackgroundAsync`); a **modal job** shows a `ProgressModalViewModel`
  (`RunBehindModalAsync`); `Launch` runs a fire-and-forget task and reports an unexpected exception as a toast. One download
  of each kind runs at a time (`IsDownloadingMatchData`, `IsDownloadingArt`).
- `RequestPacer` spaces deadlock-api.com analytics calls (one start per 0.4 s, two in flight, a 429 pauses every start).

## 6. Modals and notifications

- `IModalService.ShowModal(viewModel)` shows one dialog at a time (a second call is logged and dropped; `CloseModal()` closes
  whichever is open). `MainWindow` shows each in a borderless `ModalWindow` laid over the main window and kept in step with its
  bounds; if another app is in front (`IForegroundService`) it waits until you switch back. `Confirm`, `ShowMessage` and a
  progress dialog are helpers in `Features/Shared/Modals`. Content is found by `ViewLocator`.
- `INotificationService` raises toasts (`NotificationOverlayViewModel`): 3 seconds by default, 10 for an error, and a click
  dismisses one. `Recent` keeps the last 20 with their time, which Help → Recent Messages lists. Startup runs before the
  overlay exists, so `NotificationService` holds what is sent while nobody listens (the newest 8 from the last minute) and
  hands it to the first subscriber only; later subscribers get new messages alone.

## 7. Updates and downloads

Four things stay current and one is manual. Settings flags are in `AppSettings`; Settings → Data and the Data menu are the UI.

| What | Service (view model) | Source | Checked | Automation |
|---|---|---|---|---|
| The app | `AppUpdateService` (`AppUpdateViewModel`) | GitHub `releases/latest` | startup, reconnect | check automatic; **Update** click downloads; installed when the app closes |
| Formulas (the model) | `ModelUpdateService`, `ModelManifest`, `ModelUpdatePlan` (`DataMenuViewModel.CheckModelAsync`) | the `model` release, published by CI from `main` | startup, reconnect | automatic; files the user changed are asked about |
| Match data | `MatchSnapshotService` / `SnapshotPlan` (shared snapshot) then `MatchStatsService` / `MatchFetchPlan` (deadlock-api.com) | the `match-data` release, built daily by `tools/MatchSnapshot` | startup, reconnect | automatic when a patch is new or the current one is 36 h old (3 days via the API) |
| Art | `ArtDownloadService`, `ArtManifest`, `TopbarDerivation` (`DataMenuViewModel.DownloadArtAsync`); `ArtService` serves it to the UI | deadlock-api.com asset API and CDN | startup, weekly (daily while a hero lacks art) | automatic after the first-run consent |
| Game data | `GameApiService`, `GameSync` | deadlock-api.com | **manual**: Data → Sync from Game API | none; users get it when the model is published |

- `DeadlockApi` is the one `HttpClient` for deadlock-api.com and GitHub: timeouts, ETag conditional requests, brotli/gzip,
  `BytesReceived`, and `Reachability`, which `ConnectivityService` watches for the offline chip (it probes every 30 s while
  offline). `DataMenuViewModel.OnStartup`/`OnReconnected` run the checks; nothing re-checks while the app stays open
  (roadmap WP10).
- Bad answers don't replace good data: `GameApiService.SyncAsync` throws `InvalidDataException` for an answer with no heroes
  or no shop items, and `MatchStatsService` throws it for an empty "every match" baseline over a window longer than 6 hours.
  Both reach the user as a failure ("Nothing was changed"), and in CI as a failed job, so nothing is published. Analytics
  calls retry a 429, a 5xx once, and a dropped connection or timeout twice (scoring_model.md, "The counts behind the lifts").
- The shared snapshot is considered stale after 4 days (`MatchSnapshot.StaleAfter`) and the app then asks deadlock-api.com
  itself (about 560 calls for three patches with rank groups).
- CI publishes: `ci.yml` (tests, then `publish-model` on `main`), `match-data.yml` (daily 06:17 UTC), `new-heroes.yml` (adds the
  game's new heroes to the seed every six hours), `release.yml` (the app, started by the user). Rules for what a push publishes:
  CLAUDE.md and publishing.md.

## 8. Detect

`GlobalHotkeyService` (F9 through Win32 `RegisterHotKey`, rebindable, system-wide) or the button runs `DetectAction.RunAsync`:
`ScreenCaptureService` copies the top 22% of the game window's client area (GDI `BitBlt`, falling back to the primary monitor)
and may minimise the advisor first; `TemplateBank.Load` reads the reference art in `assets/topbar`; `Detector.Detect` finds
the grid (cached per screen size in `AppSettings.VisionGeometry`), reads the twelve slots and which is you; a grid that fits
under `Detector.MinFit` (`Detection.FoundStrip`) ends the run as "Nothing found";
`RosterContinuity` keeps heroes already applied; `NetWorthReader` reads souls; then the match is applied
(`VisionApply.ApplyToMatch`) or `DetectReviewViewModel` opens. Applied captures are kept in `captures/`; they are the corpus
detection is measured on. Rules and numbers: detection_model.md.

**The game's process is `project8.exe`** (Steam: `.../Deadlock/game/bin/win64/project8.exe`); `ScreenCaptureService` matches
that name (and `deadlock`), preferring the foreground window when it is the game's (`ChooseGameWindow`), so the capture
comes from the monitor the game is on. The log line says "Deadlock window" or "primary monitor (Deadlock's window not found)".

`DetectAction.Finished` reports how each run ended (`DetectOutcome`: `Applied`, `NeedsReview`, `NothingFound`,
`CaptureFailed`, `NoArt`), passed on by `MatchViewModel.DetectFinished`. `MainWindowViewModel.DetectFromAnywhereAsync`
(the system-wide F9) notes `IForegroundService.IsAnotherAppInFront` before capturing, and if the game was in front and
`AppSettings.SoundOnDetect` is on, calls `IAttentionService` (`AttentionService`: `MessageBeep` and `FlashWindowEx`, no-ops
off Windows): a `Done` chime for an applied match, a `NeedsLook` chime and a taskbar flash for anything else.

## 9. Tests

- `tests/DeadlockAdvisor.Tests` (xUnit, close to a thousand tests; CI takes about five minutes on Windows). Files are named
  after the features they cover. Run with `-c Release` (the app may be running from Debug); targeted runs use
  `--filter "FullyQualifiedName~Name"`; the full suite is for changes that reach beyond one area (CLAUDE.md, "Testing").
- `Ui/`: headless Avalonia through Skia (`TestAppBuilder`). `UiHarness` builds the real `MainWindow` with the app's own
  `App.RegisterServices` over fakes and a throwaway copy of the golden data; `Show()`, `Settle()`, `Screenshot(name)` writes a PNG
  to `mockups/`. `DEADLOCK_ASSETS=<an assets folder>` renders with real art. **To look at a page, run the UI test that renders it
  and read the PNG; don't launch the app.**
- `Fakes/` (`FakeDeadlockApi` and the other fake services in `FakeServices.cs`, `FakeConnectivity`, `FakeScreenCapture`,
  `FakeGlobalHotkey`, `FakeForeground`, `HeldDownloads`, `SyntheticItemStatsApi`), `Support/` (`DataFixture`, `TestStore`,
  `Golden`, vision helpers), `Golden/` (reference outputs; regenerate with `DEADLOCK_UPDATE_GOLDENS=1` as CLAUDE.md and
  scoring_model.md say). Live network tests run only with `DEADLOCK_LIVE_API` set. The vision corpus tools run on environment
  variables (detection_model.md).

## 10. Recipes

- **A setting:** a property with a default on `AppSettings`; in the page's view model (`SettingsPageViewModel` subclass) read
  `Current.X` and write `Change(s => s.X = value)`; a `settings:SettingRow` with a `ToggleSwitch` in its view; code that reacts
  observes `ISettingsService.SettingsChanged`. Test in `SettingsTests` / `Ui/SettingsPageTests`.
- **A Settings category:** a `SettingsPageViewModel` and view, then add it to `SettingsViewModel` (constructor and `Categories`)
  and to where `MainWindowViewModel` builds it.
- **A page:** a view model and view with matching names; register the view model in `App.RegisterServices`; in
  `MainWindowViewModel` add it to `Pages`, `_allPageNames`, the page constants and `IsXPage` flags and their change
  notifications (editor-only pages go last, `_editorPages`); add its panel to `MainWindow.axaml`.
- **A shortcut:** a `ShortcutAction` value, an entry in `ShortcutKeys.Defaults` (mind `_reserved`), a case in
  `MainWindowViewModel.ShortcutBindingFor` and its command; Settings → Shortcuts lists the defaults.
- **A background download:** build a `BackgroundJobViewModel`, run it through `DataMenuViewModel.RunInBackgroundAsync` with an
  `IProgress<FetchProgress>` and `job.Token`; report with `Succeeded` / `Failed`; keep a quiet mode for routine checks.
- **An update source:** a service behind a contract, a check in `OnStartup`/`OnReconnected`, a settings flag, a
  `Checked(...)` timestamp for Settings → Data, tests with `FakeDeadlockApi`.
- **A column in a published file:** publishing.md, "Changing what the files hold" (bump `ModelManifest.CurrentFormat`,
  `MatchSegment.Version` and `MatchSnapshot.Version` as it says).

## 11. Gotchas

- `Glob`/`Grep` at the repository root drown in `bin/`, `obj/`, `mockups/` and `tests/**/Golden`: search `src/`, `tests/*.cs`
  and `docs/`.
- Never launch the app or capture the real screen unless the user says so: it opens windows and holds F9, and they may be in a
  match. If a run is approved, set `DEADLOCK_ADVISOR_HOME` to a scratch folder so it keeps its own settings and data.
- `mockups/` is test output and git-ignored; images from tests that no longer exist stay behind.
- Sessions can share one working tree: run `git status` before the first edit and commit only your own files by path
  (CLAUDE.md, "Committing").
- The repository is public: no personal data, local paths or third-party material in files.
- A changed `src/Assets/SeedData` publishes to every install once it reaches `main`; changing what the shared files hold needs
  the version bumps in publishing.md.
- `gh` is installed and signed in for read-only release and workflow questions; don't publish, push or release unasked.
