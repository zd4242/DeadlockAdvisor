# WP03 Ctrl+F on Hero Items, longer error toasts and Recent messages, a save-failed style

Status: done
Effort: S · Risk: low · Depends on: WP01 · Wave: B
Touches: `src/Features/HeroItems/HeroItemsViewModel.cs` and `HeroItemsView.axaml(.cs)`, `src/Services/NotificationService.cs`, `src/Services/Contracts/INotificationService.cs`, `src/Features/Shared/Notifications/*`, **`src/Features/MainWindow/MainWindow.axaml`** (Help menu, the saving style), `src/Features/MainWindow/MainWindowViewModel.cs`
Docs to update: `README.md` (Shortcuts: Ctrl+F), `docs/architecture.md` (section 6)

## Goal

Three small rough edges go: Ctrl+F on Hero Items finds that page's search instead of jumping to Match; an error stays on screen
long enough to read and can be looked up again; "save failed" doesn't look like "saving".

## Why / premise check

1. `HeroItemsViewModel` doesn't implement `ISearchablePage` ✔ (only Match, Hero Traits and Item Formulas do), so
   `MainWindowViewModel.Find()` sends Ctrl+F from Hero Items to the Match page. The README says Ctrl+F focuses "the current page's
   search"; Hero Items has a searchable hero box (`HeroPicker`, a `SearchComboBox`).
2. `NotificationService` shows every notification, errors included, for 3 seconds (`_defaultDuration`), including
   `ShowError(message, exception)`; nothing keeps them ✔. `NotificationOverlay` has no close control.
3. The status bar's save text uses one style (`TextBlock.saving`) for "saving...", "saved" and "save failed" ✔.

Re-check by reading those files; if any is already fixed, skip that part.

## Read first

`docs/architecture.md` section 6; `HeroTraitsViewModel.FocusSearch` and `HeroTraitsView.axaml.cs` (the pattern: a view action the
view answers); `NotificationOverlayViewModel`.

## Scope

In:
- `HeroItemsViewModel : ISearchablePage`; `FocusSearch()` raises a view action and the view focuses `HeroPicker` (open its search).
- Errors last longer (about 10 s) unless a duration is given, close when clicked, and the service keeps the last 20 notifications
  with their time; Help → "Recent messages…" lists them in the existing message dialog.
- The "save failed" text gets its own style (the palette's negative colour).

Out: queueing a second modal instead of dropping it (`CloseModal()` has no identity, so a queue risks closing the wrong dialog);
a bell or persistent notification centre.

## Suggested design

- Notification time: add `DateTimeOffset At` to the `Notification` record with a default of now (positional record: add last, with a
  default).
- Click to dismiss: a `Dismiss(NotificationViewModel)` on `NotificationOverlayViewModel` and a pointer handler in the template;
  the timer that removes it must tolerate it being gone already.
- Recent list: a bounded list in `NotificationService` (`IReadOnlyList<Notification> Recent`), read by a new
  `MainWindowViewModel.RecentMessagesCommand` that formats "10:42  Error  …" lines for `_modals.ShowMessage`.
- Save style: expose a `SaveFailed` flag from the view model (`SaveState.Failed`) and bind `Classes.failed` on the text; add the
  style next to `TextBlock.saving` in `MainWindow.axaml`.

## Tests

`HeroItemsViewModelTests` / `Ui/HeroItemsPageTests` (Ctrl+F from Hero Items focuses the hero box, doesn't change page),
notification tests (default error duration, explicit duration wins, dismiss, recent list bounded), `StatusBarTests` (failed style).

## Acceptance criteria

- [ ] Ctrl+F on Hero Items focuses the hero search and stays on the page.
- [ ] An error toast stays about 10 s, can be dismissed with a click, and appears in Help → Recent messages.
- [ ] "save failed" is visibly different from "saving".
- [ ] README Shortcuts row for Ctrl+F is true.

## Conflicts

After WP01 (`NotificationService.cs`). `MainWindow.axaml` is also edited by WP09, WP13 and WP17; `MainWindowViewModel.cs` by WP02,
WP12, WP13 and WP15.

## Notes

Default chosen: error toasts last 10 s.
