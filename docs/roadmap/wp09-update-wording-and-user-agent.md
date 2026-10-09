# WP09 Update wording, status label, hide Sync from Game API, versioned User-Agent

Status: todo
Effort: S · Risk: low · Depends on: WP08 (one shared line) · Wave: C
Touches: `src/Features/MainWindow/DataStatusViewModel.cs`, `DataStatusView.axaml`, **`src/Features/MainWindow/MainWindow.axaml`** (the Data menu), `src/Services/DeadlockApi.cs`, `src/Services/ArtDownloadService.cs` (one line), `README.md`, tests (`MenuTests`, `StatusBarTests`, `DeadlockApiTests`)
Docs to update: `README.md` (Data menu, Privacy), `docs/scoring_model.md` ("Patch workflow"), `docs/architecture.md` (section 7)

## Goal

What the app says about downloads matches what it does, the status chip says whether the match data is current rather than how old it is,
"Sync from Game API" stops tempting ordinary users into diverging from the published model, and the API can tell which app version is
calling.

## Why / premise check

1. Stale wording ✔: `DataStatusViewModel.NoDataText` and the Download button's tooltip in `DataStatusView.axaml` say fetching "takes a few
   minutes"; the Data menu tooltip in `MainWindow.axaml` says "from deadlock-api.com". The usual path is the shared download from GitHub,
   in seconds; deadlock-api.com is the fallback.
2. The chip label is `Match data · patch <p> · <age>` where age is when the data was fetched (`DataStatusViewModel.Refresh`). A finished
   patch is complete and never refetched, so "14d ago" reads as stale when nothing newer exists ✔.
3. The Data menu item "Sync from Game API" is visible to everyone; its neighbours ("Sync New …", "Model Health Report", "Reload", "Export")
   have `IsVisible="{Binding ShowsEditors}"` ✔. The sync rewrites `items.csv`, `item_stats.csv`, `item_tooltips.json` and measured hero
   scores, so the user's files stop matching the published model and every later formula update becomes a "files you changed" dialog
   (`ModelUpdatePlan.For`; README says so) ✔. Ordinary users get that data through the model.
4. `DeadlockApi.UserAgent` is the constant `deadlock-advisor/1.0` (and `ArtDownloadService.UserAgent` adds " (asset downloader)") ✔: no
   version, nothing to contact.

## Read first

`docs/architecture.md` section 7; `docs/scoring_model.md` "Patch workflow"; `AppVersion` (`Release`, `Text`); the Data menu in `MainWindow.axaml`;
README "Data menu", "Formula updates" and "Privacy".

## Scope

In:
- Correct the three texts (say "seconds from the shared download, or a few minutes from deadlock-api.com").
- Label: `Match data · patch <p> · up to date` when no newer patch is known and `MatchDataCheckedAt` is within 3 days; otherwise keep the
  fetch age (so an offline or never-checked install never claims "up to date"); the fetch age stays in the card. Keep the outdated and "no
  match data" labels.
- `IsVisible="{Binding ShowsEditors}"` on Sync from Game API; README and docs say it's an editor tool (the maintainer's patch workflow
  enables the editors).
- User-Agent `deadlock-advisor/<version> (+https://github.com/zd4242/DeadlockAdvisor)` for the API client; art's derives from it; version from
  `AppVersion.Release` (3 components) or `dev`.

Out: the Updates surface (WP13); changing when checks run (WP10).

## Suggested design

- `DataStatusViewModel` already receives `ISettingsService`; read `MatchDataCheckedAt` and refresh when settings change.
- `UserAgent` becomes `static readonly`; search for `const` uses (attributes, switch cases) and for tests that compare the value.
- README Privacy: say the requests identify the app and its version, nothing else.

## Tests

`StatusBarTests`/`DataStatus` tests: up-to-date label, stale-check falls back to age, outdated label unchanged, no-data label. `MenuTests`: the
item is hidden without editors and shown with them. `DeadlockApiTests`: the header carries the version. Run `StatusBar`, `Menu`, `DataMenu`,
`DeadlockApi`, `ArtDownloadService`.

## Acceptance criteria

- [ ] No string in the app says the match data takes minutes without saying it can take seconds.
- [ ] A current install shows "up to date"; a never-checked one shows its age.
- [ ] Sync from Game API is only in the menu with the editors on.
- [ ] Requests carry `deadlock-advisor/<version> (+…)`.

## Conflicts

`MainWindow.axaml`: after WP03, before WP13 and WP17. `ArtDownloadService.cs`: after WP08, before WP11.

## Notes

The 3-day window for "up to date" is a default (a check runs at startup and, after WP10, about every 6 hours).
