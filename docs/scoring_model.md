# Scoring model and maintenance guide

Read this before changing scoring code, formula data (`stat_rules.csv`,
`item_formula_coefficients.csv`, `trait_weights.csv`, `categories.csv`), or the
game sync. It describes how items are scored, why the formula works the way it
does, and how the data is kept correct across game patches.

## The formula

```
weight(item, hero, relation) = Σ over traits of
    (hero_score[hero, trait] − baseline[trait]) × coefficient[item, trait, relation]

coefficient  = trait_weight[trait, relation] × (typed + from_stats)
baseline     = average of hero_score[·, trait] over the profiled heroes
score(item)  = Σ factor(hero) × weight over enemies (against) + allies (with) + you (as)
```

`factor` is 1 unless the scores lean on net worth or enemies are focused (see
"Net worth" and "Focus" below). A
best-target line, which is every line of a single-target item on the team it's cast on
(and, for an ally-cast item, the enemies too) or a line marked Best target, is
counted on its best targets instead of summed over the team (see "Best-target
items" below).

- A **relation** is `against` (an enemy has the trait), `with` (an ally has it)
  or `as` (your own hero has it).
- **Profiled heroes** are heroes with at least one nonzero trait
  (`DataStore.IsProfiled`). Unprofiled heroes are left out of the baseline, and
  they contribute nothing to any score. If they counted, a hero with every trait
  at 0 would look "below average at everything".
- `typed` is a hand-typed coefficient. `from_stats` is derived from the item's
  real stats through `stat_rules.csv` (see below).
- A **score above 0** means *this match wants the item more than a typical match
  does*. The results list only shows items that score above 0, unless its
  cutoff is set to "Every item".

Where this lives in the code:

| Piece | Where |
|---|---|
| Baselines | `DataStore.TraitBaselines()`, computed on demand, never cached, because `SetHeroScore` doesn't trigger a rebuild |
| Weight matrix | `ItemScoring.BuildWeightMatrix` → `WeightMatrix`, each weight split into its summed and best-target parts, with the typical best-target sums |
| One line-up's score | `ItemScoring.Total` over a `LineUp`, shared by `ScoreAll` (every item, whatever it scores: the Match page's lists and `DataOnlyPicks`) and the model health simulation |
| Best-target maths | `BestTargets` (`Sum`, `Expected`, `RankFactor`) |
| The match data's verdict | `ItemScoring.DataStrength`: enemies lift ÷ `PickMinAgainst` + your lift ÷ `PickMinAs`, in "bars"; 1 or more is a standout |
| Net worth factors | `NetWorthWeights.For(match)`; `NetWorthWeights.None` when the toggle is off |
| Focus factors | `FocusWeights.For(enemies, focused)`, built into every `LineUp` by `RelevantHeroes`; `LineUp.Factor` multiplies both |
| Per-hero, per-trait explanation | `ItemScoring.Contribution` → `HeroContribution` (with `NetWorth`, `Factor`) → `TraitPart` (`HeroScore`, `Baseline`, `Deviation`, `Amount`) |
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

### Best-target items

An item whose active is cast on one hero (`Item.CastOn`: Decay, Knockdown,
Slowing Hex on an enemy, Rescue Beam on an ally…) is only as good as its best
target. A plain sum counts "no use against this hero" once for every such hero.
Decay against one big healer and five non-healers lost 186 points for each
non-healer, when you'd simply cast it on the healer. So for these items, the
relation they're cast on (`against` for an enemy, `with` for an ally) uses:

```
best-target sum = Σ over the team's profiled heroes, best weight first, of weight × ½^(rank − 1)
relation score  = best-target sum − the same sum's average over every team of that size
```

The average has a closed form (`BestTargets.Expected`). With the roster's weights
sorted best first, the r-th best of an n-hero team is roster hero j with probability
C(j−1, r−1)·C(N−j, n−r)/C(N, n). `WeightMatrix` works it out up front for each team
size up to 6. The consequences:

- Every item still averages 0 over random matches.
- A constant shift of every weight cancels out, so the deviation-based weights work as they are.
- A one-hero team (a 1v1 lane) is exactly the plain sum.
- Net worth factors apply before the sort.
- `as` always sums: it's one hero.
- An enemy-cast item's allies sum as usual. Spirit Sap is cast on one enemy, but
  every ally with spirit damage hits that enemy through its lowered Spirit Resist.
