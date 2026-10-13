# WP31 Read activity cues: show when a read is happening and what it changed

Status: todo
Effort: L · Risk: low-medium (additive UI and one new observable on the readers; no scoring, no change to what is read) · Depends on: WP27 (done), and after WP29 (it edits `MatchViewModel.cs` and `MatchView.axaml`, which WP29 has open) · Wave: E
Touches: new `src/Features/Match/Activity/*` (`ReadActivity`, `ReadReport`, the status chip and its flyout), `src/Features/Match/Scoreboard/ScoreboardAction.cs`, `src/Features/Match/Detect/DetectAction.cs`, `src/Features/Match/MatchViewModel.cs`, `src/Features/Match/Board/*` (`RosterSlotViewModel`, `RosterView.axaml`, `MatchBoardViewModel`), `src/Features/Match/Results/*` (row marks), `src/Theme` (one short pulse style), `src/Models/AppSettings.cs` (one setting), `src/Features/Settings/Detection/*`, `tests/DeadlockAdvisor.Tests` (`ScoreboardActionTests`, `DetectActionTests`, `MatchBoard*`, `ResultsViewModelTests`, `Ui/*`, `AccessibilityTests`)
Docs to update: `docs/architecture.md` (sections 2, 8, 10), `README.md` (Match page, Detect, Settings), `docs/detection_model.md` (one line: what a read reports)

## Goal

Make every read visible in the advisor's window: that one is happening, that it finished, and exactly what it changed (a hero's net worth, items bought or sold, the order of the list). The cue is passive. It
lives only inside the advisor's own window, so the Tab read stays as silent in the game as WP25 made it (no window, focus, toast, sound or taskbar flash), and the player who glances over from the game, or
alt-tabs back after a death, can tell at once what the last read did.

## Why / premise check

1. **The Tab read says nothing.** `ScoreboardAction` ends every press with `Ignored(...)` or a log line and calls `changed()`; no state is exposed, so the Match page rescores and the bar changes with nothing marking
   that a read just happened ✔ (`ScoreboardAction.OnPressedAsync`, `TryReadAsync`; architecture.md section 8, "It shows nothing and plays nothing"). That was right for the game and wrong for the advisor window.
2. **Detect's only cue is a modal that appears late.** `DetectAction.ReadWithProgressAsync` shows a `ProgressModalViewModel` only when the grid search outlasts `_progressDelay`; a quick F9 or any
   later stage (review, apply) has no cue, and a read from the system-wide F9 while the window is behind the game shows nothing until the window comes forward ✔.
3. **What a read changed is not recorded.** `RosterSlotViewModel` keeps the latest `NetWorth`, `NetWorthChange` ("+3.1k in 2 min") and `Build`, replaced in place by `Fill`, so the previous value is gone the
   moment the new one is set; the Match page re-scores in `MatchViewModel.MatchUpdated` and the list reorders with no sign of which rows moved ✔.
4. **Ignored presses are invisible too.** `DifferentMatch`, `NotFound`, `NoArt` and `Released` are the cases where the player has turned the feature on and nothing happens; today only the log says why ✔
   (`ScoreboardOutcome`). Seeing "Tab read ignored: these aren't the match's heroes" is more useful than a silence they must debug from a log.
5. The state to show already exists: `MatchState.NetWorth` snapshots, `MatchState.Builds` with `Apply` returning whether a hero's build changed, `BuildSummaries` (age, `IsOld`), and the existing animations pattern in
   `NotificationOverlay.axaml` (`Style.Animations`) ✔.
6. Re-check when starting: that `ScoreboardAction` and `DetectAction` still expose no activity, and that WP29 has landed (`git log` for `BuildShifts`).

## Read first

`docs/architecture.md` sections 1, 5, 6, 8, 10; `ScoreboardAction`, `DetectAction` (`RunAsync`, `ReadWithProgressAsync`), `MainWindowViewModel.DetectFromAnywhereAsync`; `MatchViewModel.MatchUpdated` and `WhenReplaced`;
`MatchBoardViewModel` and `RosterSlotViewModel` (`Fill`); `HeroBuild.Apply`; `BuildSummaries`; `NotificationOverlay.axaml` (an animation); `docs/detection_model.md` ("The scoreboard's items");
`Tests/Ui` headless rendering and `AccessibilityTests`.

