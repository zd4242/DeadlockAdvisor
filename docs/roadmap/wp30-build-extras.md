# WP30 Build extras: real HP and durability, enemy counters, sell hints

Status: todo (optional; each part is its own decision)
Effort: M each · Risk: medium · Depends on: WP29 · Wave: E
Touches: `src/Scoring/BuildShifts.cs`, `src/Services/GameApi/GameSync.cs` (`MeasuredMaxHp`, `HeroDurability`), `ItemScoring.cs`, `src/Features/Match/Results/*`, tests
Docs to update: `docs/scoring_model.md`, `README.md`

## Goal

Spend what the build reading knows on the next three things, one at a time, each only if the user wants it after living with WP27-WP29.

## Candidates

1. **Real max HP and durability.** `max_hp` is estimated from a hero's starting health, level-ups and kit (`GameSync.MeasuredMaxHp`); `durability` from damage taken. The scoreboard itself prints each hero's max health and bullet and spirit resist percentages under their bars (WP24 saw them; not measured), so reading those may replace the estimate more directly than working it out from items. With the owned vitality items'
   `BonusHealth`, `BonusBaseHealth`, resists and barriers (`item_stats.csv`) the app can replace the estimate for this match, through the same shift mechanism and the same score units
   (`MaxHpSpread`). The estimate stays as the baseline: shift = (hero's real health from items) − (their typical items' health).
2. **Enemies who already hold a counter.** An enemy with a Debuff Remover, a Metal Skin or a stack of slow resist makes your debuff items worth less against them. Derive "holds a counter" from the
   same effects data as WP28 (a resist or removal effect matching a debuff the item applies), as a per-enemy factor on that item's `against` lines, like focus.
3. **Sell and swap hints.** Items you hold that now score at or below 0 for this match, shown as "consider selling" beside the list; presentation only. Show them as the items (an `ItemChip`: icon, name, tier;
   [WP33](wp33-items-as-pictures.md)), not a line of names, so the player recognises them at a glance.

## Why / premise check

Decide each part only after the user has used WP27-WP29: whether the damage-mix shift reads as sensible, and whether the effects data from WP28 is reliable enough to build on.

## Scope / design / tests / acceptance

Written when a part is chosen: each follows the rules in `docs/scoring_model.md` (no flat bonuses; multipliers on parts or mean-zero shifts; Model Health bounds) and gets its own filter.

## Conflicts

Scoring files are hot; one part at a time, after WP29.
