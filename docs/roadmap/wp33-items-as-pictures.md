# WP33 Items as pictures: what a hero holds is shown as the items, not a list of names

Status: todo
Effort: M · Risk: low-medium (presentation only; the one unknown is a card or tooltip opened from inside another popup) · Depends on: WP27 (done); best before WP32 and WP30 part 3, which reuse what it builds · Wave: E
Touches: new `src/Controls/ItemStrip.cs` (or a small view in `src/Features/Shared/ItemCard/`), `src/Features/Match/Board/BuildSummaries.cs`, `RosterSlotViewModel.cs`, `RosterView.axaml`, `src/Controls/RosterSlot.cs` (its automation peer text stays), `src/Features/Match/Explain/*` (the overlap note), `src/Features/Shared/ItemCard/*` (a card opened from a strip), `src/Models/AppSettings.cs` only if a setting is wanted (see Scope), `tests/DeadlockAdvisor.Tests` (`BuildSummaries` tests, `Ui/BuildsInTheMatchTests`, `AccessibilityTests`, a new `Ui/ItemStripTests`)
Docs to update: `docs/architecture.md` (section 4's builds paragraph, section 1's accessibility note), `README.md` (the Match page's builds paragraph, which says "hover a portrait for their items by name")

## Goal

Where the app talks about what a hero holds, show the items themselves: each item's icon in the shop's colour with its tier, in the game's own layout, with the name and the in-game card on hover.
A cell the reader couldn't identify shows as a tile of its shop and tier (a question mark), not as a missing name. The words stay, as the accessible text and as the fallback when the art isn't downloaded.

## Why / premise check

1. **Today the build reaches the player as text.** The match bar draws three bars per hero; the hover is `BuildSummaries.Tip`, one string: "Items: Mystic Burst, Improved Spirit, ..." and "Not recognised: a weapon item (tier 3)" ✔ (`BuildSummaries.Tip`, `BuildSummary.Tip`).
   The same names carry the overlap toast and the explain note (`ExplainText.SharedNotice`, `ExplainText.Overlap`), and the planned alerts (WP32), change report (WP31) and sell hints (WP30) would name items in text too.
2. **The pieces exist.** `ArtImage` (`Kind="Item"`) already draws item icons from `assets/items` in the results list, the explain panel, Hero Items and By Item, falling back to a lettered tile when art isn't downloaded ✔;
   `ItemCardHover.ItemId` opens the in-game card on hover ✔ (`src/Features/Shared/ItemCard`); `HoverFlyout` opens a flyout on rest ✔; the build has each cell's shop, tier, item id and whether it was read ✔ (`HeroBuild`, `BuildCell`).
   Re-check: `ArtImage` still has an item kind and a placeholder, and `ItemCardHover` still works on a control inside a flyout.
3. **Where the game puts them.** A hero holds at most 12 cells, six rows of two, filled row by row in tier order (`ItemGrid`, WP24). A strip in that layout reads like the board the player just looked at.
4. **The unknown quarter is the design problem.** About one cell in seven is unidentified (WP26: 86% confident on native captures), and a covered or hidden cell says nothing about what was there. A blank would read as "holds nothing"; the tile must say
   "an item we couldn't read" (shop colour, tier numeral, `?`) and a hidden or covered cell must look different from both.

## Read first

`docs/architecture.md` sections 1 (accessibility, painted controls), 4 (builds); `BuildSummaries`, `RosterSlot`, `PaintedPeer`; `ArtImage` (`src/Controls/Art`); `ItemCardHover`, `ItemCardPresenter`; `HoverFlyout`; `HeroBuild`, `BuildCell`, `CellKind`;
`Ui/BuildsInTheMatchTests`, `AccessibilityTests`.

## Scope

**Stage 1: the hero's items, on the match bar.**
- `ItemStrip`: a control that draws a `HeroBuild`'s cells in the game's layout (two columns, up to six rows): an `ArtImage` per identified item, framed in its shop's colour with the tier numeral; an unidentified cell as a tile of its shop and tier with `?`;
  a hidden or covered cell as a dimmed tile with no shop. Each identified icon opens the item's card on hover (`ItemCardHover`) and has the item's name as its tooltip if the card can't be opened from where the strip sits.
- The roster slot's hover shows the strip above the existing text lines (age, souls by shop, how many cells weren't read), which stay: they carry what the picture can't. `BuildSummary.Tip` stays as the accessible description and as what shows when art is missing.
  If a card can't open from inside the roster's tooltip, make the hover a `HoverFlyout`-style popup that the pointer can enter, or show the name under the pointer instead: decide in this stage with a headless test, and write the finding here.
