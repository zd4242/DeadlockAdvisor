# WP15 Single-instance guard

Status: todo
Effort: S · Risk: low-medium (it runs before anything else; the update installer must still work) · Depends on: none · Wave: A
Touches: `Program.cs`, new `src/Core/SingleInstance.cs`, `App.axaml.cs`, `src/Features/MainWindow/MainWindowViewModel.cs` (a public `BringForward()`), new `tests/DeadlockAdvisor.Tests/SingleInstanceTests.cs`
Docs to update: `docs/architecture.md` (section 3), `README.md` (a line under Install)

## Goal

Starting the app a second time raises the running window and exits, instead of running two copies that share `settings.json`, the data folder and a
hotkey that only one of them can hold.

## Why / premise check

1. There is no single-instance guard ✔ (search the source for `Mutex`, `SingleInstance`, `NamedPipe`, `EventWaitHandle`: nothing).
2. Two instances overwrite each other's `settings.json` (last writer wins), edit the same CSVs (README: "Nothing locks the files, so don't edit them from
   two places at once"), and the second can't register F9 (`GlobalHotkeyService` reports `Taken`) ✔ (read it).
3. `Program.Main` runs `AppUpdateService.InstallIfAsked(args)` first: the update installer is a copy of this exe started with `--install …` that waits for
   the old process to exit and then installs; it must not be blocked or counted as a second instance ✔ (read `Program.cs` and `AppUpdateService`).

## Read first

`docs/architecture.md` section 3; `Program.cs`; `AppUpdateService.InstallIfAsked` / `InstallIfDownloaded`; `MainWindow.BringForward` and
`MainWindowViewModel`'s `BringForwardAction`; `JsonSettingsService.AppDataPath` and `HomeVariable`.

## Scope

In:
- After `InstallIfAsked` returns false, on Windows only: take a per-user named mutex; if another instance holds it, signal that instance and exit.
- The running instance listens for the signal and raises its window (restoring it if minimised, as `BringForward` does).
- The mutex name includes a short hash of `JsonSettingsService.AppDataPath`, so a scratch instance started with `DEADLOCK_ADVISOR_HOME` can run beside the
  real one.

Out: command-line forwarding (`--install` stays separate); Linux and macOS (the code stays behind `OperatingSystem.IsWindows()`).

## Suggested design

- `SingleInstance.TryBecomePrimary(string name)` returns a disposable handle or `null`; `SingleInstance.SignalPrimary(string name)`;
  `SingleInstance.Listen(string name, Action onSignal)` runs a background thread on an auto-reset `EventWaitHandle` named `<name>.show`.
- In `Program.Main`: `if (OperatingSystem.IsWindows()) { var instance = SingleInstance.TryBecomePrimary(...); if (instance is null) { SingleInstance.SignalPrimary(...); return; } }`
  keeping the handle alive for the process's life.
- `App.OnFrameworkInitializationCompleted`: start listening after the main window exists; on signal, `Dispatcher.UIThread.Post` a call to the main window
  view model's new public `BringForward()` (wrapping `RequestViewAction(BringForwardAction)`).
- Release the handle on exit so "Restart now" (the installer starts the new exe only after this one exits) isn't refused.

## Tests

`SingleInstanceTests` with a unique name per test: the first caller becomes primary, the second doesn't; signalling invokes the primary's callback once;
disposing the primary lets a new one take over. (The Windows-only parts can be skipped on other platforms with the repo's usual pattern.)

## Acceptance criteria

- [ ] A second launch exits and the first window comes forward (restored if minimised).
- [ ] An update's installer and restart still work (`AppUpdateTests` pass).
- [ ] A scratch instance (`DEADLOCK_ADVISOR_HOME` different) starts alongside.
- [ ] Never launch the app to check this: the tests cover the helper; say in your report that the end-to-end behaviour needs a human run.

## Conflicts

`MainWindowViewModel.cs` and `App.axaml.cs`: small edits, other packages touch other parts.

## Notes

A mutex abandoned by a crashed instance is acquired normally by the next start (handle `AbandonedMutexException`).
