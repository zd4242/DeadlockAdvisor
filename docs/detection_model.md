# How detection reads the match

Detect (F9) captures the top of the game, finds the strip of twelve portraits, names the hero in
each slot, works out which slot is you, and either applies the match or shows it for review. This
is how each step works, why it works that way, and how to change it without making it worse.

## The pipeline

1. **Capture** (`ScreenCaptureService`): the top 22% of Deadlock's window, in physical pixels. The
   window is the foreground one when its process is the game's (`project8.exe`, or `deadlock`), else
   the running game's main window; with no game running it reads the primary monitor.
2. **Grid** (`Layout`): twelve slots in two blocks of six, evenly pitched, with a gap for the clock.
   Five numbers place every slot: centre, pitch, middle gap, art width, top. The grid is searched
   for once per screen size and then cached (`AppSettings.VisionGeometry`, versioned by
   `Layout.CacheVersion`). A cached grid is searched for afresh when its **fit** falls under 0.7.
   Fit is the trimmed mean of the slots' best scores (`Detection.Fit`). A right grid fits at 0.81 to
   0.92 even with four players dead. A grid fitting under `Detector.MinFit` (0.5) isn't a scoreboard
   (`Detection.FoundStrip`): a lobby, a black or HDR-blank frame fit under 0.1. `DetectAction` ends
   such a run as "Nothing found", caches no grid, and `VisionCorpusTests` checks every labelled
   capture clears it with margin.
3. **Read** (`Detector`): a wide pass finds and refits the grid. Then every slot is read in its own
   box, within ±4% and at 0.95, 1.0 and 1.05× its size. The score is the dot product of a high-passed
   24×40 descriptor (`ImageOps.Descriptor`) against every reference image. A hero takes their best
   image.
4. **Assign** (`Matcher`): one hero per slot, no hero twice. A slot with nothing scoring 0.30 is left
   unread. A read is **confident** at a score of 0.40 and a lead of 0.08 over the runner-up.
5. **You** (`Detector.SelfSlotScores`): your slot is backed by a flat rectangle in your team's colour
   above the portrait:
   - amber is hue 25–45 and sapphire 210–230, at saturation 0.45 or more;
   - it's you if it's at least 0.6 bright and 0.15 brighter than any other.

   Your lane partner's backplate is the same colour at half the brightness. A red critical backdrop
   and the spectator strip aren't team hues. On a kill streak your backplate turns teal, so you're
   reported unknown rather than guessed. Every team-hue score is then 0.00 (the two late-game captures in
   the corpus), so the weak scores hold nothing to guess from, and the runner-up is your lane partner
   whenever there is one. What does show is the teal itself: `Detector.StreakSlot` returns the one slot
   whose backplate is bright teal (hue 140–185, saturation 0.3, brightness 0.9; yours read hue 159–162,
   0.41, 0.99–1.00). It becomes `Detection.LikelyYou`, only while you're unknown, and is **only offered**:
   the match bar tags that hero "YOU?" and asks for a click. `TheKillStreakHintPointsAtYouOrSaysNothing`
   holds it to the labelled captures: it is you or nothing, and you wherever you were missed.
6. **Same match** (`RosterContinuity`): heroes don't change slots mid-match. It's the same match when
   at least six confident slots agree with the match already applied and none disagrees. Then any
   slot the read couldn't settle keeps the applied hero ("kept"), and you carry over.
7. **Apply or review** (`DetectAction`): if every slot is confident or kept (`Detection.HeroesSettled`), the
   match is applied without the review, and **Review** beside Detect reopens it. A Street Brawl's
   blank slots count as read (below). When you weren't found, the heroes are still applied, each in its
   slot but on no team (`MatchState.Unsided`): all the app is missing is which side is yours, and the match
   page asks for one click on your hero, which splits the teams (the run ends as `DetectOutcome.NeedsYou`).
   Otherwise the review opens. Either way the run reports how it
   ended (`DetectAction.Finished`); an F9 pressed in the game turns that into a sound, and the review
   (or the click on your hero) stays behind the game unless *Switch here for a review* is on.
   The capture kept for an unsided apply has no `self_slot` and its `read.self_slot` is null; when the click
   comes, `DetectAction.LabelSelf` writes the chosen slot into it (a note says it was picked by hand), so every
   miss becomes a labelled capture: `read.self_slot` null with `self_slot` set.

