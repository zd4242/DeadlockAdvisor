# Project Context
This is a dotnet project using Avalonia and Reactive UI: Deadlock Item Advisor, a Windows desktop app that recommends in-game items based on the heroes in your match. It can read the match off the screen, and takes a second opinion from real match results on deadlock-api.com. The repo is public on GitHub under the MIT license, so keep personal data, local paths and anything that isn't ours (beyond the Valve art and API data the README credits) out of it.

# Scoring model
Read `docs/scoring_model.md` before touching scoring, the formula CSVs (`stat_rules.csv`, `item_formula_coefficients.csv`, `trait_weights.csv`, `categories.csv`) or `GameSync`. The rules to follow:
- A hero's trait counts as `(score − roster average over profiled heroes) × coefficient` (`DataStore.TraitBaselines`, `ItemScoring.BuildWeightMatrix`). Don't reintroduce raw-score sums or flat per-item bonuses: they make the same items win every match.
- Prefer a `stat_rules.csv` line, which follows patches automatically, over a typed coefficient. Type coefficients only for what stats can't express, such as counters or debuffs on enemies.
- Every property the tooltip shows under a scored stat's label must be an alias in `GameSync.Stats` or listed, with a reason, in `GameSync.Unscored`. `GameApiTests.TheSnapshotShopHasNoUnmappedStatsOrStaleOverrides` enforces this.
- After a deliberate scoring or sync change, regenerate the goldens with `dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter ...` and review the diff. Use Data → Model Health Report (`Scoring/ModelHealth.cs`) to check the effect on the whole model.

# Shared match data
A scheduled workflow (`.github/workflows/match-data.yml`) runs `tools/MatchSnapshot`, which runs the app's own download code (`MatchSnapshotJob`), and publishes the match counts as `match-data` release assets that every install downloads (`MatchSnapshotService`). Released apps read those files, so a change to what they hold (`MatchSegment`'s JSON, the manifest) must bump `MatchSegment.Version` or `MatchSnapshot.Version`. An app that reads an unknown version falls back to deadlock-api.com.

# Formula updates
`src/Assets/SeedData` is the published model: new installs start from it, and existing ones update from `main` on GitHub (`ModelUpdateService`). Publish with `tools/PublishModel` (see `docs/publishing.md`), which copies the data folder in and rewrites `model.json`; `ModelUpdateTests.TheSeedsModelJsonListsEveryModelFileByItsHash` fails if the two disagree. Pushing a changed seed publishes it to every install, so change it only when the user means to publish. A change older apps can't read, such as a new column, bumps `ModelManifest.CurrentFormat`.

# Releases
`.github/workflows/release.yml` publishes a GitHub Release built with `Properties/PublishProfiles/release.pubxml`, on a `v*` tag or when run with a version bump. Only the user starts releases. `docs/publishing.md` is the maintainer's guide to all three kinds of update: keep it current when any of them changes. Code that only works on Windows stays behind `OperatingSystem.IsWindows()`, because the workflow also builds Linux and macOS previews. Adding, removing or upgrading a package means regenerating `THIRD-PARTY-NOTICES.txt` (`ThirdPartyNoticesTests`, with `-e DEADLOCK_UPDATE_GOLDENS=1`).

# Detection model
Read `docs/detection_model.md` before touching `src/Vision`, the detect flow (`Features/Match/Detect`) or the art download. The rules to follow:
- Every portrait is cut from the hero's card at `TopbarDerivation.InGameFrame`, so it sits in the slot's box, and the final read only looks there. Don't widen that search to rescue one hero: fix the art or the grid.
- Detection may not read the labelled corpus any worse (`VisionCorpusTests`, `Golden/vision/corpus_report.json`), and must never read anything confidently wrong: a confident read is applied without review. After a deliberate change, regenerate with `-e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~GoldenVisionTests|FullyQualifiedName~VisionCorpusTests"` and review the diff, including `mockups/detection_report.md`.

# Testing
The full suite takes a minute or two, so don't run it after every change. Run tests with `-c Release`, because the app may be running from a Debug build.
- For most changes, run only the tests for the area you touched, with `--filter "FullyQualifiedName~ResultsViewModel|FullyQualifiedName~MatchPage"` and so on. The test files are named after the features they cover.
- Run the full suite when a change can affect areas beyond the one you touched: scoring, the formula CSVs, `GameSync`, `DataStore`, vision or detection, and shared infrastructure (app startup, DI wiring, base view models, styles and themes). Also run it whenever you can't confidently name the affected tests, or when the user asks.
- If a full run fails in an area you didn't touch, look for the recent commit that broke it before assuming it's another session's in-progress work.

# Committing
When you finish a feature or fix and its tests pass (targeted or full, as above), commit it without being asked. Follow-up changes after that get their own commits.

Push only when the user says to, and then with a plain `git push`: never force-push, push tags, or push a branch or ref by name. A push publishes to everyone at once: a changed `src/Assets/SeedData` reaches every install at its next startup, and workflow changes run on GitHub straight away. It also pushes every unpushed commit, so check `git log origin/main..` first and mention any commits that aren't yours.

Other agents may be working in this repo at the same time, so the working tree can contain changes that aren't yours:
- Run `git status` before your first edit and note what's already modified. That work isn't yours.
- Commit only the files you changed, by path: `git commit -F <message file> -- <paths>` (add new files with `git add -- <paths>` first). Never use `git add -A`, `git add .` or `git commit -a`, and don't stash, reset or discard changes you didn't make.
- If a file you changed also holds another session's uncommitted changes, don't try to split it. Tell the user which files are mixed and ask whether to commit them whole or leave them.
- If tests fail in code you didn't touch, another session's work may be in progress. Say so, and still commit your own work if its tests pass.

# Guidelines
- Prefer solutions that follow good practices and result in cleaner code, over whatever the simplest solution is.
- Ideally focus on solving the root issue of a problem rather than just treating the symptoms.
- When it makes sense to (e.g. doesn't overly complicate something simple), refactor logic to be shared instead of duplicating it.
- For `if` statements with a single statement afterwards, move that statement to the following line instead of on a single line.
- When debugging a bug, consider if any debug statements (and their output) would help you better understand what is not working correctly.
- Only leave comments if they are concise and explain a somewhat complicated or not immediately apparent section of code, and don't leave comments if it is only relevant to explain or highlight the recent set of changes that were made.
- The global usings file contains the following:

```csharp
global using Avalonia;
global using Avalonia.Controls;
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;
```
