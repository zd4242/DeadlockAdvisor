# Project Context
This is a dotnet project using Avalonia and Reactive UI, a C# port of the PyQt6 Deadlock Item Advisor (`D:\Dev\Python\deadlock_advisor`), which recommends in-game items based on the heroes in your match. The port plan is `D:\Dev\Python\deadlock_advisor\docs\csharp_port_plan.md`. The Python app is retired: read it to understand the original behaviour, but don't edit or run it. **This repo is now the source of truth, and scoring has diverged from Python on purpose.**

# Scoring model
Read `docs/scoring_model.md` before touching scoring, the formula CSVs (`stat_rules.csv`, `item_formula_coefficients.csv`, `trait_weights.csv`, `categories.csv`) or `GameSync`. The rules to follow:
- A hero's trait counts as `(score − roster average over profiled heroes) × coefficient` (`DataStore.TraitBaselines`, `ItemScoring.BuildWeightMatrix`). Don't reintroduce raw-score sums or flat per-item bonuses: they make the same items win every match.
- Prefer a `stat_rules.csv` line, which follows patches automatically, over a typed coefficient. Type coefficients only for what stats can't express, such as counters or debuffs on enemies.
- Every property the tooltip shows under a scored stat's label must be an alias in `GameSync.Stats` or listed, with a reason, in `GameSync.Unscored`. `GameApiTests.TheSnapshotShopHasNoUnmappedStatsOrStaleOverrides` enforces this.
- After a deliberate scoring or sync change, regenerate the goldens with `dotnet test <tests csproj> -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter ...` and review the diff. Use Data → Model Health Report (`Scoring/ModelHealth.cs`) to check the effect on the whole model.

# Committing
When you finish a feature or fix and the tests pass, commit it without being asked. Follow-up changes after that get their own commits. Don't push.

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
