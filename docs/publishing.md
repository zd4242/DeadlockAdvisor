# Publishing updates

Three things reach the people using the app, each its own way:

| What | How installs get it | What you do |
|---|---|---|
| **Hero ratings and item formulas** (the model) | At their next startup, without a new download | `dotnet run --project tools/PublishModel -- --push` |
| **Match data** (item win rates) | At startup, when it's newer than theirs | Nothing: a workflow refreshes it daily |
| **The app itself** | A new download from Releases | Actions → Release → Run workflow, choose `patch` / `minor` / `major` |

Formula and match data changes never need an app release. Release the app for code changes:
features, fixes, new screens, or a change to what the data files hold.

## Hero ratings and item formulas

Edit them in the app as usual: they save into your data folder (Settings → Data shows where). When
you're happy with them, from the repository:

```
dotnet run --project tools/PublishModel -- --push
```

That:

1. Checks your data folder loads, so a broken file isn't published.
2. Copies every model file that differs into `src/Assets/SeedData`: `hero_category_scores.csv`,
   `item_formula_coefficients.csv`, `trait_weights.csv`, `stat_rules.csv`, `categories.csv`, and the
   game data they go with (`heroes.csv`, `items.csv`, `item_stats.csv`, `item_tooltips.json`).
3. Copies the match data too, as the starting point for new installs, unless yours leans toward some
   ranks (switch the Match page's filter back to every rank first).
4. Rewrites `model.json`, the list of each file's hash, dated today.
5. Commits just `src/Assets/SeedData`, and pushes.

Leave out `--push` to commit without pushing, or both flags to only copy, and review with `git diff`
first. `--from <folder>` publishes another data folder than the app's.

**What installs do with it** (`ModelUpdateService`): at their next startup they read `model.json` from
`main` (GitHub may cache it for a few minutes), and compare each file with what they installed:

- Files they haven't changed are replaced quietly, the old copy kept in `data\.backups`, with a notice
  saying what was updated.
- Files they have changed (by hand, or with Sync from Game API) are listed in a dialog, unticked: they
  tick the ones to replace and keep the rest, and aren't asked about that version again.
- New installs start from the seed built into their copy of the app, then update the same way.

They can turn this off in Settings → Data, or check on demand with Data → Check for Formula Updates.

**Safety nets.** A test (`ModelUpdateTests.TheSeedsModelJsonListsEveryModelFileByItsHash`) fails if the
seed's files and `model.json` disagree, so CI goes red if a file was copied in by hand without the
tool. Installs check every file against its hash and change nothing if one doesn't match.

## Match data

Nothing to do. The **Match data** workflow (`.github/workflows/match-data.yml`) runs daily at 06:17 UTC
and keeps the `match-data` pre-release up to date: a new or ended patch at the next run, the current
patch every other day, a finished one never. A run with nothing due makes one call to deadlock-api.com.
Installs download what's newer than theirs at startup, in seconds, and fall back to asking
deadlock-api.com themselves only if the shared download hasn't been updated for four days. Details:
[scoring_model.md](scoring_model.md), "The shared download".

- **Run it now:** `gh workflow run match-data.yml`, or Actions → Match data → Run workflow.
- **GitHub stops scheduled workflows after 60 days without a commit to the repo.** Publishing formulas
  counts as one. If it stops, re-enable it from the Actions tab.

## The app

From GitHub: **Actions → Release → Run workflow**, choose:

- `patch` for fixes (0.1.0 → 0.1.1),
- `minor` for new features (0.1.1 → 0.2.0),
- `major` for big changes (0.2.0 → 1.0.0),
- `none` to build everything without releasing, to try a build first.

Or from the command line: `gh workflow run release.yml -f release=patch`.

The **Release** workflow (`.github/workflows/release.yml`) then runs the tests, tags the next version
up from the last tag (only from `main`, and only once the tests pass), builds every platform with that
version, and publishes a GitHub Release with notes made from the commits since the last one and
`DeadlockAdvisor.exe` attached. It keeps that name in every release, so
<https://github.com/zd4242/DeadlockAdvisor/releases/latest/download/DeadlockAdvisor.exe> (the README's
download link) is always the newest. It takes about 10 minutes. Pushing a tag yourself
(`git tag v1.2.0 && git push origin v1.2.0`) does the same with the version you chose.

Installs don't update themselves: people download the new version from
[Releases](https://github.com/zd4242/DeadlockAdvisor/releases), and Settings → Data shows which one they
have. Their data folder carries over, as it lives outside the app.

Linux and macOS builds are made each time as preview artifacts on the workflow run, not attached to the
release: see the README's "Linux and macOS".

## Adding or upgrading a package

`THIRD-PARTY-NOTICES.txt` lists every package the app ships with, its license and copyright, and the
notices the packages carry; the app shows it under Help → Third-Party Notices. It's generated from the
app's build, and `ThirdPartyNoticesTests` fails when a package is added, removed or upgraded until it's
regenerated:

```
dotnet test tests/DeadlockAdvisor.Tests/DeadlockAdvisor.Tests.csproj -c Release -e DEADLOCK_UPDATE_GOLDENS=1 --filter "FullyQualifiedName~ThirdPartyNoticesTests"
```

A package under a license other than MIT or Apache 2.0 stops the generator: check it allows shipping the
package inside the app before adding it there.

## Changing what the files hold

Released apps keep reading the files published after them, so a change to a file's shape needs care:

| You change | Bump | So that |
|---|---|---|
| A model file's columns or meaning | `ModelManifest.CurrentFormat` | older apps ignore the new model instead of misreading it |
| `MatchSegment`'s JSON | `MatchSegment.Version` | older apps fall back to asking deadlock-api.com |
| The match data manifest | `MatchSnapshot.Version` | the same |

Release the app before (or with) publishing data in a new format: until people update, they keep the
last version their app understands.

## Where things live

| | |
|---|---|
| `src/Assets/SeedData/` | The published model, and the starting data built into the app |
| `src/Assets/SeedData/model.json` | Each model file's hash and the date it was published |
| `tools/PublishModel/` | Publishes your data folder's model into the seed |
| `tools/MatchSnapshot/` | What the Match data workflow runs |
| `.github/workflows/ci.yml` | Builds and tests every push and pull request |
| `.github/workflows/match-data.yml` | The daily match data |
| `.github/workflows/release.yml` | App releases |
| `Properties/PublishProfiles/release.pubxml` | The single-file build: `dotnet publish -p:PublishProfile=release [-r linux-x64]` |