- An ally-cast item's enemies are best-target too (`BestTargets.AppliesTo`).
  Rescue Beam pulls one ally out of one pick per cooldown, so it's worth what the
  worst picker is worth, not the sum over every enemy that can pick.

On the Decay example above, the enemy side goes from −457 to −31.

`GameSync.CastOn` decides from the API: an active whose tooltip shows
`AbilityCastRange` and no `*Radius` property, except the items in
`_notSingleTarget`. Silence Wave's projectile hits everyone in its path, and Warp
Stone's range is how far you teleport. The API doesn't name the target's team,
but every ally-cast active's text says "Can be self-cast" and no enemy-cast one
does, so that phrase makes it `with`. `_forceSingleTarget` adds items with no
targeted active that still work one hero at a time: Counterspell's parry blocks
one enemy ability per cooldown, so it counts its best enemy, not all of them.
The result goes in `items.csv`'s
`single_target` column (`against`, `with` or empty; an old `1` loads as
`against` until the next sync). The sync report lists every item that became or
stopped being single-target or switched sides, and flags a `_notSingleTarget` or
`_forceSingleTarget` entry that no longer matches.
The explain panel ranks each hero ("2nd target ×0.5") and takes the typical team
off as a line of its own.

#### Best-target lines

Some items have no targeted active but still pay off once per cooldown, however
many heroes give them a reason to. Reactive Barrier gives one barrier when you're
first crowd-controlled, so one stunner on the enemy team triggers it every fight,
and a fifth adds next to nothing. A plain sum gets this wrong both ways: four
low-CC enemies cancel out the one stunner, and six stunners count six times.

So any item × trait × relation line can be marked to count its best targets:
the Best target column on By Trait (click it, or press B), or the "best target"
pill on a By Item rule card. The mark lives in `item_formula_coefficients.csv`'s
`best_target` column (`DataStore.BestTargetLines`). A row with coefficient 0 marks a line
that only has a stats part. The mark covers the line's typed and stats parts alike,
and never applies to `as`.

Mark only the lines that trigger once per cooldown, not the whole item.
Indomitable's Applies Crowd Control line is one, but its bullet and spirit
resist lines (from `stat_rules.csv`) protect against every enemy's damage and keep summing.

On one relation, the marked lines and any line `CastOn` covers
(`DataStore.OnBestTargets`) form one best-target part per hero. The other lines
form a summed part:

```
relation score = Σ summed parts + best-target sum of the ranked parts − its typical value
```

Heroes are ranked by their ranked part alone, and the typical value comes from the
ranked parts too, so every item still averages 0. The explain panel keeps one card
per hero: the card notes the hero's rank, and each best-target line shows the rank's
factor in its arithmetic ("× 0.25"), so the lines still add up to the card. Copying an item's
rules copies its marks, and clearing or deleting a rule card clears them.

## Match data on the Match page

The real-match lifts (`DataScores`) are **never added into the formula score**,
because they're in different units and are partly about who buys the item. They
change the list in three ways only:

- **Rank by** (`AppSettings.ResultsRankBy`, `ResultsViewModel.Ranked`) orders the
  list by **Formula + match data** by default, the two added in common units (below),
  or by the score or `DataStrength` alone. Without match data it ranks by the score.
- **DATA ★** marks a row whose `DataStrength` is 1 or more.
- **"Match data also likes"** lists the items with a strength of 1 or more that the
  formula scores 0 or below (`DataOnlyPicks`), at the end of the formula-ranked list.

### Formula + match data

```
blend(item) = score ÷ σF + DataStrength ÷ σD
```

σF and σD (`BlendScale`) say how far a formula score and a data strength typically
stray from 0 in line-ups like the one being scored. Each is the root mean square of
the nonzero values over 200 seeded random line-ups of the same shape: as many
enemies and allies, with or without you (`ScoreScales`). One unit of either is then about as unusual as one of the other,
so the two are weighted equally.

- **Missing opinions add 0.** The ranking used to take `min(score share, data share)`,
  which let a missing opinion veto an item. Items the formula has no rules for, such
  as Slowing Bullets or Refresher, could never appear.
