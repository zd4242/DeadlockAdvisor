# WP11 Heavy download work off the UI thread

Status: done
Effort: M · Risk: medium (threading around a non-thread-safe store; goldens must stay byte-identical) · Depends on: WP08 · Wave: C
Touches: `src/Services/MatchSnapshotService.cs`, `src/Services/MatchStatsService.cs` (`Apply`, `Reanalyse`), `src/Services/DataStore.cs` (the lift/segment save methods), **`src/Features/MainWindow/DataMenuViewModel.cs`** (`DownloadMatchDataAsync`, `ApplySegment`), `src/Services/ArtDownloadService.cs` (the top-bar cut), `src/Services/Contracts/` (the `FetchAsync` callbacks), tests that fake these services
Docs to update: `docs/architecture.md` (section 5)

## Goal

While match data or art downloads "in the background", the window stays responsive: unpacking, analysing, serializing and image cutting no longer
run on the UI thread.

## Why / premise check

1. Services use no `Task.Run` and no `ConfigureAwait(false)` (only `JsonSettingsService` has one; `Task.Run` appears only for Model Health and Detect) ✔
   (search the source). Their `await`s resume on the UI context, so everything between two awaits in a "background" job runs on the UI thread.
2. Per patch applied (agent estimates, not measured: about 0.3-0.8 s each, up to six per download):
   `MatchSnapshot.Unpack` (SHA-256, gunzip, JSON parse) in `MatchSnapshotService.FetchAsync`; `MatchStatsService.Apply` → `Reanalyse`
   (`MatchStatsMath.Analyse`, then `SaveMatchLift` formatting about 10,000 rows through `BigInteger`); the callback runs on the UI thread
   (`DataMenuViewModel.ApplySegment`). `TopbarDerivation.Run` (up to 120 decode/resize/PNG-encode steps) runs at the end of an art download.
3. `DataStore` is not thread-safe, so the pure work can move but store mutation can't (see design).

Re-check: search for `Task.Run`/`ConfigureAwait`; add a stopwatch log around the steps and run the existing download tests to see real numbers
before changing anything. If a step is under ~50 ms, leave it.

## Read first

`docs/architecture.md` section 5; `docs/scoring_model.md` "The counts behind the lifts"; `MatchSnapshotService`, `MatchStatsService`,
`DataMenuViewModel.DownloadMatchDataAsync`; tests `MatchDownloadTests`, `MatchSnapshotTests`, `MatchStatsServiceTests`, `GoldenMatchStatsTests`.

## Scope

In:
- Unpack (hash, gunzip, parse) of each snapshot file in `Task.Run`.
- `Apply` split into the pure part (analysis, serializing the segment and the lifts) in `Task.Run`, and the store mutation (put, prune, assign
  `MatchLift`/`MatchMeta`) on the calling context; the file writes may stay with the pure part if the ordering is kept.
- `TopbarDerivation.Run` in `Task.Run`.
- The `finished` callbacks of `FetchAsync` become asynchronous (`Func<MatchSegment, Task>`), so the next file isn't requested before the previous
  one is applied.

Out: any change to the numbers (every golden and the published snapshot stay byte-identical); `GameSync.Apply` (small; measure and mention);
rank-filter reanalysis (`DataRanksViewModel` → `Reanalyse`: noted in the report if it is also slow, not changed here).

## Suggested design

- Keep `IMatchStatsService.Apply` (tests use it) and add `ApplyAsync`; share the pure helpers.
- Split `DataStore.SaveMatchLift`/`SaveMatchSegment` into "produce bytes" (pure) and "write" if that is the heavy part.
- Snapshot what the background step reads (`store.MatchSegments.ToList()`, `store.Items.Values.ToList()`) before `Task.Run`, so nothing mutates under it.
- Preserve cancellation: honour the token between steps; a cancel after unpack but before apply drops the result as today.

## Tests

`MatchDownloadTests`/`MatchSnapshotTests`/`MatchStatsServiceTests`: results identical to before; cancel mid-apply keeps what finished; the callback
isn't invoked concurrently; a second download can't start meanwhile. `GoldenMatchStatsTests` and the fetch goldens: unchanged (do not regenerate).
Run `MatchDownload`, `MatchSnapshot`, `MatchStatsService`, `GoldenMatchStats`, `DataMenu`, `ArtDownloadService`; full suite (shared service
signatures change).

## Acceptance criteria

- [ ] The UI thread does no hashing, unzipping, analysing, number formatting or image cutting during a download (shown by the stopwatch log or a
      thread-id assertion in a test).
- [ ] Every existing result and golden is unchanged.
- [ ] Docs section 5 no longer says services run CPU work on the UI thread.

## Conflicts

`DataMenuViewModel.cs` hot file (after WP10; before WP13/WP14). `ArtDownloadService.cs`: after WP08 and WP09.

## Notes

If the measurements show only one or two of these steps matter, do those and say so; the package is about felt hitches, not tidiness.
