# How detection reads the match

Detect (F9) captures the top of the game, finds the strip of twelve portraits, names the hero in
each slot, works out which slot is you, and either applies the match or shows it for review. This
is how each step works, why it works that way, and how to change it without making it worse.

## The pipeline

1. **Capture** (`ScreenCaptureService`): the top 22% of Deadlock's window, in physical pixels.
2. **Grid** (`Layout`): twelve slots in two blocks of six, evenly pitched, with a gap for the clock.
   Five numbers place every slot: centre, pitch, middle gap, art width, top. The grid is searched
   for once per screen size and then cached (`AppSettings.VisionGeometry`, versioned by
   `Layout.CacheVersion`). A cached grid is searched for afresh when its **fit** falls under 0.7.
   Fit is the trimmed mean of the slots' best scores (`Detection.Fit`). A right grid fits at 0.81 to
   0.92 even with four players dead.
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
   reported unknown rather than guessed.
6. **Same match** (`RosterContinuity`): heroes don't change slots mid-match. It's the same match when
   at least six confident slots agree with the match already applied and none disagrees. Then any
   slot the read couldn't settle keeps the applied hero ("kept"), and you carry over.
7. **Apply or review** (`DetectAction`): if every slot is confident or kept and you're known, the
   match is applied without the review, and **Review** beside Detect reopens it. Otherwise the review
   opens.

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
- Download Art only asks whether a file changed (If-None-Match, answered with 304).
- The app checks quietly at startup about weekly, and at once when `_derived.json` is out of date.
- Nothing hero-specific ships with the app.

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
- Say "unknown" rather than guess, for heroes and for you alike: the review, and the match already
  applied, handle unknowns.
