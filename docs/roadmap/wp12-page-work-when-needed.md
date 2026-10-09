# WP12 Match rescoring only when shown; Hero Items table cache

Status: done
Effort: M · Risk: low-medium · Depends on: WP03 (both edit `HeroItemsViewModel`) · Wave: C
Touches: `src/Features/Match/MatchViewModel.cs`, `src/Features/HeroItems/HeroItemsViewModel.cs`, **`src/Features/MainWindow/MainWindowViewModel.cs`**, tests (`MatchPageTests`, `MatchBoardTests`, `HeroItemsViewModelTests`)
Docs to update: `docs/architecture.md` (section 4)

## Goal

Typing in the model editors no longer rescores a hidden Match page on every pause, and sorting or filtering Hero Items no longer rebuilds its
whole table each time.

## Why / premise check

1. Every page stays alive (`MainWindow.axaml` keeps all panels), and `MatchViewModel` subscribes `_data.ScoresChanged` → `Refresh()` ✔. An edit
   in Hero Traits or Item Formulas raises `ScoresChanged` at most every 100 ms (`DataService.MarkEdited`), and each `Refresh` does `ScoreAll`, re-ranks
   the list and recomputes `Scale()`; `DataService.RebuildMatrix` makes a new `ScoreScales` every time, which re-measures 200 line-ups (about 40 ms
   per shape by the doc; agent estimates 40-100 ms per tick) the first time the Match page asks. All of it runs while the page isn't visible.
2. `HeroItemsViewModel.Refresh()` calls `HeroItemTable.Build(...)` on every hero, mode, range, sort, tier-toggle and slider change ✔
   (`HeroItemsViewModel`; the table includes `HeroFits`, per-hero lifts for the whole roster, agent). Sorting and the usage slider only reorder or
   filter rows that were already built.

Re-check by reading both `Refresh` methods and the subscription.

## Read first

`docs/architecture.md` sections 4 and 5; `MatchViewModel` (`Refresh`, `Rebind`, the `ScoresChanged` and `StoreReplaced` subscriptions);
`MainWindowViewModel` (`IsMatchPage`, `CurrentPage`); `HeroItemsViewModel` (`Refresh`, `PickedSegments`, `Range`); `HeroItemTable`, `HeroFits`.

## Scope

In:
- `MatchViewModel` learns whether it is shown (a small method called by `MainWindowViewModel` when `IsMatchPage` changes). While hidden,
  `ScoresChanged` only sets a dirty flag; becoming shown with the flag set refreshes once. `StoreReplaced` still rebinds immediately. The default
  is "shown" so code that builds a `MatchViewModel` without the main window behaves as today.
- Hero Items keeps the last built `HeroItemTable` keyed by hero, mode, rank range and the picked patches' start times; sorting, tier toggles and the
  usage slider reuse it; it is dropped on `StoreReplaced` (and whenever the match counts change).

Out: changing any score or table value; caching the Match scoring itself.

## Suggested design

- A private record for the cache key; invalidate with a generation counter bumped by the data service's `StoreReplaced`.
- For testability inject the table builder (`Func<...>` defaulting to `HeroItemTable.Build`) so a test can count builds.

## Tests

`MatchPageTests`/`MatchBoardTests`: with the page hidden an edit doesn't refresh (count `ScoreAll` through a seam or observe that `Results` didn't
change), showing it does; `StoreReplaced` still rebinds when hidden. `HeroItemsViewModelTests`: one build for hero+mode+range+patches however
many sorts, tier toggles and slider moves; a changed hero, mode, range or patch rebuilds; `StoreReplaced` rebuilds. Existing Hero Items and Match UI
tests pass unchanged.

## Acceptance criteria

- [ ] No Match rescoring while the page is hidden; one refresh when it is shown again.
- [ ] Hero Items sort/filter doesn't rebuild the table.
- [ ] Rendered pages (mockups) look the same.

## Conflicts

After WP03 (`HeroItemsViewModel.cs`). `MainWindowViewModel.cs`: WP02, WP03, WP13, WP15 touch other methods.

## Notes

If `ScoreScales` turns out to be the only cost, a lazy "measure when first read" fix there is an acceptable smaller alternative; say so in the report.
