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

Last checked against the code: 2026-10-10. Names below are stable; line numbers aren't given on purpose. If you add a
service, page, setting, shortcut or update channel, update the matching section here in the same commit.

## What the app is

A Windows desktop app (C#, .NET 10, Avalonia) that recommends Deadlock items for the heroes in your match. Heroes are
rated on traits, items respond to traits through formulas, and a match's score for an item is a sum over its heroes
measured against the roster average. Detect reads the match off the screen; match data from deadlock-api.com gives a
second opinion. The "model" (hero ratings, item formulas, game data) and the match data are published by CI to GitHub
releases that installs download.

Words used throughout: **hero**, **item** (shop upgrade, tier 1-4), **trait** (a `categories.csv` row), **relation**
(`against` an enemy, `with` an ally, `as` you), **profiled hero** (rated on at least one trait; a blank score is "not rated", 0 is a rating), **model** (the formula
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
- **Accessibility** (screen readers and contrast): a button with only a picture or a symbol in it (×, +, a glyph) gets
  `AutomationProperties.Name` in its XAML, because without one it is read out as its content's type name; a button with
  words in it needs nothing. A control that paints itself overrides `OnCreateAutomationPeer` and returns a `PaintedPeer`
  (`src/Controls`) with its role, a name such as "Haze, enemy, net worth 25k" and, where it explains itself, a help
  text; a picture whose meaning is printed beside it (`ScoreBar`, `ArtImage`) is marked decorative. A `SettingRow` names
  its lone control (a toggle, a drop-down) after its title and description. Text colours come from `Palette` and must read
  at 4.5:1 (WCAG AA) on the surfaces they sit on: `TextFaint` is the dimmest, 4.6:1 on `Surface2`, and
  `PaletteTests` holds the pairs. `AccessibilityTests` fails for any visible button of ours with no name.
- **Layout** is designed for a window of at least 900 × 600 at 100% zoom, and `MainWindow` scales its minimum size with
  the zoom (up to the screen), so every zoom shows a page the same room: at 150% the minimum is 1350 × 900, and the
  window waits to grow until the pointer leaves the status bar's zoom buttons. Shared widths (the Hero Items table's
  columns) are `x:Double` resources used by header and rows alike. A region that must lay out differently when narrow
  takes `behaviors:Responsive.NarrowBelow="<width>"`, which adds the class `narrow` for styles to key on (the Results
  rows, the Why-this-item lines); a Grid's `SharedSizeGroup` keeps the widest width it has had, so a view that moves
  cells out of a shared column resets its scope when `Responsive.IsNarrow` changes (`ExplainView`). A type selector
  such as `controls|DataText` doesn't match a control that overrides `StyleKeyOverride`: give it a class instead.
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
| `src/Features/<Page>` | one folder per page or area, each with its view models and views: `Match` (`Board`, `Results`, `Explain`, `Detect`, `Import`), `HeroItems`, `HeroTraits`, `ItemFormulas` (`ByItem`, `ByTrait`), `Settings` (`General`, `Shortcuts`, `Detection`, `Data`), `MainWindow` (window, menus, status bar, `Updates`, `Welcome`, `MatchDownload`, `ModelUpdate`, `DataMenuViewModel`), `Shared` (`Modals`, `Notifications`, `BackgroundJobs`, `ItemCard`) |
| `src/Controls`, `src/Behaviors`, `src/Converters`, `src/Theme`, `src/Enums` | custom controls and their automation peers, behaviours (middle-click autoscroll, popups following the zoom, the `narrow` class), converters, palette and fonts, enums |
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
   offer, match-data and art checks) and `AppUpdateViewModel.OnStartupAsync()`, then starts the `UpdateScheduler` (section 7).
   A reconnect repeats the startup checks (`OnReconnected`). `MainWindowViewModel` builds the page view models through DI and a few children with `new`
   (`DataStatusViewModel`, `AppUpdateViewModel`, `ConnectionViewModel`, `UpdatesViewModel`, `SettingsViewModel` and its pages).

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
  saved in `AppSettings.LastMatch`. **A detection that found every hero but not you** leaves them *unsided*: in
  `MatchState.RoleMap` as `Role.None` with a top-bar slot (`Unsided`, `HasUnsided`; the old "former you, unassigned" `None`
  has its slot removed, so the two don't collide), and the page shows a question instead of a list (`MatchViewModel.NeedsSelf`,
  `MatchBoardViewModel.IsPickingSelf`: the bar mirrors the game's two sides in neutral rings, `RosterSlot.IsUnsided`). Any role
  given to an unsided hero (`MatchState.SetRole`: a click, the role menu, the picker) splits the sides by their slot, so
  clicking yourself makes your side the allies and the other the enemies; a role given to a hero from outside the read drops
  the unsided ones. While the pointer is over one the bar previews that split (`MatchBoardViewModel.PreviewSelf`), the
  hero a kill streak's backplate pointed at is tagged "YOU?" (`MatchState.LikelyYou`, saved as `SavedMatch.LikelyYou`), and
  the click that resolves it opens the list on its best item and writes the slot into the kept capture's label
  (`DetectAction.LabelSelf`). Every page stays alive, so `MainWindowViewModel` tells `MatchViewModel.SetShown` when
  the Match tab is on screen (Settings covers it, which counts as hidden): while hidden, `ScoresChanged` (a formula edit)
  only marks the list stale and the page rescores once when it shows again. A changed match or `StoreReplaced` still
  refreshes at once.
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
  (`match_item_lift.csv` + `.meta.json`); `Hero Items` builds its table from the segments (`HeroItemTable`, `HeroFits`). `HeroItemsViewModel` keeps the last table
  it built, keyed by hero, mode, rank range and the picked patches, so sorting, tier toggles and the usage slider only
  reshape its rows; a different pick, or `StoreReplaced` (every download ends in one), builds it again.
- **Game data:** `Data → Model Tools → Sync from Game API` (`GameApiService` → `GameSync`; the submenu shows only with the model editors on)
  rewrites heroes, items, item stats and tooltips and
  measures the max-HP and durability traits (scoring_model.md). Everyone else gets those files through the model update.

## 5. Threading and background jobs

- Services' `async` methods resume on the UI thread (there is no `ConfigureAwait(false)` outside `JsonSettingsService`), so
  CPU-heavy steps are handed to `Task.Run` by name: Model Health, Detect's capture, vision and archiving steps, and a
  download's unpacking of each snapshot file (`MatchSnapshotService.FetchAsync`), `MatchStatsService.ApplyAsync` (writing the
  patch's counts, `MatchStatsMath.Analyse`, formatting the lift files with `DataStore.FormatMatchLift`) and the top-bar cut
  (`TopbarDerivation.Run`). Model Health saves pending edits, then loads its own copy of the
  data folder inside `Task.Run`, so editing while it runs can't change what it reads (and it won't start if the save fails).
