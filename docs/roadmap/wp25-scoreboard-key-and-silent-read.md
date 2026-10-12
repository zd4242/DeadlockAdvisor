# WP25 Scoreboard key observer and the silent Tab read

Status: todo
Effort: M · Risk: medium (a global key poll, a taller capture, a refactor of Detect's front half) · Depends on: WP24 · Wave: B
Touches: new `src/Services/Contracts/IScoreboardKeyService.cs` and `src/Services/ScoreboardKeyService.cs`, `src/Services/Contracts/IScreenCaptureService.cs` and `ScreenCaptureService.cs`, new `src/Vision/ScoreboardReader.cs`, `src/Features/Match/Detect/DetectAction.cs` (uses the shared reader), new `src/Features/Match/Scoreboard/ScoreboardAction.cs`, `src/Models/AppSettings.cs`, `App.axaml.cs` (`RegisterServices`), `src/Features/MainWindow/MainWindowViewModel.cs`, `src/Features/Settings/Detection/*`, `tests/DeadlockAdvisor.Tests` (fakes, `ScoreboardActionTests`, `ScoreboardKeyTests`, settings tests)
Docs to update: `docs/architecture.md` (sections 2, 8, 10), `docs/detection_model.md` (pipeline), `README.md` (Detect and Settings)

## Goal

When the player holds the game's scoreboard key (Tab by default) in a match the advisor already holds, it quietly reads the board and refreshes the match: net worth now, items when WP26 lands.
It never opens a window, takes focus or makes a sound, and it is off until the player turns it on.

## Why / premise check

1. F9 uses `RegisterHotKey`, which delivers the key to the app **instead of** the game. Tab must reach the game, so it can't be registered ✔ (`GlobalHotkeyService`; check `ShortcutKeys` too).
2. The capture is the top 22% of the game window (`ScreenCaptureService.BandFraction`); the item cells reach about 7 pitches down, roughly the top 58-60% ✔ (WP24 gives the number).
3. `DetectAction.RunAsync` mixes UI (modals, review) with the pure front half (grid, detect, continuity, net worth) ✔; the front half is what Tab needs.
4. A Tab press that finds no applied match, or a different roster, must do nothing visible. Decided: **ignore silently** (logged only).

## Read first

`docs/architecture.md` sections 1, 3, 8, 10; `GlobalHotkeyService`; `ScreenCaptureService` (`FindGameWindow`, `ChooseGameWindow`, `Grab`); `DetectAction`; `RosterContinuity`;
`MainWindowViewModel.DetectFromAnywhereAsync`; `ForegroundService`; the fakes in `Fakes/FakeServices.cs`.

## Scope

In:
- `IScoreboardKeyService`: `Pressed` (an edge, on the UI thread) and a status. Implementation polls `GetAsyncKeyState` about every 30 ms **only while the game's window is in front**
  (`ChooseGameWindow`) and Alt is not held; no hook, no access to the game's process. Windows only, behind `OperatingSystem.IsWindows()`; other platforms report unsupported.
- Settings: `ReadBuildsOnTab` (default **off**) and `ScoreboardKey` (default Tab; the game lets players rebind it), in Settings → Detection with a plain description. A key outside the
  supported set is refused with a message, as the shortcut editor does.
- `IScreenCaptureService.CaptureTopAsync(fraction)` for the taller band, never minimising or activating the advisor.
- `ScoreboardReader`: the shared front half (cached grid → `Detector` → `RosterContinuity` → `NetWorthReader`), returning a record; `DetectAction` calls it, behaviour unchanged.
- `ScoreboardAction`: on a press, wait ~250 ms with the key still held, capture, read off the UI thread (`Task.Run`), and if it is the **same match** (`RosterContinuity.IsSameMatch`)
  add the net-worth snapshot (`VisionApply.SnapshotFor`) and call an items hook (a no-op until WP26). If the board isn't recognised (`Detection.FoundStrip`), retry once at ~600 ms; if the key
  was released, stop. No match, a different roster, a modal open, the settings off: do nothing and log one line.
- Fakes (`FakeScoreboardKey`) and tests below.

Out: reading items (WP26); storing builds (WP27); any scoring; a toast or sound on Tab.

## Suggested design

- Time goes through an `IScheduler` and the key state through a small injectable function (the architecture rule for classes that depend on time), so tests drive presses and holds.
- Run reads one at a time; a press while one is running is dropped.
- Update the match on the UI thread and raise the same change the Detect apply does, so the Match page refreshes.
- The shared reader must produce byte-identical Detect results: `VisionCorpusTests` and `DetectActionTests` stay green, goldens unchanged.

## Tests

`ScoreboardKeyTests` (press edge, held doesn't repeat, release, Alt held ignored, game not in front ignored, rebind, unsupported platform); `ScoreboardActionTests` with `FakeScreenCapture`
(same match adds net worth; different roster, no match, modal open and setting off do nothing; board not found retries once; released early stops; no dialog is shown in any case); settings
tests and `Ui/SettingsPageTests` for the new rows; existing Detect tests untouched and passing. Run `Scoreboard`, `Detect`, `Settings`, `VisionCorpus`.

## Acceptance criteria

- [ ] With the setting off, nothing polls and nothing changes.
- [ ] With it on, a Tab in the game updates net worth for the applied match and never shows a window, toast or sound.
- [ ] A Tab outside a matching match is silently ignored.
- [ ] `DetectAction` behaves as before (corpus and Detect tests unchanged).
- [ ] The Linux and macOS preview builds still compile.

## Conflicts

`MainWindowViewModel.cs` and `AppSettings.cs` are hot (WP27 follows); `DetectAction.cs` is touched by no other package. Can run beside WP26 (different files).

## Notes

- The advisor's own Tab handling (focus navigation) is unaffected: the poller runs only while the game is in front.
- The game must be borderless or windowed for the capture to work, as for F9; say so in the setting's description.
