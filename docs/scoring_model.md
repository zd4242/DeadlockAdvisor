# Scoring model and maintenance guide

Read this before changing scoring code, formula data (`stat_rules.csv`,
`item_formula_coefficients.csv`, `trait_weights.csv`, `categories.csv`), or the
game sync. It describes how items are scored, why the formula works the way it
does, and how the data is kept correct across game patches.

The scoring started as a port of the Python app, but it has since diverged on
purpose (September 2026). **The C# code is now the source of truth for scoring.**
Don't "restore parity" with the Python `scoring.py`.

## The formula

```
weight(item, hero, relation) = Σ over traits of
    (hero_score[hero, trait] − baseline[trait]) × coefficient[item, trait, relation]

coefficient  = trait_weight[trait, relation] × (typed + from_stats)
baseline     = average of hero_score[·, trait] over the profiled heroes
score(item)  = Σ weight over enemies (against) + allies (with) + you (as)
```

- A **relation** is `against` (an enemy has the trait), `with` (an ally has it)
  or `as` (your own hero has it).
- **Profiled heroes** are heroes with at least one nonzero trait
  (`DataStore.IsProfiled`). Unprofiled heroes are left out of the baseline, and
  they contribute nothing to any score. If they counted, a hero with every trait
  at 0 would look "below average at everything".
- `typed` is a hand-typed coefficient. `from_stats` is derived from the item's
  real stats through `stat_rules.csv` (see below).
- A **score above 0** means *this match wants the item more than a typical match
  does*. The results list only shows items that score above 0.

Where this lives in the code:

| Piece | Where |
|---|---|
| Baselines | `DataStore.TraitBaselines()`, computed on demand, never cached, because `SetHeroScore` doesn't trigger a rebuild |
| Weight matrix | `ItemScoring.BuildWeightMatrix` |
| One line-up's score | `ItemScoring.Total`, shared by the results lists, `DataOnlyPicks` and the model health simulation |
| Per-hero, per-trait explanation | `ItemScoring.Contribution` → `TraitPart` (`HeroScore`, `Baseline`, `Deviation`, `Amount`) |
| Displayed arithmetic | `ExplainText.Arithmetic` / `ExplainText.Deviation` show "(80 − 61 avg) × 3"; `FormulaText.Arithmetic` reuses them |
| User-facing explanation | `MainWindowViewModel.HowScoringWorks` (Help menu) |

### Why the baseline is subtracted

The original formula used raw trait scores. Every hero has some spirit damage,
debuffs and silence-able abilities, so any rule on such a trait added about the
same amount in every match. The result was that the ranking measured how many
rules an item had, not what the match needed. In 2,000 simulated random
matches, Rusted Barrel, Mystic Vulnerability and Spirit Resilience were top-3 of
their tier in 96–100% of them.

Measuring each hero against the roster average removes that constant. Across
random matches, every item's expected score is about 0. After the change, the
tier leaders are top-3 in 30–45% of matches, and 12–33 items per tier reach the
top 3 in some match.

### Consequences to keep in mind when editing data

- **A coefficient measures sensitivity, not value.** An item's influence on the
  ranking scales roughly with *coefficient × how much heroes differ on the trait
  × how many heroes the relation counts*. A trait every hero scores about the same
  on barely moves anything, however big its coefficient.
- **Relations count different numbers of heroes.** `against` sums 6 heroes, `with` 5
  and `as` 1, so an `as` rule needs a larger coefficient to matter as much.
- **Don't add flat per-item bonuses** (a "base value" that doesn't depend on
  heroes). The formula has no place for one on purpose, and adding one would
  bring back the "always recommended" problem.
- **Correlations with match data don't change.** Subtracting a baseline shifts
  each item's weights by a constant, so the Pearson r values in the model health
  report compare directly with values from before the change.

## Where coefficients come from

1. **Stat rules (preferred).** A line in `stat_rules.csv` reads *"every point of
   stat X is worth `per_unit` on (trait, relation), counted at
   `conditional_factor` when the stat is conditional"*. It covers every item
   carrying that stat, and it follows patches automatically: when a patch changes
   a number, the score follows with no review. Use this whenever the effect is
   a number the game API exposes.
2. **Typed coefficients (`item_formula_coefficients.csv`)**, only for what stats
   can't express: counters (Knockdown vs fliers, Silence vs casters) and debuffs
   on enemies. Every typed rule becomes a review item whenever that item's
   tooltip changes (see the patch workflow below), so keep them few.
3. **Trait weights (`trait_weights.csv`)** scale a whole (trait, relation) at once.
   Use them for global tuning, not per-item fixes.

### Hero scores measured from match data

