# Publishing updates

Four things reach the people using the app, each its own way:

| What | How installs get it | What you do |
|---|---|---|
| **Hero ratings and item formulas** (the model) | At their next startup once CI passes, without a new download | `dotnet run --project tools/PublishModel -- --note "What changed" --push` |
| **New heroes** | As part of the model: a workflow adds them, unrated, within hours of the game listing them | Nothing, until you rate them (an issue reminds you) |
| **Match data** (item win rates) | At startup, when it's newer than theirs | Nothing: a workflow refreshes it daily |
| **The app itself** | A new download from Releases | Actions → Release → Run workflow, choose `patch` / `minor` / `major` |

Formula and match data changes never need an app release. Release the app for code changes:
features, fixes, new screens, or a change to what the data files hold.

## Hero ratings and item formulas

Edit them in the app as usual: they save into your data folder (Settings → Data shows where). When
you're happy with them, from the repository:

```
dotnet run --project tools/PublishModel -- --note "Spirit items rate higher against Haze." --push
```

That:

1. Checks your data folder loads, so a broken file isn't published.
2. Copies every model file that differs into `src/Assets/SeedData`: `hero_category_scores.csv`,
   `item_formula_coefficients.csv`, `trait_weights.csv`, `stat_rules.csv`, `categories.csv`, and the
   game data they go with (`heroes.csv`, `items.csv`, `item_stats.csv`, `item_tooltips.json`).
