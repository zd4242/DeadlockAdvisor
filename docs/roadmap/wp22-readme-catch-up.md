# WP22 README and docs catch-up

Status: partly done (existing features and corrections landed with wave A; the final sweep waits for the other packages)
Effort: S · Risk: low (words only) · Depends on: none (run its last sweep after the other packages) · Wave: A, then last
Touches: `README.md`, `docs/publishing.md`, `docs/scoring_model.md`, `docs/detection_model.md`, `docs/architecture.md`
Docs to update: those

## Goal

The README and docs describe the app as it is: features that were never written down get a place, and statements that stopped being true are corrected.

## Why / premise check

The review compared the docs with the code. Each item below was true when written (✔ = checked by me, (agent) = reported by an audit and not re-checked); re-check each before editing, and skip
any already fixed by another package.

**Never documented** (README): ✔ clicking an enemy **focuses** the recommendations on items good against them (`docs/scoring_model.md` has the maths, the README doesn't mention it); the
**net-worth** reading (Detect reads each hero's souls) and the filter "Lean toward heroes ahead on net worth"; **Import a match** by its ID (the empty Match page offers it; the README only mentions it under
Privacy); **rebindable shortcuts** (Settings → Shortcuts); the **Steam account** setting that picks you out of an imported match; **Street Brawl** (4v4) reading; the mouse's **back and forward** buttons stepping
through pages. The Shortcuts table also lacks F6-F8 (Random), Ctrl+, (Settings), Ctrl+Q, Ctrl+Z/Ctrl+Y (Hero Traits undo/redo) and B (best-target column).

**Stale or inaccurate:**
- README "Hero Items": "**Usage at least**" is "Min usage" in the UI (`HeroItemsView.axaml`).
- README "Detecting the match": "It only comes up if there's something for you to check" is true only with Settings → Detection → "Switch here for a review" on, which defaults to off ✔
  (`AppSettings.ComeUpForReview`; the setting's own description says it waits behind the game). WP02 adds a sound; coordinate wording.
- README and `docs/detection_model.md` say Detect finds the game's window; today it never does (`project8.exe`; WP02). Update when WP02 lands.
- README "Data menu" says Sync from Game API re-measures max HP twice in two sentences (a duplicated sentence).
- README "Data folder": "last 12" backups is true only for CSVs and only for the last burst (WP05 changes the rule: update then).
- README "Data menu": Sync from Game API is an editor tool after WP09; the other update sentences change with WP10, WP13 and WP14.
- `docs/publishing.md` says release notes are "made from the commits since the last release" but the notes of the releases are only a "Full Changelog" link ✔ (`gh release view v0.3.0 --json body`);
  and says the match-data workflow "runs daily at 06:17 UTC" while scheduled runs start hours later (agent).
- README "Code signing policy" presents signing as active ("Only DeadlockAdvisor.exe on this repo's releases is signed") while no SignPath variable or secret is set in the repository ✔ (`gh variable list`,
  `gh secret list` are empty) and `docs/publishing.md` says releases go out unsigned until it is set up. **Ask the user which wording they want before touching it**: the README section may be what their
  signing application points at.

**Check:** run every file path and identifier the docs mention against the code (the review did this for `.claude/CLAUDE.md` and found nothing wrong).

## Read first

`README.md`, `docs/publishing.md`, `docs/architecture.md` (section 10 shows where each feature lives); the code each statement is about.

## Scope

In: the README additions and corrections above (short, in the README's voice: what a person sees and does); the docs corrections; a final check that the package list in
`docs/roadmap/README.md` matches the files and the statuses.
Out: rewriting docs that are fine; new features; the signing wording until the user decides.

## Suggested design

- Put focus and net worth next to the Match page paragraph that talks about clicking the roster; Import next to "Edit heroes"; shortcuts in the existing table plus one line saying they're rebindable.
- Keep each new sentence checkable: name the setting or menu entry.

## Tests

None; run `dotnet test … --filter "FullyQualifiedName~ThirdPartyNotices"` only if you touched `THIRD-PARTY-NOTICES.txt` (you shouldn't). Read the rendered Markdown once for table breakage.

## Acceptance criteria

- [ ] Each feature above is described once, where a reader would look.
- [ ] Each stale statement is fixed or consciously left for the package that changes it (listed in the report).
- [ ] No sentence promises something the code doesn't do.

## Conflicts

Other packages edit README lines too: run this package's first part (existing features) any time, and its final sweep after the others.

## Progress

First part (after WP01, WP02, WP05, WP06, WP15 and WP20): the README now describes focus, net worth, Import a match, the Steam
account, Street Brawl, the rebindable shortcuts and the missing keys (F6-F8, Ctrl+, / Ctrl+Q, mouse back/forward, Ctrl+Z/Y in Hero
Traits, B in By Trait); "Min usage" and the duplicated Sync sentence are fixed; `docs/publishing.md` no longer promises commit-based
release notes or a 06:17 UTC run time. Already true after WP02 and WP05: the Detect wording (`project8.exe`, review off by default)
and the backup rule.

Left for the final sweep: the Data menu / update sentences (WP09, WP10, WP13, WP14), art download wording (WP08), Ctrl+F on Hero Items
(WP03), the copy button (WP16), Match page keyboard (WP18), the post-match review (WP20/WP21), and the README's "Code signing policy"
wording, which needs the user's decision (no SignPath variable or secret is set; the section reads as if signing were active).

## Notes

The sweep at the end is also the moment to mark finished packages `done` in their files if they weren't, and to move `docs/roadmap` to a "done" note if everything is.
