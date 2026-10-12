# WP24 Tab-view spike: can the scoreboard's items be read, and what do they say?

Status: done
Effort: M · Risk: medium (it decides whether WP25-WP30 go ahead as written) · Depends on: none · Wave: A
Touches: new env-driven tool `tests/DeadlockAdvisor.Tests/TabViewSpike.cs` (writes to `mockups/tab_spike/`, like `VisionCorpusTools`), this file's "Findings"
Docs to update: this file only (WP26 writes the detection doc)

## Goal

Answer, with measurements on real captures, the questions the Tab-scoreboard feature rests on, and end with a go / no-go / Plan B note. Nothing ships; the output is numbers, contact sheets
and a decision.

## Why / premise check

The feature reads each hero's items off the scoreboard shown while Tab is held, keeps them in the match, and feeds scoring (WP27-WP29). Everything below was read off **one** screenshot
(2557×1437) and must be re-checked:

1. The Tab view's top (portraits, level badges, net-worth pills, team totals) is the normal top bar, so `Detector` finds the same grid on it and `NetWorthReader` reads its pills.
2. Item cells sit at fixed multiples of the slot pitch: two columns at the slot centre ±0.20 pitch, rows from about 3.93 pitches below the top of the screen, spaced about 0.395 pitch,
   icons about 0.33 pitch wide (about 39 px at 2560 wide, 29 px at 1920). Up to six rows were visible; the maximum is unknown.
3. A cell's frame or background colour gives its shop category (orange weapon, green vitality, purple spirit).
4. **Enemy items are visible in a live match.** The screenshot may be a spectator or replay view, which can show more than a player sees. Everything about enemies depends on this.
5. The three bars under each portrait: what a full bar means, and how their heights relate to the souls spent per shop category.
6. The art in `assets/items` (the "Item icons" group) matches the in-game icons once scaled and framed.
7. The match metadata (`/v1/matches/{id}/metadata`, see `MatchLookupService` and WP20) has each player's damage split by kind (bullet, spirit) and their item purchases, which WP29 needs to
   fit one response number. `docs/scoring_model.md` says the metadata has a damage matrix; the fields have not been looked at.

## Read first

`docs/detection_model.md` (all of it); `docs/architecture.md` sections 8 and 9; `Vision/NetWorthReader.cs` (pitch-relative reading); `Vision/TopbarDerivation.cs`; `Vision/Matcher.cs`;
`VisionCorpusTools` (the env-variable tool pattern); `ArtDownloadService` (where item icons come from).

## What the user provides

At least 8 captures of the Tab view, **their own** (full frame, not video frames), saved as PNGs outside the repository: two resolutions or HUD scales (their 2560×1440 and one smaller, such as
1920×1080), early, mid and late game, one with a hero dead, one with a kill streak, one with a Street Brawl, and **at least one live match** where enemies' items show. Players' names print in
every column: see Notes.

## Scope

In:
- A tool that, for each capture: finds the grid with `Detector`; reports the item-cell centres it measures against the premise numbers (in pitches) for every slot; counts occupied cells per
  hero; classifies each occupied cell's category by colour and reports confusions against a hand-checked label; tries a first-cut template match against `assets/items` icons framed as in
  game and reports confident-right, confident-wrong and unknown; shrinks each capture to 1080p and 900p to simulate smaller screens; reads the three bars and regresses their heights against
  the cost sums of the identified items per category (read-only, on the identified cells).
- Premise 7, read-only: fetch one finished match's metadata and one bulk answer, note which fields split damage by kind and give items; no real response is committed.
- Findings written into this file, with the numbers and the decision.

Out: any production code; the key observer (WP25); the reader proper (WP26); committing any capture.

## Suggested design

- Reuse `Detector.Detect`, `Geometry.Centers()` and `Pitch`; measure with `ImageOps` helpers. Keep the first-cut matcher crude: its job is to say whether a good one can exist.
- Draft labels the way `DEADLOCK_VISION_DRAFT` does: a contact sheet per slot beside its best candidates, so a person checks them quickly.

## Tests / how to run

An env-driven test, skipped unless `DEADLOCK_TAB_SPIKE=<folder of captures>` is set. It writes sheets and `report.md` under `mockups/` (git-ignored). Never launch the app or capture the real
screen.

## Decision rules (proposed; adjust with the user)

