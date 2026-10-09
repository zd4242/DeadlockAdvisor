# WP13 One Updates chip and flyout

Status: todo
Effort: L (three stages) · Risk: medium (a visible redesign of the status bar) · Depends on: WP09, WP10 · Wave: D
Touches: new `src/Features/MainWindow/Updates/*` (`UpdatesViewModel`, row view models, `UpdatesView.axaml`), **`src/Features/MainWindow/MainWindow.axaml`** (status bar), `MainWindowViewModel.cs`, **`DataMenuViewModel.cs`** (state exposed, not logic moved), `DataStatusViewModel.cs` / `DataStatusView.axaml` (the match-data card moves), `tests/DeadlockAdvisor.Tests/Ui/StatusBarTests.cs` and new tests
Docs to update: `README.md` (Install, Data menu, status bar), `docs/architecture.md` (sections 2 and 7), the mockup list in this package's report

## Goal

One place answers "is everything up to date?": a single status-bar chip with a flyout listing the app, the formulas, the match data and the art,
each with its state, when it was last checked and one action, plus **Check all**. The scattered chips and cards fold into it.

## Why / premise check

1. Today ✔: `DataStatusView` (match-data chip and card, left), `ConnectionView` (offline), `AppUpdateView`, the job chips (art, match data, "New
   heroes", the first-run welcome chip), the Data menu and Settings → Data each show part of the picture; "Check now" exists only for formulas and
   app (`DataSettingsView.axaml`); nothing says "everything is current". The status bar is built in `MainWindow.axaml` ("StatusBar").
2. Per-source state already exists as observable properties: `AppUpdateViewModel` (`State`, `Available`, `Installed`), `DataMenuViewModel`
   (`NewerPatch`, `IsDownloadingMatchData`, `IsDownloadingArt`, `Jobs`), settings timestamps (`MatchDataCheckedAt`, `ModelCheckedAt`,
   `AppUpdateCheckedAt`, `ArtCheckedAt`), `IConnectivityService.State`, `IDeadlockApi.BytesReceived` ✔. The package can be a thin view model over them.

Re-check: read `MainWindow.axaml` (status bar), `DataStatusViewModel`, `AppUpdateViewModel`, and WP10's `CheckAllAsync`. If WP09/WP10 aren't `done`, say
so and ask whether to proceed on what exists.

## Read first

`docs/architecture.md` sections 5-7; `StatusChipStyles.axaml`; `BackgroundJobView`; the existing mockups `status_*.png` (in `mockups/` after running
`Ui/StatusBarTests`); `DataSettingsViewModel` (how the settings refresh when a check finishes).

## Scope

In:
- `UpdatesViewModel` with four rows (App, Formulas, Match data, Art): state (`UpToDate`, `Checking`, `Updating`, `Available`, `Offline`, `Off`, `Failed`),
  a one-line summary ("patch 10-07, checked 2 h ago"), a primary action (Update / Restart now, Apply, Download, Check), and a "Details…" that opens what
  exists today (the match-data card facts, the download dialog, What's new). An aggregate state with precedence failed > updating > available > offline >
  up to date.
- Footer: **Check all** (`DataMenu.CheckAllAsync(manual: true)` and `AppUpdate.CheckNowCommand`), and "Downloaded this session: X MB" from `BytesReceived`.
- The status bar: one "Updates" chip on the left (dot colour and short text: "Up to date", "Updating match data 40%", "Patch 10-07 is out", "2 updates")
  opening the flyout; running downloads keep their progress and cancel chips on the right; the match-data card's content becomes the Match data row.
  The offline chip (it has Retry) and transient offers that need a click (new heroes, the first-run chip) stay and count toward the aggregate state.

Out: the Settings → Data modes (WP14); splitting `DataMenuViewModel` (backlog B09); new network calls.

## Stages (commit after each, suite green)

1. `UpdatesViewModel` and rows with unit tests over fake services (no UI change yet).
2. The chip and flyout views replacing the left match-data chip and (when idle) the app chip; keep every old action reachable.
3. Polish: wording, spacing, hover/focus, tooltips; remove dead styles and the replaced controls; docs.

## Suggested design

- Rows are small records built from observables with `CombineLatest`; nothing in them caches state (no second source of truth).
- Reuse `StatusChipStyles.axaml` and the `DataStatusView` flyout pattern (`HoverFlyout`, `Placement="TopEdgeAlignedLeft"`).
- Keep `AppUpdateView`'s Update/Restart/Dismiss behaviour inside the App row (or keep the chip while an update is downloading, if the row can't carry
  progress well); don't lose "Dismiss this version".
- The row's last-checked text reuses `MatchStatsMath.Age`.

## Tests

`UpdatesViewModelTests` (state and aggregate from fake services; Check all calls both paths; bytes line), `StatusBarTests` (chip text per state, flyout
opens, old chips gone), existing `AppUpdateTests`/`DataMenuTests` unchanged. Render the flyout in each state to `mockups/status_updates_*.png` and look at
them. Full suite (shared window layout).

## Acceptance criteria

- [ ] A fresh, current install shows "Up to date" and the flyout says what was checked and when.
- [ ] Every action that existed in the old chips and card is still reachable.
- [ ] Offline, failed, updating and available states each look right.
- [ ] README and architecture docs describe the new surface.

## Conflicts

`MainWindow.axaml`: after WP03 and WP09, before WP17. `DataMenuViewModel.cs`: after WP10/WP11, before WP14. WP14 builds the Settings side.

## Notes

Show the user the rendered flyout PNGs in your report; the look is theirs to steer.
