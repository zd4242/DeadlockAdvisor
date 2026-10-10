# Roadmap: improvement work packages

Planned improvements, cut into packages that one agent session can pick up and finish. Each package file carries its own
context (why, evidence, what to read, design, tests, acceptance criteria); this README holds what they share.

**To hand one over, say "implement WP08"** (or "implement WP08 and WP09, in that order", or "implement the art download
package"). `/implement WP08` does the same. One package per session; if several sessions run at once, give each a package
from the same wave whose files don't overlap (below). To see where everything stands, search for `^Status:` under
`docs/roadmap` (for example `rg -m1 "^Status:" docs/roadmap`).

Not scheduled yet: [backlog.md](backlog.md) (ideas evaluated and set aside, each with the evidence behind it).

## How to implement a package

1. **Read.** `.claude/CLAUDE.md` is already loaded; read [../architecture.md](../architecture.md) (the sections the package
   names, at least sections 1, 3 and 11), then the package file top to bottom, including its "Read first".
2. **Premise check.** "Why / premise check" lists what the package rests on and how to re-check it. The code moves as other
   packages land. If the problem is gone or the code described no longer exists, stop and tell the user instead of inventing
   work. If the facts differ a little, say how and carry on in the spirit of the package.
3. **`git status` before the first edit.** Other sessions may share the working tree. Files that aren't yours stay untouched
   (CLAUDE.md, "Committing").
4. **Dependencies.** If the package says "Depends on WPnn" and that package's `Status:` isn't `done`, tell the user. The file
   says when the work can go ahead anyway.
5. **Implement** in the order the package suggests, inside its Scope. Anything else you notice goes in your final report, not
   the commit. Follow the repo's rules from CLAUDE.md (scoring, detection, shared match data, formula updates, releases) and
   its guidelines (shared logic over duplication, single-statement `if` on the next line, comments only where something isn't
   obvious).
6. **Tests.** Add or update the tests the package names. Run the targeted ones with
   `dotnet test tests/DeadlockAdvisor.Tests/DeadlockAdvisor.Tests.csproj -c Release --filter "FullyQualifiedName~A|FullyQualifiedName~B"`
   (`-c Release` because the app may be running from a Debug build). Run the full suite when the package says so, or when you
   touch shared infrastructure: startup, DI, base view models, styles or themes, scoring, `DataStore`, vision.
7. **UI changes.** Check them through the headless UI tests, which write PNGs to `mockups/`; open them with the Read tool.
   Never launch the app, press hotkeys or capture the screen: the user may be in a match. If only a real run can answer a
   question, ask first, then set `DEADLOCK_ADVISOR_HOME` to a scratch folder.
8. **Docs.** Update what the package lists, the `docs/architecture.md` sections it names, and any README or docs sentence your
   change makes untrue, in the same commit.
9. **Commit** when the package's tests pass: only your files, by path (`git add -- <new files>`, then
   `git commit -F <message file> -- <paths>`). The message starts with an area ("Detect: ...", "Art download: ...",
   "Settings: ...") and says what changed for a person, like the existing `git log`; end it with the attribution lines your
   session was given. Set the package's `Status:` line to `done (<short hash>)` in that commit (write `done` if you can't know
   the hash yet and say so in your report). A big package may land in several commits, each leaving the suite green.
10. **Don't** push, release, publish the seed (`src/Assets/SeedData`), bump a file-format version the package doesn't name, add
    or upgrade packages (that means regenerating `THIRD-PARTY-NOTICES.txt`), or change workflows unless the package says so.
11. **Report:** what changed, which tests ran and how they ended, what you left out or found on the way, and any decision the
    package left to the user.

### Definition of done

