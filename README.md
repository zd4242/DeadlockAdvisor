# Deadlock Item Advisor

Recommends items based on who's actually in your match, using a
category-based formula system instead of hand-typed per-hero-per-item
weights.

This is the C# / Avalonia port of the Python app (`deadlock_advisor`). It
reads and writes the same data files, scores items the same way, and
reads the screen the same way, so the two can share one data folder.

## Install

Copy `DeadlockAdvisor.exe` anywhere and run it. It's a single
self-contained file (Windows x64); nothing else needs installing.

On first run it creates `%AppData%\DeadlockAdvisor\` with a starter copy
of the data, and offers to download the hero and item art (about 13 MB,
from `deadlock-api.com`). Settings live beside it in `settings.json`.

To build it yourself (.NET 10 SDK):

```
dotnet run                                          # debug build
dotnet publish -p:PublishProfile=win-x64            # the single exe, into publish\
dotnet test                                         # everything, including the parity tests
```

## The three pages

**Match** — who you're playing against, and what to buy.

Pick what you're assigning with the Enemy / Ally / You buttons (or
Alt+1/2/3), then click heroes in the palette. Clicking a hero who already
has that role clears them; double-click sets You; right-click opens the
role menu. The search box keeps focus, so `hay` <kbd>Enter</kbd> drops
Haze onto the current side and clears the field for the next name
(<kbd>Up</kbd>/<kbd>Down</kbd> move the highlight).

Or press <kbd>F9</kbd> and let it read the whole match off the screen:
see **Detecting the match from the screen** below.

Click a portrait in the roster to toggle it in or out of your lane.
**Lane Phase** scores only yourself plus the heroes in your lane (normally
1 ally and 2 enemies), tiers 1–2 only; **Full Match** uses everyone across
all four tiers. The cutoff hides items scoring under 20/40/60% of the
best, and **By tier** groups the list into collapsible tiers.

Click any recommendation to see **why** it scored what it did, per hero
and per trait, with the arithmetic shown. With nothing selected, the panel
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
| <kbd>F9</kbd> | detect the match from the screen |
| middle click | autoscroll |

## Data folder

Everything lives under one folder holding `data/` and `assets/`: by
default `%AppData%\DeadlockAdvisor`. **Data → Change Data Folder…** points
the app somewhere else, such as the Python app's repository, so both apps
share one set of files. Nothing locks the files, so don't edit in both
apps at once. **Data → Open Data Folder** opens it in Explorer.

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
| `match_item_lift.csv` + `.meta.json` | | **generated** by Fetch Match Stats |

The CSVs are the source of truth and stay hand-editable. Every write
keeps a timestamped copy of the previous file under `data/.backups/`
(last 12) and goes through a temp file, so an interrupted save can't leave
a truncated CSV. The files are written byte-for-byte as the Python app
writes them.

## Data menu

- **Sync New Heroes / Items / Categories**: after adding rows to the CSVs
  by hand. Backfills missing hero × trait rows at 0, drops rows for
  deleted ids, and lists unrated heroes and items with no rules.
- **Sync from Game API**: pulls heroes and the shop from
  `deadlock-api.com`, following patches (costs, tiers, new items), and
  rewrites the item stats and tooltips. Only changed files are written.
  Run it after each patch.
- **Fetch Match Stats**: item win rates against, with and as each hero,
  shown beside each recommendation as "data": a second opinion, not part
  of the score. The status bar turns red when a newer patch is out than
  the data covers.
- **Reload from Disk** (Ctrl+R), **Export Snapshot to Excel** (a read-only
  `deadlock_advisor_data.xlsx`, never read back), **Open / Change Data
  Folder**, **Download Art…**.

## How scoring works

Every hero is rated on a fixed list of traits (`categories.csv`): "deals
spirit damage", "has high max HP", "applies slows"… Every item has
coefficients saying which traits it responds to, for which relation
(`against` an enemy with the trait, `with` an ally, or `as` your own
hero), and how much:

```
weight      = Σ over traits of hero_score[hero, trait] × coefficient[item, trait, relation]
coefficient = trait_weight[trait, relation] × (typed + from_stats)
```

An item's score is its weight summed over everyone in the match (or in
your lane, for Lane Phase). Coefficients run roughly 1 = mild, 3 = strong,
5 = the item exists for that trait; negatives discourage an item.
**Help → How scoring works** has the long version.

### Coefficients from item stats

`typed` is what you enter in Item Formulas. `from_stats` comes from
`stat_rules.csv`, one line per "this stat matters for this trait": *every
point of Spirit Resist (`TechResist`) is worth 0.1 against an enemy who
deals spirit damage, counted at half when conditional.* The two add; type
a coefficient for whatever the stats can't express. The By Trait grid
shows both side by side, with the arithmetic in the tooltip.

## Art

**Data → Download Art…** fetches hero portraits, item icons, and the
top-bar art detection matches against, into `assets/heroes`,
`assets/items` and `assets/topbar`. It matches by name, keeps what's
already there unless you re-download everything, and lists anything it
couldn't match. Anything without art shows as a coloured initials tile.
To add art by hand, name the file after the id (`grey_talon.png`) and use
**View → Reload Art**.

## Detecting the match from the screen

Press <kbd>F9</kbd> (or **Detect from screen**) while Deadlock is in a
match on the primary monitor. The advisor minimises itself, captures the
top of the screen, and reads the scoreboard strip: all twelve heroes,
which one is you (off the coloured backplate behind your slot), and who's
in your lane. During laning the game marks all four lane players and the
lane is read from those marks; later it falls back to the layout, where
each team's pairs face each other in order. The lane the review shows is
the lane that's applied.

**Nothing is applied until you say so.** The review shows each slot's
crop, what it was read as and how sure that was, with a dropdown to
correct it. Uncertain reads are highlighted. If your own slot couldn't be
found, press **You** on it: that's what splits the teams, so Apply waits
for it.

Some slots can't be read: a player who was dead at the moment of capture
(a black silhouette), a hero in a skin, or one whose reference art has
gone stale. The art download already installs alternates for the heroes
known to have gone stale (Apollo, Seven, Silver and Yamato). Correct the
rest and leave *Remember my corrections as reference
art* ticked: the crop is saved to `assets/topbar/<hero_id>/`, and the
matcher scores those alternates alongside the main image, so the portrait
is recognised directly from then on.

There's no calibration. Detection searches for the grid of portraits the
heroes themselves agree with best, refits it to where they landed, and
nudges each slot, which absorbs any resolution or HUD scale. The grid is
cached per screen resolution, so only the first detection on a new
resolution searches; if a cached grid starts reading badly it's searched
for afresh.

## Tests

```
dotnet test
```

Besides the ported `test_scoring.py` and `test_vision.py`, the suite
checks parity against reference outputs generated by the Python app
(`scripts/export_golden.py` there, into
`tests/DeadlockAdvisor.Tests/Golden/`): scoring to 1e-9, byte-identical
CSVs, sync and fetch results, and for detection Pillow's resize byte for
byte, descriptors to 1e-4, and identical readings on every capture.
Headless UI tests render each page into `mockups/`.

## Deliberately out of scope

As in the Python app: per-(hero, item) overrides, situational
multipliers, hooking the game, reading net worth off the strip, and
detecting on a timer.