## Scope

Stages; stop at a stage boundary with a green commit if the session runs short.

**Stage 1: one activity state, one chip.**
- `ReadActivity` (a small service or a property object on `MatchViewModel`, whichever the DI conventions in section 3 favour) with a state: `Idle`, `Reading(source, since)`, `Updated(ReadReport)`,
  `Skipped(reason)`, `Failed(reason)`. `source` is Tab or Detect. Both actions set it through the UI scheduler; tests drive it with a test scheduler.
- `ScoreboardAction` reports `Reading` after the 250 ms settle (when it is actually capturing), then `Updated` with the report, or `Skipped` with a short plain reason mapped from `ScoreboardOutcome`
  (`DifferentMatch`: "The heroes on the board aren't this match's. Detect again to switch."; `NotFound`: "No scoreboard found. Hold the key until the board is up."; `NoArt`; `CaptureFailed`).
  `Off`, `Busy`, `Released`, `ModalOpen` and `NoMatch` stay quiet (they are the player's own doing or nothing to say).
- `DetectAction` reports `Reading` from the start (so a fast read has a cue; the late modal stays for a long search) and `Updated` or `Skipped` at the end, with the same vocabulary as its `DetectOutcome`.
- `ReadReport`: when; the source; per hero (by slot) the net worth before and after, the items added and removed (names, from `DataStore.Items`), and the heroes whose bars moved by more than a few points;
  the number of results rows whose rank changed (Stage 3). Built where the data is already compared (`HeroBuild.Apply` already knows whether a hero's build changed; extend it to say what).
- **A status chip on the match bar** beside Detect: a small dot or ring while `Reading` (a slow indeterminate pulse, not a spinner that suggests a long wait), a tick with the time when `Updated`
  ("Read 2:41 · 3 items, net worth"), a plain muted line for `Skipped`. The text ages ("2 min ago", amber when `IsOld`, the same rule as the bars). Clicking it opens a **flyout: what the last read changed**,
  per hero, plus the last ten reads kept for the session (not saved), so a player back from a fight can read what happened while they weren't looking. It also carries the way out for `Skipped`
  (a "Detect again" button for `DifferentMatch`).
- Accessible name and a polite live-region announcement for the chip's text, so a screen reader hears "Read the scoreboard: 3 items, net worth updated" without moving focus.

**Stage 2: change cues on the roster.**
- `RosterSlotViewModel` keeps what the last read changed for that hero (`ReadChange`: net worth delta, items added and removed, a monotonically increasing `ReadSerial`). `Fill` computes it from the previous values
  instead of overwriting them blind.
- On the match bar: the net worth number briefly highlights and shows its delta ("+1,240") for about two seconds; the mini-bars ease to their new lengths; a hero with new items gets a one-off glint on the
  bars and the new item names at the top of their tooltip ("New: Mystic Burst, Improved Spirit").
- **Unseen changes stay marked.** A two-second flash is easy to miss from the other monitor, so a hero changed by a read carries a small dot that stays until the player has seen it: the window is
  activated, or the pointer rests on the match bar. The flash gets attention; the dot answers "did anything change while I was away?".
- Motion rule: one short pulse style in `src/Theme`, durations at most 600 ms for an ease and 2 s for a highlight, never looping except the reading ring. Honour a setting (below) and, when the platform
  reports reduced motion, default it off. Colour is never the only cue (the delta text and the dot carry meaning too), and every pulse colour reads at 4.5:1 on its surface (`Palette`).

**Stage 3: the list shows what moved.**
- After a read changes the order, rows that moved get a short "▲2" / "▼1" mark that fades after a few seconds, and a row whose item the player just bought gets the "Owned" mark with a fade-in rather than
  a pop. Rows that dropped out because of "Hide items you own" are counted in the chip's report ("2 items you bought left the list"), so the list shrinking isn't a mystery. The list's selection and scroll
  position are untouched: the cue never moves anything under the pointer.

**Settings (one):** Settings → Detection → *Show read activity* (`AppSettings.ShowReadActivity`, default on) turns off the animations and the unseen dots; the chip's text stays. No sound and no taskbar
flash are added here, in any state: `SoundOnDetect` and `ComeUpForReview` keep their meaning for F9 and are unchanged.

Out: any change to what a read does or to scoring; sounds, toasts or window focus for the Tab read; an overlay drawn over the game (a different product: it would need to render in the game's window); a saved
read history; changing the review dialog.

## Suggested design

- Cues are driven by view model state with an expiry (`ReadSerial` and a scheduler-timed `IsFresh` that falls back to false), not by reading the control tree, so tests set the state and assert it, and a PNG
  can be rendered with the flash on. Styles key off classes (`fresh`, `unseen`) toggled by the binding, so there is one place that names the timings.
- Keep the diff cheap: compute the report from the values the apply step already touches; do not rescan the match.
- `Skipped` replaces nothing: a later `Updated` overwrites it, and an `Updated` is not overwritten by `Busy`/`Off`-class presses.
- The chip is part of the match bar, not a notification: `INotificationService` stays unused here (toasts are for things the player must act on).
- If the platform offers no reduced-motion signal, the setting is the control; do not add platform-specific code behind `OperatingSystem.IsWindows()` unless it also compiles on the Linux and macOS previews.

## Tests

`ScoreboardActionTests` (state goes Reading → Updated with the right report on a same-match read; Skipped with the mapped text for a different roster, no board, no art; quiet outcomes leave the state alone;
state sequence under a test scheduler); `DetectActionTests` (Reading from the start of a fast read, Updated and Skipped per `DetectOutcome`); `HeroBuild` / `MatchStateTests` (the report's items added and
removed, a build with no change reports nothing); `MatchBoard*` (a roster slot's `ReadChange`, `IsFresh` expiry, the unseen dot cleared on activation); `ResultsViewModelTests` (moved ranks, owned fade-in,
the hidden-owned count; scores unchanged); `Ui/*` rendering the match bar idle, reading, updated with the flash on, updated with the dot, and skipped, the PNGs read; `AccessibilityTests` for the chip's
name and live region; settings tests for `ShowReadActivity` and the older-`settings.json` default. Run `Scoreboard`, `Detect`, `MatchBoard`, `Results`, `Settings`, `Ui/`, `Accessibility`.

## Acceptance criteria

- [ ] Holding the scoreboard key shows the reading cue on the match bar and then what changed; nothing else on the machine changes (no window, focus, sound, toast or taskbar flash).
- [ ] A hero whose net worth or items changed is marked on the match bar, with the delta, and stays marked until seen.
- [ ] A press that is ignored for a reason the player can fix says so in the chip, in plain words.
- [ ] F9 shows the same cues, including for a read quick enough that no progress dialog appeared.
- [ ] The list marks rows that moved or became owned without moving the selection or scroll.
- [ ] With *Show read activity* off the animations and dots are gone and the chip's text stays; an older `settings.json` loads with it on.
- [ ] Idle, reading, updated and skipped are rendered and looked at; `AccessibilityTests` are green; Linux and macOS preview builds still compile.

## Conflicts

`MatchViewModel.cs`, `MatchView.axaml`, `MainWindowViewModel.cs` and `AppSettings.cs` are hot: start after WP29 has landed. `ScoreboardAction.cs` and `DetectAction.cs` are touched by no other open package. WP13 (the
Updates chip) is a separate surface; keep the two chips visually related but do not merge them.

## Notes

- Ideas considered and left out: a global "reading" toast (noisy, and intrudes when the advisor is on top of the game); a sound for the Tab read (the player is mid-fight); an always-on-top mini overlay (see Out);
  a net worth sparkline per hero on the bar (that is WP21's curve, after a match).
- A decision for the user after living with Stage 2: whether the unseen dot should also appear on the Windows taskbar button (a badge, not a flash) when the window is behind the game. It stays off here because it
  is outside the window.