- **Go** (WP25-WP29 as written): at 1440p, category right on at least 98% of occupied cells; identity confident-right on at least 85% with zero confident-wrong; at 1080p, at least 70% with
  zero confident-wrong; enemy items visible in a live match.
- **Plan B**: identity too weak at 1080p but category and bars read: re-scope WP26 to category counts and bars, WP27 keeps the roster bars, WP28 (overlap) is dropped, WP29 stays.
- **Stop**: enemy items are not visible in live play, or the grid doesn't carry over to the Tab view. Rework the feature around your own team and your own hero.

## Acceptance criteria

- [x] Each premise above is marked confirmed, changed (with the new numbers) or refuted.
- [x] The report lists, per resolution, category accuracy, identity confident-right / confident-wrong / unknown, and the bars-versus-cost relation.
- [x] A go / Plan B / stop decision is written here and the later packages' premises are updated to match.
- [x] No capture, player name or API response is committed.

## Conflicts

None.

## Notes

- **Privacy.** The repository is public. A Tab capture prints every player's name; those and any video frame that isn't ours stay out of the repository. The screenshot that started this idea
  came from a video: use it for this spike only. Anything that later joins a corpus (WP26) has its name rows painted over first.
- If the grid doesn't carry over to the Tab view, say how the columns are found instead (the panel edges, the portraits themselves) before anything else is planned.

## Findings (2026-10-11)

**Decision: Go, with the changes listed under "What the later packages take from this".** Identity reads reached 75% confident (not the 85% the rules asked for) with no confident read wrong in
240 checked by eye; the user should confirm that this is enough (see "Against the decision rules").

### What was measured, and on what

The tool is `TabViewSpike.MeasureTheTabView` (run: `DEADLOCK_TAB_SPIKE=<captures> DEADLOCK_ASSETS=<assets folder> dotnet test tests/DeadlockAdvisor.Tests/DeadlockAdvisor.Tests.csproj -c Release --filter "FullyQualifiedName~TabViewSpike"`;
it takes about 15 seconds and writes `mockups/tab_spike/report.md` and contact sheets). It finds the grid the way the app does (one cached grid, searched afresh under a fit of 0.7), reads the net worth pills
and totals, slides each hero's item cells to where they fit, reads the corner colour, matches each filled cell against every icon in `assets/items`, and reads the three bars.

