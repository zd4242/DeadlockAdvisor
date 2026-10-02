# Deadlock Item Advisor

Recommends items based on who's actually in your match, using a
category-based formula system instead of hand-typed per-hero-per-item
weights.

It's a Windows desktop app (C#, .NET 10, Avalonia). It can read the match
off your screen, and it takes a second opinion from real match results on
[deadlock-api.com](https://deadlock-api.com).

> **Not affiliated with Valve.** Deadlock, its heroes, items and art are
> Valve's. This is an unofficial fan-made tool. Match statistics, hero and
> item data and art come from [deadlock-api.com](https://deadlock-api.com),
> a free community project; thanks to them.

## Install

`dotnet publish` (below) makes a single self-contained
`DeadlockAdvisor.exe` (Windows x64). Copy it anywhere and run it; nothing
else needs installing.

On first run it creates `%AppData%\DeadlockAdvisor\` with a starter copy
of the data. One dialog offers the hero and item art and the match data,
both downloading in the background: the art from `deadlock-api.com`, the
match data ready-made from this repo's [shared download](#shared-match-data).
With the match data's "keep it up to date" on (the default, and in
Settings → Data), later startups refresh it quietly when a newer patch is
out or it's half a day old. Settings live beside it in `settings.json`.

To build it yourself (.NET 10 SDK):

```
dotnet run                                          # debug build
dotnet publish -p:PublishProfile=win-x64            # the single exe, into publish\
dotnet test                                         # everything, including the golden tests
```

## The three pages

**Match** — who you're playing against, and what to buy.

Press <kbd>F9</kbd> and let it read the whole match off the screen:
see **Detecting the match from the screen** below.

To set or correct heroes by hand, open the hero picker with **Edit heroes**
(or <kbd>Ctrl</kbd>+<kbd>F</kbd>, Alt+1/2/3, or a click on an empty slot in
the match bar). Pick what you're assigning with the You / Enemy / Ally
buttons (or Alt+1/2/3), then click heroes. Clicking a hero who already has
that role clears them; double-click sets You; right-click opens the role
menu. The search box keeps focus, so `hay` <kbd>Enter</kbd> drops Haze onto
the current side and clears the field for the next name
(<kbd>Up</kbd>/<kbd>Down</kbd> move the highlight). <kbd>Esc</kbd> clears
the search, then closes the picker.

Click a portrait in the roster to change its role or remove it.
Recommendations score everyone in the match across all four tiers. The
dropdown beside them picks what ranks the list: the formula, the match
data, or both. The rest sits behind **Filters**: the cutoff hides items
scoring under 20/40/60% of the best, **Group by tier** splits the list
into collapsible tiers, and **Match data from** leans the match data
toward a range of ranks: the numbers move only where those ranks play
detectably differently. That's worked out on the spot from the downloaded
rank groups, and stays set until changed. The button fills in while a
filter the list doesn't show is on (hiding rarely built items, or leaning
toward ranks), and its tooltip says which.

Click any recommendation to see **why** it scored what it did, per hero
and per trait, with the arithmetic shown. **Settings → General** can hide
the arithmetic, which then shows when you hover a line. With nothing selected, the panel
lists what the match data likes that the hand model doesn't.

**Hero Traits** — the hero × trait grid, edited in place.

| Key | Does |
|---|---|
| `0`–`100` | commits as soon as no more digits fit; <kbd>Space</kbd> or <kbd>Tab</kbd> ends a short number |
| `-` first | negative value, for the ± traits |
| <kbd>Backspace</kbd> | clears the cell |
| <kbd>Enter</kbd> | next hero, same trait |
| double-click / <kbd>F2</kbd> | a numeric editor, for decimals |

**Copy from…** clones another hero's profile; **Clear hero** resets one.
The footer explains whichever trait you're on.

**Item Formulas** — what each item responds to.

- *By Item*: pick an item, edit its rules, and watch the "Would apply to"
  preview show which heroes they fire against, with the arithmetic in the
  tooltips. The item's in-game card sits alongside.
- *By Trait*: pick one trait and relation, then run down every item typing
  coefficients, one keystroke each. **Weight ×** scales the whole trait and
  relation at once, and the **stat rules** strip fills coefficients from
  each item's real stats (see *Coefficients from item stats* below).

Edits save themselves a fraction of a second after you stop typing, and
the Match page rescores as you go.

## Shortcuts

| Keys | Does |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>-</kbd> / <kbd>0</kbd>, <kbd>Ctrl</kbd>+wheel | zoom (remembered, along with the window and your last match) |
| <kbd>Ctrl</kbd>+<kbd>Tab</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Tab</kbd> | next / previous page |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | focus the current page's search |
| <kbd>Ctrl</kbd>+<kbd>R</kbd> | reload the data from disk |
| <kbd>F9</kbd> | detect the match from the screen, from the game too (see below) |
| middle click | autoscroll |

## Data folder

Everything lives under one folder holding `data/` and `assets/`: by
default `%AppData%\DeadlockAdvisor`. **Data → Change Data Folder…** points
the app somewhere else, such as a synced folder shared between machines.
Nothing locks the files, so don't edit them from two places at once.
**Data → Open Data Folder** opens it in Explorer.

| File | Shape | Notes |
|---|---|---|
| `heroes.csv` | hero_id, hero_name, game_id | game_id filled in by the game sync |
| `items.csv` | item_id, item_name, category, tier, game_id, cost | category is the shop: weapon/vitality/spirit |
| `categories.csv` | category_id, category_name, scale_min, scale_max, description | the trait list |
| `hero_category_scores.csv` | hero_id, category_id, score | every hero × trait pair |
| `item_formula_coefficients.csv` | item_id, category_id, relation, coefficient | only the traits an item cares about |
| `trait_weights.csv` | category_id, relation, weight | only weights other than 1 |
| `stat_rules.csv` | stat, category_id, relation, per_unit, conditional_factor, note | see below |
| `item_stats.csv`, `item_tooltips.json` | | **generated** by the game sync; don't edit |
| `match_item_lift.csv` + `.meta.json` | | **generated** from the match counts, for the chosen ranks; no backups |
| `match_counts/<patch date>.json` | | **generated** by Download Match Data: one patch's raw totals, every match and per rank; no backups |
| `model.json` | | which published version of the model each file came from ([formula updates](#formula-updates)) |

The CSVs are the source of truth and stay hand-editable. Every write
keeps a timestamped copy of the previous file under `data/.backups/`
(last 12) and goes through a temp file, so an interrupted save can't leave
a truncated CSV. An unchanged table saves byte for byte, so the files only
change when the data does.

## Data menu

- **Sync New Heroes / Items / Categories**: after adding rows to the CSVs
  by hand. Backfills missing hero × trait rows at 0, drops rows for
  deleted ids, and lists unrated heroes and items with no rules.
- **Sync from Game API**: pulls heroes and the shop from
  `deadlock-api.com`, following patches (costs, tiers, new items), and
  rewrites the item stats and tooltips. Only changed files are written.
  Run it after each patch: the report lists each item stat that moved,
  items with hand-typed rules whose tooltip changed, stats it doesn't know
  how to map, and per-item overrides that no longer match the game.
- **Download Match Data…**: item win rates against, with and as each hero,
  shown beside each recommendation as "data": a second opinion, not part
  of the score. A dialog first shows the patches stored, what the download
  will fetch, and about how long it takes and how big it is. It comes from
  the [shared download](#shared-match-data) in a few seconds, with the
  rank groups that let the Match page lean the data toward a range of
  ranks. When that isn't available it asks `deadlock-api.com` directly:
  every match (under a minute a patch), or with the rank groups too
  (about 3 minutes a patch), with each phase behind its chip in the status
  bar. Either way a finished patch is never fetched again, each patch's
  numbers are in use as soon as they arrive, and the status bar turns red
  when a newer patch is out than the data covers.
- **Check for Formula Updates**: takes the newest published hero ratings
  and item formulas ([formula updates](#formula-updates)), asking again
  about files you kept your own changes in.
- **Model Health Report**: simulates 2,000 random matches and lists items
  recommended whatever the heroes, items never recommended (and why),
  traits no hero is scored on, and where real match data disagrees with
  the hand model.
- **Reload from Disk** (Ctrl+R), **Export Snapshot to Excel** (a read-only
  `deadlock_advisor_data.xlsx`, never read back), **Open / Change Data
  Folder**, **Download Art…**.

## Formula updates

The hero ratings, item formulas and the game data they're tuned against
(everything in `src/Assets/SeedData` but the match lift) are the **model**.
A new install starts from the copy built into the app, and later startups
take the newest one published on this repo's `main` branch (Settings →
Data turns that off; **Data → Check for Formula Updates** checks on
demand). `model.json` lists each file's SHA-256, and a copy in the data
folder records what was installed there, so the app can tell a file that's
only out of date from one you've changed, by hand or with Sync from Game
API. Files you haven't changed are replaced quietly, with the old ones
kept in `data/.backups/`; for files you have changed, a dialog asks which
to replace, and one you keep isn't asked about again for that version.

To publish a new version of the model: copy the files from your data
folder into `src/Assets/SeedData`, regenerate `model.json` with
`dotnet test -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter
"FullyQualifiedName~TheSeedsModelJsonListsEveryModelFileByItsHash"`, run
the tests, commit and push. Installs pick it up at their next startup. A
change older apps can't read, such as a new column, bumps
`ModelManifest.CurrentFormat`, so only apps that know it take that version.

## Shared match data

Every install would otherwise make the same few hundred calls to
`deadlock-api.com` for the same numbers. Instead, the **Match data**
workflow (`.github/workflows/match-data.yml`) runs every 3 hours and
publishes them as assets of this repo's rolling
[`match-data`](../../releases/tag/match-data) pre-release: a
`manifest.json` and one gzipped file per patch, about half a megabyte
each. It runs `tools/MatchSnapshot`, which is the app's own download code
(`MatchSnapshotJob`): it restores the last run's files, syncs the heroes,
and fetches only what's due, so most runs make one call for the patch
list. The current patch is fetched again every 12 hours, a new or ended
patch at the next run, and a finished one never.

The app reads the manifest, checks each file's size and SHA-256, and
falls back to asking `deadlock-api.com` itself when the manifest can't be
reached or hasn't been updated for two days. GitHub turns scheduled
workflows off after 60 days without activity in the repo; re-enable it
from the Actions tab, or run it by hand with `gh workflow run match-data`.

## How scoring works

Every hero is rated on a fixed list of traits (`categories.csv`): "deals
spirit damage", "has high max HP", "applies slows"… Every item has
coefficients saying which traits it responds to, for which relation
(`against` an enemy with the trait, `with` an ally, or `as` your own
hero), and how much:

```
weight      = Σ over traits of (hero_score[hero, trait] − roster_average[trait])
                                × coefficient[item, trait, relation]
coefficient = trait_weight[trait, relation] × (typed + from_stats)
```

An item's score is its weight summed over everyone in the match. Because each hero counts by how far they sit
from the average hero, a score above 0 means this match wants the item
more than a typical match does, and a trait every hero has doesn't make
its items look good in every match. Coefficients run roughly 1 = mild, 3 = strong,
5 = the item exists for that trait; negatives discourage an item.
**Help → How scoring works** has the long version, and
[docs/scoring_model.md](docs/scoring_model.md) the maintainer's guide.

### Coefficients from item stats

`typed` is what you enter in Item Formulas. `from_stats` comes from
`stat_rules.csv`, one line per "this stat matters for this trait": *every
point of Spirit Resist (`TechResist`) is worth 0.1 against an enemy who
deals spirit damage, counted at half when conditional.* The two add; type
a coefficient for whatever the stats can't express. The By Trait grid
shows both side by side, with the arithmetic in the tooltip.

## Art

**Data → Download Art…** fetches hero portraits, item icons, and the art
detection matches against, into `assets/heroes`, `assets/items` and
`assets/topbar`. It matches by name, lists anything it couldn't match, and
keeps what's already there, except art it downloaded that the API has
changed since. Only a "not modified" is asked for each file, so checking is
cheap, and the app does it quietly about once a week. Anything without art
shows as a coloured initials tile. To add art by hand, name the file after
the id (`grey_talon.png`) and use **View → Reload Art**; art put there by
hand is never replaced.

## Detecting the match from the screen

Press <kbd>F9</kbd> (or **Detect from screen**) while Deadlock is in a
match, on any monitor and in any window mode. The advisor finds the game's
window, minimises itself if it's covering it, captures the top of the
game, and reads the scoreboard strip: all twelve heroes, and which one is
you (off the coloured backplate behind your slot).

F9 works from inside the game as well, without switching windows first:
the advisor holds it system-wide (no admin needed) and captures. It only
comes up if there's something for you to check. While it's running,
other apps don't get F9; turn that off under **Settings → Detection** and
F9 only works while the advisor has focus.

**When every hero is certain, the match is applied straight away**, and
**Review** beside Detect shows what was read. Otherwise the review opens
first. It shows each slot's crop, what it was read as and how sure that
was, with a dropdown to correct it. Uncertain reads are highlighted. If
your own slot couldn't be found, press **You** on it: that's what splits
the teams, so Apply waits for it. *Apply without asking when every hero
is certain*, under **Settings → Detection**, turns the shortcut off.

Every portrait is matched against the hero's own card, cut where the top
bar crops it, including the critical-health and on-fire versions. Faded
portraits (enemies out of sight) read too. A player who was dead at the
moment of capture can't be read, but detecting again later in the same
match keeps the heroes already applied, so a dead player's slot, or your
own on a kill streak, fills itself in. A hero in a skin can be corrected
in the review: leave *Remember my corrections as reference art* ticked
and the crop is saved to `assets/topbar/<hero_id>/`, so the skin is
recognised from then on.

There's no calibration. Detection searches for the grid of portraits the
heroes themselves agree with best and refits it to where they landed,
which absorbs any resolution or HUD scale. The grid is cached per screen
resolution, so only the first detection on a new resolution searches; if
a cached grid stops fitting, it's searched for afresh. How it all works,
and how it's measured against real captures, is in
[docs/detection_model.md](docs/detection_model.md).

## Tests

```
dotnet test
```

Besides unit tests small enough to check by hand, the suite checks
reference outputs in `tests/DeadlockAdvisor.Tests/Golden/`: scoring to
1e-9, byte-identical CSVs, sync and fetch results, and for detection
Pillow's resize byte for byte. Detection is also measured against a corpus of labelled real
captures, which it may not read any worse
([docs/detection_model.md](docs/detection_model.md)). After a deliberate
change, the goldens regenerate with
`dotnet test -c Release -e DEADLOCK_UPDATE_GOLDENS=1`; see
[docs/scoring_model.md](docs/scoring_model.md).
Headless UI tests render each page into `mockups/`.

## Deliberately out of scope

Per-(hero, item) overrides, situational multipliers, hooking the game, and
detecting on a timer.

## License

The code is under the [MIT license](LICENSE). Deadlock's art and data, and
the statistics from deadlock-api.com, belong to their owners and aren't
covered by it.