- **The scales depend on the data.** `DataService.RebuildMatrix` makes a new
  `ScoreScales` on every data change and on new match data. Each shape is measured
  the first time it's needed, which takes about 40 ms.
- **The row** shows the blend on the right and two bars in the same units, which go
  below 0 when negative: the formula's part on top and the data's below. The row
  tip gives the working.
- **DISAGREE** marks an item the two rate at least a unit each in opposite
  directions (`BlendScale.Disagree`). The Match tab's "Hide items the formula and
  data disagree on" filter drops them from this ranking's list; it never changes a score.
- **The explain panel** heads the item with the verdict, such as "Formula +1.6 · data
  +0.7 → +2.3", and says when either opinion has nothing to add. Under the hero cards'
  total in points it shows the conversion, "402.0 pts ÷ 251 typical = +1.6".

`DataStrength` nets the two relations instead of taking the better one, so a
strong counter that does badly on your own hero doesn't count as a standout.

### An enemy's own purchases

An "against E" query only sees players on E's opposing team, so E's own purchases
are never in it. "Every match" includes them. Compared with every match, an item
E buys a lot and does badly with would look like a counter to E. For example,
Lash is Refresher's biggest buyer and does about 8 points worse with it than
average, which gave "Refresher vs Lash" a raw lift of +1.7. So `MatchStatsMath.Analyse`
compares each enemy's query with every match minus that enemy's own purchases
(`MatchStatsMath.Subtract`), taken from the `as` family.

That needs both families over the same window. They come from the same
per-patch counts (below), so they always do. A lifts file from before own
purchases were taken out has `own_excluded: false` in its family meta, and the
status card and the filters say to fetch again.

### Items your hero rarely builds

An enemy lift averages over the players who build the item. If your hero hardly
ever builds it, those players aren't like you. For example, Refresher's enemy lifts
come from the ult-heavy heroes who build it, and Vindicta rarely does. So
`DataScores` multiplies the "against" sum by `ItemScoring.Relevance`:

```
build ratio = (item's share of your hero's purchases in its tier)
            ÷ (item's share of everyone's purchases in that tier)
relevance   = clamp(build ratio ÷ RareBuildRatio, 0, 1)      RareBuildRatio = 0.25
```

Build ratios come from the `as` download over the lifts' rank range
(`MatchStatsMath.BuildRatios`), each patch counting by its share of the weight in
the lifts (see "Weighing the patches"). `DataStore.BuildRatios` caches them and clears the
cache whenever the counts, the meta or the items change. Relevance is 1 when there's
no self hero, no counts file, or no purchases by your hero in that tier at all. It is
0 when your hero never buys the item. The row shows a RARELY BUILT badge, and its tip
and the explain panel say how much the enemy lifts count. The Match tab's "Hide items
your hero rarely builds" filter drops them from the list; it never changes a score. `DataStrength` and DATA ★
use the reduced sum.

### Hero fit on the Hero Items page

**Hero fit** is the "you" lift, called that wherever the app describes it. On the
Hero Items page its column is the lift worked out over the
matches the table counts (`HeroFits`, built in `HeroItemTable.Build`): the same
`MatchStatsMath.RawLifts`, then `Shrink`, so it follows the table's patches,
match mode and rank range instead of the Match tab's fixed download. Everyone's win rate with
an item is every hero's own purchases added up, which is every match's however
the table is filtered, so no per-mode baseline is needed. An item bought in
under `HeroFits.MinMatches` (500) of the hero's matches has no fit but still counts
toward its tier's average. The noise scale is the every-match halves' (ranked-only and rank-group counts have none).

Why the page shows the lift beside the win rate, and why the Match tab ranks by
the lift alone: a win rate mostly records *when* an item is bought. Tier 4 items
are only bought in games already going well, so every one of them wins well above
the hero's average, and the win rate can't tell the item from the game. The lift
compares the hero with everyone who builds the same item, which cancels that. It
doesn't say how strong an item is for everyone, only how much more this hero gets
from it. The Match tab wants what *this match* changes about an item's value,
so adding the raw win rate there would be a flat per-item bonus, the "always
recommended" problem described above.

### The counts behind the lifts

