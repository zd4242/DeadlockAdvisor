# WP10 Re-check for updates while the app is open

Status: done
Effort: M · Risk: medium (several flows share it) · Depends on: none (do after WP07, which edits the same view model) · Wave: C
Touches: **`src/Features/MainWindow/DataMenuViewModel.cs`**, **`src/Features/MainWindow/MainWindowViewModel.cs`**, new `src/Features/MainWindow/UpdateScheduler.cs`, `tests/DeadlockAdvisor.Tests/DataMenuTests.cs`
Docs to update: `README.md` (Install: how updates are found; Settings → Data wording), `docs/architecture.md` (section 7), `docs/publishing.md` ("How installs get it")

## Goal

A session left open for days still notices a new patch, newer formulas and a new app version, without the user restarting the app.

## Why / premise check

1. Nothing re-checks while the app runs ✔: the only timers are the offline probe (`ConnectivityService`) and UI timers. The checks run from
   `DataMenuViewModel.OnStartup`, `OnReconnected` and `AppUpdateViewModel.OnStartupAsync`; `AppUpdateViewModel`'s own doc says "checked once a
   startup". Search the source for `Observable.Timer`, `Observable.Interval`, `PeriodicTimer`.
2. A companion app with a system-wide F9 hotkey is likely to stay open across days, so the "newer patch is out" chip, the match-data refresh
   (36 h) and app/formula updates never happen in such a session ✔ (reasoning).

If a periodic check already exists, report.

## Read first

`docs/architecture.md` sections 5 and 7; `DataMenuViewModel` (`OnStartup`, `OnReconnected`, `StartModelCheck`, `StartDataChecks`,
`CheckMatchDataAsync`, `CheckModelAsync`, `OfferNewHeroesAsync`); `AppUpdateViewModel.CheckAsync`; `MainWindowViewModel` (the
`connectivity.Reconnected` subscription and `OnOpened`); `DataMenuTests` (how the scheduler and fakes are used).

## Scope

In:
- `UpdateScheduler`: every 6 hours ±10%, after `OnOpened`, runs one check pass; skips (and tries at the next tick) while offline, while a
  modal is open, or while `DataMenu.IsBusy`; stops when disposed.
- `DataMenuViewModel.CheckAllAsync(bool manual)`: the match-data check, the art due check (the art part of `StartDataChecks`, extracted), and the
  formula check; `OnStartup`/`OnReconnected` reuse the same pieces. `AppUpdate.CheckAsync()` runs beside it from the scheduler.
- Formula policy (default; say so in your report): with the model editors hidden, an update found mid-session installs quietly like at startup; with
  them shown, a chip offers it ("Formulas: update available, click to apply") using the same pattern as the new-heroes offer, so nothing changes under
  someone editing.

Out: the Updates surface (WP13) and settings changes (WP14); any new network call (the checks are the existing ones); a system-resume hook.

## Suggested design

- `UpdateScheduler(IScheduler clock, Func<double> jitter, Action pass)`; a public static interval constant; `Start()`, `Dispose()`; schedule each next
  tick from the previous one (a `SerialDisposable`), so jitter differs per tick and a late tick after sleep doesn't pile up.
- The pass runs the checks through `Launch(...)` so unexpected exceptions become a toast, as at startup. Never start a second download while one runs
  (`IsDownloadingMatchData`, `IsDownloadingArt` already guard).
- `MainWindowViewModel` creates the scheduler where it creates its other children and disposes it with them.
- The formula offer: `await _models.PublishedAsync()`, `ModelUpdatePlan.For(published, dataDir, askAgain: false)`, `HasWork` → a job chip whose open
  action runs `CheckModelAsync(manual: true)`.

## Tests

`DataMenuTests` with a `TestScheduler`: a tick runs a due match-data check (new patch, stale data) and leaves fresh data alone; offline, modal-open and
busy ticks do nothing and the next tick works; two ticks never start two downloads; with editors hidden a newer model installs, with editors shown a chip
appears and nothing is written; `CheckAllAsync(manual: true)` says how it went (reuse the existing messages). Run `DataMenu`, `AppUpdate`,
`ConnectivityService`, `StatusBar`.

## Acceptance criteria

- [ ] With the app left open, a new patch (fake clock advanced past the interval) produces the same result as a restart.
- [ ] Nothing runs while offline, in a modal, or while a job is busy.
- [ ] Editors-shown never has its files replaced by a tick.
- [ ] Docs say checks also run about every 6 hours.

## Conflicts

`DataMenuViewModel.cs` hot file: after WP07, before WP11/WP13/WP14. `MainWindowViewModel.cs`: other packages touch other methods.

## Notes

WP13 builds its "Check all" on `CheckAllAsync`.
