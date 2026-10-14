# WP26 The item and bar reader, with its corpus

Status: done
Effort: L · Risk: medium-high (small icons; the one real unknown of the feature) · Depends on: WP24 · Wave: B
Touches: new `src/Vision/Items/` (`ItemGrid.cs`, `ItemTemplates.cs`, `ItemDerivation.cs`, `ItemReader.cs`, `BarReader.cs`, result records), `src/Services/ArtDownloadService.cs` (only if cut icons are cached), `src/Vision/LabeledCapture.cs` or a sibling label type, `tests/DeadlockAdvisor.Tests` (`ItemCorpusTests`, `Golden/vision/items/`, helpers in `Support/`)
Docs to update: `docs/detection_model.md` (a new section, "The scoreboard's items"), `docs/architecture.md` (sections 2, 8, 9), `CLAUDE.md`'s detection rules only if the user agrees to extend them to items

## Goal

Given a scoreboard capture and the grid, return for each of the twelve slots its occupied cells (a shop category and, when sure, which item) and its three bars, with the same discipline as
hero detection: never confidently wrong, unknown is an answer.

## Why / premise check

Read WP24's findings first. This package builds the reader its numbers justify; if WP24 chose Plan B, do stage 1 and 3 only and drop identity. Re-check:

1. Cell positions hold in pitches at the resolutions WP24 measured (WP24's Findings have them: columns -0.185 and +0.213, rows 3.82 + 0.396 × row below the **grid's top**, cells 0.31 square, at most 12 cells; slide each hero's cells ±0.06 pitch vertically, ±0.05 horizontally). WP24 chose **Go** with identity at about 75% confident and zero wrong on soft video frames; native captures are what this package must still test.
2. `assets/items` holds one icon per `Item` (the art download's "Item icons" group; `Owned: false` means it is the app's own download) ✔ (`ArtDownloadService`).
3. `Item.Cost` and `Item.Category` are in `items.csv`, so investment can be worked out from identified items ✔.

## Read first

`docs/detection_model.md` (all, especially "The reference art", "What the numbers are", "Rules"); `NetWorthReader` (positions in pitches); `TopbarDerivation` and `TemplateBank` (frame
the reference art the way the game crops it); `Matcher` (one-per-slot assignment and the confident test); `ImageOps.Descriptor`; `VisionCorpusTests`, `LabeledCapture`, `CaptureArchive`.

## Scope

In (stages; stop at a boundary with a green commit if the session runs short):
1. **Cells and category.** `ItemGrid` places the cells for a slot from the geometry; each cell reads empty or occupied with a category from the colour of the tier corner at its top right (WP24: orange weapon, green vitality, purple spirit; the cell's background is the item's own art), and a state (normal, dimmed for a
   dead hero, streak backplate). Rows beyond the last occupied one read empty. Blank Street Brawl slots are skipped.
2. **Identity.** `ItemTemplates` frames each `assets/items` icon as the game draws it (its own `Version`, and a test that the game frames every icon the same way, like
   `TheGameCropsEveryCardTheSameWay`); `ItemReader` scores a cell by the high-passed descriptor against the icons of its own category, with a score and a lead over the runner-up. Above both
   bars it is **confident**, otherwise the cell keeps only its category.
3. **Bars.** `BarReader` returns the three bars' heights in pitches per slot; a cross-check compares them with the cost sums of the identified cells and flags disagreement.
- The corpus: labelled Tab captures with `Golden/vision/items/corpus_report.json` pinned like `VisionCorpusTests` (a change may add right and confident reads and take from confident-wrong,
  never the reverse), a generated `mockups/items_report.md`, and drafting/promotion tools driven by environment variables like `VisionCorpusTools`.

Out: storing builds (WP27); any scoring; the key observer (WP25); reading anything else on the board (names, K/D/A, abilities).

## Suggested design

- All lengths in pitches; the reader is a pure function of the image and the geometry, like `NetWorthReader`.
- `Item` identity is an `ItemId`; the reader never invents one: no template, or no confident match, is category-only.
- Cost can narrow candidates within a category, but isn't known for an unidentified cell: investment counts an unknown cell at its category's median cost, flagged as an estimate.
- Keep the hero corpus unaffected: `VisionCorpusTests` must not read worse, and nothing in `TemplateBank` changes.

## Tests

`ItemCorpusTests` (accuracy per state and resolution, confusions, zero confident-wrong), `ItemGridTests` (cell centres in pitches, empty rows, blanks), `ItemDerivationTests` (framing,
version), `BarReaderTests`. Corpus captures are the user's own, **with the player-name rows painted over before they are committed**. Regenerate with
`dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~ItemCorpus"` and review the diff. Keep the NetWorthReader tests out of golden runs.

## Acceptance criteria

- [ ] Every occupied cell of the corpus has a category; confident identities are all right (zero confident-wrong) at the resolutions WP24 set.
- [ ] Unreadable cells say so rather than guess; the report shows the unknown share per resolution.
- [ ] `VisionCorpusTests` is unchanged and green.
- [ ] No player names, account ids or third-party frames in the repository.

## Conflicts

`ArtDownloadService.cs` is a hot file (WP08 → WP09 → WP11); touch it only if cut icons need a cache, and after those. Can run beside WP25.

## Notes

- A new item arrives with a patch and its icon with the next art download; until then it reads category-only, which is correct.
- Skins or cosmetic variants of an icon, if the game has them, are handled like hero variants: learn from a correction, never from a guess. WP24 found one already: Tankbuster is drawn dark in game where the API's art is cream, so it never matched.
- WP24's first-cut matcher (a correlation on colour at the cell's own pixel size, candidates limited to the corner's category, each item once per hero; confident at 0.70 with a lead of 0.08) is `tests/DeadlockAdvisor.Tests/TabViewSpike.cs`; loosening it to 0.55 was not safe. Mask the corner and the "ACTIVE" strip along the bottom, which only active items carry.

## Findings (2026-10-12)

All three stages are built (`src/Vision/Items/`), with the corpus (`tests/DeadlockAdvisor.Tests/Golden/vision/items/`: 12 boards of the user's own native 2560×1440 captures, masked to the heroes' columns below their bars, and the 156 icons at 100 pixels). How it works is in `docs/detection_model.md`, "The scoreboard's items". Nothing in the app calls it yet: `ScoreboardAction.OnRead` is WP27's.

**What it reads, on the corpus** (970 item cells; `mockups/items_report.md`): 86% confidently right at native size, 85% and 86% shrunk to 1080p and 900p (simulated), **none confidently wrong at any size**, every cell found with its category, every tier right, every hero's cells counted right (133 of 133). The rest are unknown, the same cells again and again: icons the game draws differently from the API's art (Tankbuster, Healbane and Enduring Speed, whose API art is a running man while the game draws a boot), some active items, and a few cells at 0.6 to 0.7. WP24's guess of 15 to 25% unknown on soft frames holds on sharp ones at 14%.

**What WP24 got that native captures correct**
- A cell is **0.30 pitches** square (0.31 on soft video), and sits exactly at the numbers in premise 2 (rows at 3.82 + 0.396 × row, columns -0.185 and +0.213) on a standard board once the grid's own few pixels of error are slid out (-0.02 pitch down, 0 across).
- **A Street Brawl's grid is a per cent out in pitch (118 against 116.8) and in the gap between the teams**, so its deeper rows drift by up to 0.06 pitches and its two teams sit differently. The reader fits its own lattice (a scale, a vertical slide, a sideways slide per team) from the tags, then settles the scale by matching icons.
- **The tier numeral is legible** (dark strokes on the tag, I to IV) and **every item costs 800 · 2^(tier-1)** exactly, so a cell's cost is known from its numeral even when its item is not: WP24's "unknown cells at their category's median, flagged as an estimate" is not needed, and WP27's investment is exact except for hidden and unseen cells.
- **The bars are `1 - e^(-souls/10000)`** of the souls in the category, the same for the three bars, to a mean error of 0.012 over 411 bars (WP24's "rises and flattens, correlation 0.9" was this on soft frames); a Street Brawl's bars follow a steeper curve and aren't checked.
- Tier numerals also **settle items drawn alike at different tiers** (Superior and Compress Cooldown are both a stopwatch) and **caught two confident misreads a person had checked by eye** (Enduring Speed read as Sprint Boots): a confident read the numeral contradicts is taken back.

**What else is on the board that WP27 and the later packages meet**
- A **hidden item** is a black cell with a white question mark (a Street Brawl's enemy picks while items are chosen); it counts as filled and has no category (`CellKind.Hidden`). None of the standard-mode boards in the corpus has one.
- **Something drawn over cells**: the kill feed over a first slot's first rows reads as unseen cells (`CellKind.Unseen`), a tooltip or window over a slot reads as `Covered` (flat where the bars are), and a screen-wide colour wash (a purple flash over everything) reads as every slot covered. **None of these say the hero has nothing**: WP27 must keep what it knew. A slot that a tooltip's edge leaves half visible isn't recognised as covered and can read short (one such board is in the corpus, pinned as it is); the bars check flags it.
- Glares through the panel look like tags in colour; only the tier numeral's dark strokes tell them apart.
- A read takes 0.6 to 0.9 seconds on a desktop CPU.

**Left out**
- Learning an item's in-game look from a correction (there is no review UI for items): `assets/items/<item_id>/*.png` are matched as variants of the item, and nothing writes them. The unknown cells above would be the first to gain.
- A native capture at 1080p or any other HUD scale: the corpus had only 2560×1440, and the smaller sizes were shrunk from it (WP24 did the same). Native 1080p boards were added on 2026-10-13, below.
- `CLAUDE.md`'s detection rules were not extended to items (the file says only if the user agrees); `docs/detection_model.md`, "Rules", carries the item rule.
- Wiring (WP27), and what to do with a tooltip's half-covered slot beyond not trusting an empty read.

## Findings: native 1080p (2026-10-13)

Twelve native 1920×1080 boards of three spectated matches (and a 1440p twin of one paused moment) went into the corpus, and the reader was changed for what they showed. The numbers, against the shrunk copies of the
1440p boards (`mockups/items_report.md`): native 1080p reads **89%** of 920 cells confidently right, none wrong, every hero's cells counted right; the 1440p boards shrunk to 1080p read 85%. The same paused moment
captured at both sizes reads the same 88 cells natively, 87 shrunk, 85 at 900p: a native 1080p capture is as good as a 1440p one. What native showed that shrinking hid:

- **A confident wrong read.** Sprint Boots (tier I; a black boot, drawn darker than the API's winged one) read as Debuff Reducer (tier II, a dark arrow), 0.91 against 0.83, in four cells of two boards. Nothing in the
  corpus had a Sprint Boots. The tier numeral would have caught it, but at 1080p the numeral's shape can't be read (strokes a pixel wide, I, II and III correlate at 0.96 or better). The numeral's **ink** can
  (I 4.0, II 5.4, III 5.9, IV 5.0, each within 0.1): `TierReader` uses it between cells of 24 and 31 pixels, to read the tier of unknown cells (85% of them now have one, they had none) and to veto. The boots are
  now unknown at tier I, which is their cost, exactly. Above 31 pixels the shapes read and the ink doesn't (IV varies 6.0 to 8.5 at 1440p); below 24 neither does.
- **Missed cells.** A freshly bought item's tag flashes, and spirit's purple is blown out to a pale lilac (255, 231, 255) that the tag test (saturation 0.30) didn't take for a tag: the newest cell of a hero went
  unread, or read as *unseen*. It had been missed on a 1440p Street Brawl board too, and the corpus label had missed it with the reader (`brawl_item_choice`, slot 6, Boundless Spirit). `TagMap` takes a pale lilac of hue 280 to 330 as spirit.
- **Phantom cells.** The pause banner's black and white lettering lay below a short panel (a hero with one item) and read as five hidden and unseen cells; a hidden cell is a black cell with a mark *in the middle*,
  not one with light at its edge.
- **A tier numeral test that was written for 1440p** (5% unread) is per size now (25% allowed at 1080p).
- **Net worth** at 1080p had four faults of its own (the left total, a 3, a point after a 7, dead heroes' dim digits), fixed in `NetWorthReader` with six native strips as references: detection_model.md, "Net worth".

Still open: the 900p numerals (simulated only), and a slot whose first cells lie under the pause banner reads as having nothing there (the half-covered case above), which a spectator can see and a player can't.