**The scoreboard read** shares steps 2 to 6 and the net worth with the pipeline above (`ScoreboardReader.Read`, which `DetectAction` calls
too) but starts from a capture of the top 52% of the window, taken while the player holds the scoreboard key (`ScoreboardAction`), and ends not in
a review but in a net worth snapshot and each hero's items (below), and only when the read is the match already applied (`RosterContinuity.IsSameMatch`); anything else is dropped
silently. The grid comes from the same per-screen cache, so a read that fits under 0.7 searches afresh as Detect does.

## The reference art

**The top bar crops every hero's card the same way**: x 0.164, y 0.148, width 0.661 of the card
(`TopbarDerivation.InGameFrame`). It crops the card for whichever state the hero is in: normal,
critical health, or on a kill streak (the API's "gloat").

Every portrait matched against is therefore cut from the API's cards at that frame by
`TopbarDerivation`:
- `topbar/<hero>/card_normal.png`, `state_critical.png` and `state_gloat.png`, 120×200, over grey;
- the cards themselves are in `topbar/_cards/{normal,critical,gloat}/`;
- `topbar/_derived.json` records each hero's card hashes, so a hero is only cut again when a card
  changes or `TopbarDerivation.Version` is bumped.

Every cut sits exactly in the slot's box, 0.526 pitches wide (`TopbarDerivation.WidthRatio`).
That's what lets step 3 read each slot in a small window. A wide per-hero search lets a wrong
hero's best of hundreds of tries crowd out the right one.

The API's own top-bar image (`topbar/<hero>.png`) is cropped hero by hero, by up to a fifth of a slot.
Some of those images are stale, and there are no critical or on-fire versions. So the bank only uses
it for a hero with no cut portraits.

**Corrections** made in the review are saved as `topbar/<hero>/variant_NN.png`:
- They're cut at the slot's grid box, so they frame like the cuts.
- They're only kept if the portrait has contrast (`ImageOps.Contrast` ≥ 0.13). A faded or dead
  portrait teaches nothing.
- A hero keeps their three newest; older ones move to `topbar/_quarantine/<hero>/`, which the bank
  ignores. They're for skins, and for Silver's wolf form, which no card shows.

**Keeping art current**: `assets/art_manifest.json` records each download's URL, ETag and hash.
- Download Art only asks whether a file changed (If-None-Match, answered with 304), once per distinct URL: the normal hero card is both the hero portrait and the card the top-bar portraits are cut from, so the later group copies the file (and its manifest entry) instead of asking again. Hand-placed art (no manifest entry) is never a copy source.
- Up to four images of a group are in flight at once; a 5xx or 429 answer is retried twice (2 s, then 6 s), and a connection failure is not (see the next point).
- The app checks quietly at startup about weekly, and at once when `_derived.json` is out of date.
- A download whose connection fails for three images in a row stops and keeps what arrived. It isn't recorded as a check, so the next startup, or the connection returning, tries again. An image the site answers with an error status is skipped alone.
- Nothing hero-specific ships with the app, so offline, a first run has no art to match against until the connection returns.

## Street Brawl

Street Brawl is 4v4 on the same strip. Each team's two outer slots (0 and 1, 10 and 11) stay blank
(`StreetBrawl`). The strip is a Street Brawl when those four are blank panels, with contrast under
0.03 (`StreetBrawl.MaxBlankContrast`) and no hero, and the eight inside them all read confidently.
Then the blanks are settled with no hero, and the match applies without review like any other.

Contrast is what tells a blank from a dead or faded portrait. On the labelled captures
(`streetbrawl_*` in the corpus) the blanks read at 0.02 or less, the faintest faded portrait at 0.04
and the faintest dead one at 0.05. Score doesn't separate them: dead portraits score 0.15 to 0.27,
the same as a blank. `StreetBrawlTests` checks that no six-a-side capture is taken for one.

## The scoreboard's items

While the scoreboard key is held, the board shows every hero's items and three bars under their portrait (`Vision/Items`, roadmap WP26; `ItemReader.Read(band, grid, templates, blankSlots)` is a
pure function of the capture, the grid and the icons, like `NetWorthReader`). It answers the same way hero detection does: a cell's identity is only given when it is sure, and unknown is an
answer. Every length is in pitches (`ItemGrid`), so it holds at any resolution or HUD scale.

