# WP19 Zoom reaches dialogs (and tooltips and flyouts if feasible)

Status: done
Effort: M (two stages) · Risk: medium (popups sit outside the window's visual tree) · Depends on: none · Wave: D
Touches: `src/Features/Shared/Modals/Base/Modal.axaml`, `ModalViewModel.cs`, `ModalWindow.axaml.cs`, `src/Features/MainWindow/MainWindow.axaml.cs` (`OpenModalWindow`, `SyncModalBounds`), `src/Features/Shared/ItemCard/ItemCardPresenter.cs` (the existing hand-made scaling, as the model), `Themes/Styles.axaml` (stage 2), `tests/DeadlockAdvisor.Tests/Ui/*`
Docs to update: `docs/architecture.md` (section 6), `README.md` (Zoom: what it covers)

## Goal

When the user zooms the app (Ctrl+wheel, Ctrl+=, the status-bar buttons), dialogs scale with everything else; if possible so do tooltips, flyouts and drop-down lists.

## Why / premise check

1. The window's zoom is one `LayoutTransformControl` around the main window's content (`MainWindow.axaml`, `ScaleTransform` bound to `UiScale`) ✔. Modals are separate
   `ModalWindow`s laid over the main window (`MainWindow.OpenModalWindow`), outside that transform, and `Modal.axaml` only scales the dialog card for its entrance animation
   (`Border.modal` 0.9 → 1), with no `UiScale` ✔. So at 150% the page is large and every dialog is at 100%.
2. The item card, shown in a popup-like presenter, is rescaled by hand (`ItemCardPresenter.Zoom` bound to `UiScale`) ✔, which suggests the same limit applies to other popups.
   Tooltips, flyouts, menus and combo-box lists are hosted in their own popup roots and probably aren't scaled either (suspected: verify in a real zoomed run, which a headless
   screenshot may not show).

Re-check by reading `Modal.axaml`, `MainWindow.OpenModalWindow` and the `LayoutTransformControl` in `MainWindow.axaml`.

## Read first

`docs/architecture.md` sections 6; `MainWindow.axaml.cs` modal code; `ItemCardPresenter`; `ZoomLevels`; `Modal.axaml`.

## Scope

In:
- **Stage 1 (modals).** `ModalViewModel` gets `UiScale` (kept in step with `MainWindowViewModel.UiScale` while the dialog is open, including a zoom change with the dialog up), and the dialog card is
  wrapped in a `LayoutTransformControl` using it; `MaxWidth`/`MinWidth` stay in unscaled units and the card still fits the window at the highest zoom (the transform's layout handles that).
- **Stage 2 (popups, optional).** A shared scale source (a small singleton exposing the current scale) and styles that apply it to `ToolTip` and `FlyoutPresenter`/`MenuFlyoutPresenter`
  content and combo-box drop-downs; check anchoring (popups are positioned in unscaled coordinates, so a scaled popup may need its alignment adjusted). If a headless render can't show popups,
  say so and ask the user to check by eye: don't run the app yourself.

Out: changing zoom levels; scaling the OS-level title bar buttons; per-dialog layout changes (WP17 handles page layouts).

## Suggested design

- Subscribe to `UiScale` in `MainWindow.OpenModalWindow` where `modalVm` is created, and dispose with `_modalBoundsSync`.
- Keep the entrance animation on the inner card (`ScaleTransform` on `Border.modal`), with the layout transform outside it.
- Stage 2: prefer one global style per popup type over editing each flyout.

## Tests

UI tests: open a dialog at 100%, 150% and the maximum zoom and render PNGs (Welcome, Confirm, Import, the model update dialog) and look at them; change zoom while one is open; a dialog never exceeds
the window. Existing modal tests unchanged.

## Acceptance criteria

- [ ] Dialogs scale with the app zoom and stay inside the window at every zoom level.
- [ ] Stage 2, if done: tooltips and flyouts scale and stay anchored; if not feasible, the report says what was tried and why.

## Conflicts

`MainWindow.axaml.cs` is also edited by WP07 (`OnClosing`): different method.

## Notes

The item card's own scaling should keep working untouched.
