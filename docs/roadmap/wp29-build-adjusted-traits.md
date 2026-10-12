# WP29 Build-adjusted traits: what a hero built shifts their damage mix

Status: todo
Effort: L · Risk: medium-high (new scoring input; must stay mean-zero) · Depends on: WP27 (and after WP28, which touches the same scoring files) · Wave: D
Touches: new `src/Scoring/BuildShifts.cs`, `src/Scoring/HeroTypicalBuild.cs`, `src/Services/DataStore.cs` (a cached typical-build lookup beside `BuildRatios`), `src/Scoring/ItemScoring.cs`, `ScoringTypes.cs` (`LineUp.Shifts`), `WeightMatrix.cs` (a small coefficient table), `ModelHealth.cs`, `src/Features/Match/Explain/*`, `src/Features/Match/MatchViewModel.cs`, `src/Models/AppSettings.cs`, a measuring tool (see Scope), tests and goldens
Docs to update: `docs/scoring_model.md` (a section and the table of where things live), `docs/architecture.md` (section 4), `README.md`

## Goal

An enemy (or ally, or you) who has built mostly weapon items counts as more of a bullet-damage hero in this match, and one who built spirit as more of a spirit-damage hero, so the items that
answer each (fire-rate and bullet-resist items against the first, spirit-resist against the second) shift accordingly. It adapts to each hero without per-hero rules: a hero who builds the same
every game barely moves, a flexible one moves a lot.

## Why / premise check

1. Hero traits are static ratings; `deals_bullet_damage_general` and `deals_spirit_damage_general` are measured shares of player damage at the hero's usual build (`docs/scoring_model.md`, "Hero
   scores measured from match data") ✔.
2. Each hero's usual build is already in the downloaded counts: `MatchSegment.HeroItems` holds per-hero, per-item purchase counts, which feed `HeroItemTable` and `DataStore.BuildRatios` ✔. No new
   download is needed for the typical build.
3. The weight is linear in a hero's trait score (`Σ (score − baseline) × coefficient`), so a per-match shift of a score is one added term, correct for best-target lines too if applied before
   ranking, as net worth is ✔ (`ItemScoring.Total`, `BestTargets`).
4. **Not** a valid shortcut: inverting `stat_rules.csv` to turn owned stats into trait scores. A rule says what a trait values, not what a build makes of a hero (`BonusHealth → max_hp` is
   negative because flat health helps low-HP heroes). Don't revisit it.
5. The response of damage mix to investment (the number α below) has to be **measured** from matches, not typed. WP24 checked that the match metadata splits damage by kind: `GET /v1/matches/{id}/metadata`'s `damage_matrix` has per-dealer, per-target cumulative series for the source rows `Bullet`, `Ability` and `Melee` (target slot 0 is not a hero, so count targets 1 to 12), and the player's `items[]` with buy and sell times. The bulk `/v1/matches/metadata` has players and items but no damage matrix, so this needs one request per match (about 1.1 MB each).

## Read first

`docs/scoring_model.md` (all: especially "Net worth", "Focus", "Items your hero rarely builds", "Hero scores measured from match data"); `ItemScoring` (`BuildWeightMatrix`, `Total`,
`ExplainItem`, `BuildRatio`); `NetWorthWeights.For`; `DataStore.BuildRatios` and its cache clearing; `MatchSegment.HeroItems`; `ModelHealth` and `ModelHealthTests` (how focus is bounded).

## Scope

In:
- **Typical build.** `HeroTypicalBuild`: for a hero, Σ purchases × cost per shop category over the lifts' rank range and patch weights, as shares t_c. Cached like `BuildRatios`; cleared when counts,
  meta or items change. No counts → no typical build → the effect is off (never a guess).
- **Observed build.** From `MatchState.Builds`: shares s_c with unknown cells at their category's median cost.
- **Shift.** d_c = s_c − t_c. The two damage-type traits shift by α · (d_weapon − d_spirit), in opposite directions, scaled by `ramp = clamp(spent ÷ FullRampSouls, 0, 1)` so an early,
  small inventory doesn't swing the score (the typical build is a whole-match average). Only `deals_bullet_damage_general` and `deals_spirit_damage_general` at first; decide whether the burst
  traits follow.
- **Applying it.** `BuildShifts.For(match, store)` returns each hero's Δ per trait, carried on `LineUp` beside `NetWorth` and `Focus`. `weight' = weight + Σ_T Δ × coefficient(item, T, relation)`;
  `WeightMatrix` gains a small (item, trait, relation) → coefficient lookup so `Total` adds the term without a rebuild; best-target lines take the shifted weight before ranking and their typical
  sums stay the static roster's. `ExplainItem` shows "deals bullet damage 62 → 71 (built weapon)" on the hero's trait line.
- **α.** One global number, measured: a tool (an env-driven test tool or a `tools/` program, whichever WP24's note favours) samples finished matches through `/v1/matches/{id}/metadata`, fits each
  player's bullet-damage share against their weapon-minus-spirit investment deviation from the hero's typical, pooled over heroes, and prints α with its interval. Where α lives (a constant, or a
  model file so it can be re-measured without a release) follows WP28's decision on new model files. No real player data is committed.
- **Control.** A Match filter "Lean toward what heroes have built" beside the others (`AppSettings.ResultsByBuilds`, default on, inert until builds exist and match data is there).
- **Model Health.** Simulate random inventories drawn from typical builds: every item's average score stays about 0 and the swing is bounded; add a bound test like focus's.

Out: shifting any other trait (HP and durability are WP30); a different α per hero (only if the fit shows large spread, as a follow-up); the overlap discount (WP28).

## Suggested design

- Everything derives from the match data and `items.csv`; the only typed numbers are α and `FullRampSouls`, both documented with where they came from.
- Keep `BuildShifts` a pure function of the match and the store, as `NetWorthWeights.For` is, so it is testable by hand.

## Tests

`ScoringTests` on `TestStore` (a hero at their typical build shifts by 0; a weapon-heavy build shifts the traits by hand-checkable amounts; the ramp; no counts → no shift; best-target item);
`ModelHealthTests` (random typical builds: mean ≈ 0, bounded swing); `GoldenScoringTests` (regenerate with
`dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~GoldenScoringTests"` and review the diff); explain tests; a UI render of the filter. Run the full
suite (scoring, `DataStore`).

## Acceptance criteria

- [ ] With the filter off or no builds read, every score is as before.
- [ ] A hero at their typical build changes nothing; a gun-heavy Ivy raises the items that answer bullet damage against her and lowers spirit-resist ones, by an amount α fixes.
- [ ] Over random typical builds the average score of every item stays about 0 (no flat bonus).
- [ ] α comes with its measurement, and the explain panel shows the shifted score.

## Conflicts

Starts after WP28 (same scoring files). `MatchViewModel.cs` and `AppSettings.cs` are hot.

## Notes

If the fit says the damage mix barely follows investment, the right outcome is to keep this off by default and say why, not to raise α until it shows.