- The painted peer and `AccessibilityTests` keep the words: the strip is decorative to a screen reader and the slot's name still carries the numbers; add the item names to its help text so nothing is lost.
- A hero with no read shows nothing; with *Read the scoreboard* off nothing changes (as `ShowsBuilds` does today).

**Stage 2: the other places an item is named about a hero.**
- The explain panel's overlap note ("Dynamo holds Mystic Vulnerability, which covers 100% of ...") shows the held item's icon beside its name, and `SharedNotice`'s toast text is unchanged (a toast is a line of words; if toasts gain a small leading icon
  slot for WP32, use it, but don't build one here).
- A shared helper (`ItemChip`: icon, name, optional tier) for any list that names an item, so WP30 part 3 (sell hints), WP31's "what the last read changed" and WP32's alert list use it rather than each drawing their own.

**Stage 3 (optional; ask the user after Stage 2):** a **Builds** strip row under the match bar, off by default (`AppSettings.ShowBuildStrips`), with each hero's items at icon size for a player with room to spare (900 x 600 stays as it is: it never shows at the minimum size).
This is the one part that costs layout, so it only goes ahead if Stage 1's hover turns out to be too slow to reach in a fight.

Out: item art that isn't the game's (no custom icons); any score or filter change; showing the held items of heroes on the results list (WP27's out list stands); a build editor (the build is read, not typed).

## Suggested design

- `ItemStrip` takes a `BuildStripModel` (a plain list of cells with item id, name, shop, tier, kind), built by `BuildSummaries` beside the tip text, so the control knows nothing about the store and tests build the model by hand.
- Sizes follow the zoom like everything else; at 100% an icon is about 28 px, so a hero's strip is about 60 x 180 including gaps; the strip is its own ~16 px tile at the bar's size only if Stage 3 happens.
- Colours come from `Palette` (shop colours, tier colours) and tier numerals read at 4.5:1 on their tile. A cell's state is never carried by colour alone: the tile has the numeral, the `?`, or a dash for covered.

## Tests

- `BuildSummaries`: the model for identified, unidentified, hidden and covered cells in one build, in cell order.
- `Ui/ItemStripTests`: a strip renders one tile per cell in two columns, an identified tile has its art or its placeholder letters, an unidentified one shows `?` and its tier; a render of a full read written to `mockups/` and looked at; the strip at 150% zoom.
- `AccessibilityTests`: the slot's name and help text still carry the items by name; the strip has no unnamed button.
- A test that the hover on a roster slot opens a strip, and that hovering an icon in it shows the item's name or card (the Stage 1 finding).

## Acceptance criteria

- [ ] Hovering a hero whose build has been read shows their items as icons in the game's layout, with the unread cells marked as such.
- [ ] Without art downloaded the strip still reads (placeholder tiles with the item's letters), and the text lines are all still there.
- [ ] A screen reader gets the same information as before (the slot's name and its help text).
- [ ] The explain panel's overlap note shows the held item's icon.
- [ ] Nothing about scoring, settings or the match bar's size changed unless Stage 3 was chosen.

## Conflicts

`RosterSlotViewModel.cs`, `RosterView.axaml` and `MatchViewModel.cs` are touched by WP31 (change marks) and WP32 (tile badges): do this after WP31 and before WP32, so WP32's badge tooltips and "Notable items" list use `ItemChip` from Stage 2. `BuildSummaries.cs` is touched by WP31 ("New: ..." line in the tip): that line becomes chips with this package.

## Notes

The user's wish: "show actual items instead of just names". Names stay as the text under the pictures; the pictures are the primary thing. If the art download hasn't run, say so in the strip's tooltip once ("Download the art to see the items").