The ten captures are **frames from gameplay videos, all 2557×1437** (not captures from the user's own screen, so there is no second native resolution). They are soft: upscaled video, compressed, some in motion. Three are the
view of a dead player spectating their own live match (the game's "Spectating" strip, which moves everything about 12 px down); seven are a living player's. 1080p and 900p are those frames shrunk (0.752 and 0.626), then run
through the whole pipeline again, grid search included. Covered: early to late game (13k to 273k team souls), dead heroes, the dead player's view, overlays on top of the board (a damage panel, purchase toasts).
**Not covered:** a Street Brawl, a native 1080p capture, a kill-streak backplate, and any capture that is the person's own.
The red-bordered "corrupted" items in one capture no longer exist in the game; they read as unknown, which is the right answer, and are left out of what follows.

### The premises

1. **Same top bar and grid: confirmed.** `Detector` finds the grid on all ten at fit 0.67 to 0.87 at every size (the lowest, 0.67 to 0.68, is the capture with five dead portraits on one team), pitch 117.2 at 2557 wide (0.0816 of the height).
   Net worth pills read 12 of 12 at full size, 10 to 12 at 1080p and 9 to 12 at 900p, and **no pill ever read wrong** against the full-size read. Two team totals
   read wrong at full size (a lost leading digit: 3k for 273k, 18k for 218k; right at 1080p). `PillSouls` only uses a total to settle three-digit pills, but a total that is wrong rather than unread breaks the app's own rule: look at it in WP25.
   The grid's **top** from the portraits is good to about ±0.045 pitch (±5 px) from capture to capture, and the dead player's view has its top 12 px lower: anchor the cells to the grid but let the reader slide them vertically.
2. **Cell positions: confirmed, with corrections.** Rows are measured from the top of the portraits, not the top of the screen. Columns at the slot centre -0.185 and +0.213 pitches (not ±0.20); rows at 3.82 + 0.396 × row pitches below the grid's top
   (the premise had 3.93 from the screen's top); cells 0.31 pitches square. After sliding each hero's cells to fit: dx median 0.000 (5th to 95th percentile -0.013 to 0.000), dy median -0.023 (-0.045 to 0.000) at full size,
   and the same at 1080p and 900p. **The most a hero holds is 12 cells (six rows of two)**, filled row by row in tier order (the tier is printed in the corner as a numeral, legible only on a sharp capture). The deepest cell reaches 6.1 pitches below the top of the screen:
   50% of its height, so the Tab capture needs the top **52%**, not 58 to 60.
3. **Category from the frame or background: refuted; the corner says it.** An icon's background is the item's own art. The category is the colour of the small tier corner at the top right of the cell: orange weapon (hue about 16-50), green vitality (50-100), purple spirit (230-340; the faintest, saturation 0.2-0.4).
   Matched to any item without being told the category, strongly (0.80, lead 0.08), the corner names the item's category on **798 of 798** cells at full size, 769 of 769 at 1080p and 818 of 818 at 900p. The corner was classable on every filled cell.
4. **Enemy items are visible in a live match: confirmed.** In all ten captures every hero on both teams shows all their items. The user says the three "Spectating" ones are the dead player's own view of a live match, so this is what a player sees, not a third-party spectator's extra.
5. **The bars: confirmed, and the relation measured.** Left to right the bars are weapon (orange), spirit (purple) and vitality (green): fill is a share of a track 0.27 pitches tall (2.17 to 2.44 below the grid's top), lit pale against a dark track (read by brightness, since compression washes the hue out). Against the souls
   in that category (the cost sum of the identified items, over heroes with at most two unread cells), the fill **rises and flattens**: about 0.05 under 2k, 0.3 to 0.45 at 2k to 6k, 0.6 to 0.75 at 8k to 12k, 0.8 to 0.9 at 14k to 18k, and 1.0 only past about 28k. Correlation at full size
   is 0.94 weapon, 0.86 spirit, 0.91 vitality (58 bars each); at 1080p 0.85, 0.77, 0.64; at 900p 0.84, 0.74, 0.50 (a bar is 0.065 pitches wide, 5 px at 900p). So a bar says how much a hero has put into a category to within a few thousand souls, best at full size, and a full bar is a ceiling, not a count.
6. **Icon art matches in-game icons: confirmed, with exceptions.** The in-game icon is the API's art scaled down, with the tier corner over its top right and, on active items, an "ACTIVE" strip over the bottom. The tool shrinks each icon to the cell's own pixel size and ignores the corner and the strip.
   Exception: **Tankbuster is drawn dark (a dark bomb with a red eye) where the API's art is cream with a purple eye**, so colour matching fails on it (it read as Compress Cooldown at 0.5, never confidently). Expect others; learn them from corrections as hero variants are.
7. **Match metadata: confirmed, with a caveat.** `GET /v1/matches/{id}/metadata` (about 1.1 MB) has each player's `items[]` (`item_id`, `game_time_s`, `sold_time_s`, flags) and a `damage_matrix`: per dealer, per source (`source_details` names them: the kind rows `Bullet`, `Ability`, `Melee`, `UnknownAbility`
   and every ability and item), per target slot, a cumulative series sampled every 180 s and at the end. Target slot 0 is not a hero (creeps, objectives), so damage to heroes is targets 1 to 12; summing every target doesn't equal `player_damage`. The bulk
   `/v1/matches/metadata` has players (`include_player_info`) and their items (`include_player_items`) but **no damage matrix**, so WP29 needs the single-match endpoint, one request per match, for the damage mix.
   Nothing was committed.

### Per resolution

| | 1437p (full) | 1080p | 900p |
|---|---|---|---|
| grid found, fit | 10 of 10, 0.68-0.87 | 10 of 10, 0.67-0.86 | 10 of 10, 0.67-0.86 |
| net worth pills read / wrong | 120 of 120 / 0 | 113 of 120 / 0 | 112 of 120 / 0 |
| heroes' cell counts exactly right (36 counted by eye) | 30 | 29 | 31 |
| cells that really hold an item (373 counted by eye): confident / unknown / read as empty | 287 / 85 / 1 | 247 / 103 / 23 | 296 / 76 / 1 |
| empty cells read as filled | 8 | 1 | 6 |
| all filled cells read: confident share | 75% (909 of 1219) | 73% (872 of 1197) | 76% (928 of 1218) |
| confident reads checked by eye / wrong | 120 / 0 | 60 / 0 | 60 / 0 |
| corner colour against a strong any-item match | 798 of 798 | 769 of 769 | 818 of 818 |

Confident means a score of 0.70 and a lead of 0.08 over the runner-up, with candidates limited to the corner's category and each item used once per hero. No resolution trend shows (1080p is the odd one out because of its occupancy errors, not its identity): the frames are soft
whichever way they are shrunk, so these are numbers for poor captures, and a sharp native capture should do better, which this spike could not show.
Loosening to 0.55 / 0.10 raises coverage to about 82% but **is not safe**: of the cells it adds, those looked at were often wrong (empty cells and dark icons matched to a clock or a stopwatch).

### What the unknown quarter is

Of the 88 unknown cells among those that really hold an item (full size; 60 of them looked at one by one): about a third are right but under-confident (a score of 0.6 to 0.7, or a thin lead: Fleetfoot, Stamina Mastery, Witchmail, Superior Cooldown), about a quarter are active items whose "ACTIVE" strip lowers the score,
a sixth are Tankbuster's different art, and the rest are dark or motion-blurred. A second, shape-only descriptor (high-passed luminance) agrees with the colour match on 871 of 885 confident cells and adds only three more, so a better descriptor alone doesn't buy much;
the headroom is in learned in-game variants (Tankbuster), an "ACTIVE" variant per active item, and priors (tier order within a hero, an item once per hero).
Occupancy is the weakest step: with a per-capture split on the match score plus the corner, 30 of 36 heroes were counted exactly (34 within one), and the errors are cells on empty or busy backgrounds. The panel's edge would help.

### Against the decision rules

- Enemy items visible in a live match: **met**.
- Category right on 98% of occupied cells at 1440p: **met wherever a strong match could check it** (798 of 798); the rest could not be labelled.
- Identity at 1440p, 85% confident-right with zero confident-wrong: **zero confident-wrong met** (0 of 120 checked by eye), **85% not met**: 75% of the filled cells and 77% of those that really hold an item.
- At 1080p, 70% with zero confident-wrong: 73% of filled cells but 66% of those that really hold an item (more were missed as empty at that size); 0 of 60 checked wrong. At 900p: 76% and 79%, 0 of 60 wrong.

This is neither Plan B (identity is not too weak: it is partly there and never wrong) nor a stop. The 85% is short on soft frames; whether a native capture closes the gap is the first thing WP26 should find out, which is why its reader keeps unknown as an answer.

### What the later packages take from this

- **WP25:** capture the top **52%** of the window (not 58 to 60); don't trust a `NetWorthReader` total that a lost digit makes wrong (premise 1); the cached grid carries over to the Tab view, but the dead player's view sits 12 px lower and a refit can drift when most of a team is dead, so a grid fitting under 0.7 is searched afresh, as Detect does.
- **WP26:** use the positions in premise 2 (rows from the grid's top, a vertical slide of ±0.06 pitch, a horizontal one of ±0.05); the corner colour is stage 1's category (not a frame colour) and gives the shop category on every filled cell; stage 2 limits candidates to that category, uses each item once per hero, masks the corner and the "ACTIVE" strip;
  start at confident 0.70 / lead 0.08 and report the unknown share per resolution (expect 15 to 25%); add variants learned from corrections (Tankbuster is the first); count the cells with their panel edge as well as their score. Fixtures need native captures. The bars are read by brightness in a 0.27-pitch track and are a ceiling-shaped signal, not a sum.
- **WP27:** per-hero cells and bars are available as described; investment from an unknown cell is an estimate, flagged; the bars give a rough "how invested in each category", best at full size.
- **WP28:** its premise holds only for confidently identified items; an unknown cell keeps it silent for that hero. Expect about one cell in four unidentified until the reader improves.
- **WP29:** the damage mix needs the single-match metadata (damage to targets 1 to 12, from the `Bullet` and `Ability` rows), not the bulk endpoint.
- **WP30:** the board also prints each hero's **max health** (the heart and number under the bars) and **two resist percentages** (bullet, spirit) beside a shield, so candidate 1 can read real HP and durability off the board instead of working them out from items. Not measured here; it is the same kind of read as the net worth pills.

### Left for the user

Whether 75% confident (zero wrong) at full size, on soft frames, is enough to go ahead. If it is not, WP26 can start with stage 1 and stage 3 (cells, category, bars) only, which is Plan B in effect, and add identity once a native capture shows what a sharp one reads.