- [ ] The premise was checked, and any difference was reported.
- [ ] The behaviour in "Acceptance criteria" works, shown by tests (or, for looks, by a rendered PNG you looked at).
- [ ] Targeted tests pass with `-c Release`; the full suite passes if the package asked for it.
- [ ] Golden files changed only if the package said they would, and the diff was reviewed.
- [ ] Docs updated (package list, architecture sections, README/docs sentences that became untrue).
- [ ] One commit per step, your files only, `Status:` line updated, nothing pushed.

## Waves and file overlap

Packages in the same wave can start together if their "Touches" don't overlap; the hot files below are touched by several
packages, so those packages go one at a time, in the order shown. Sessions on one working tree must not edit the same file at
once; separate git worktrees avoid that.

| Wave | Packages |
|---|---|
| A (no dependencies) | WP01, WP02, WP05, WP06, WP15, WP20, WP22 (its part that documents existing features) |
| B | WP03 (after WP01), WP04 (after WP01), WP07, WP08, WP16 |
| C | WP09 (after WP08), WP10, WP11 (after WP08), WP12 (after WP03), WP21 (after WP20) |
| D | WP13 (after WP09 and WP10), WP14 (after WP13), WP18 (after WP16), WP19 |
| E | WP17 (after WP13), then WP22's final sweep |

Hot files, in the order their packages should run:

| File | Packages |
|---|---|
| `src/Features/MainWindow/DataMenuViewModel.cs` | WP07 → WP10 → WP11 → WP13 → WP14 |
| `src/Features/MainWindow/MainWindowViewModel.cs` | WP02, WP03, WP12, WP13, WP15 (different methods; not at the same moment) |
| `src/Features/MainWindow/MainWindow.axaml` | WP03 → WP09 → WP13 → WP17 |
| `src/Features/MainWindow/MainWindow.axaml.cs` | WP07, WP19 |
| `src/Services/NotificationService.cs` | WP01 → WP03 |
| `src/Services/ArtDownloadService.cs` | WP08 → WP09 (one line) → WP11 |
| `src/Features/Match/Results/ResultsView.axaml(.cs)` | WP16 → WP18 |
| `App.axaml.cs` | WP01, WP02, WP15 (small edits) |
| `README.md`, `docs/*.md` | every package edits its own lines; WP22 last |

## The packages

| ID | File | Title | Effort | Depends | Wave |
|---|---|---|---|---|---|
| WP01 | [wp01](wp01-notifications-and-settings-safety.md) | Startup messages are kept; an unreadable settings.json is kept; unhandled exceptions are logged | S | none | A |
| WP02 | [wp02](wp02-detect-fixes.md) | Detect: find the game (`project8.exe`), reject empty frames, chime on in-game F9 | M | none | A |
| WP03 | [wp03](wp03-small-ux-bugs.md) | Ctrl+F on Hero Items, longer error toasts and Recent messages, a save-failed style | S | WP01 | B |
| WP04 | [wp04](wp04-startup-recovery.md) | Recover from a bad CSV at startup; no pruning on empty tables | M | WP01 | B |
| WP05 | [wp05](wp05-backup-retention.md) | Time-spaced backups for CSV and JSON | S | none | A |
| WP06 | [wp06](wp06-api-guards-and-retries.md) | Sanity guards and retries for the API paths | S-M | none | A |
| WP07 | [wp07](wp07-close-flush-and-health-snapshot.md) | Confirm on a failed save at close; Model Health on a snapshot | S | none | B |
| WP08 | [wp08](wp08-art-download.md) | Art: fetch once, in parallel, retry, honest size | M | none | B |
| WP09 | [wp09](wp09-update-wording-and-user-agent.md) | Update wording, status label, hide Sync from Game API, versioned User-Agent | S | WP08 | C |
| WP10 | [wp10](wp10-update-scheduler.md) | Re-check for updates while the app is open | M | none | C |
| WP11 | [wp11](wp11-downloads-off-the-ui-thread.md) | Heavy download work off the UI thread | M | WP08 | C |
| WP12 | [wp12](wp12-page-work-when-needed.md) | Match rescoring only when shown; Hero Items table cache | M | WP03 | C |
| WP13 | [wp13](wp13-updates-surface.md) | One Updates chip and flyout | L | WP09, WP10 | D |
| WP14 | [wp14](wp14-update-settings-modes.md) | Settings → Data modes; manual match-data "Check now" | M | WP13 | D |
| WP15 | [wp15](wp15-single-instance.md) | Single-instance guard | S | none | A |
| WP16 | [wp16](wp16-copy-top-picks.md) | Copy the top picks to the clipboard | S | none | B |
| WP17 | [wp17](wp17-accessibility-names-contrast-layout.md) | Accessible names, contrast, layouts at minimum size and zoom | M | WP13 | E |
| WP18 | [wp18](wp18-match-page-keyboard.md) | Keyboard navigation on the Match page | M-L | WP16 | D |
| WP19 | [wp19](wp19-zoom-reaches-dialogs.md) | Zoom reaches dialogs (and tooltips and flyouts if feasible) | M | none | D |
| WP20 | [wp20](wp20-match-lookup-builds.md) | Import parses each player's items and net-worth curve | M | none | A |
| WP21 | [wp21](wp21-post-match-review.md) | Post-match review: your build against the advice, and a net-worth curve | L | WP20 | C |
| WP22 | [wp22](wp22-readme-catch-up.md) | README and docs catch-up | S | none | A, then last |
| WP23 | [wp23](wp23-data-menu-simplify.md) | Data menu: one Check for Updates, maintainer tools in Model Tools (done) | M | WP13, WP14 | D |

