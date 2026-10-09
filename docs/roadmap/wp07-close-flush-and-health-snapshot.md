# WP07 Confirm on a failed save at close; Model Health on a snapshot

Status: todo
Effort: S · Risk: low · Depends on: none · Wave: B
Touches: `src/Features/MainWindow/MainWindow.axaml.cs` (`OnClosing`), `src/Features/MainWindow/MainWindowViewModel.cs` (`OnClosing`, `HoldCloseForJobs`), **`src/Features/MainWindow/DataMenuViewModel.cs`** (`ShowModelHealthAsync`)
Docs to update: `docs/architecture.md` (section 5)

## Goal

Closing the app can no longer silently throw away edits that couldn't be written, and the Model Health report can't read a data
store the user is editing while it runs.

## Why / premise check

1. `MainWindowViewModel.OnClosing` calls `_data.FlushSaves()` and ignores the result; `MainWindow.OnClosing` then closes. When the
   write fails (disk full, read-only or locked folder) `FlushSaves` shows an error toast and returns false, and the app closes anyway,
   losing the edits (agent; read both). The same ignored result appears in `DataService.Reload` and `ChangeDataRoot`.
2. `DataMenuViewModel.ShowModelHealthAsync` runs `ModelHealth.Build(store, matrix)` in `Task.Run` over the **live** store; `IsBusy`
   blocks other commands but not the editors, which can mutate the same dictionaries meanwhile (agent) ✔ (the capture of `store` and
   `matrix` is in the code; the editors stay enabled).

Re-check by reading `OnClosing` in both files and `ShowModelHealthAsync`.

## Read first

`docs/architecture.md` section 5; `HoldCloseForJobs` (the existing "close and ask" pattern with a confirm modal); `DataService.FlushSaves`;
`ModelHealthTests`; `DataMenuTests` (`ModelHealthReport...`).

## Scope

In:
- On close, if `FlushSaves()` fails: keep the window open and ask ("Your latest edits couldn't be saved (…). Close anyway?" with
  "Close anyway" / "Keep the app open"); never block an OS shutdown (`WindowCloseReason.OSShutdown`).
- Model Health runs on a snapshot: after `FlushSaves()`, `DataStore.Load(_data.DataDir)` and `ItemScoring.BuildWeightMatrix` of that copy,
  inside `Task.Run`.

Out: changing `FlushSaves`; retry logic for saves; the Reload/ChangeDataRoot ignored results (note them in the report if you see a
cheap fix).

## Suggested design

- Make `MainWindowViewModel.OnClosing()` return whether closing may go on, sharing the `_closeConfirmed` flag with `HoldCloseForJobs` so a
  confirmed close isn't asked twice; `MainWindow.OnClosing` sets `e.Cancel` when told to.
- For the snapshot, load on the thread-pool thread too; report a load failure as an error message instead of a crash.

## Tests

A UI/view-model test with a data folder whose write fails (open the CSV with `FileShare.None`, or make the folder read-only) that closing
asks, "Keep open" stays, "Close anyway" closes. `ModelHealthTests`/`DataMenuTests`: the report is unchanged for the golden store; an edit made
while it runs doesn't affect it (hold the task with a gate and edit).

## Acceptance criteria

- [ ] A failed flush at close asks before closing, and OS shutdown isn't blocked.
- [ ] Model Health reads a copy; the report for the test data is byte-identical to before.

## Conflicts

`DataMenuViewModel.cs` is a hot file (WP07 → WP10 → WP11 → WP13 → WP14): do this first. `MainWindow.axaml.cs` is also edited by WP19.

## Notes

If loading a second store for Model Health proves slow on real data, say so in the report; the alternative is to disable the editors while
it runs.
