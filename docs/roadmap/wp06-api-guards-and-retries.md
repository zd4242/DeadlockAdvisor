# WP06 Sanity guards and retries for the API paths

Status: todo
Effort: S-M · Risk: medium (shared with the CI job that publishes the match data) · Depends on: none · Wave: A
Touches: `src/Services/GameApiService.cs`, `src/Services/MatchStatsService.cs` (`GetAnalyticsAsync`, the baseline phase), `src/Services/MatchSnapshotJob.cs` (read only), tests (`GameApiTests`, `MatchStatsServiceTests`, `MatchDownloadTests`)
Docs to update: `docs/scoring_model.md` ("The counts behind the lifts": retries), `docs/architecture.md` (section 7)

## Goal

A bad answer from deadlock-api.com (an empty list, a half-working endpoint) can no longer replace good local data, or the published
snapshot, with nothing; and a dropped connection mid-download is retried instead of ending a four-minute fetch.

## Why / premise check

1. `GameApiService.SyncAsync` fetches heroes and the shop and hands them to `GameSync.Apply`; with an empty list `Apply` replaces the item
   stats and tooltips with nothing, and tooltips have no backup (agent; check `GameSync.Apply` and `DataStore` for the tooltips file).
2. `MatchStatsService` reads answers with `as JsonArray ?? []` and `JsonRecord.Int` returns 0 for a missing field, so an empty baseline
   becomes a stored segment with no counts, and a finished patch is never fetched again (agent; `GetAnalyticsAsync`). The CI job
   (`tools/MatchSnapshot` → `MatchSnapshotJob`) uses this same code, so a bad answer there is published to everyone.
3. `GetAnalyticsAsync` retries 429 (waits 30 s) and 5xx (once) only; `HttpRequestException` without a status (a dropped connection) and
   `TimeoutException` end the whole run ✔ (read the catch clauses).

Re-check by reading the three places; confirm whether guards already exist.

## Read first

`docs/scoring_model.md` "The counts behind the lifts" and "The shared download"; `MatchStatsService` (the phase code around the
`answers` array and `GetAnalyticsAsync`); `RequestPacer`; the `FakeDeadlockApi` and `SyntheticItemStatsApi` fakes.

## Scope

In:
- `SyncAsync` throws `InvalidDataException` (a clear message) before touching the store when the API lists no heroes or no shop items.
- The match-count phase rejects an "every match" baseline with no rows for a window longer than 6 hours (`InvalidDataException`), so
  nothing is stored.
- `GetAnalyticsAsync` retries a dropped connection and a timeout twice with backoff (use the existing injected delay), beside the
  429 and 5xx handling.

Out: changing what is fetched; the shared snapshot's file format (no version bump); the art download (WP08).

## Suggested design

- Keep the checks next to where the answers are read, with the numbers named and documented.
- A new `FetchWait` text for the retry ("deadlock-api.com didn't answer, trying again") so the progress view explains the pause.
- The sync failure already reaches `DataMenuViewModel` as a "Sync failed … Nothing was changed" message (`IsNetworkFailure` includes
  `InvalidDataException`); keep that path.

## Tests

`GameApiTests`: empty heroes / empty shop → exception, store and files unchanged. `MatchStatsServiceTests` / `MatchDownloadTests`:
empty baseline over a long window is rejected and nothing is saved; over a short window it isn't; a connection drop then success
completes; two drops then success completes; three drops fail. Golden files must not change (run `GoldenMatchStatsTests` and the sync
goldens without regenerating).

## Acceptance criteria

- [ ] An empty answer changes no stored data, with a message that says so.
- [ ] A transient connection error mid-download is retried.
- [ ] Existing goldens unchanged.

## Conflicts

None for the files; WP11 changes how results are applied after the fetch (different code in the same service).

## Notes

The 6 h threshold is a default: a patch's first hours can legitimately have few matches, but never none.