- `DataStore` is not thread-safe, so a background step reads only what it was handed (the segment list, which is replaced
  and never edited, and a copy of the items) and the calling thread changes the store: it puts and prunes the segments,
  assigns the lifts and writes the lift files. `FetchAsync`'s `finished` callback is a `Func<MatchSegment, Task>` that is
  awaited before the next file or phase is asked for, so applies never overlap; `ApplyAsync` takes no token and always
  finishes, so a cancel lands between patches. If the rank filter changes while the lifts are being worked out, they are
  worked out again for the new range. The filter's own `Reanalyse` still runs on the UI thread.
- **Closing:** `MainWindow.OnClosing` first lets `MainWindowViewModel.HoldCloseForJobs` ask about running downloads, then
  `MainWindowViewModel.OnClosing` flushes pending saves. If that fails (the error toast has the reason) the window stays
  open and asks "Close anyway" / "Keep the app open"; an OS shutdown is never held up. Each question has its own
  "confirmed" flag, so an answer to one doesn't skip the other.
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
- **Zoom** is one `LayoutTransformControl` around the main window's content (`MainWindowViewModel.UiScale`); the window's
  `MinWidth`/`MinHeight` follow it (section 1, "Layout"). A dialog is a
  window of its own, so `MainWindow.OpenModalWindow` copies `UiScale` into `ModalViewModel.UiScale` for as long as it is
  open and `Modal.axaml` wraps its card in a `LayoutTransformControl` (a panel between the two, since the control sets its
  child's `RenderTransform`, which the card's entrance animation uses). Popups (tooltips, flyouts, menus, drop-down lists)
  also sit outside that transform: `PopupsFollowZoom.Enable()` (from `App.Initialize`) sets `Popup.InheritsTransform` on every
  popup as it gets its content, so each takes its placement target's scale. `ItemCardPresenter` scales its own card by
  `Zoom` and opts out, or it would scale twice.