Download Match Data keeps the raw win and match totals per patch, in
`data/match_counts/<patch date>.json` (`MatchSegment`). It keeps the current
patch and the one before, plus the one before that when those two have under 14
days between them (`MatchFetchPlan`). Each file covers its patch's window, split
at the midpoint into the halves that calibrate the noise. It holds every match
and, optionally, each rank group. How the patches are weighed against each other
is the next section.

A download asks only for what's missing (`MatchFetchPlan.For`):

- a patch with no counts, or the current one, which keeps collecting matches;
- a patch that's over but was fetched before it ended or within a day of its end
  (`MatchSegment.SettleSeconds`), once more;
- the rank groups of a finished patch fetched without them.

A finished, settled patch is never fetched again. The API adds the matches up
itself, so a call over a day costs the same as one over a month: a download's
cost is its number of calls, not how many days it covers. Every match comes
first, for each patch, and goes into use as it arrives; the rank groups follow.
A download stopped part-way keeps the phases it finished.

Before it starts, the download dialog (`MatchDownloadViewModel`) lists the
patches stored and what the plan does to each, with and without the rank groups.
Each choice shows about how long it takes and how much it brings
(`MatchFetchEstimate`). The estimate starts from a measured 0.405 s and 15 KB a
call, and moves toward each finished run's pace. While it runs, the status-bar
chip opens each phase's progress (`MatchDownloadProgressViewModel`).

Each half of a window takes one call for every match, one for every hero's own
purchases (`bucket=hero`), and one per enemy hero. The whole window then takes three
more, for the Hero Items page rather than the lifts: each hero's matches
(`/hero-stats`), the same in ranked matches alone (`match_mode=ranked`), and every
hero's own purchases in ranked matches. That's 83 calls per patch with 38 heroes
(`MatchFetchPlan.EveryMatchCalls`). Each rank group repeats them but the two ranked
ones: unranked matches have no badge, so a rank group holds only ranked matches. The calls
start at most one per 0.4 s, two at a time (`RequestPacer`), and a 429 holds every
start back for 30 s. A failed call is retried by what went wrong: a 429 up to three
times after 30 s, a 5xx once after 5 s, and a dropped connection or a timeout twice,
after 5 s and then 10 s (`MatchStatsService.GetAnalyticsAsync`); the progress view
says why it paused. Anything else ends the download.

Two answers are refused rather than stored, because a segment with no counts would
look finished and the patch would never be fetched again. An "every match" answer
with no rows over a window longer than 6 hours (`MatchStatsService.EmptyBaselineWindow`)
throws `InvalidDataException`, so nothing of that patch is saved (and the CI job fails
instead of publishing it); a quiet first few hours of a patch are fine. And
`GameApiService.SyncAsync` throws the same before touching the store when the API lists
no heroes or no shop items, since the item stats and tooltips have no backup.

#### The shared download

Usually none of that runs in the app. A scheduled GitHub Actions job
(`.github/workflows/match-data.yml`, running `tools/MatchSnapshot`) makes the
same calls with the same code (`MatchSnapshotJob`) and publishes the segments as
assets of the repo's rolling `match-data` release: `manifest.json` and one
gzipped segment per patch, named by its hash (`MatchSnapshot`). The job restores
the last run's files, so it follows the same plan as an app would: a new or ended
patch at the next daily run, and the current patch again every other day
(`MatchSnapshot.RefreshAfter`), always with the rank groups. A run with nothing due
makes one call, for the patch list, and skips the hero sync.

The app prefers it (`MatchSnapshotService`, `SnapshotPlan`): a patch is fetched
when the snapshot's copy is new, has ended or settled since, brings rank groups
the stored one lacks, or is simply fetched later. Counts the app holds newer than
the snapshot's are kept, and a snapshot without a patch the app already has is
behind, so it isn't used. Each file is checked against the manifest's size and
SHA-256 before it's applied, through the same `IMatchStatsService.Apply` as an API
download. The app asks `deadlock-api.com` itself only when the manifest can't be
had or hasn't been updated for four days (`MatchSnapshot.StaleAfter`).

### Weighing the patches

Right after a patch, the new patch has few matches and the old one many, but
the old one describes a game that has changed. `MatchStatsMath.AnalysePatches`
works each patch's lifts out against that patch's own baseline, then combines
them per hero and item:

```
weight(patch) = 1 / (se² + patches back × δ²)
lift          = Σ weight × lift ÷ Σ weight          se = 1 / √(Σ weight)
```

δ² is how far real lifts move from one patch to the next. `EstimateDrift2`
measures it from the lifts the two newest patches share: the spread of their
differences minus what their noise alone would give. With fewer than 100 shared
lifts, which is only the first hours of a patch, τ² stands in. So:

- **A young patch leans on the one before.** Its lifts have a large se, so the
  older patch's weight dominates. As matches arrive its se shrinks and it takes
  over.
- **A patch that changed a lot fades faster.** A bigger δ² penalises every
  older lift more.
- **An old lift alone still counts, but says less.** Its se takes the drift on
  top of its noise, so it shrinks harder.

The noise scale is calibrated from every patch's halves at once. An item needs
`MinN` matches over all the patches together, and a patch counts toward it from
a quarter of that. With a single patch this is the old single-window
analysis (`AnalyseFamily`).

Each patch's average share of the weight goes in the meta's `segments`, and δ in
each family's `drift`. The status card lists every patch with its share, the
reports say how far lifts move patch to patch, and the build ratios weigh each
patch's purchases by the same shares.

### Leaning toward ranks

There are five rank groups of two ranks each, by the average badge of both teams
(`MatchStatsMath.RankGroups`). The last takes Ascendant and Eternus, which are too
rare to stand alone. Unranked matches have no badge, so they're only in "every
match", and the groups don't add up to it. Ranked matches are about a third of all
purchases, and Ascendant and Eternus under 1%.

The Match tab's filters (`DataRanksViewModel`) lean the data toward a range of
ranks rather than narrowing it to them. Narrowing used to empty the data: Phantom+
alone kept 65 of 3,631 your-hero lifts. Now the numbers stay every match's and move
only where the range plays detectably differently (`MatchStatsMath.LeanTowards`):

```
lift(range) = shrunk every-match lift + π_C × shrink(L_R − L_C, σ²)
```

- **L_R and L_C.** L_R is the range's lift, over the patches with a rank breakdown,
  with a floor of a quarter of `MinN`. L_C is the same over every other match,
  unranked ones included. Every match mixes the two, so the range's own lift is
  every match's plus π_C (the rest's share of the matches) times the difference.
- **σ².** It says how much real differences vary, estimated across the family as
  τ² is, and each difference is shrunk toward 0 against it. When the differences
  disagree between the halves, σ² is 0.
- **Thin ranges look more different than they are.** The same few players play on
  both days of a window, so their habits agree between the halves and look real.
  On patch 09-29's first two days, enemy lifts spread ±1.5 points at Phantom+
  against ±0.5 over every match. Real lifts are taken to spread as widely at every
  rank as over every match (τ²), so either side's extra spread counts as noise in
  each difference. On those matches Phantom+ moved enemy lifts by about ±0.1 points
  and your-hero lifts by about ±1.3: high-rank players build differently, and that
  difference agrees between the halves without spreading the lifts any wider.
- **A range can't empty a family.** Whether a family is kept is up to every match.
  Build ratios come from every match too: how a hero is built barely changes with
  rank, and a thin range would mark items rarely built by chance.

The lifts file records each lift's `rank_shift`, which the explain card shows as
"ranks +0.20". Each family's `lean` in the meta holds σ, the split-half reliability
and how many lifts moved, and the filters flyout lists it. The range goes in the
meta's `rank`, and a new download keeps it.

## Net worth

Detect reads each hero's net worth off the game's top bar (`Vision/NetWorthReader.cs`)
into `MatchState.NetWorth`. With the Match tab's "Lean toward heroes ahead on net worth" filter on
(`AppSettings.ResultsByNetWorth`, on by default), each hero's whole term in a
score is multiplied by

```
factor(hero) = clamp(1 + Strength × (souls / average − 1), 1 − MaxShift, 1 + MaxShift)
Strength = 0.5, MaxShift = 0.3
```

A hero at 1.5× the average counts ×1.25; the effect stops at ±30%.

- **The average** is over the match's heroes in the same reading as the hero's
  latest value. A reading drops a side whose pills didn't add up to its total,
  so a hero's latest value can be older than another's. Comparing across
  readings would make whoever was read last look ahead.