WP13, WP18 and WP21 list stages: stop at a stage boundary with a green commit if the session runs short.

## Where this came from

A review of the whole app (code, docs, rendered pages, live endpoints, a real data folder, three read-only audits) produced
the list. Each package keeps the evidence it rests on. Evidence marks used in the package files: **✔** verified in code, data
or against a live service when the package was written; **(agent)** reported by an audit and not re-checked; **(est.)** an
estimate, not measured. Line numbers are hints; names are what to search for.

Facts that several packages lean on, and how to re-check them (read-only; no windows, no screen):

- The art CDN: `curl -sI https://assets-bucket.deadlock-api.com/assets-api-res/images/heroes/inferno_card.png` (ETag present,
  no `cache-control`, `cf-cache-status: DYNAMIC`); the asset lists: `curl -sI "https://api.deadlock-api.com/v1/assets/heroes?only_active=true"`
  (`cache-control: public, max-age=3600`, brotli).
- Releases: `gh release view match-data --repo zd4242/DeadlockAdvisor --json assets`, `gh release view v<x.y.z> --repo zd4242/DeadlockAdvisor --json body`,
  `gh variable list` and `gh secret list` (signing is set up only if they list SignPath entries).
- A finished match's metadata (what WP20 and WP21 parse): `GET https://api.deadlock-api.com/v1/matches/<id>/metadata`; an id comes
  from `GET https://api.deadlock-api.com/v1/matches/metadata?limit=1&min_unix_timestamp=<recent unix seconds>`. Each player has
  `items[]` (`item_id`, `game_time_s`, `sold_time_s`, ...) and `stats[]` (`time_stamp_s`, `net_worth`, ...).
- Colour contrast: WCAG relative luminance on the `src/Theme/Palette.cs` colours.

### Numbering in the first draft of the review

The review's working draft numbered its packages differently: quick wins (1) became WP08 and WP09, re-checks while open (2)
WP10, the Updates surface (3) WP13 and WP14, Detect (5) WP02, data safety (6) WP01, WP04, WP05, WP06 and WP07, small UX bugs
(7) WP03, responsiveness (8) WP11 and WP12, accessibility and zoom (9) WP17, WP18 and WP19, single instance (11) WP15, copy
(12) WP16, post-match review (14) WP20 and WP21, docs (19) WP22 and `docs/architecture.md`.
