# WP24 Tab-view spike: can the scoreboard's items be read, and what do they say?

Status: todo
Effort: M · Risk: medium (it decides whether WP25-WP30 go ahead as written) · Depends on: none · Wave: A
Touches: new env-driven tool `tests/DeadlockAdvisor.Tests/Vision/TabViewSpike.cs` (writes to `mockups/tab_spike/`, like `VisionCorpusTools`), this file's "Findings"
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

- [ ] Each premise above is marked confirmed, changed (with the new numbers) or refuted.
- [ ] The report lists, per resolution, category accuracy, identity confident-right / confident-wrong / unknown, and the bars-versus-cost relation.
- [ ] A go / Plan B / stop decision is written here and the later packages' premises are updated to match.
- [ ] No capture, player name or API response is committed.

## Conflicts

None.

## Notes

- **Privacy.** The repository is public. A Tab capture prints every player's name; those and any video frame that isn't ours stay out of the repository. The screenshot that started this idea
  came from a video: use it for this spike only. Anything that later joins a corpus (WP26) has its name rows painted over first.
- If the grid doesn't carry over to the Tab view, say how the columns are found instead (the panel edges, the portraits themselves) before anything else is planned.