- A dialog whose view model implements `IShrinksToFit` (the detect review, twelve tall rows) is shown through `ShrinkToFit`,
  inside the card: when the window leaves less height than it wants, it is scaled down to `MinFitScale` before the card's
  `ScrollViewer` takes over. Popups inside follow the scale like the zoom.
- `INotificationService` raises toasts (`NotificationOverlayViewModel`): 3 seconds by default, 10 for an error, and a click
  dismisses one. `Recent` keeps the last 20 with their time, which Help → Recent Messages lists. Startup runs before the
  overlay exists, so `NotificationService` holds what is sent while nobody listens (the newest 8 from the last minute) and
  hands it to the first subscriber only; later subscribers get new messages alone.

## 7. Updates and downloads

Four things stay current and one is manual. Settings flags are in `AppSettings`; the Updates chip and flyout, Settings → Data and
the Data menu are the UI. The Data menu is only **Check for Updates** (`UpdatesViewModel.CheckAllCommand`, the flyout's
button), **Downloads and Updates…** (`MainWindowViewModel.OpenDataSettingsCommand`: Settings on the Data page, which has every
per-source button, the modes and the folders), Settings and Quit; the maintainer tools (Sync from Game API, Sync New, Model Health
Report, Reload, Export, Open Data Folder) are its **Model Tools** submenu, shown with `ShowsEditors`. A new download or check
belongs in the flyout row and Settings → Data, not as a menu item of its own.

| What | Service (view model) | Source | Checked | Automation |
|---|---|---|---|---|
| The app | `AppUpdateService` (`AppUpdateViewModel`) | GitHub `releases/latest` | startup, reconnect, every 6 h | check automatic; **Update** click downloads; installed when the app closes |
| Formulas (the model) | `ModelUpdateService`, `ModelManifest`, `ModelUpdatePlan` (`DataMenuViewModel.CheckModelAsync`) | the `model` release, published by CI from `main` | startup, reconnect, every 6 h | automatic; files the user changed are asked about (while open: offered as a chip) |
| Match data | `MatchSnapshotService` / `SnapshotPlan` (shared snapshot) then `MatchStatsService` / `MatchFetchPlan` (deadlock-api.com) | the `match-data` release, built daily by `tools/MatchSnapshot` | startup, reconnect, every 6 h | automatic when a patch is new or the current one is 36 h old (3 days via the API) |
| Art | `ArtDownloadService`, `ArtManifest`, `TopbarDerivation` (`DataMenuViewModel.DownloadArtAsync`); `ArtService` serves it to the UI | deadlock-api.com asset API and CDN | startup, every 6 h (it downloads weekly, daily while a hero lacks art, and at once after a model update, Add heroes or Sync from Game API adds one; the row names heroes still without a portrait and waits as "available") | automatic after the first-run consent |
| Game data | `GameApiService`, `GameSync` | deadlock-api.com | **manual**, editors only: Data → Model Tools → Sync from Game API | none; users get it when the model is published |

- `DeadlockApi` is the one `HttpClient` for deadlock-api.com and GitHub: timeouts, ETag conditional requests, brotli/gzip,
  `BytesReceived`, the `UserAgent` every request carries (`deadlock-advisor/<AppVersion.Release as x.y.z, or dev> (+repo URL)`;
  art's adds "(asset downloader)"), and `Reachability`, which `ConnectivityService` watches for the offline chip (it probes every 30 s while
  offline). `DataMenuViewModel.OnStartup`/`OnReconnected` run the checks.
- **Checks while the app stays open:** `UpdateScheduler` (`MainWindowViewModel` creates it, `OnOpened` starts it, it is disposed
  with the window) ticks every 6 hours ±10%, each tick scheduled from the last. A tick runs on the UI thread through
  `MainWindowViewModel.CheckForUpdates`: `DataMenuViewModel.OnScheduledCheck` skips (and the next tick tries again) while
  offline, a modal is open or `IsBusy` (`CanCheckInBackground`); otherwise it launches `CheckAllAsync` (the formula check, the
  match-data check and the art check, the last two as at startup, and none of them while the first-run offer is still
  pending), and `AppUpdateViewModel.CheckAsync` runs beside it. The downloads' own guards (`IsDownloadingMatchData`,
  `IsDownloadingArt`) stop a second one. The formula check of a tick (`CheckModelWhileOpenAsync`) installs quietly only
  when the model editors are hidden and none of the user's own files are in the way; otherwise it offers a "Formulas"
  chip whose click runs `CheckModelAsync(manual: true)`, so nothing changes under someone editing and no dialog opens
  over a match. `CheckAllAsync(manual: true)` is Check for Updates (`CheckForUpdatesAsync`): the same checks, and the click
  is also the consent to fetch what was never downloaded: match data through `CheckMatchDataNowAsync` (the shared
  snapshot; with match data already there and no snapshot, the routine patch-list check rather than the dialog) and art
  when there is none. A turned-down offer with still no art or match data is repeated once, as the "Downloads" chip,
  `DownloadsReminderAfter` (3 days) after it was made (`RemindAboutDownloads`, from the startup and scheduled checks;
  `AppSettings.WelcomeOfferedAt`, `WelcomeReminded`). Ctrl+U runs Check for Updates (a fixed key, reserved in
  `ShortcutKeys`). A first run that was never offered the downloads gets `OfferWelcomeAsync` instead, which shows
  what they cost. Each source passes `saysWhenCurrent: false`, so a current one says nothing; a source that failed or
  did something still says so.
