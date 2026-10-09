# WP05 Time-spaced backups for CSV and JSON

Status: todo
Effort: S · Risk: low · Depends on: none · Wave: A
Touches: `src/Services/BackedUpFile.cs`, `tests/DeadlockAdvisor.Tests/FileFormatTests.cs`
Docs to update: `README.md` ("Data folder": "last 12"), `docs/architecture.md` (section 1)

## Goal

The backups under `data/.backups/` reach back further than the last editing burst, and JSON files are trimmed like CSVs.

## Why / premise check

1. `BackedUpFile.Keep = 12`: every save copies the old file and keeps the newest 12 per file ✔. The editors save about 600 ms after
   each pause, so one session of edits fills the ring: on a real data folder the 12 `item_formula_coefficients` backups spanned 13
   minutes, and the 12 `hero_category_scores` ones about 6. A mistake made an hour earlier can't be undone ✔.
2. `TrimBackups` only matches names ending in `.csv`, but model updates write `item_tooltips.json` through `BackedUpFile.Write`, so JSON
   backups are never trimmed (agent; the code at the filter confirms the `.csv` test ✔).

Re-check: read `BackedUpFile.cs`; list a real `.backups` folder if you have one (read-only).

## Read first

`BackedUpFile.cs`, `AtomicFile.cs`, `FileFormatTests` (around the backup tests), README "Data folder".

## Scope

In: a retention rule that keeps the newest 12 plus the newest copy per hour for the last 24 hours plus the newest per day for the last 14
days; trimming by the file's own extension; the README sentence.
Out: a restore UI; changing where backups live; backing up generated files that aren't backed up today.

## Suggested design

- Parse each backup's time from its name (`<stem>.<yyyyMMdd-HHmmss>.<ext>`), not from the file's modified time.
- `TrimBackups(backupDir, stem, extension)` keeps `newest 12 ∪ per-hour(24h) ∪ per-day(14d)` and deletes the rest; failures to delete stay
  silent as today. Keep the "two saves in one second keep the first copy" rule.
- Make the windows constants (named, documented) so the README can quote them.

## Tests

`FileFormatTests`: with fabricated backup names across 3 weeks, the right ones survive; JSON backups are trimmed; a different stem isn't
touched (`items.` vs `item_stats.`); same-second saves keep the first. Update the existing "keeps 12" test deliberately.

## Acceptance criteria

- [ ] After many saves in one burst, copies from earlier hours and days still exist (up to the windows).
- [ ] JSON backups are trimmed too.
- [ ] README says what is kept.

## Conflicts

None (nothing else edits `BackedUpFile`).

## Notes

The windows (12, 24 h, 14 d) are a default; they cost little disk (the CSVs are small, `item_tooltips.json` is the biggest).