3. Copies the match data too, as the starting point for new installs, unless yours leans toward some
   ranks (switch the Match page's filter back to every rank first).
4. Rewrites `model.json`, the list of each file's hash, dated today, with your note at the top of its
   `notes` (the last 20 are kept).
5. Commits just `src/Assets/SeedData`, and pushes.

Leave out `--push` to commit without pushing, or both flags to only copy, and review with `git diff`
first. `--from <folder>` publishes another data folder than the app's. The note is optional, but it's
what people see: write it for players, one or two sentences. It's only added with a new version, so a
run that changes nothing drops it.

**Then CI publishes it.** The push runs the **CI** workflow (`.github/workflows/ci.yml`). Once its tests
pass, its `publish-model` job puts the seed's files on the rolling
[`model`](https://github.com/zd4242/DeadlockAdvisor/releases/tag/model) pre-release, each named by its
hash (`trait_weights-1a2b3c4d.csv`), then `model.json`, then deletes the files it no longer names. A
push that fails the tests publishes nothing, and installs keep the last version that passed. It takes
about 10 minutes from the push, and `--push` waits for it with the GitHub CLI (`gh`), then says
whether it was published or why not, with a link to the run. Ctrl+C stops the waiting, not CI. To publish again without a push (say, if the job failed for GitHub's
reasons), run CI by hand: `gh workflow run ci.yml`, or Actions → CI → Run workflow.

**What installs do with it** (`ModelUpdateService`): at their next startup they read `model.json` from
that release, and compare each file with what they installed:

- Files they haven't changed are replaced quietly, the old copy kept in `data\.backups`, with a notice
  giving the notes they haven't seen yet (or the files updated, without any).
- Files they have changed (by hand, or with Sync from Game API) are listed in a dialog, unticked, under
  the notes: they tick the ones to replace and keep the rest, and aren't asked about that version again.
  The exception is heroes: whatever they changed, the heroes the published model has and their folder lacks
  are added to `heroes.csv` and the ratings first (`ModelUpdateService.AddNewHeroes`), with every row already
  there left as it is, so `heroes.csv` is never in the dialog and the ratings' dialog is only about their
  own ratings.
- New installs start from the seed built into their copy of the app, then update the same way.

They can turn this off in Settings → Data, or check on demand with Data → Check for Formula Updates.
Settings → Data also shows the version installed and when it was last checked, every version's notes,
**Reset…** to put files back to the published version, and **Undo update**, which puts back what the
last update or reset replaced (kept in `data\.model-previous` until the next one), and then doesn't
offer that version again.

Versions 0.1.0 and 0.1.1 of the app read `model.json` straight from `main` instead, so they get a push
before CI has tested it, and don't show notes. Both go away as people update.

## New heroes

A hero the game releases reaches everyone without anyone touching the app, except for rating it:

1. **The workflow.** **New heroes** (`.github/workflows/new-heroes.yml`) runs every six hours and runs
   `dotnet run --project tools/PublishModel -- --add-new-heroes` (`ModelPublisher.AddNewHeroesAsync`). That
   compares the heroes deadlock-api.com lists as active (`/v1/assets/heroes?only_active=true`, so a hero the API doesn't list as active yet
   doesn't count, and neither does a placeholder such as `hero_testhero`) with the seed's
   `heroes.csv`, adds the missing ones with every trait at 0 (`GameSync.ApplyRoster`), and dates `model.json`
   with a note ("New hero: Baba. Its ratings are still to come, …"). When the seed changed it commits it, and
   starts CI by hand, because a push made with the workflow's own token doesn't start it. CI's tests and its
   `publish-model` job then publish it as above. It also opens an issue, "Rate new hero: Baba".
2. **Why that's safe to publish unattended.** A hero with every trait at 0 is unprofiled
   (`DataStore.IsProfiled`): it's left out of the baselines and out of scoring, so no recommendation changes. It
   just makes the hero pickable and detectable.
3. **You rate it.** Data → Check for Formula Updates puts the new hero in your data folder, then rate it in Hero
   Traits and publish as usual. `PublishModel` refuses to publish from a data folder that lacks a hero the seed
   has, since copying its `heroes.csv` over the seed would drop the hero for everyone.
4. **Installs.** They take it with the model update at their next startup, as above, with the note. Their art
   follows at once: a hero without a portrait starts the quiet art check (for anyone who has downloaded art
   before), and one the API had no art for yet is asked about again after a day rather than a week
   (`DataMenuViewModel.MissingArtRetryInterval`). Someone who has turned model updates off gets a "New heroes"
   chip in the status bar naming them, and a click adds them and their art (Settings → Data, "Say when new
   heroes are out", turns that off).

Run it now with `gh workflow run new-heroes.yml`, or `--add-new-heroes` locally, which only edits the seed for
you to review with `git diff`.

**Safety nets.** A test (`ModelUpdateTests.TheSeedsModelJsonListsEveryModelFileByItsHash`) fails if the
seed's files and `model.json` disagree, so CI goes red, and nothing is published, if a file was copied
in by hand without the tool. Installs check every file against its hash and change nothing if one
doesn't match.

## Match data

Nothing to do. The **Match data** workflow (`.github/workflows/match-data.yml`) is scheduled daily for 06:17
UTC (GitHub starts scheduled runs late under load: in practice between about 12:00 and 15:00 UTC) and keeps the `match-data` pre-release up to date: a new or ended patch at the next run, the current
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
version, and publishes a GitHub Release with `DeadlockAdvisor.exe` attached and GitHub's generated notes
(`--generate-notes`): the pull requests merged since the last release, which with commits made straight
to `main` is only the "Full Changelog" link to the compare view. Edit the release's notes on GitHub if
players should read more; the app's "What's new" link opens that page. It keeps that name in every release, so
<https://github.com/zd4242/DeadlockAdvisor/releases/latest/download/DeadlockAdvisor.exe> (the README's
download link) is always the newest. It takes about 10 minutes. Pushing a tag yourself
(`git tag v1.2.0 && git push origin v1.2.0`) does the same with the version you chose.

**How installs get it** (`AppUpdateService`, `AppUpdateViewModel`): at startup an install asks GitHub's
API for the newest release (the `match-data` and `model` pre-releases don't count) and, if it's newer,
shows "Version x.y.z is out · Update · What's new" in the status bar. **Update** downloads the release's
`DeadlockAdvisor.exe` beside the running one as `DeadlockAdvisor.update.exe`, and checks it against the
size and SHA-256 GitHub gives for it. A single-file app reads its own exe while it runs, so the swap waits
for the app to close: then a copy of the old exe (`DeadlockAdvisor.installer.exe`, started with
`--install`) waits for it to exit, copies the download over it, whatever the file was named, and with
**Restart now** starts it. The installer is the old version, so a new release needn't know how to install
itself. The next start deletes both copies and says "Updated to version x.y.z". If the exe's folder can't
be written to, such as under Program Files, Update opens the release page instead. Their data folder
carries over, as it lives outside the app.

To try a release build without touching your own data, set `DEADLOCK_ADVISOR_HOME` to an empty folder
before starting it: settings, the log and the default data folder go there. A build published with
`-p:Version=0.1.9` sees the newest release as newer, so it exercises Update end to end.

Closing the notice skips that version; Settings → Data shows the version with when it was last checked,
has Check now, and turns the startup check off. Builds made outside the Release workflow (`0.0.0-dev`)
never check. Versions 0.1.0 and 0.1.1 came before the check, and 0.2.0 before Update: people on those
download the next version by hand once.

Linux and macOS builds are made each time as preview artifacts on the workflow run, not attached to the
release: see the README's "Linux and macOS".

## Code signing

The Release workflow signs `DeadlockAdvisor.exe` through [SignPath](https://signpath.org)'s free program
for open source projects, once it's set up; until then releases go out unsigned and the step is
skipped. A signed exe names its publisher instead of "Unknown publisher", and SmartScreen's warning
fades as the signature builds a reputation over the first downloads. It doesn't vanish on day one.

**Setting it up (once):**

1. Turn on two-factor authentication on your GitHub account: SignPath requires it.
2. Apply at <https://signpath.org/apply> with the repository URL, the MIT license, and
   `.github/workflows/release.yml`. The README's "Code signing policy" section is the policy page
   they ask for; keep its roles and privacy notes true. Approval can take a few weeks.
3. Once accepted, in SignPath:
   - Link the project to GitHub as its trusted build system, so it only signs builds made by this
     repository's workflows.
   - Check the artifact configuration signs the exe at the root of the zip GitHub Actions makes of the
     artifact, something like:
     ```xml
     <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
       <zip-file>
         <pe-file path="DeadlockAdvisor.exe" product-name="Deadlock Item Advisor">
           <authenticode-sign/>
         </pe-file>
       </zip-file>
     </artifact-configuration>
     ```
   - Make an API token for a CI user that may submit to the release signing policy.
4. In the repository, add the token as a secret and the organization ID as a variable:
   ```
   gh secret set SIGNPATH_API_TOKEN
   gh variable set SIGNPATH_ORGANIZATION_ID --body <organization id>
   ```
   If the project or signing policy slug isn't `DeadlockAdvisor` / `release-signing`, set
   `SIGNPATH_PROJECT_SLUG` / `SIGNPATH_SIGNING_POLICY_SLUG` the same way.

**Each release** then stops at its Windows build until you approve the signing request in SignPath
(it emails you), for up to 45 minutes; the release is published with the signed exe. If it times out or
is rejected, nothing is released: re-run the failed jobs from the workflow run once you're ready. Only
real releases are signed, not `none` builds.

To turn signing off again, delete the `SIGNPATH_ORGANIZATION_ID` variable.

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
| A model file's columns or meaning | `ModelManifest.CurrentFormat` | older apps ignore the new model instead of misreading it, and say it needs a newer version when asked to check for formula updates |
| `MatchSegment`'s JSON | `MatchSegment.Version`, and `MatchSnapshot.Version` with it | apps check the manifest's version before downloading anything, so older apps fall back to asking deadlock-api.com instead of failing on each file |
| The match data manifest | `MatchSnapshot.Version` | the same |

Release the app before (or with) publishing data in a new format: until people update, they keep the
last version their app understands.

## Where things live

| | |
|---|---|
| `src/Assets/SeedData/` | The published model, and the starting data built into the app |
| `src/Assets/SeedData/model.json` | Each model file's hash, the date it was published, and the notes |
| `tools/PublishModel/` | Publishes your data folder's model into the seed; CI runs it with `--assets` to lay out the release |
| `tools/MatchSnapshot/` | What the Match data workflow runs |
| `.github/workflows/ci.yml` | Builds and tests every push and pull request, then publishes the model from `main` |
| `.github/workflows/match-data.yml` | The daily match data |
| `.github/workflows/new-heroes.yml` | Adds the game's new heroes to the seed, every six hours |
| `.github/workflows/release.yml` | App releases, signed once SignPath is set up |
| `Properties/PublishProfiles/release.pubxml` | The single-file build: `dotnet publish -p:PublishProfile=release [-r linux-x64]` |
