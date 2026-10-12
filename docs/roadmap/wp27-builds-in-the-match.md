# WP27 Builds in the match: owned marks, roster bars, settings

Status: todo
Effort: M · Risk: low-medium (additive state and UI; no scoring change) · Depends on: WP25, WP26 · Wave: C
Touches: new `src/Models/HeroBuild.cs`, `src/Models/MatchState.cs` (`Builds`, saved and restored), `src/Models/AppSettings.cs` (`SavedMatch.Builds`, `ResultsHideOwned`), `src/Vision/VisionApply.cs` (keep builds across a re-detect of the same twelve), `src/Features/Match/Scoreboard/ScoreboardAction.cs` (the items hook becomes real), `src/Features/Match/Results/*` (an owned mark and the filter), `src/Features/Match/Board/*` (roster mini-bars and item chips), `src/Controls` (a painted bars control with its automation peer), `src/Features/Settings/Detection/*`, `tests/DeadlockAdvisor.Tests` (`MatchStateTests`, `ScoreboardActionTests`, `ResultsViewModelTests`, `Ui/*`)
Docs to update: `docs/architecture.md` (sections 2, 4, 8, 10), `README.md` (Match page, Detect, Settings), `docs/detection_model.md` (how a Tab read reaches the match)

## Goal

What each hero has built is part of the match: kept, saved, shown on the match bar, and used for the first thing it makes easy, marking and hiding the items you already own, so the list answers
"what next". No score changes in this package.

## Why / premise check

1. Nothing in `MatchState` or the results knows what anyone owns ✔ (search `owned`, `inventory`: nothing; backlog B02).
2. `NetWorthHistory` is the pattern for per-match readings: kept per match, saved in `SavedMatch`, kept across a re-detect of the same twelve heroes (`VisionApply.ApplyToMatch`) ✔.
3. `SavedMatch` loads older saves by ignoring what they lack, so a new optional field is safe ✔ (check `LoadSaved` and the JSON options).
4. Hero cards already show items elsewhere (`ItemCardPresenter`), so chips can reuse it ✔.
5. WP24: a bar is a saturating share of the souls in its category (about 0.45 at 4k, 0.75 at 10-12k, 0.9 at 18k, full only past about 28k), correlating 0.9 with the cost sums at full size and worse at 1080p and below. Show it as "how invested", not as a count of souls.

## Read first

`docs/architecture.md` sections 1, 4, 6; `MatchState`; `NetWorthHistory`; `VisionApply`; `MatchBoardViewModel` and `RosterView.axaml`; `ResultsViewModel` / `ResultRowViewModel` (how filters such
as "Hide items your hero rarely builds" work); `PaintedPeer` and one painted control in `src/Controls`; WP25 and WP26's final shape.

## Scope

In:
- `HeroBuild`: the hero's cells (each: category; item id or none; confident or not), the three investment shares (items at cost, unknown cells at their category's median, flagged as an estimate),
  the bars if read, and when it was read. `MatchState.Builds`: the latest per hero, replaced by each read, cleared with the match, kept across a re-detect of the same heroes, saved and restored.
  Decide here whether to keep a short history (needed only if WP29 wants a trend; the default is latest only).
- `ScoreboardAction`'s hook stores what WP26 returns, through the same UI-thread path net worth uses, and the Match page refreshes.
- **Owned for your hero**: a result row whose item you hold shows an "Owned" mark; a Match filter "Hide items you own" (`AppSettings.ResultsHideOwned`, default off) removes them from the list,
  never from a score. The explain panel notes it.
- **Roster**: on the match bar, each hero's three mini-bars (weapon, vitality, spirit) from the build, with a tooltip listing their items by name and flagging unrecognised cells; hover and
  accessibility names ("Ivy, enemy: weapon 70%, vitality 10%, spirit 20%"). Nothing shows for a hero with no read, and nothing when the setting is off.
- Settings → Detection: a line saying when the last read was (not a toggle; the toggle is WP25's).

Out: any effect on scores (WP28, WP29); sell or swap hints; showing items for heroes on the results list.

## Suggested design

- Item ids resolve through `DataStore.Items`; an id the store no longer has is dropped quietly, as `LoadSaved` drops unknown heroes.
- The bars control is painted from `Palette` colours that read at 4.5:1 on their surface; the peer carries the numbers.
- The filter follows the net-worth filter's plumbing (`MatchViewModel`, `AppSettings`, `SettingsChanged`).

## Tests

`MatchStateTests` (builds saved, restored, cleared, kept across a re-detect, dropped for a hero who leaves); `ScoreboardActionTests` (a read lands in the match); `ResultsViewModelTests` (owned
mark, filter on and off, scores unchanged); UI tests rendering the roster with and without builds, with the PNGs read; `AccessibilityTests` still green. Run `MatchState`, `Scoreboard`, `Results`,
`Ui/`.

## Acceptance criteria

- [ ] After a Tab read the match bar shows each hero's bars, and a restart restores them.
- [ ] Your owned items are marked; the filter hides them and changes no score.
- [ ] A match with no read looks exactly as before.
- [ ] An older `settings.json` loads unchanged.

## Conflicts

`MainWindowViewModel.cs`, `MatchViewModel.cs`, `AppSettings.cs` and `MatchState.cs` are hot (after WP25). WP28 and WP29 read `MatchState.Builds`, so they start after this.

## Notes

Show the read's age: a build from ten minutes ago is shown, but marked old (the next Tab refreshes it).
