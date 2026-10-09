# WP18 Keyboard navigation on the Match page

Status: todo
Effort: M-L (three stages) · Risk: medium (focus handling in custom controls) · Depends on: WP16 (same results view) · Wave: D
Touches: `src/Controls/RosterSlot.cs`, `src/Controls/HeroTile.cs`, `src/Controls/RosterRow.cs`, `src/Controls/InfoBadge.cs`, `src/Features/Match/Results/ResultsView.axaml(.cs)`, `src/Features/Match/Board/RosterView.axaml`, `MatchBoardView.axaml(.cs)`, `src/Features/MainWindow/MainWindow.axaml` (tab `Focusable`), tests (`Ui/BoardMouseTests.cs`, `Ui/ShortcutTests.cs`, new keyboard tests)
Docs to update: `README.md` (Shortcuts table: the new keys), `docs/architecture.md` (section 9 if test helpers are added)

## Goal

The whole Match page works without a mouse: move between roster slots, assign and remove heroes, move through the recommendations, open "Why this item?", and reach every
context menu.

## Why / premise check

1. The page is pointer-driven (agent; confirm): `RosterSlot` is a painted `Control` whose `OnPointerPressed` raises routed events (`RemovedEvent`, `MenuRequestedEvent`,
   `SelfRequestedEvent`, `FocusRequestedEvent`, `EmptyClickedEvent`); its remove and focus badges are painted on hover only; `HeroTile` handles pointer presses; `ResultsView.axaml.cs`
   selects rows from `PointerPressed` on the list (`OnListPressed`); the wiki/formula and role menus open on right-click; the page tabs are `Focusable="False"`.
2. Very few elements take focus (6 focus/tab attributes in XAML, 3 in code), so Tab skips the roster and the list ✔.

Re-check by reading the four controls and `ResultsView.axaml(.cs)`.

## Read first

`docs/architecture.md` sections 1 and 9; the five routed events on `RosterSlot` and who handles them (`RosterView`, `MatchBoardViewModel`); `ShortcutKeys` (don't clash with
`_reserved` or the Match page's Alt+1/2/3 and F6-F9); `BoardMouseTests` and `ShortcutTests` for how input is simulated headlessly.

## Scope

In (stages, a green commit after each):
1. **Roster.** Slots are focusable with a visible focus ring; Left/Right move along the bar (use `KeyboardNavigation.DirectionalNavigation` on the row if it fits); Enter or Space does what a
   left click does; Delete or Backspace removes; F focuses an enemy; the Menu key and Shift+F10 open the role menu; an empty slot's Enter starts adding to that team.
2. **Results.** The list takes focus; Up/Down/Home/End move the selection (skipping section headers, which Enter or Space fold); the selection updates "Why this item?"; Enter/Space re-selects or
   clears as a click does; the Menu key opens the row's context menu; a focus style on the selected row.
3. **Badges and tabs.** `InfoBadge` is focusable and opens its text on Enter or Space; the page tabs and status-bar buttons become focusable where that doesn't steal focus from
   typing in a page (the tabs are `Focusable="False"` on purpose, "you usually switch pages to start typing into one": keep that behaviour for the mouse and make keyboard reach them another way, such as Ctrl+Tab, which already cycles pages).

Out: accessible names and peers (WP17); changing what any key does elsewhere; new global shortcuts beyond these.

## Suggested design

- Raise the same routed events the pointer raises, so `RosterView` and the view models don't change.
- Keep `Render` cheap: draw the focus ring only when `IsKeyboardFocusWithin` / `:focus-visible`.
- Document each new key in the README Shortcuts table.

## Tests

Headless keyboard tests next to `BoardMouseTests`: Tab reaches the first slot; arrows move; Enter on an ally makes them you; Delete removes; F focuses an enemy; Up/Down changes the selected
recommendation and the explain panel follows; the Menu key opens the menu (assert the flyout is open); focus survives a rescore. Render a focused slot and a focused row to PNG and look.

## Acceptance criteria

- [ ] Every pointer action on the roster and results has a keyboard equivalent.
- [ ] A visible focus indicator on slots and rows.
- [ ] Typing into a search box isn't hijacked by the new keys.
- [ ] README Shortcuts lists the keys.

## Conflicts

After WP16 (`ResultsView.axaml(.cs)`); WP17 follows and adds names.

## Notes

If Avalonia's headless platform can't open a flyout from the keyboard, assert on the command/event raised instead and say so.
