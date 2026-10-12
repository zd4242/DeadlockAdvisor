# WP26 The item and bar reader, with its corpus

Status: todo
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