- **No reading, or an average under 500 souls** (everyone still on their
  starting 600): factor 1.
- **Why it multiplies instead of adding.** It reweights the heroes; it isn't a
  bonus of its own. A counter to a fed enemy gains, and an item that is poor
  against that enemy loses by the same proportion. Factors average about 1
  across the lobby, so the "no flat bonuses" rule above still holds.
- **The match-data lifts** (`DataScores`, `DataParts`) are never weighted. They
  stay a separate second opinion.
- **The model health report** simulates line-ups with no net worth
  (`NetWorthWeights.None`), so it measures the hand model alone.
- The explain panel shows "×1.18 · 25k vs 19k avg" on a hero whose factor isn't
  1 (`ExplainText.NetWorth`), and that hero's amount includes the factor. Their
  trait lines stay unweighted.

## Focus

A click on an enemy on the match bar focuses the recommendations on them
(`MatchState.Focused`, any number of enemies, such as both lane opponents). Each
enemy's whole term is multiplied by a factor (`FocusWeights`):

```
focused   = Ratio × n / (Ratio × f + n − f)        Ratio = 5
unfocused =         n / (Ratio × f + n − f)        n enemies, f of them focused
```

One focused enemy in a full match counts ×3 and the others ×0.6, so it's half the
enemy side; two count ×2.14 and the rest ×0.43. Focusing nobody or everybody
changes nothing.

- **Why the factors add up to n.** Focus moves weight between the enemies and adds
  none, so the enemy side weighs what it did against you and your allies. Scoring
  the focused enemy alone would shrink the enemy side to one hero and tilt the list
  toward items for your own hero; a bare ×5 would lift every counter item. Like net
  worth's factors, these average 1 over the enemies, so focus is no flat bonus.
- **It multiplies with net worth**, before best-target ranking, as net worth does.
  Because `BestTargets.Sum` is convex, weighting one enemy up raises a single-target
  item's average a little: Decay gains a lot when the focused enemy is its best
  target, and loses less when they're a poor one, since you'd cast it on someone
  else. `ModelHealthTests` bounds that lift over random focused matches.
- **The match data takes it too.** `DataScores` multiplies each enemy's lift by
  the enemy's focus factor before `Relevance`, and the explain panel's data lines
  show it. Net worth never weights the data, because it's an opinion about who
  matters; focus changes the question, and the data has an answer for each enemy.
  Without it, ranking by "Match data" would ignore focus, and the default ranking
  would follow it at half strength.
- **The blend scale knows it.** `LineUpShape.Focused` counts the focused enemies,
  and `Draw` focuses that many, so `ScoreScales` measures line-ups as concentrated
  as the one being scored.
- **Only a profiled enemy can be focused** (`MatchBoardViewModel.CanFocus`). An
  unprofiled one adds nothing to the formula, so focusing them would only take
  weight from the others.
- **It belongs to the match.** A hero who stops being an enemy drops out of it, a
  new line-up starts without it, a detection of the same twelve heroes keeps it
  (as it keeps the net worth history, `VisionApply.ApplyToMatch`), and it's saved
  with the match.
- The explain panel notes "focused ×3.00" or "not focused ×0.60" on each enemy
  (`ExplainText.Focus`), and the results header shows who's focused, with a ×
  that stops focusing.

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

`max_hp` follows the game itself: every Sync from Game API measures it
(`GameSync.MeasuredMaxHp`) and lists the heroes whose score moved. A hero's mid-game
health is its starting max health plus the health of every standard level-up it reaches
by 10,000 souls (`MidGameSouls`, 15 level-ups), plus what its own abilities add, which the
API doesn't show (`_kitHealth`: Abrams' third ability gives 200). The score is how far
that sits from the median over every hero the API lists, with ±`scale_max` at ±30%
(`MaxHpSpread`), as the trait's description says. Only profiled heroes are written: a new
hero rated on nothing else would count as below average at everything. A `_kitHealth`
entry whose hero the API no longer lists shows under the report's stale overrides.

