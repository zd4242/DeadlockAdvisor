# WP21 Post-match review: your build against the advice, and a net-worth curve

Status: todo
Effort: L (three stages) · Risk: medium (new UI, new view models) · Depends on: WP20 · Wave: C
Touches: `src/Features/Match/Import/*` (`ImportMatchViewModel`, `ImportMatchView.axaml`, `ImportMatchAction`), new `src/Features/Match/Review/*` (view model and view), new `src/Controls/CurveChart.cs`, `src/Theme/Palette.cs` (read only), tests (`Ui/ImportMatchTests.cs`, new review tests)
Docs to update: `README.md` (the Match page: Import), `docs/architecture.md` (sections 2 and 4)

## Goal

After importing a finished match, one more click opens a review: what you built (in the order you bought it) with how the advisor rates each item for that line-up, the items it would have picked
that you didn't buy, and a net-worth curve for both teams.

## Why / premise check

1. WP20 must be `done`: `LookedUpMatch.Players[].Items` and `.Worth` exist. If not, stop and say so.
2. Import already applies the roster as roles (`ImportMatchAction.Apply`) and knows who you are (`ImportMatchViewModel.Self`, from the saved Steam account or a click) ✔; scoring a line-up is
   `ItemScoring.ScoreAll(store, matrix, match, netWorth)` and items map from the API's id through `Item.GameId` ✔.
3. The app has no chart control; the painted controls (`ScoreBar`, `RosterSlot`) show the pattern for custom drawing with `Palette` colours ✔.

## Read first

`docs/architecture.md` sections 2, 4 and 6; `ImportMatchViewModel`, `ImportMatchAction`, `ImportMatchView.axaml`; `ItemScoring.ScoreAll` and `ScoredItem`; `ResultsViewModel` (how tiers and
ranks are presented); `ScoreBar` for painted-control style; the confirm/message modal pattern in `Features/Shared/Modals`.

## Scope

In (stages, a green commit after each):
1. **The numbers.** A `BuildReviewViewModel` (no UI) built from the store, the matrix, the applied `MatchState` and your `MatchPlayer`: your final items (bought and not sold) in purchase
   order, each with the advisor's score for the imported line-up, its rank within its tier (1 = the advisor's top pick) and a verdict (the advisor liked it / was neutral / rated it below 0);
   "the advisor's top picks you didn't buy" (positive-score items not in your final build, best first, a handful per tier); a summary line ("5 of your 9 items are among the advisor's top 5 of
   their tier"). No net-worth weighting (a finished match has only a final state).
2. **The view.** The Import dialog gets "Apply and review build" beside Apply (enabled once you're known and your build is in the lookup); it applies the roster as today, closes the dialog,
   then shows a review modal (item icons via the existing art host, tier chips, score bars). The existing "Apply" is unchanged.
3. **The curve.** `CurveChart`: a painted control for two series (each team's total net worth over time from the players' curves), time on the x axis (mm:ss) and souls (compact, as the app
   shows them) on the y axis, team colours from `Palette` (ally green, enemy red), a hover readout if cheap. Shown at the top of the review.

Out: replaying the advisor at a chosen minute (a scrubber over the curve is the natural follow-up: leave a seam); saving reviews; reviewing other players' builds (your own is the point); anything
that changes scoring.

## Suggested design

- Use the **store's** items by `GameId`; ignore purchases with no matching `Item` (abilities, unreleased items) and say how many were ignored if it matters.
- Open the review after `_modals.CloseModal()` of the import dialog (a second `ShowModal` while one is open is dropped: close first).
- Keep the view model testable with `TestStore` and hand-made `MatchPlayer` data; the chart gets its series as plain numbers.

## Tests

`BuildReviewViewModelTests` (rank and verdict maths on a small store, sold items excluded, unknown items ignored, empty build, top-picks-you-missed exclude what you bought); `Ui/ImportMatchTests` (the new
button, Apply unchanged, review opens); `CurveChart` render test to PNG (two series, one series, empty); look at the PNGs.

## Acceptance criteria

- [ ] Importing a match and choosing "Apply and review build" shows your build with the advisor's rating of each item, the top picks you missed, and the curve.
- [ ] Plain Apply behaves exactly as before.
- [ ] The review works for a match where you bought nothing the store knows (an empty-state message, not a crash).

## Conflicts

`ImportMatchViewModel`/`View` are touched by nothing else; `Palette.cs` is read-only here (WP17 may change one colour).

## Notes

Show the user the rendered review in your report; the layout is theirs to steer. If the time-sliced `stats[]` is coarse for short matches, say what resolution you saw.
