# WP01 Startup messages are kept; an unreadable settings.json is kept; unhandled exceptions are logged

Status: done
Effort: S · Risk: low · Depends on: none · Wave: A
Touches: `src/Services/NotificationService.cs`, `src/Services/JsonSettingsService.cs`, **`App.axaml.cs`** (`InstallGlobalExceptionHandlers`), `src/Services/Contracts/INotificationService.cs` (only if the interface changes)
Docs to update: `docs/architecture.md` (sections 3 and 6)

## Goal

Nothing the app says during startup is lost before the window can show it, a settings file that can't be read is kept instead
of being overwritten with defaults, and an exception that escapes every handler leaves a line in the log.

## Why / premise check

1. `NotificationService` is a plain `Subject<Notification>`; `NotificationOverlayViewModel` subscribes in its constructor. The
   overlay is created when `MainWindowViewModel` is resolved, which is after `DataService.Initialize()`, so a message sent from
   `Initialize` (for example "Couldn't load the data folder …; using the default one instead") reaches nobody ✔ (read the
   three files).
2. `JsonSettingsService.LoadAsync` catches every exception, logs it and leaves the defaults in place; the next `Update(...)`
   writes the defaults over the unreadable file, and `DataRoot` and every other setting are gone ✔.
3. `InstallGlobalExceptionHandlers` covers the Rx default handler, the dispatcher and `TaskScheduler.UnobservedTaskException`,
   not `AppDomain.CurrentDomain.UnhandledException` ✔.

Re-check: search for `Subject<Notification>`; read `LoadAsync`; search for `UnhandledException` in `App.axaml.cs`. If a replay
or buffer already exists, or the settings file is already kept, report instead.

## Read first

`docs/architecture.md` sections 3 and 6; the three files above; `NotificationOverlayViewModel`; `LoggingService` (does it write
each line to the file at once? an unhandled-exception line must survive the process dying).

## Scope

In:
- Messages sent before anyone has subscribed are held and delivered to the **first** subscriber (not replayed to every later
  one), so tests that subscribe after emitting don't see old messages. Bound the hold (about 8 messages, about 1 minute).
- On a settings file that can't be read: copy it to `settings.bad-<yyyyMMdd-HHmmss>.json` beside it (best effort, keep the newest
  3), log it, and tell the user in a notification ("settings.json couldn't be read, so defaults are in use; the old file is kept as
  …"). Defaults still apply.
- Log `AppDomain.CurrentDomain.UnhandledException` (message, exception, whether the process is terminating).

Out: showing a dialog; restoring settings automatically; anything about the data folder's CSVs (WP04).

## Suggested design

- A small pending queue inside `NotificationService` drained by the first subscription (`Observable.Create`), after which it is
  an ordinary subject.
- `JsonSettingsService` takes an optional `INotificationService` (the internal test constructor keeps working with `null`); the
  notification goes through the held-message path above, so it reaches the overlay even though the file is read before the window
  exists.
- The handler line: `loggingService.Error("Unhandled exception" + (e.IsTerminating ? " (the app is closing)" : ""), e.ExceptionObject as Exception)`.

## Tests

- New `NotificationServiceTests`: messages sent before the first subscriber arrive once, in order; a second subscriber gets only
  new ones; the hold is bounded.
- `SettingsTests`: an unreadable `settings.json` ends up as `settings.bad-*.json`, defaults are used, a notification is sent, only
  3 copies are kept, and a later `Update` doesn't touch the bad copy.
- Run: `NotificationService`, `Settings`, `Logging`, and the UI tests that look at toasts (`StatusBar`, `Menu`).

## Acceptance criteria

- [ ] A message sent before the overlay exists is shown when it appears (test).
- [ ] An unreadable `settings.json` is preserved and the user is told (test).
- [ ] `AppDomain.UnhandledException` is logged.
- [ ] `docs/architecture.md` sections 3 and 6 describe the held messages and the handler.

## Conflicts

`NotificationService.cs` is also edited by WP03 (durations, recent messages): do WP01 first. `App.axaml.cs` is edited a little by
WP02 and WP15.

## Notes

The replay-to-everyone alternative (`ReplaySubject`) is simpler but replays old messages into later subscribers; that's why the
design above delivers to the first only.