`durability` is measured the same way, from how much damage each hero takes
(`HeroDurability`). Every Sync makes one more call, `/v1/analytics/hero-stats` over the last
30 days of Phantom+ matches (average badge 91 and up), and reads each hero's
`total_player_damage_taken ÷ matches`. That is damage after resists, barriers and healing, so
it carries what base health misses, and it also records how much a hero's role draws fire.
Heroes with under `MinMatches` (1,000) are left alone. The score is linear against the median
of the heroes the answer lists: the median is 50 on the 0–100 scale, and ±`Spread` (55%) is
the two ends, as the trait's description says. Phantom+ and every rank give nearly the same
order (Spearman 0.98), and per match was steadier than per death (0.92), so it stays per match.
The same profiled-only rule applies. A sync whose hero-stats call fails still goes ahead with
the roster and the shop and says durability wasn't measured; `GameApiService.SyncAsync` with
`measureHeroes: false` skips the call, as the match-data job does, since it throws its store
away.

Which rules key off durability and which off max HP: a rule follows durability when the stat
protects against damage in general, and max HP when it is a share of, or damages a share of, the
health pool itself.

- **Durability:** the `Barrier` rule (a flat amount of protection, a bigger share of a low-durability
  hero's pool, so it is negative) and the `TechResist` and `BulletResist` rules (resist multiplies
  health, barriers and healing alike), and Berserker, whose weapon damage stacks on damage taken.
- **Max HP:** `BonusBaseHealth` and `ShopBaseHealth` (% of base health), flat `BonusHealth`, and the
  items that take a % of the target's health (Tankbuster, Toxic Bullets, Decay, Siphon Bullets, Scourge).

Durability's scores spread about 2.5 times less than max HP's (standard deviation 19 against 47), so
a rule moved from one to the other gets its rate multiplied by that to keep its pull on the ranking.

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

**The shop bonus.** Every soul spent in a shop raises that shop's investment bonus:
Weapon Damage for weapon items, Spirit Power for spirit items, and % base health for
vitality items. The curves are in every hero's `cost_bonuses` in the API, and every hero
has the same ones (`GameSync.ShopBonuses`). Each item gets one unconditional stat for its share of the bonus:
`ShopWeaponDamage`, `ShopSpiritPower` or `ShopBaseHealth` (`GameSync.ShopBonusStats`).

```
share = cost × (bonus at the top of the curve ÷ souls at the top)     e.g. 6400 × 115 ÷ 28800 = 25.6%
```

The curve has steps, such as +28% Weapon Damage at 4,800 souls. Which step an item
tips you over depends on the build, so each item gets the average rate. The
`stat_rules.csv` lines read these stats on `as` at half the rate of the matching
real stat. That way the bonus nudges a spirit hero toward spirit items without
deciding the list. Every item in the same shop and tier shifts by the same amount,
so the bonus reorders shops, not items within one. The sync report gives one line
per shop whose curve moved, instead of a stat change for every item in it.

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
   - **Measured hero scores**: the heroes whose max HP or durability score moved.
     Publish them like any model change.
   - **Single-target changes**: items now scored on their best targets, or no
     longer (see "Best-target items"). Check that each one really is cast on one hero.
   - **Tooltip changed on items with hand-typed rules**: recheck those typed
     coefficients against the new tooltip.
   - **Shown under a scored stat's label but not mapped**: add each property to
     `GameSync.Stats` or to `GameSync.Unscored`.
   - **Overrides that no longer match the game**: remove or fix the entry in
     `_forceConditional`, `_forceShown`, `_selfInflicted`, `_forcePerStack` or
     `_assumedStacks`, or give an uncapped per-stack item a count.
2. **Match data.** The shared download picks the new patch up within a day.
   With updates on (`AppSettings.AutoUpdateMatchData`), the next startup refreshes
   it in the background (`MatchDownloadPlan.IsDue`): it fetches the new patch and
   finishes the one before, and older patches are kept as they are. It also
   refreshes the current patch once its counts are a day and a half old (three days when
   it has to ask the API itself). **Data → Download Match Data…** does the same on
   demand. Updates never start a first download, and a failed one says nothing.
3. **Data → Model Health Report** to check the model as a whole (next section).

