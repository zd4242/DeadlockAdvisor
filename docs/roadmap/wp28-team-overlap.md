# WP28 Overlap: an effect your team already has counts for less

Status: done
Effort: M · Risk: medium (touches scoring, the sync and a new model file) · Depends on: WP27 · Wave: D
Touches: `src/Services/GameApi/GameSync.cs` (record each item's non-stacking effects), `src/Services/DataStore.cs` and `Contracts/IDataService.cs` (`DataFiles`, a load/save pair), `src/Services/ModelPublisher.cs` and `ModelUpdateService.cs` (the file joins the model), new `src/Scoring/OverlapWeights.cs`, `src/Scoring/ItemScoring.cs`, `ScoringTypes.cs` (`LineUp`), `WeightMatrix.cs`, `ModelHealth.cs`, `src/Features/Match/Explain/*` (`ExplainText`), `src/Features/Match/MatchViewModel.cs` (the filter), `src/Models/AppSettings.cs`, tests, goldens (`game_api/*`, `weight_matrix.json`, `scoring_cases.json` as they change)
Docs to update: `docs/scoring_model.md` (a section), `docs/architecture.md` (sections 4, 7), `docs/publishing.md` ("Changing what the files hold"), `README.md`

## Goal

When you or an ally already holds an item whose effect doesn't stack with another copy, the same effect on a second item counts for far less: the recommendation stops telling a team with a Heroic Aura
to buy another, and a notice says when two teammates hold the same non-stacking effect. It follows patches from the game data and needs no hand-kept duplicate list.

## Why / premise check

1. Which effects don't stack is a game rule; the user named Heroic Aura and Bullet Resist Shredder. **Confirm the rule per property with the user before relying on it** (their play knowledge, or
   the wiki), and list the confirmed properties in the package's report.
2. The sync already classifies enemy debuffs: `GameSync.Unscored` marks `BulletResistReduction`, `MagicResistReduction`, `TechPowerReduction`, `FireRateSlow`, the slows and others "a debuff on
   enemies" ✔. It throws the per-item presence away; the lines that stand for them are typed rows in `item_formula_coefficients.csv` (Bullet Resist Shredder: `deals_bullet_damage_general` with 1.5,
   `applies_spirit_damage_easily` as 2) ✔.
3. An aura's effect is **not** unscored: Heroic Aura's 17% Bullet Resist is in `item_stats.csv` and scores through the `BulletResist` stat rule, so a discount of "typed rows only" would miss it ✔
   (`item_stats.csv`: heroic_aura, BulletResist 17). `StatPart.Stat` names the stat each part came from ✔.
3b. WP24: about one filled cell in four reads as an unknown item at first (never wrongly identified); an unknown cell must leave the hero's overlap unchanged, and the "your team has it" notice names only confident reads.
4. `WeightMatrix` already splits each weight into a summed and a best-target part for some items ✔; a third part, "overlapping", for the few items in a group follows that pattern.

## Read first

`docs/scoring_model.md` ("The formula", "Where coefficients come from", "How item stats are extracted"); `docs/publishing.md`; `GameSync` (`Unscored`, the override sets such as `_forceShown`);
`ItemScoring.BuildWeightMatrix`, `Total`, `ExplainItem`; `WeightMatrix`; `ModelHealthTests`; `ModelUpdateTests.TheSeedsModelJsonListsEveryModelFileByItsHash`.

## Scope

In:
- **Effects file.** The sync writes a generated file (`item_effects.csv`: item id, effect key, scope `enemy_debuff` or `aura`, value) from the raw properties, the way it writes `item_stats.csv`
  (never hand-edited), plus a small override set in `GameSync` for exceptions, each with its reason, as the existing override sets have. Which scopes count as non-stacking comes from step 1.
- **Groups and coverage.** Items sharing an effect key form a group. For an item, coverage by the team is the largest held value of that effect on you or an allies' items divided by its own value, capped at
  1 (a stronger one still adds the difference). Without values, full coverage.
- **Scoring.** For items in a group the matrix keeps the weight from the overlapping lines (typed rows for enemy-debuff groups; stat parts whose stat is the aura's for aura groups) as a separate
  part; `Total` scales that part by `1 − coverage` (a multiplier on a part, never a bonus, so the zero-mean rule holds: scale a part's typical value with it). The rest of the item, such as its
  own weapon damage, is untouched. It is one more factor on `LineUp` (`LineUp.Overlap`), built from `MatchState.Builds` like `NetWorthWeights.For`.
- **Control.** A Match filter "Discount what your team already has" beside the net-worth one (`AppSettings.ResultsByOverlap`, default on; it does nothing until builds exist). Model Health
  simulates without it.
- **Explain.** The panel says "Heroic Aura on Haze: covers 100% of this item's aura" and shows the discounted parts.
- **Notice.** When a read first shows two teammates holding items of one group, one toast (the existing notification and attention services, honouring the chime and flash settings).
- Publishing: the new file is part of the model. Bump `ModelManifest.CurrentFormat` if an older app can't ignore it, list it in `model.json`, and follow `docs/publishing.md`. **The seed only changes
  when the user means to publish** (CLAUDE.md): this package leaves the seed's copy of the file for the user to publish.

Out: enemies' items changing your scores (WP30); hand-kept duplicate lists as a source; the build-adjusted traits (WP29).

## Tests

Sync tests on the snapshot fixture (the new file's rows, overrides, the report line); `ScoringTests` on `TestStore` by hand (covered, partly covered, uncovered, aura, debuff, best-target item in a
group); a Model Health check that average scores stay about 0 with random held items; `ModelUpdateTests` for the manifest; UI test for the filter. Regenerate goldens with
`dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~GoldenScoringTests|FullyQualifiedName~SyncingTheSnapshot|FullyQualifiedName~GoldenMatchStatsTests"`
and review the diff. Run the full suite: this changes scoring, the sync and `DataStore`.

## Acceptance criteria

- [ ] Holding Heroic Aura on your team drops another one's aura value and leaves its other stats alone; two resist shredders likewise.
- [ ] With the filter off, or no builds read, scores are byte-for-byte as before.
- [ ] The effects file is generated, listed in the model, and survives a patch with no hand edits.
- [ ] The user has confirmed the list of non-stacking effects.

## Conflicts

`ItemScoring.cs` and `ScoringTypes.cs` are hot: this goes before WP29. `GameSync.cs` (1,000 lines) has no other package open.

## Notes

If the raw API records don't carry enough to tell an aura from a plain self effect, fall back to the override set as the source for auras and say so; don't guess from tooltip text.
