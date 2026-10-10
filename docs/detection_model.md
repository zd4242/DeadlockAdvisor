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
