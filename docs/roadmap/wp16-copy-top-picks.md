# WP16 Copy the top picks to the clipboard

Status: todo
Effort: S · Risk: low · Depends on: none · Wave: B
Touches: new `src/Services/Contracts/IClipboardService.cs` and `src/Services/ClipboardService.cs`, `App.axaml.cs` (`RegisterServices`), `src/Features/Match/Results/ResultsViewModel.cs`, `src/Features/Match/MatchViewModel.cs`, `src/Features/Match/Board/RosterView.axaml` (the match bar's ⋯ menu) or the results header, `src/Features/Match/Explain/ExplainView.axaml`, `tests/DeadlockAdvisor.Tests/Fakes/FakeServices.cs`, `Ui/UiHarness.cs`
Docs to update: `README.md` (the Match page), `docs/architecture.md` (section 2 if a service is added)

## Goal

One click copies the recommendation list as text to paste to teammates or into Discord, and the "why" arithmetic of the selected item can be copied too.

## Why / premise check

1. The app never uses the clipboard ✔ (search the source for `Clipboard`: nothing). Results can only be read on screen.
2. Services reach the main window the way `FilePickerService` does (`IClassicDesktopStyleApplicationLifetime.MainWindow`, then the `TopLevel`), so a clipboard
   service can follow that pattern ✔.

## Read first

`docs/architecture.md` sections 1, 4 and 10; `FilePickerService`; `ResultsViewModel` (the rows and how they're ranked and filtered; `ResultRowViewModel`);
`ExplainViewModel` / `ExplainText` (what the arithmetic text is); `RosterView.axaml` for the ⋯ menu.

## Scope

In:
- `IClipboardService.SetTextAsync(string)` and a Windows/Avalonia implementation; a fake for tests.
- Text for the **displayed** list (respecting the rank choice, cutoff, filters and search): a heading naming your hero, your allies and the enemies, then the top N
  (default 10) as `1. Juggernaut (tier 4, 6,400): 8.0`, one per line; Markdown-friendly so it pastes well into Discord.
- A "Copy top picks" command on the Match page (the match bar's ⋯ menu, next to its other actions; if the menu is the wrong place, the results header) and a
  small copy button in the "Why this item?" header for that item's text.
- A short toast "Copied the top 10".

Out: images; sharing links; copying the whole app state; a hotkey.

## Suggested design

- A static builder (`PicksText.Build(match, rows, count)`) so it's unit-testable without UI; it takes the already-ranked rows from `ResultsViewModel` rather than
  re-scoring.
- Hero and item names come from the store; tiers and prices from `ScoredItem`/`Item`; use `Format` helpers already in `src/Core` for numbers.
- Disable the command while the list is empty.

## Tests

`PicksText` unit tests (one hero, full match, ties, tier grouping on/off, empty list); a view-model test with the fake clipboard (command copies, toast shows,
disabled when empty); a UI render of the menu.

## Acceptance criteria

- [ ] "Copy top picks" puts a readable list on the clipboard matching what's on screen.
- [ ] Disabled with nothing to copy.
- [ ] No new package; tests use the fake.

## Conflicts

`ResultsView.axaml(.cs)` is also touched by WP18 (after this); `MatchViewModel.cs` by WP12.

## Notes

If the real clipboard needs a window that isn't up (tests), `ClipboardService` should return quietly rather than throw.