- Bad answers don't replace good data: `GameApiService.SyncAsync` throws `InvalidDataException` for an answer with no heroes
  or no shop items, and `MatchStatsService` throws it for an empty "every match" baseline over a window longer than 6 hours.
  Both reach the user as a failure ("Nothing was changed"), and in CI as a failed job, so nothing is published. Analytics
  calls retry a 429, a 5xx once, and a dropped connection or timeout twice (scoring_model.md, "The counts behind the lifts").
- `ArtDownloadService` runs its groups one after another (a later group may copy a file an earlier one wrote), and within a group
  up to 4 images at once, bounded by a `SemaphoreSlim` and started from the calling context so continuations and progress
  reports stay on the UI thread (not `Parallel.ForEachAsync`). A URL fetched or confirmed current earlier in the run is copied
  to the next group's folder (the hero card is in `heroes` and `topbar/_cards/normal`); a 5xx or 429 is retried twice through an
  injectable delay; `ArtManifest` locks its dictionary. `WelcomeViewModel.ArtSize` is the size of a first download.
- **The Updates chip** (`Features/MainWindow/Updates`; `MainWindowViewModel.Updates`) is the one answer to "is everything up
  to date?", at the left of the status bar, with a hover/click flyout. `UpdatesViewModel` holds no state of its own: whenever
  a service's state changes (settings, `AppUpdateViewModel`, `DataMenuViewModel`'s `NewerPatch`, `IsDownloading*`,
  `IsChecking*` and `Jobs`, connectivity, the store, new art) it works every row out again from them
  (`UpdateRowViewModel.Apply`, one per `UpdateSource`: App, Formulas, Match data, Art). A row has an `UpdateState`
  (`UpToDate`, `NotChecked`, `NotDownloaded`, `Checking`, `Updating`, `Available`, `Offline`, `Off`, `Failed`), a plain-words
  `About` beside the title, a one-line summary with when it was last checked (`MatchStatsMath.Age`), a chip headline, one
  action and a few links. The whole takes the most pressing state (failed, updating, available, offline, checking, not
  downloaded, not checked, up to date; `Off`, a source the user turned off, ranks last and doesn't count against up to
  date), and the chip says that row's headline or "2 updates". `NotDownloaded` is match data or art that was never
  fetched: the chip names it ("Match data and art not downloaded") with a neutral dot, so it doesn't claim "Up to date"
  and doesn't nag like an update waiting. The Formulas, New heroes and first-run
  ("Downloads") chips the Data menu puts in the status bar stay there for the click and count as offers: their
  `OpenCommand` is the action. The Match data row's Details is `DataStatusView`, the old card: it reads "up to date" while no
  newer patch is known and `AppSettings.MatchDataCheckedAt` is under 3 days old (`DataStatusViewModel.UpToDateWindow`),
  otherwise how old the data is, so an install that never checked doesn't claim it. A current row's button is **Check**
  (match data: `CheckMatchDataCommand`; art: `CheckArtCommand`, the quiet incremental download that says so when nothing was
  new), with the dialog-opening download as a link beside it; a row with something waiting says what it will do (Update,
  Download, Apply). **Check for updates** (`CheckAllCommand`, also Data → Check for Updates) runs
  `DataMenuViewModel.CheckAllAsync(manual: true)` and `AppUpdateViewModel.CheckAsync(manual: true, saysWhenCurrent: false)`
  and then says where things stand (`Status`, as a toast) unless a source already said something (the last
  `INotificationService.Recent` entry changed) or a dialog is open; while offline it only asks the connection to retry
  and says so, without a request. The footer's bytes come
  from `IDeadlockApi.BytesReceived`, read when the flyout opens or anything changes. Running downloads keep their own
  progress and cancel chips on the right, and the offline chip (with Retry) stays beside them.