**Cells.** A hero holds at most twelve, six rows of two, filled row after row. Cell centres sit at the slot's centre -0.185 and +0.213 pitches, and 3.82 + 0.396 × row pitches below the grid's top;
the icon is 0.30 pitches square (0.97 of what WP24 measured on soft video frames; 1.00 on native captures). The deepest cell ends 5.95 pitches down, inside the top 52% the capture takes.

**Where the cells are.** The grid comes from the portraits and the cells need not sit where it says: a Street Brawl's grid is a per cent out in pitch (118 against 116.8) and in the gap between the teams,
and its top varies by a few pixels (and by 12 for a dead player's view). `ItemPlace` is the grid's numbers plus a scale, a vertical slide and a sideways slide for each team. The **tier tags** find
them: every filled cell has a small solid tag in its top right corner, a triangle 0.47 of a cell along each edge, in its shop category's colour (`TagMap`: weapon hue 31-38, vitality 76-88, spirit 262
on a blue panel and 303 on a brown one, the same on every board; the panel behind is never as saturated). `ItemPlace.Fit` slides and scales the cells to where the most tag windows fill with tag colour
and offers the best few scales; `ItemReader.Refine` then slides each candidate, a team at a time, to where a spread of tagged cells match their category's icons best, and the candidate that matches best
wins (a scale shows only in the deepest rows and trades off against the vertical slide, so the tags alone can't settle it).

**Which cells are filled.** A cell is filled when its tag window holds tag colour **and** the tier numeral's dark strokes (`TierReader.InkShare`: 0.23 or more on real tags at every size, 0.08 or less on
a glare or a coloured scene seen through the panel, which has the colour and no numeral). Further, a hero's cells are the leading ones, so a tag with more empty cells before it than tags is a stray
colour, and a gap of more than two empty cells ends the hero's cells (`PlausibleEnd`). A cell that must be filled (a later one is) with no tag is **unseen** (the kill feed is over it); a black cell with
a white question mark is **hidden** (a Street Brawl's enemy picks while items are chosen) and counts as filled, with no category. A slot is **covered**, and says nothing at all about its items, when the
band across its bars is flat (a tooltip or window: spread 2 to 3 against 5 and up), or when four of its tag windows hold tag colour with no numeral (a screen-wide colour wash). A hero a tooltip's edge
leaves half visible isn't recognised as covered; a consumer must not take an empty read for "has nothing" (see the rules).

**Which item.** `ItemFrame` frames a cell as 32×32 area-averaged pixels with the tier corner and the "ACTIVE" strip left out, each colour channel zero-mean and all of them unit length, so a dot product is
a correlation; `ItemTemplates` frames each `assets/items` icon the same way, shrunk to the cell's own pixels first (it has a `Version`, and `TheFramingMatchesItsVersion` fingerprints it). A cell is scored against
the icons of its own category, each item once per hero, and given an item at a score of **0.70** and a lead of **0.08** over the runner-up. Anything in `assets/items/<item_id>/` is another way the game
draws that item and is matched too (a dark variant of an icon the API draws light); there is no review to learn one from yet, so it is a file dropped in.

**Which tier, and the cost.** Every item costs 800 · 2^(tier-1) souls, so a cell's tier is its cost, exactly, even when its item is unknown (`ItemTemplates.CostOf`, `HeroItems.Souls`). The numeral
(I to IV, dark strokes on the tag) is read against references cut from the **same board**: the cells the icons identified carry a known tier, so each board brings what it needs at its own resolution
(`TierReader`; leave one out, the numerals of the corpus read as their item's tier or not at all). The tier does three things:
- **Veto.** A cell read confidently as an item whose tier the numeral clearly contradicts (another tier's references at 0.90 or better, and 0.05 over its own) is made unsure: either the numeral or the
  item is wrong, and the item is more likely, an upgrade drawn like what it upgrades from (Enduring Speed, tier II, is drawn like Sprint Boots, tier I; the API's art for it is a running man).
- **A second look** among the items of the numeral's tier alone, for cells still unsure: this settles items drawn alike at different tiers (Superior and Compress Cooldown). Because the best of one tier
  looks like a match whatever the truth if the true item has no art that matches, it has to be better: a score of **0.75**, and no item of another tier matching more than 0.05 better.
- Unknown items keep their tier, so their cost is known.

**Bars.** Left to right weapon, spirit and vitality, each a pale fill rising from the foot of a dark track 0.27 pitches tall at 2.17 to 2.44 below the grid's top (`BarReader`, heights in pitches, read by
brightness). In a standard match a bar's fill is `1 - e^(-souls / 10000)` of the souls in its category, the same for the three (`BarReader.ExpectedFill`: a mean error of 0.012 over 411 bars, never over
0.19); `BarReader.Check` compares a hero's bars with the items read and flags a bar more than 0.15 from it (with exact souls) or emptier than the least the cells say (an unknown cell). A disagreement says a
cell was read as the wrong tier or category or a bar was misread, not which. A Street Brawl's bars follow another, steeper curve and aren't checked.

**What it measures.** A read takes 0.6 to 0.9 seconds on a desktop CPU (matching and refining dominate), off the UI thread. On the labelled boards (`Golden/vision/items/corpus`, 12 boards from the start of a match to its end, native 2560×1440, 970 item cells): 86% of the cells read confidently right, none
confidently wrong, every cell found with its category and tier, every hero's cells counted right, at native size and shrunk to what 1080p and 900p screens draw (85% and 86%; simulated, the corpus has no native
smaller capture). The rest are unknown, and they repeat from board to board: icons the game draws in a way the API's art doesn't (Tankbuster, Healbane and Enduring Speed are dark or different in game), some
active items, and cells at a score a little under the bar. The corpus is the user's own captures with everything outside the heroes' columns and above their bars painted black (so no name, kill feed
or nameplate is in it), and a tooltip over a slot's edge and a screen-wide colour wash are in it as the cases they are.

## What the numbers are

All of these are measured on the labelled corpus, and each constant's comment says where it came from:
- A hero in their own slot scores 0.76 or better nineteen times in twenty.
- No other hero has scored above 0.41 in a slot that isn't theirs.
- Visible slots lead by at least 0.11.

Before changing a threshold, see what the report says it trades.

## Measuring a change

`tests/DeadlockAdvisor.Tests/Golden/vision/corpus/` holds real captures of the strip (1920×180 about
the grid). Each has a JSON label in the fixture format (`LabeledCapture`):
- all twelve heroes, and `self_slot` where known;
- `match`, so a whole match can be held out, and `phase`;
- `grid`, so reading needs no search;
- `states`: `faded`, `critical`, `gloat`, `dead` or `transformed`. A slot without one is visible.

The tests that use the corpus:
- **`VisionCorpusTests.TheCorpusReadsNoWorseThanItsReport`** pins, per capture, which slots are read
  right, which confidently, which confidently wrong, and whether you're found
  (`Golden/vision/corpus_report.json`). A change may add to the first two and take from the third,
  never the reverse. `mockups/detection_report.md` explains the numbers: accuracy by state, margins,
  confusions, where each hero's art sits. After a deliberate change, regenerate with
  `dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~GoldenVisionTests|FullyQualifiedName~VisionCorpusTests"`
  and review the diff. Keep the NetWorthReader tests out of that run: they rebuild the glyphs.
- **`TheGameCropsEveryCardTheSameWay`** measures afresh where each cut portrait sits
  (`mockups/template_frames.md`). It fails if Valve changes the crop.
- **`LaterCapturesOfAnAppliedMatchNeedNoChecking`** replays each match, the first capture applied.

Tools in `VisionCorpusTools`, each run only when its environment variable is set:

| Variable | What it does |
|---|---|
| `DEADLOCK_VISION_DRAFT=<captures>` | Draft labels and contact sheets (each slot beside its best candidates) into `mockups/vision_drafts`. |
| `DEADLOCK_VISION_PROMOTE=<captures>` | Cut the capture a corpus label names (`"source"`) into the corpus, with its grid. |
| `DEADLOCK_VISION_FETCH_ART=<folder>` | Run the real art download into a folder. |
| `DEADLOCK_VISION_DERIVE=<assets>` | Cut every card into portraits and draw them side by side. |
| `DEADLOCK_VISION_AUDIT=<topbar>` | Score each learned portrait for the reads it rescues and breaks. `DEADLOCK_VISION_AUDIT_APPLY=1` quarantines the ones that don't earn their place. |
| `DEADLOCK_ITEM_ART=<an assets/items folder>` | Shrink every item icon to 100 pixels into `Golden/vision/items/art` (the item corpus's art). |
| `DEADLOCK_ITEM_DRAFT=<kept scoreboard captures>` | Draft an item label for each capture, with two sheets in `mockups/item_drafts` to check it by: the confident reads (the cell beside its icon) and the others (the cell beside its three best candidates). `DEADLOCK_ASSETS` uses the full-size art. |
| `DEADLOCK_ITEM_PROMOTE=<kept scoreboard captures>` | Cut the capture each label in `Golden/vision/items/corpus` names as its `source`, with everything outside the heroes' columns and above their bars painted black. |

**How a Tab read reaches the match.** `ScoreboardAction` reads the board (strip, net worth), and once it is the match already applied it runs `ItemReader.Read` on the same band and grid (the blank slots of a Street
Brawl left out) and hands each hero's `HeroItems` to `MatchState.Builds` (`HeroBuild.Merge`), which keeps it as new information about the cells it saw and never as the whole of the hero's items: a covered slot
changes nothing; a hidden or unseen cell keeps what was known of it; an unsure cell keeps the item it was known as when its category and tier agree; cells past a short read stay; an item is held once. A hero's souls per category are
the cells at their tiers' costs, and where a cell may hold more (hidden, unseen, no tier) the bar's reading (`1 - e^(-souls/10000)` inverted) raises the figure as an estimate; a Street Brawl's steeper bars are not
corrected for, so an estimate there runs high. Nothing here is confidence-gated beyond the reader's own: only cells the reader gave an item for count as owned. With no item art the read is skipped and the
net worth still lands.

**To add item boards:** turn on *Keep the boards it reads* (`AppSettings.KeepScoreboardCaptures`) and hold the scoreboard key in a match; the boards are kept in `<data>/captures/scoreboard_*`, at most one every 10 seconds,
and the newest 300 only (move the ones you want to keep elsewhere). Draft them, look at **every** confident read on the sheets (a wrong item can look right: the numeral caught two that did), copy the label into the corpus
with its `match` and `phase`, name the cells you checked (`weapon:active_reload`; one you couldn't tell stays `weapon`), promote, and regenerate the report with
`dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~ItemCorpusTests|FullyQualifiedName~ItemDerivationTests"`. `ItemCorpusTests.NoCellIsReadConfidentlyWrong` fails for a confident read of a cell the label
doesn't name, so a change that reads more is never unverified.

**To add captures:** Detect keeps every applied one in `<data>/captures/detect_*.png` and `.json`,
labelled with the heroes applied and what was read. Draft them, check the contact sheets, write the
corpus label with its states, promote, and regenerate the report.

## Rules

- The final read looks in each slot's own box. Don't widen it to rescue one hero: fix the art or
  the grid instead.
- Match against art that sits in the slot's box. New art (a skin, a state) goes through the cut
  frame or the grid box, never through a hero-specific crop.
- Anything that can make a read confident must keep confident-wrong at zero on the corpus. A wrong
  confident read is applied without anyone checking it.
- Say "unknown" rather than guess, for heroes and for you alike: the review, the match already
  applied, and the match page's question about which hero is you handle unknowns. A hint such as
  `LikelyYou` is only ever pointed out for a click, never applied.
- The same holds for items (`ItemCorpusTests`, `Golden/vision/items/corpus_report.json`): nothing that can make an item read confident may be confidently wrong on the corpus at any size, and a change may
  add confident reads and take away wrong ones, never the reverse. A read that says nothing (a covered slot, an unseen or hidden cell, an unknown item) is not a read that says "nothing there": a consumer
  keeps what it knew about those, and a hero a tooltip's edge half covers can read short.
