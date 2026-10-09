# WP20 Import parses each player's items and net-worth curve

Status: todo
Effort: M · Risk: low (additive; the import dialog keeps working as is) · Depends on: none · Wave: A
Touches: `src/Services/MatchLookupService.cs` (the records and `LookUpAsync`), `src/Services/GameApi/JsonRecord.cs` (only if a helper is missing), `tests/DeadlockAdvisor.Tests/MatchImportTests.cs`, `Fakes/FakeServices.cs` if the fake API needs a richer answer
Docs to update: `docs/architecture.md` (section 4: what the lookup returns), `README.md` (only if the Import description changes)

## Goal

The match lookup keeps what the API already returns for every player (what they bought and when, and how their net worth grew) so WP21 can compare a finished match's builds with
the advisor's advice. Nothing visible changes in this package.

## Why / premise check

1. `MatchLookupService.LookUpAsync` fetches `GET /v1/matches/{id}/metadata` and keeps only `account_id`, `hero_id`, `team`, `player_slot` per player, plus `start_time`, `duration_s`,
   `winning_team` ✔ (read it). The rest of the answer is dropped.
2. The answer carries, per player ✔ (fetched live while writing this package): `items[]` with `game_time_s`, `item_id`, `upgrade_id`, `sold_time_s`, `flags`, `imbued_ability_id`,
   `upgrade_info`; and a time-sliced `stats[]` with `time_stamp_s`, `net_worth`, `gold_player`, `kills`, `deaths`, `assists`, `player_damage`, `player_damage_taken`, `max_health`, `level`, and
   more. The bulk endpoint `/v1/matches/metadata?include_player_info=true` does **not** include `items`; use the per-match one.
3. `item_id` is deadlock-api.com's game id; our `Item.GameId` holds the same id (`items.csv` `game_id`) ✔ (`Item` model, `GameSync`).

Re-check (read-only): see the README's probe for finding a match id, then fetch `/v1/matches/<id>/metadata` and look at one player. Confirm what `sold_time_s` is for an item never sold
(expected 0) and whether `items[]` includes ability upgrades that have no shop item (they will simply not map to an `Item`).

## Read first

`docs/architecture.md` sections 4 and 7; `MatchLookupService`; `JsonRecord` helpers; `MatchImportTests` (the match JSON is built in code); `ImportMatchViewModel` and `ImportPlayerViewModel`
(who consumes `LookedUpMatch`).

## Scope

In:
- New records: a purchased item (`GameItemId`, `AtSeconds`, `SoldAtSeconds` nullable) and a net-worth point (`Seconds`, `NetWorth`); `MatchPlayer` gains `Items`, `Worth` (the curve) and the final
  `NetWorth`, all with empty defaults so every existing construction keeps compiling.
- `LookUpAsync` fills them; a player with no `items` or `stats` gets empty lists; malformed entries are skipped rather than failing the lookup.
- Tests built from in-code JSON (as the existing ones are): do **not** commit a real response: it contains real players' account ids.

Out: any UI; fetching more endpoints; mapping to our `Item` (WP21 does that with the store).

## Suggested design

- `JsonRecord.Items(player, "items")` and `JsonRecord.Int(...)` already read the shapes used in this file; follow them.
- Sort `Items` by `AtSeconds`; keep sold items (WP21 decides what "your build" means).
- Curve: one point per `stats[]` entry (`time_stamp_s`, `net_worth`); keep as is (the API's own granularity).

## Tests

`MatchImportTests`: items and curve parsed; ordering; a sold item; missing arrays; a non-numeric field skipped; the existing lookup tests (players, teams, errors, rate limit) unchanged. Run
`MatchImport`, `Ui/ImportMatch`.

## Acceptance criteria

- [ ] `LookedUpMatch` carries each player's purchases and net-worth curve.
- [ ] The import dialog behaves exactly as before.
- [ ] No real player data in the repository.

## Conflicts

None.

## Notes

Keep the records plain data: WP21 turns them into the review.
