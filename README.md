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

**[Download DeadlockAdvisor.exe](https://github.com/zd4242/DeadlockAdvisor/releases/latest/download/DeadlockAdvisor.exe)**
(Windows, the newest version; [all releases](https://github.com/zd4242/DeadlockAdvisor/releases)),
put it anywhere and run it; nothing else needs installing. Windows
SmartScreen may warn the first time, as it does for any download it hasn't
seen often: **More info → Run anyway**.
Settings → Data shows which version you have. When a new one is out, the
status bar says so: **Update** downloads it in the background, and it's
installed in place of the old exe when you close the app; **Restart now**
does that straight away. The formulas and match data keep themselves up to date without a
new version.
Only one copy runs at a time: starting it again brings the open window to the front.

On first run it creates `%AppData%\DeadlockAdvisor\` with a starter copy
of the data. One dialog offers the hero and item art and the match data,
both downloading in the background: the art from `deadlock-api.com`, the
match data ready-made from this repo's [shared download](#shared-match-data).
With the match data's "keep it up to date" on (the default, and in
Settings → Data), later startups refresh it quietly when a newer patch is
out or it's a day and a half old. Settings live beside it in `settings.json`.

Offline, it all still works from what's saved: the starter data recommends
items, and until the art has been downloaded heroes show as their initials
(Detect from screen needs that art). A status bar chip says **Offline**;
clicking it checks again, and it checks by itself every half minute. When the
connection returns, the startup checks that couldn't be made are made then. A
first run that starts offline offers its downloads, as a status bar chip, once
you're back online.

To build it yourself (.NET 10 SDK):

```
dotnet run                                          # debug build
dotnet publish -p:PublishProfile=release            # the single exe, into publish\win-x64\
dotnet test                                         # everything, including the golden tests
```

To try a build without touching your own settings and data, set
`DEADLOCK_ADVISOR_HOME` to another folder first: the app keeps its settings,
log and default data folder there instead of `%AppData%\DeadlockAdvisor`.

### Publishing updates

Formulas, match data and the app each reach installs their own way; see
[docs/publishing.md](docs/publishing.md). In short: publish formulas with
`dotnet run --project tools/PublishModel -- --note "What changed" --push`, match data looks
after itself, and Actions → Release → Run workflow releases the app.

### Linux and macOS

Not supported yet, but on the way: the release workflow builds
`linux-x64` and `osx-arm64` alongside Windows (as preview artifacts, not
release downloads), so they keep compiling. Nobody has tried them yet;
everything but Detect should work. What's missing:

- **Detect** reads the game's window through Windows APIs
  (`ScreenCaptureService`, `ForegroundService`) and listens for F9 through
  a Windows hotkey (`GlobalHotkeyService`). Each is behind
  `OperatingSystem.IsWindows()`, so elsewhere Detect just says it isn't
  supported. Linux would need X11/Wayland capture (Wayland only through
  its screenshot portal), macOS ScreenCaptureKit and its screen-recording
  permission.
- **macOS packaging**: a proper `.app` bundle, and signing and
  notarization with an Apple Developer ID, or Gatekeeper blocks it.
- Data lives in .NET's application data folder, `~/.config/DeadlockAdvisor`
  on both; macOS users would expect `~/Library/Application Support`.

## The pages

**Match** — who you're playing against, and what to buy.

Press <kbd>F9</kbd> and let it read the whole match off the screen:
see **Detecting the match from the screen** below.

To look at a finished match instead, choose **Import a match…** on the empty
page (or **Import match by ID…** under the **⋯** menu beside Edit heroes) and
enter its ID or paste a link with the ID in it. The heroes come from
[deadlock-api.com](https://deadlock-api.com), and you're picked out by the
Steam account saved under **Settings → General → Steam account** (an account
ID, a SteamID64 or a steamcommunity.com/profiles link), or by pressing **You**
on your row; tick *Remember* there to save it for the next match.

To set or correct heroes by hand, open the hero picker with **Edit heroes**
(or <kbd>Ctrl</kbd>+<kbd>F</kbd>, Alt+1/2/3, or a click on an empty slot in
the match bar). Pick what you're assigning with the You / Enemy / Ally
buttons (or Alt+1/2/3), then click heroes. Clicking a hero who already has
that role clears them; double-click sets You; right-click opens the role
menu. The search box keeps focus, so `hay` <kbd>Enter</kbd> drops Haze onto
the current side and clears the field for the next name
(<kbd>Up</kbd>/<kbd>Down</kbd> move the highlight). <kbd>Esc</kbd> clears
the search, then closes the picker.

In the roster, right-click a portrait to change its role or remove it (so does
a click on yours, or on an enemy not rated on any trait yet); the × on a
portrait removes it. Clicking an ally plays as them. Clicking an enemy
**focuses** the recommendations on them: a focused enemy counts 5× as much as
each of the others, and the enemies together still count as much as before, so
the list leans toward items good against whoever you focused, and the match
data's enemy numbers lean the same way. Focus both your lane opponents if you
like; click again to stop, or use the × beside the "vs …" label to stop focusing on everyone.
Recommendations score everyone in the match across all four tiers. The
dropdown beside them picks what ranks the list: the formula, the match
data, or both. The rest sits behind **Filters**: the cutoff hides items
scoring under 20/40/60% of the best, **Group by tier** splits the list
into collapsible tiers, each cut against its own best so the cheap tiers
keep their picks, and **Match data from** leans the match data
toward a range of ranks: the numbers move only where those ranks play
detectably differently. That's worked out on the spot from the downloaded
rank groups, and stays set until changed. The button fills in while a
filter the list doesn't show is on (hiding rarely built items, or leaning
toward ranks), and its tooltip says which.

When Detect reads the match it also reads each hero's **net worth** (their
souls) off the top bar: it shows on the portraits and as each team's total.
With **Lean toward heroes ahead on net worth** ticked under Filters, a hero's
share of every score is scaled by how far ahead or behind the match's average
they are, by at most 30%, so counters to a fed enemy and items for a fed ally
(or a fed you) count for more. It's greyed out until a reading exists.

Click any recommendation to see **why** it scored what it did, per hero
and per trait, with the arithmetic shown. **Settings → General** can hide
the arithmetic, which then shows when you hover a line. With nothing selected, the panel
lists what the match data likes that the hand model doesn't.

**Hero Items** — what one hero's players buy, and how they do with it.

Every item bought on a hero in a patch's matches, with its win rate, how
often it's bought (its usage), its wins and losses, and how the win rate and
usage moved since the patch before. Pick the hero (it opens on yours from
the Match page, and you can type to search them), the patch, ranked or
unranked matches or both, a range of ranks and the tiers; click a column's
header to sort by it. Tick several patches to add their matches up; the
changes are then since the patch before the oldest one. A win rate half a
point or more above or below the hero's own average is tinted green or red,
the more strongly the further it is. **Hero fit** is how much more the hero
wins with an item than everyone who builds it, next to the hero's other
items of the same tier: the same measure as the Match page's "you" match
data, which its tooltips call hero fit. A win rate partly shows an item being bought late in games already
going well; the fit takes that out, so it shows what suits this hero in
particular, not how strong the item is for everyone, so a low fit doesn't make
an item a bad buy: it only means the hero gains less from it than from its usual picks. Right-click an item for its Deadlock
Wiki page, or its formula while the model editors are shown. **Min
usage** hides the items bought in only a few of the hero's matches, whose
win rates rest on too few matches, and on who buys them, to mean much. It
reads the downloaded match data, so it needs **Data → Download Match Data**.

**Hero Traits** — the hero × trait grid, edited in place.

| Key | Does |
|---|---|
| `0`–`100` | commits as soon as no more digits fit; <kbd>Space</kbd> or <kbd>Tab</kbd> ends a short number |
| `-` first | negative value, for the ± traits |
| <kbd>Backspace</kbd> | clears the cell |
| <kbd>Enter</kbd> | next hero, same trait |
| double-click / <kbd>F2</kbd> | a numeric editor, for decimals |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | undo / redo |

**Copy from…** clones another hero's profile; **Clear hero** resets one.
The footer explains whichever trait you're on.

**Item Formulas** — what each item responds to.

- *By Item*: pick an item, edit its rules, and watch the "Would apply to"
  preview show which heroes they fire against, with the arithmetic in the
  tooltips. The item's in-game card sits alongside.
- *By Trait*: pick one trait and relation, then run down every item typing
  coefficients, one keystroke each (<kbd>B</kbd>, or a click in its column,
  toggles *Best target*). **Weight ×** scales the whole trait and
  relation at once, and the **stat rules** strip fills coefficients from
  each item's real stats (see *Coefficients from item stats* below).

Edits save themselves a fraction of a second after you stop typing, and
the Match page rescores as you go.

## Shortcuts

| Keys | Does |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>-</kbd> / <kbd>0</kbd>, <kbd>Ctrl</kbd>+wheel, or the wheel over the status bar's zoom buttons | zoom (remembered, along with the window and your last match) |
| <kbd>Ctrl</kbd>+<kbd>Tab</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Tab</kbd> | next / previous page |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | focus the current page's search (on Hero Items, the hero picker) |
| <kbd>Ctrl</kbd>+<kbd>R</kbd> | reload the data from disk |
| <kbd>Ctrl</kbd>+<kbd>,</kbd> / <kbd>Ctrl</kbd>+<kbd>Q</kbd> | open Settings / quit |
| <kbd>F9</kbd> | detect the match from the screen, from the game too (see below) |
| <kbd>F6</kbd> / <kbd>F7</kbd> / <kbd>F8</kbd> | random match / keeping your hero / random enemies, keeping your team (with **Settings → General → Show the Random buttons** on) |
| mouse back / forward buttons | step back and forward through the pages (Settings closes on back) |
| middle click | autoscroll |

F9 and F6–F8 can be moved or taken away under **Settings → Shortcuts**: click
a key, press the new one.

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
(per file: the newest 12, the newest of each hour for a day, and the newest
of each day for two weeks) and goes through a temp file, so an interrupted
save can't leave a truncated CSV. An unchanged table saves byte for byte, so the files only
change when the data does.

A file that can't be read when the app starts (a typo in a row, a half-synced
copy, an empty `heroes.csv`, `items.csv` or `categories.csv`) doesn't stop it.
The app keeps the damaged file next to the others as `<file>.bad-<date>-<time>`
(the newest 3 per file), puts the newest backup that loads in its place (or the copy bundled with the
app), and tells you which file it replaced and from what. **Data → Reload from
Disk** does the same for the folder in use. A folder you pick with **Change
Data Folder…** is not repaired: it is refused, and left as it was.

## Data menu

- **Sync New Heroes / Items / Categories**: after adding rows to the CSVs
  by hand. Backfills missing hero × trait rows at 0, drops rows for
  deleted ids (never while `heroes.csv`, `items.csv` or `categories.csv` has
  no rows), and lists unrated heroes and items with no rules.
- **Sync from Game API**: pulls heroes and the shop from
  `deadlock-api.com`, following patches (costs, tiers, new items), and
  rewrites the item stats and tooltips. It also re-measures the Has High
  Max HP and Durability traits from the game's numbers. Only changed files are written.
  Run it after each patch: the report lists each item stat that moved,
  items with hand-typed rules whose tooltip changed, stats it doesn't know
  how to map, and per-item overrides that no longer match the game. New
  heroes don't need it: a workflow adds them to the published model, rated
  at 0 until they're rated ([formula updates](#formula-updates)).
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
take the newest one published to this repo's rolling
[`model`](https://github.com/zd4242/DeadlockAdvisor/releases/tag/model)
pre-release, which CI publishes from `main` once the tests pass (Settings →
Data turns that off; **Data → Check for Formula Updates** checks on
demand). `model.json` lists each file's SHA-256, and a copy in the data
folder records what was installed there, so the app can tell a file that's
only out of date from one you've changed, by hand or with Sync from Game
API. Files you haven't changed are replaced quietly, with the old ones
kept in `data/.backups/`; for files you have changed, a dialog asks which
to replace, and one you keep isn't asked about again for that version.
Either way, the update says what changed, in the words of whoever
published it. Heroes are the exception to asking: the ones the game adds
reach your data folder whatever you've changed, with their ratings (and
their art, if you've downloaded art before), and nothing already there is
touched. With updates off, a status bar
chip offers them instead (Settings → Data, *Say when new heroes are out*).

Settings → Data shows which version is installed and when it was last
checked for, with **What's new** (every version's notes), **Reset…** (puts
files that differ from the published version back to it, ticking which)
and **Undo update** (puts back what the last update or reset replaced, as
long as nothing has been changed since).

To publish a new version of the model, run
`dotnet run --project tools/PublishModel -- --note "What changed" --push`
from the repository: it copies your data folder's model into
`src/Assets/SeedData`, dates `model.json` and adds the note, commits and
pushes ([docs/publishing.md](docs/publishing.md)).

## Shared match data

Every install would otherwise make the same few hundred calls to
`deadlock-api.com` for the same numbers. Instead, the **Match data**
workflow (`.github/workflows/match-data.yml`) runs daily and
publishes them as assets of this repo's rolling
[`match-data`](https://github.com/zd4242/DeadlockAdvisor/releases/tag/match-data) pre-release: a
`manifest.json` and one gzipped file per patch, about half a megabyte
each. It runs `tools/MatchSnapshot`, which is the app's own download code
(`MatchSnapshotJob`): it restores the last run's files and fetches only
what's due, so a run with nothing due makes one call, for the patch list.
The current patch is fetched again every other day, a new or ended patch
at the next run, and a finished one never.

The app reads the manifest, checks each file's size and SHA-256, and
falls back to asking `deadlock-api.com` itself when the manifest can't be
reached or hasn't been updated for four days. GitHub turns scheduled
workflows off after 60 days without activity in the repo; re-enable it
from the Actions tab, or run it by hand with `gh workflow run match-data.yml`.

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

**Data → Download Art…** fetches hero portraits, item icons, the rank badges
that dress the rank pickers, and the art detection matches against, into
`assets/heroes`, `assets/items`, `assets/ranks` and `assets/topbar`. It matches
by name, lists anything it couldn't match, and keeps what's already there,
except art it downloaded that the API has changed since. Only a "not
modified" is asked for each image (once, even when two folders keep it), so
checking is cheap, and the app does it quietly about once a week. A first
download is about 23 MB. An image the site fails to serve for a moment is asked
for again twice before it is reported. Anything without art shows as a coloured initials
tile (a rank without a badge just shows none). To add art by hand, name the
file after the id (`grey_talon.png`; a rank's is its tier to two digits,
`01.png`) and use **View → Reload Art**; art put there by hand is never
replaced.

## Detecting the match from the screen

Press <kbd>F9</kbd> (or **Detect from screen**) while Deadlock is in a
match, on any monitor and in any window mode. The advisor finds the game's
window, minimises itself if it's covering it, captures the top of the
game, and reads the scoreboard strip: all twelve heroes, and which one is
you (off the coloured backplate behind your slot), and each hero's net worth
(their souls). In Street Brawl (4v4) the four empty slots are recognised as
such and count as read, so the eight heroes can still be applied without
asking.

F9 works from inside the game as well, without switching windows first:
the advisor holds it system-wide (no admin needed) and captures the game's
own window, on whichever monitor it is. By default it stays behind the game
and tells you how it went with a short sound: one tone when the match was
read and applied, another (and a flashing taskbar button) when it needs a
look or found no scoreboard to read. **Settings → Detection** can turn the
sound off, or make the advisor come up for a review instead. While it's
running, other apps don't get F9; turn that off under **Settings →
Detection** and F9 only works while the advisor has focus.

If there's no scoreboard on screen (a lobby, a loading screen, a hidden
HUD, or a game captured as a black screen by exclusive fullscreen or HDR),
Detect says "Nothing found" instead of opening an empty review.

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

## Code signing policy

Releases aren't signed yet. Free code signing is being set up through
[SignPath.io](https://about.signpath.io), with a certificate from
[SignPath Foundation](https://signpath.org), and this is its policy.

Once it's in place, only `DeadlockAdvisor.exe` on this repo's releases will be
signed. It's built from this repository by the Release workflow on GitHub's
own runners, and each signing request is approved by hand. Until then,
SmartScreen warns about the download as described under Install.

- Committers and reviewers: [Zach Davis (zd4242)](https://github.com/zd4242)
- Approvers: [Zach Davis (zd4242)](https://github.com/zd4242)

**Privacy.** This program will not transfer any information to other
networked systems unless specifically requested by the user or the person
installing or operating it. It downloads the following, and its requests
carry no personal information beyond what any web request does:

- From [deadlock-api.com](https://deadlock-api.com): hero, item and patch
  data, art, and match statistics. Importing a match sends the match ID you
  enter, and then the public account IDs of that match's players, for
  their names. Their [data privacy page](https://deadlock-api.com/data-privacy)
  applies.
- From GitHub: this repository's shared match data and published formulas,
  and whether a newer version is out.
  [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)
  applies.

Settings → Data turns off the startup checks for match data, formulas and
new versions.

## License

The code is under the [MIT license](LICENSE). Deadlock's art and data, and
the statistics from deadlock-api.com, belong to their owners and aren't
covered by it. The libraries the app is built with are under their own
licenses: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), also in the
app under Help → Third-Party Notices.
