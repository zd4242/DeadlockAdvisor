# WP23 Data menu: one Check for Updates

Status: done
Effort: M · Risk: low-medium · Depends on: WP13, WP14 · Wave: D
Touches: `MainWindow.axaml` (the Data menu), `DataMenuViewModel`, `Updates/*`, `Settings/Data/*`, `ShortcutKeys`, many hint strings, the docs

## Goal

An ordinary user never has to know there are four kinds of data. The Data menu is a few items, "Check for Updates" asks for everything,
and the maintainer tools sit behind the existing "Edit the scoring model" switch.

## What it settled (so later work doesn't undo it)

- **The Data menu is Check for Updates (Ctrl+U), Downloads and Updates… (Settings on the Data page), Settings and Quit.** The maintainer
  tools (Sync from Game API, Sync New, Model Health Report, Reload, Export, Open Data Folder) are the **Model Tools** submenu, shown with
  `ShowsEditors`. A new download or check belongs in the Updates flyout row and Settings → Data, not as a menu item.
- **Check for Updates is one command** (`UpdatesViewModel.CheckAllCommand`: the menu, Ctrl+U, the flyout and the top of Settings → Data).
  Sources that are current say nothing; one toast sums up unless something already spoke or a dialog is open. It fetches match data
  that was never downloaded, asks before art (23 MB: Download / Not now / Don't ask again), and makes the first-run offer if it was
  never made. Offline it only asks the connection to retry.
- **Rows:** a current row's button is Check; the dialog-opening download is a link. Match data and art that were never fetched read
  "not downloaded" (`UpdateState.NotDownloaded`, neutral dot), not "Up to date". Titles stay Formulas, Match data and Art, the Match
  page's own names; each row says in plain words what it is (`UpdateRowViewModel.About`).
- **A turned-down first-run offer is repeated once** as the "Downloads" chip after 3 days (`RemindAboutDownloads`).
- Automatic updates say what changed in toasts; the flyout's "What changed recently" opens Help → Recent Messages.

## Not done, and ideas

- A persistent update history (what changed, when, across restarts): Recent Messages keeps only the last 20 toasts of the session.
- Row titles in plainer words would have to change the Match page's "Formula + match data" too.
- A first-run dialog that sets the update modes (Automatic / Tell me / Off) up front, rather than Settings → Data afterwards.
- Offering the Model Tools to someone who edits CSVs by hand without wanting the editors' tabs.
