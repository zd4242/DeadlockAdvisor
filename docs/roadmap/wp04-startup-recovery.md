# WP04 Recover from a bad CSV at startup; no pruning on empty tables

Status: done
Effort: M · Risk: medium (touches the first thing the app does; test well) · Depends on: WP01 · Wave: B
Touches: `src/Services/DataStore.cs` (`Load`, `PruneOrphans`), `src/Services/DataService.cs` (`Initialize`, `Open`, `SeedIfEmpty`), `src/Features/MainWindow/DataMenuViewModel.cs` (`SyncNewData` message only), `src/Services/Formats/Csv.cs` (only if errors need the file name)
Docs to update: `README.md` ("Data folder": what happens to a damaged file), `docs/architecture.md` (sections 3 and 4)

## Goal

A typo, a half-synced file or a zero-byte CSV no longer stops the app from starting: the damaged file is set aside, the newest good
backup (or the bundled copy) takes its place, and the user is told which file and what happened. Sync New can no longer delete
every rule because a base table loaded empty.

## Why / premise check

1. The CSVs are hand-editable and the data folder can sit in a synced folder (README). `CsvRow.Required` throws `FormatException`;
   `DataStore.Load` only catches around the match-count JSON; `DataService.Initialize` falls back only for a *non-default* data root;
   `App.OnFrameworkInitializationCompleted` writes `startup-error.log` and rethrows. So one bad row stops the app, with nothing for
   the user to read ✔ (read those four).
2. A zero-byte `items.csv`, `heroes.csv` or `categories.csv` loads as an empty table (agent), and `DataStore.PruneOrphans` (reached
   from Data → Sync New Heroes / Items / Categories, which the editors' menu shows) then drops every score, coefficient and weight
   whose id isn't in an empty table, and the sync saves that ✔ (reasoned from `PruneOrphans` and `DataMenuViewModel.SyncNewData`).
3. `.backups/` holds timestamped copies (`BackedUpFile`), and the bundled starter copy is embedded (`DataService.SeedIfEmpty` shows
   how it is read) ✔.

Re-check: read `DataStore.Load`, `DataService.Initialize/Open`, `PruneOrphans`. Tests don't cover corrupt files (agent): confirm by
searching the tests for a corrupt-CSV case.

## Read first

`docs/architecture.md` sections 3 and 4; `docs/scoring_model.md` "Data locations"; `BackedUpFile`; `DataServiceTests`, `DataStoreTests`;
WP01's notification hold (this package relies on a message sent during startup being shown).

## Scope

In:
- `DataStore.Load` reports which file failed (a typed exception with the file name and the reason).
- `DataService.Open` recovers per file: try the newest backup of that file that loads, else the bundled copy; keep the damaged file as
  `<file>.bad-<yyyyMMdd-HHmmss>`; send one notification saying which file, why, and what it was restored from. Bound the attempts;
  if recovery fails, the app still starts on the bundled data.
- Zero data rows in `heroes.csv`, `items.csv` or `categories.csv` counts as damaged.
- `PruneOrphans` does nothing when `Heroes`, `Items` or `Categories` is empty, and Sync New says so.

Out: changing what a valid file looks like; recovering the match-count JSON (already tolerated); a recovery dialog (a notification is
enough, and a dialog can't show before the window exists).

## Suggested design

- `DataLoadException(string file, string reason, Exception? inner)`; wrap each table's loader in `DataStore.Load` so the file name is
  attached.
- In `DataService.Open`, a loop: `DataStore.Load` → on `DataLoadException` choose the replacement, move the bad file aside, copy the
  replacement in (through `AtomicFile`), and retry (at most one pass per distinct file, then the bundled data for all). Only the folder
  the app is already using recovers this way (startup, and `Reload` of the current folder). A folder the user *picks* in Change Data
  Folder is still refused with an error and left alone (`ChangeDataRoot` already loads it before switching).
- Sync New's message gets a line when it skipped pruning.

## Tests

`DataServiceTests`: a malformed row in `hero_category_scores.csv` restores from the newest good backup and notifies; no backup falls back
to the bundled file; a zero-byte `items.csv` is treated as damaged; the bad file is kept; two damaged files both recover; a folder that
can't be fixed still starts on the bundled data. `DataStoreTests`: `PruneOrphans` does nothing with an empty base table and still prunes
real orphans otherwise. Run `DataService`, `DataStore`, `DataMenu`, `FileFormat`, `Settings`, and the full suite (startup path).

## Acceptance criteria

- [ ] An app started on a data folder with a broken CSV starts, and says which file was restored and from what.
- [ ] The damaged file is still on disk, renamed.
- [ ] Pruning refuses to run on an empty base table.
- [ ] Full suite green; README "Data folder" describes the behaviour.

## Conflicts

`DataService.cs` has no other package; `DataMenuViewModel.cs` is a hot file (only the Sync New message line here). Depends on WP01 so the
message isn't lost.

## Notes

If the first failing file is `categories.csv` or `heroes.csv`, other tables may only fail because of it; recover those first.
