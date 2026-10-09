# WP02 Detect: find the game, reject empty frames, chime on in-game F9

Status: todo
Effort: M · Risk: low-medium (the detection rules apply) · Depends on: none · Wave: A
Touches: `src/Services/ScreenCaptureService.cs`, `src/Vision/Detector.cs`, `src/Features/Match/Detect/DetectAction.cs`, **`src/Features/MainWindow/MainWindowViewModel.cs`** (`DetectFromAnywhereAsync`), new `src/Services/AttentionService.cs` and `src/Services/Contracts/IAttentionService.cs`, `App.axaml.cs` (`RegisterServices`), `src/Models/AppSettings.cs`, `src/Features/Settings/Detection/*`, `tests/DeadlockAdvisor.Tests/Ui/UiHarness.cs` and `Fakes/FakeServices.cs`
Docs to update: `README.md` ("Detecting the match from the screen"), `docs/detection_model.md` (pipeline step 1, capture), `docs/architecture.md` (section 8)

## Goal

Detect reads the game's own window on whichever monitor it's on, says clearly when there is no scoreboard to read, and tells the
user by sound when an F9 pressed inside the game has finished.

## Why / premise check

1. `ScreenCaptureService.GameProcess` is `"deadlock"` (`Process.GetProcessesByName`). The game's executable is
   `project8.exe` (`…/Deadlock/game/bin/win64/project8.exe`; Valve's forums and community guides) ✔. So `FindGameWindow` finds
   nothing and the **primary monitor is always captured**: wrong for a game on monitor 2 or in a window. And `Covers(...)`
   assumes the advisor covers the strip when the game is unknown, so an in-game F9 minimises the advisor, then restores it with
   `Activate()` after the capture (can steal focus from the game; agent). Re-check: search for `GameProcess`; Task Manager →
   Details while the game runs; the Detect log line says "Deadlock window" or "primary monitor (Deadlock's window not found)"
   (`DetectAction`).
2. `Layout.Search` always returns a grid and `Detector.Detect` never rejects by fit, so a lobby, a black frame or an HDR capture
   costs a full cold search (about 5 s) and ends in an empty review of twelve unknowns (agent; `DetectTests` pins the empty review
   for a blank frame).
3. `AppSettings.ComeUpForReview` defaults to false ✔, so with the game in front success and failure are invisible. The README says
   the advisor "only comes up if there's something for you to check".

If 1-3 no longer hold, report instead of re-implementing.

## Read first

`docs/detection_model.md` (all of it: its rules apply to anything here), `docs/architecture.md` sections 8 and 9, the Touches files,
`DetectTests`, `ShortcutTests`.

## Scope

In:
- Process-name matching (`project8`, and `deadlock` in case) and, on a hotkey press, preferring the foreground window when its
  process is the game.
- `Detection.FoundStrip` (a fit gate) and a "Nothing found" message that lists the likely causes.
- `IAttentionService` (a short sound and a taskbar flash) used for an F9 pressed while another app is in front; a setting for it.

Out: widening any search or changing any read (the detection rules); a topmost "peek" window (backlog B08); packages.

## Suggested design

- **Window.** Extract the choice into small testable functions: a name matcher, and "given the foreground process and the running
  game processes, which window". `FindGameWindow` first tries `GetForegroundWindow` → `GetWindowThreadProcessId` → process name;
  else enumerates the names. Keep the primary-monitor fallback.
- **Gate.** `Detector.MinFit` (a constant with a comment on where the number comes from: right grids fit 0.81-0.92, even with four
  players dead) and `Detection.FoundStrip => Fit >= Detector.MinFit`. In `DetectAction.RunAsync`, treat a detection that fails it as
  `null` (the existing "Nothing found" modal) and extend the text: a lobby or loading screen, HDR or exclusive fullscreen capturing
  black, a hidden HUD. Choose the threshold from the lowest real fit in the corpus with margin (start at 0.5) and add a test that
  every corpus capture passes it. Optionally keep the failed capture with the existing capture archive for diagnosis.
- **Attention.** `IAttentionService.Chime(AttentionKind)` (`Done`, `NeedsLook`) and `FlashWindow()`; the Windows implementation uses
  `MessageBeep` and `FlashWindowEx` through P/Invoke behind `OperatingSystem.IsWindows()` (no package); elsewhere a no-op. Register in
  `App.RegisterServices`. `DetectAction` exposes outcomes (`Applied`, `NeedsReview`, `NothingFound`, `CaptureFailed`) through an
  observable that `MatchViewModel` passes on; `MainWindowViewModel.DetectFromAnywhereAsync` notes `IForegroundService.IsAnotherAppInFront`
  **before** the capture (the capture can move focus), then chimes by outcome. New `AppSettings` bool (default true) and a
  `SettingRow` in Settings → Detection ("Sound when Detect from the game finishes").

## Files

Modify: the Touches list. Create: `AttentionService`, `IAttentionService`, a `FakeAttention` in `Fakes/FakeServices.cs`, its
registration in `UiHarness`.

## Tests

- `DetectTests`: outcomes; the gate (a flat frame ends as "Nothing found"; update the test that pins today's empty review,
  deliberately); every corpus capture has `FoundStrip`.
- `ShortcutTests`/UI: with `FakeForeground.IsAnotherAppInFront = true` an applied detection chimes `Done`, a review or failure chimes
  `NeedsLook`; with the advisor in front, silence; the setting off, silence.
- Unit test for the name matcher and the window choice.
- `VisionCorpusTests` and `GoldenVisionTests` must be unchanged (nothing here changes a read): run them.

## Acceptance criteria

- [ ] With the game running as `project8.exe`, the capture comes from its window (the log line says "Deadlock window").
- [ ] A frame with no scoreboard ends in "Nothing found" with causes, quickly, not in an empty review.
- [ ] An in-game F9 makes a sound by outcome when the setting is on, and nothing when the advisor is in front.
- [ ] Corpus tests unchanged; README and `detection_model.md` say what really happens (including that the review stays behind the
      game unless "Switch here for a review" is on).

## Conflicts

`MainWindowViewModel.cs` (WP03, WP12, WP13, WP15 touch other methods), `App.axaml.cs` (WP01, WP15).

## Notes

Defaults chosen: the chime is on by default and only for an F9 pressed while another app is in front. After this lands, correct the
`project8.exe` paragraph in `docs/architecture.md` section 8.