Most hero trait scores are hand-rated, but `deals_bullet_damage_general`,
`deals_spirit_damage_general` and `does_melee_damage` are measured. Each is the hero's
share of player-vs-player damage of that type (StatLocker's "Damage identity by hero",
Eternus lobbies, Jun 26 – Jul 7 2026, built from Valve's per-match `damage_matrix`),
multiplied by a mild volume factor: `sqrt(hero's player damage per match ÷ roster
average)` from `/v1/analytics/hero-stats` (Phantom+, Aug 27 – Sep 26 2026). That factor
ranges from about 0.75 to 1.10, so low-damage supports score a little lower. Melee
shares top out near 30%, so they're multiplied by 3 to keep Melee Resist's signal about
as strong as the old hand values. Re-measure after a hero rework rather than tweaking
single values by hand; `/v1/matches/{id}/metadata` has the damage matrix if the chart
goes stale.

## How item stats are extracted (`Services/GameApi/GameSync.cs`)

`item_stats.csv` and `item_tooltips.json` are **generated** by Data → Sync from
Game API. Never hand-edit them; fix the extraction instead.

- **`GameSync.Stats`** maps API property names to our stat names. Several API
  names often feed one stat (for example `BonusSpirit`, `SpiritPower` and
  `AmbushBonusTechPower` all become `TechPower`), so a single stat rule covers
  every item that has any of them.
- **`GameSync.Unscored`** lists properties that are left out on purpose, each with
  a reason: debuffs on enemies, one-shot procs, and effects on allies.
- **Per-stack values** (`_perStack`, plus `_forcePerStack` for ones filed under a
  plain name, like Spellslinger's Fire Rate) are counted fully stacked: value ×
  the item's `MaxStacks`, filed as conditional. `_assumedStacks` gives a count
  for items the game doesn't cap (Ballistic Enchantment stacks per hero hit).
- **Conditional detection** (`IsConditional`) uses the API's `ConditionallyApplied`
  flag, whether the property is on an active or a passive, and whether its
  sibling properties are flagged. Three per-item override sets handle the known
  exceptions: `_forceConditional`, `_forceShown` and `_selfInflicted`.

**The rule for new properties:** every property the tooltip shows under a label
a scored stat uses (such as "Fire Rate" or "Spirit Resist") must be either an
alias in `Stats` or an entry in `Unscored`. Two things enforce this:

- The sync report's "not mapped" section lists every property that is neither.
- The test `GameApiTests.TheSnapshotShopHasNoUnmappedStatsOrStaleOverrides`
  fails if the fixture shop has any unclassified property.

A new alias changes `item_stats.csv` output, so regenerate the sync goldens
afterwards (see "Tests and goldens" below).

## Patch workflow

1. **Data → Sync from Game API.** Beyond the usual added-items and field-changes
   sections, the report now contains:
   - **Item stat changes**: every stat that moved, e.g. "Long Range: Weapon
     Damage (conditional) none -> 40%". These need no action; stat rules pick
     them up.
   - **Tooltip changed on items with hand-typed rules**: recheck those typed
     coefficients against the new tooltip.
   - **Shown under a scored stat's label but not mapped**: add each property to
     `GameSync.Stats` or to `GameSync.Unscored`.
   - **Overrides that no longer match the game**: remove or fix the entry in
     `_forceConditional`, `_forceShown`, `_selfInflicted`, `_forcePerStack` or
     `_assumedStacks`, or give an uncapped per-stack item a count.
2. **Data → Fetch Match Stats** to refresh the real-match lifts.
3. **Data → Model Health Report** to check the model as a whole (next section).

## Model health report (`Scoring/ModelHealth.cs`)

Data → Model Health Report simulates 2,000 random full matches (seed 1, drawn
from the profiled heroes) using the real scoring code, then lists:

- **Recommended whatever the heroes:** items in their tier's top 3 in 80% or more
  of matches. This should normally be empty; anything here is behaving like a
  flat bonus.
- **Never recommended:** items that never score above 0, grouped by cause: no
  rules at all, rules only on traits no hero is scored on, or rules that never
  add up to a positive score.
- **Empty traits:** traits some rule uses that every hero scores 0 on.
- **Match data disagrees:** per item and relation, the Pearson r across heroes
  between the hand weight and the real lift. Listed at r ≤ −0.2, with at least
  10 heroes.
- **Real standouts the hand model gives nothing:** lifts that clear the
  `ItemScoring.PickMinAgainst` / `PickMinAs` bars where the hand weight is 0 or
  less.

When you tune data or change the formula, compare the report before and after.
Treat the match-data sections as leads, not verdicts: real `against` effects
are small (τ ≈ 0.4 points), and `as` lifts partly reflect who buys the item
rather than what it does.

## Tests and goldens

- `ScoringTests` are hand-checkable on `TestStore`. Its roster averages 2 on
  spirit damage (heroes score 5, 0 and 1) and −4/3 on max HP. Keep new scoring
  tests small enough to check by hand.
- `ModelHealthTests` builds a 12-hero roster so the simulation can run.
- Golden files in `tests/DeadlockAdvisor.Tests/Golden/` are regression snapshots
  now, not Python parity. The Python exporter is retired. After a deliberate
  behaviour change, regenerate the affected goldens with:

  ```
  dotnet test tests/DeadlockAdvisor.Tests/DeadlockAdvisor.Tests.csproj -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~GoldenScoringTests|FullyQualifiedName~SyncingTheSnapshot"
  ```

  The golden tests then rewrite their files in the source tree
  (`Support/Golden.cs`: `Updating`, `WriteJson`, `CopyFile`), keeping each
  file's line endings. Review the diff before committing: a change you didn't
  intend shows up there. The files that support regeneration are:
  - `weight_matrix.json`
  - `scoring_cases.json`
  - `store_queries.json` (`item_contributions`)
  - `game_api/sync_cases.json`
  - `game_api/{fresh,stale}/*.csv`

## Data locations

- `src/Assets/SeedData/` is the starter data bundled into the executable.
- The app actually runs on `%AppData%\DeadlockAdvisor\data\` (or the folder set in
  Data → Change Data Folder).
- The two can drift apart. When you analyse the model, check which one you're
  reading.