- **Settings → Data's modes** (`DataSettingsViewModel`, `UpdateModes`) are a view over the existing flags, so `settings.json` reads
  the same in an older version: Match data *Automatic* = `AutoUpdateMatchData`, *Tell me* = it off and `CheckForNewerPatch`
  on, *Off* = both off; Formulas the same with `AutoUpdateModel` and `CheckForNewHeroes`; App is *Tell me* (`CheckForAppUpdates`)
  or *Off*. Choosing *Automatic* leaves the check-only flag as it was; every combination of flags reads as one mode.
  **Check now** for match data is `DataMenuViewModel.CheckMatchDataCommand`: with the shared snapshot usable it stamps the check,
  starts the download when the plan `HasWork` (a toast says so) or toasts "Match data is up to date (patch 10-07, fetched 3h
  ago)"; the download dialog opens only when the snapshot isn't usable, and from the Updates row's buttons.
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
(`VisionApply.ApplyToMatch`) or `DetectReviewViewModel` opens. With *Apply without asking* on, every hero settled
(`Detection.HeroesSettled`) is applied without review, with you or, when you weren't found, unsided (section 4: the match
page asks for a click on your hero). Applied captures are kept in `captures/`; they are the corpus
detection is measured on. Rules and numbers: detection_model.md.

**The game's process is `project8.exe`** (Steam: `.../Deadlock/game/bin/win64/project8.exe`); `ScreenCaptureService` matches
that name (and `deadlock`), preferring the foreground window when it is the game's (`ChooseGameWindow`), so the capture
comes from the monitor the game is on. The log line says "Deadlock window" or "primary monitor (Deadlock's window not found)".

`DetectAction.Finished` reports how each run ended (`DetectOutcome`: `Applied`, `NeedsYou`, `NeedsReview`, `NothingFound`,
`CaptureFailed`, `NoArt`), passed on by `MatchViewModel.DetectFinished`. `MainWindowViewModel.DetectFromAnywhereAsync`
(the system-wide F9) notes `IForegroundService.IsAnotherAppInFront` before capturing, and if the game was in front and
`AppSettings.SoundOnDetect` is on, calls `IAttentionService` (`AttentionService`: `MessageBeep` and `FlashWindowEx`, no-ops
off Windows): a `Done` chime for an applied match, a `NeedsLook` chime and a taskbar flash for anything else, `NeedsYou`
included (it needs a click). With `AppSettings.ComeUpForReview` the window also comes forward for a review, a problem or
`NeedsYou`.

## 9. Tests

- `tests/DeadlockAdvisor.Tests` (xUnit, close to a thousand tests; CI takes about five minutes on Windows). Files are named
  after the features they cover. Run with `-c Release` (the app may be running from Debug); targeted runs use
  `--filter "FullyQualifiedName~Name"`; the full suite is for changes that reach beyond one area (CLAUDE.md, "Testing").
- `Ui/`: headless Avalonia through Skia (`TestAppBuilder`). `UiHarness` builds the real `MainWindow` with the app's own
  `App.RegisterServices` over fakes and a throwaway copy of the golden data; `Show()`, `Settle()`, `Screenshot(name)` writes a PNG
  to `mockups/`. `DEADLOCK_ASSETS=<an assets folder>` renders with real art. **To look at a page, run the UI test that renders it
  and read the PNG; don't launch the app.**
  `AccessibilityTests` walks every page, Settings category and the Updates flyout for unnamed buttons and checks the painted
  peers; `LayoutTests` renders every page at the smallest window at 100% and 150% (`mockups/layout_<page>_<zoom>.png`) and
  fails for anything painted past the edge of the window or its scroll viewer. It can't see one control drawn over
  another, so look at the PNGs after a layout change.
- `Fakes/` (`FakeDeadlockApi` and the other fake services in `FakeServices.cs`, `FakeConnectivity`, `FakeScreenCapture`,
  `FakeGlobalHotkey`, `FakeForeground`, `HeldDownloads`, `SyntheticItemStatsApi`), `Support/` (`DataFixture`, `TestStore`,
  `Golden`, `PumpContext`: a stand-in UI thread whose continuations the test runs itself, to show what ran off it, vision helpers), `Golden/` (reference outputs; regenerate with `DEADLOCK_UPDATE_GOLDENS=1` as CLAUDE.md and
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
