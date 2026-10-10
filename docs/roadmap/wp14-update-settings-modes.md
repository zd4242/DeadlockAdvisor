# WP14 Settings → Data modes; manual match-data "Check now"

Status: done
Effort: M · Risk: low-medium · Depends on: WP13 · Wave: D
Touches: `src/Features/Settings/Data/DataSettingsViewModel.cs` and `DataSettingsView.axaml`, **`src/Features/MainWindow/DataMenuViewModel.cs`** (`OfferMatchDownloadAsync`), `src/Features/MainWindow/MatchDownload/*` (wording), `tests/DeadlockAdvisor.Tests` (`SettingsTests`, `Ui/SettingsPageTests`, `DataMenuTests`)
Docs to update: `README.md` (Install, Data menu, Settings → Data), `docs/architecture.md` (section 7)

## Goal

Settings → Data shows one choice per source (Match data, Formulas, App) instead of five overlapping toggles, and "Check now" for match data does the
check instead of opening a dialog that usually says "nothing to download".

## Why / premise check

1. Settings → Data has five toggles ✔ (`DataSettingsView.axaml`): *Keep match data up to date* (`AutoUpdateMatchData`), *Check for a newer patch on
   startup* (`CheckForNewerPatch`, only matters with the first off), *Keep the hero ratings and item formulas up to date* (`AutoUpdateModel`), *Say when
   new heroes are out* (`CheckForNewHeroes`, only matters with the previous off), *Say when a new version is out* (`CheckForAppUpdates`).
2. `Data → Download Match Data…` and the status card's button open `MatchDownloadViewModel` even when the shared snapshot is usable and nothing is due; the
   dialog then says "Everything is already up to date, so there's nothing to download." (`MatchDownloadViewModel.Describe`) ✔. Its choices (rank groups) only
   exist for the deadlock-api.com fallback; for the snapshot the only choice left is "Keep it up to date automatically".

Re-check both files; WP13 should be `done`.

## Read first

`docs/architecture.md` section 7; `DataSettingsViewModel`; `DataMenuViewModel.OfferMatchDownloadAsync`, `CheckMatchDataAsync`, `DownloadMatchDataAsync`;
`MatchDownloadViewModel`; `SettingsTests` (how settings round-trip).

## Scope

In:
- Three per-source mode selectors as a **view over the existing booleans** (no new stored settings, so `settings.json` stays compatible both ways):
  - Match data: *Automatic* = `AutoUpdateMatchData` on; *Tell me* = it off and `CheckForNewerPatch` on; *Off* = both off.
  - Formulas: *Automatic* = `AutoUpdateModel` on; *Tell me* = it off and `CheckForNewHeroes` on (the existing "new heroes" offer); *Off* = both off.
  - App: *Tell me* = `CheckForAppUpdates` on; *Off* = off. (Automatic download is backlog.)
  Each row keeps its buttons (Download…, What's new, Reset…, Undo update, Check now, Releases) and shows its last-checked text.
- The match-data "Check now": with a usable, up-to-date snapshot, a toast "Match data is up to date (patch 10-07, fetched 3 h ago)"; with work due and the
  snapshot usable, start the download straight away with a toast; the dialog stays for the deadlock-api.com fallback and from the Updates row's "Details…".

Out: new settings; automatic app installs; changes to what each mode does beyond naming it.

## Suggested design

- A small enum and two-way mapping functions in the view model (unit tested), setting both booleans in one `Settings.Update`.
- Short plain descriptions under each selector (one sentence each; say what *Tell me* does for that source).
- Keep `MatchDownloadViewModel.AutoUpdateInfo` as the text behind an info badge.

## Tests

`SettingsTests`/`SettingsPageTests`: each mode round-trips through the booleans (all combinations map to a mode and back without changing an
untouched setting); old settings files show the right mode. `DataMenuTests`: Check now with a current snapshot toasts and starts nothing; with a due patch
starts a download; with the snapshot unavailable opens the dialog. Look at the rendered Settings → Data page.

## Acceptance criteria

- [ ] Three selectors replace the five toggles, and every old combination maps to one.
- [ ] A settings file written by the previous version reads correctly, and one written now still works in the previous version (the booleans are unchanged).
- [ ] "Check now" for match data never opens a dialog just to say nothing is due.

## Conflicts

After WP13 (the Updates surface and this page share wording); `DataMenuViewModel.cs` hot file (last in its chain).

## Notes

If a combination the old toggles allowed has no mode (for example a check-only-heroes state with updates on), pick the closest and say which in the report.
