# WP17 Accessible names, contrast, layouts at minimum size and zoom

Status: done
Effort: M · Risk: low-medium (touches many views; mostly additive) · Depends on: WP13 (so the new chip and flyout are included) · Wave: E
Touches: many `*.axaml` (**`MainWindow.axaml`**, settings views, match views), `src/Controls/*` (`RosterSlot`, `HeroTile`, `ScoreBar`, `Badge`, `SignedAmount`, `HeatCell`, `InfoBadge`), `src/Theme/Palette.cs`, `Themes/*.axaml`, `src/Features/MainWindow/MainWindow.axaml` (minimum width), tests (`Ui/*`)
Docs to update: `docs/architecture.md` (sections 1 and 9: the accessibility conventions), `README.md` (nothing unless a shortcut changes)

## Goal

A screen reader can name what's on screen, text is readable, and every page lays out sensibly at the smallest window and at high zoom.

## Why / premise check

1. Accessibility metadata is nearly absent ✔: only a few `AutomationProperties` in the whole app (the Updates chip has one; they were in `AppUpdateView.axaml` until WP13 replaced it) and a handful of `Focusable`/`IsTabStop`
   settings; icon-only buttons (settings gear, caption buttons, zoom buttons, search and filter, info badges, close ×) rely on tooltips; the painted controls have
   no automation peers (search the source for `AutomationProperties` and `OnCreateAutomationPeer`).
2. `TextFaint #6d6b77` has a contrast of 3.5:1 on `Bg #131317` and 3.0:1 on `Surface2 #22222a` ✔ (WCAG relative luminance; AA wants 4.5:1 for small text) and is used
   at 11 px in about 75 places (agent). Its brush is `{x:Static theme:Palette.TextFaint}` in `Themes/DarkTheme.axaml`, so `Palette` is the one place to change.
   About `#8b8995` gives 5.4:1 on Bg, 4.6:1 on Surface2 and 4.0:1 on Surface3 (computed); `TextDim #a2a0a9` is 7.2:1 on Bg.
3. Layouts at the 900 px minimum window width and at 150% zoom were never rendered (agent): By Item has a fixed 344 px card and Hero Items has seven fixed column
   widths written twice (header and rows) with no horizontal scroll (`ByItemView.axaml`, `HeroItemsView.axaml`); `MinWidth` ignores the zoom.

Re-check each (grep, a small contrast calculation, render a page at 900 px).

## Read first

`docs/architecture.md` sections 1, 6 and 9; `src/Theme/Palette.cs`; `Themes/Styles.axaml`; `UiHarness` (window size, `Screenshot`); two or three of the controls above.

## Scope

In:
- `AutomationProperties.Name` on every icon-only button and `OnCreateAutomationPeer` for the painted controls (names like "Haze, enemy, net worth 25k"); settings toggles named by
  their row titles.
- A UI test that walks the main window's visual tree (with and without the editors) and fails for any `Button` with no text content and no automation name (a short, commented
  allow-list is fine).
- `TextFaint` raised to about `#8b8995` with the hierarchy against `TextDim` checked in rendered pages; assertions on the `Palette` pairs that matter.
- Render each page at the minimum window size and at 150% zoom with `UiHarness`; scale `MinWidth` with zoom; fix what breaks (shared column definitions for the Hero Items
  table, a collapsible By Item card, results rows that fit 420 px).

Out: keyboard navigation of the Match page (WP18); zoom for dialogs and popups (WP19); a light theme; localization.

## Suggested design

- A tiny helper for the common case (an attached property or style that copies `ToolTip.Tip` to the automation name when none is set) is fine if it keeps XAML small, but explicit names win.
- Painted-control peers: a `ControlAutomationPeer` subclass per control returning name, role and (for toggles) state.
- Do the layout pass last, with the PNGs in front of you; list what you changed and what you left (with reasons) in the report.

## Tests

The name-coverage test; `Palette` contrast assertions; UI renders at 900 px and 150% (assert no horizontal overflow where the control tree exposes it, and save PNGs); existing
UI tests unchanged. Full suite (styles and themes are shared infrastructure).

## Acceptance criteria

- [ ] Every icon-only button has an accessible name; painted controls have peers.
- [ ] `TextFaint` meets 4.5:1 on Bg and Surface2.
- [ ] No page clips or scrolls sideways at 900 px or at 150% zoom (or the exceptions are listed).
- [ ] Docs describe the conventions so new controls follow them.

## Conflicts

Last in the chain for `MainWindow.axaml`; touches many views, so run it alone, after WP13, WP18 and WP19 if they're in flight.

## Notes

Show the user before/after PNGs of one or two pages in the report.