**New heroes need no step here.** The New heroes workflow adds each hero the game
lists as active to the seed with every trait at 0 (`GameSync.ApplyRoster`, run by
`PublishModel --add-new-heroes`), and installs take it with the next model update, so
the only manual work is rating it in Hero Traits and publishing as usual. Until then
the hero is unprofiled: left out of the baselines and out of scoring, as above, so
nothing about the other heroes' recommendations moves. [publishing.md](publishing.md),
"New heroes", has the whole path.

## Model health report (`Scoring/ModelHealth.cs`)

Data → Model Health Report simulates 2,000 random full matches (seed 1, drawn
from the profiled heroes) using the real scoring code, then lists:

- **Recommended whatever the heroes:** items in their tier's top 3 in 80% or more
  of matches. This should normally be empty; anything here is behaving like a
  flat bonus.
- **Never recommended:** items that never score above 0, grouped by cause: no
  rules at all, rules only on traits no hero is scored on, or rules that never
  add up to a positive score.
- **Biggest swings:** the 10 items whose scores stray furthest from 0 (the root
  mean square over the simulated matches), next to the median item's. An item far
  above the median lands at the top or the bottom of nearly every list. The usual
  cause is a coefficient much bigger than the rest, such as a stat rule giving
  5.5 per point of healing.
- **Empty traits:** traits some rule uses that every hero scores 0 on.
- **Match data:** which patches it comes from with each one's share of the
  weight, which ranks, and how far lifts move from patch to patch.
- **Match data disagrees:** per item and relation, the Pearson r across heroes
  between the hand weight and the real lift. Listed at r ≤ −0.2, with at least
  10 heroes.
- **Real standouts the hand model gives nothing:** lifts that clear the
  `ItemScoring.PickMinAgainst` / `PickMinAs` bars where the hand weight is 0 or
  less.
- **Formula vs match data:** one number per relation to compare before and after
  tuning. It's the correlation between hand weight and raw lift over every item ×
  hero pair, each counted by 1/se², with both measured from their item's own
  weighted average first, so it's how items vary across heroes that counts, not
  which items are popular (`ModelHealth.CenteredPairs`).
- **Traits the enemy lifts follow:** for every item and trait, the weighted slope
  of the enemy lifts on the heroes' distance from the trait's average
  (`MatchStatsMath.Slope`), listed where |t| ≥ `SuggestT` (3.5, strict because
  it's thousands of fits) and the hand coefficient is 0 or the other sign. When
  the pooled hand weights follow the lifts (a positive slope), that slope turns
  each one into a coefficient in Item Formulas' units; when they don't, there's
  no such scale and only the slope is given.

When you tune data or change the formula, compare the report before and after.
Treat the match-data sections as leads, not verdicts: real `against` effects
are small (τ ≈ 0.4 points), and `as` lifts partly reflect who buys the item
rather than what it does.

## Tests and goldens

- `ScoringTests` are hand-checkable on `TestStore`. Its roster averages 2 on
  spirit damage (heroes score 5, 0 and 1) and −4/3 on max HP. Keep new scoring
  tests small enough to check by hand.
- `ModelHealthTests` builds a 12-hero roster so the simulation can run.
- Golden files in `tests/DeadlockAdvisor.Tests/Golden/` are regression snapshots.
  After a deliberate behaviour change, regenerate the affected goldens with:

  ```
  dotnet test tests/DeadlockAdvisor.Tests/DeadlockAdvisor.Tests.csproj -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~GoldenScoringTests|FullyQualifiedName~SyncingTheSnapshot|FullyQualifiedName~ADownloadWritesEachPatch|FullyQualifiedName~GoldenMatchStatsTests"
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
  - `match_fetch/{result.json,match_item_lift.csv,match_item_lift.meta.json}`,
    from a download off the synthetic API (`Fakes/SyntheticItemStatsApi`)
  - `match_stats_math.json` (`fetch_result_meta`, `fetch_result_lines`)

  `data/items.csv` (the store the other goldens load) and `csv_roundtrip/items.csv`
  are hand-kept inputs. Give them any new `items.csv` column by hand.

## Data locations

- `src/Assets/SeedData/` is the starter data bundled into the executable.
- The app actually runs on `%AppData%\DeadlockAdvisor\data\` (or the folder set in
  Data → Change Data Folder).
- The two can drift apart. When you analyse the model, check which one you're
  reading.
