# WP08 Art: fetch once, in parallel, retry, honest size

Status: todo
Effort: M · Risk: low-medium · Depends on: none · Wave: B
Touches: `src/Services/ArtDownloadService.cs`, `src/Services/ArtManifest.cs`, `src/Features/MainWindow/Welcome/WelcomeViewModel.cs` (`ArtSize`), `tests/DeadlockAdvisor.Tests/ArtDownloadServiceTests.cs`, `tests/DeadlockAdvisor.Tests/Fakes/FakeServices.cs` (`FakeDeadlockApi`)
Docs to update: `README.md` ("Art"), `docs/detection_model.md` ("Keeping art current"), `docs/architecture.md` (section 7)

## Goal

The first art download is about a sixth smaller and several times faster, the weekly check is cheaper, and a hiccup mid-download no
longer leaves files missing until the next check.

## Why / premise check

1. The normal hero card is downloaded twice ✔. The "Hero portraits" group (to `assets/heroes`) and the "Hero cards" group (to
   `assets/topbar/_cards/normal`) both pick `icon_hero_card`, the first key of the portraits' key list (`ArtDownloadService`:
   `_heroImageKeys`, the groups array). On a real data folder `assets/heroes/<hero>.png` and `assets/topbar/_cards/normal/<hero>.png` are
   byte-identical: 40 heroes, about 4.6 MB duplicated, and 40 extra conditional requests every week. Re-check with a file compare.
2. Requests are strictly sequential: `RunGroupAsync` awaits `GetBytesIfChangedAsync` one file at a time, about 360 files ✔.
3. No retry: a failed file is listed as "download failed" and tried again only at the next weekly check (hero art missing retries daily) ✔.
4. The welcome dialog says "about 22 MB" (`WelcomeViewModel.ArtSize`) ✔; a real first run is about 28 MB (heroes 4.6, items 7.2, cards 14,
   top-bar verticals 1.5, ranks 1.0 on a real folder), about 24 MB once the duplicate is gone.
5. The CDN doesn't cache the images (`cf-cache-status: DYNAMIC`, no `cache-control`) but serves ETags, so conditional requests work: every
   install hits the origin bucket of a community service, so be frugal ✔ (see the README for the probe commands).

## Read first

`docs/architecture.md` sections 5 and 7; `docs/detection_model.md` "The reference art" and "Keeping art current"; `ArtDownloadService`,
`ArtManifest`, `ArtDownloadServiceTests` (what the fakes can count), `RequestPacer` (the injectable-delay pattern).

## Scope

In:
- Download each distinct image once per run: when a later group resolves to a URL already fetched or confirmed current in this run, copy that
  file (and its manifest entry) instead of requesting it.
- Up to 4 files in flight within a group.
- Retry 5xx and 429 responses twice with backoff (injectable delay for tests).
- `ArtSize` shown in the welcome dialog matches reality.

Out: dropping the top-bar vertical art (a separate decision that needs the corpus tests; it is only a fallback for heroes with no cut
portraits); art sourced anywhere but deadlock-api.com; re-hosting art.

## Suggested design

- A per-run map `url → destination path` filled whenever a file is written or confirmed unchanged (a 304); hand-placed art (no manifest
  entry, "not owned") is never recorded as a source. Groups stay sequential (the copy source must exist); parallelism is within a group.
- **Parallelism pitfall:** keep it on the UI synchronization context: start the per-file tasks from the calling context and bound them with
  a `SemaphoreSlim`, so continuations (and the `IProgress` reports, which mutate view-model properties) stay on the UI thread. Do **not** use
  `Parallel.ForEachAsync`, which continues on pool threads.
- Fold results into the report in id order; keep `MaxConnectionFailures` working with an `Interlocked` counter.
- `ArtManifest` is a plain `Dictionary`: add a lock (its callers may run on any thread in tests).
- Retry: a helper around `GetBytesIfChangedAsync` using an internal constructor that takes `Func<TimeSpan, CancellationToken, Task>`; the public
  constructor delegates (the `RequestPacer` pattern). Don't retry connection failures (`StatusCode: null`): the three-in-a-row abort handles those.
- `ArtSize`: set to the real number after the dedupe (about 24 MB) with the arithmetic in the commit message; or compute it from the manifest of
  a finished download if you find a cheap, honest way.

## Tests

`ArtDownloadServiceTests` (with a fake that counts requests per URL): one request per distinct URL across groups (the second group's file exists
and its manifest entry is right); a second run asks once per distinct file; in-flight never exceeds 4; retry then success; retry exhausted lists
the file; hand-placed art isn't used as a copy source or replaced; the report counts (downloaded/updated/skipped) still add up; progress reaches
the total. Run `ArtDownloadService`, `DataMenu` (the art job tests), `Welcome` (in `DataMenuTests`), `ArtPlaceholder`, and the vision tests that read
the art bank (`TopbarDerivation`, `Detect`).

## Acceptance criteria

- [ ] A first download makes one request per distinct image; both folders have the files.
- [ ] The weekly check makes one conditional request per distinct image.
- [ ] A transient 503 on an image doesn't leave it missing.
- [ ] The welcome dialog's size is within a few percent of the real download.
- [ ] Existing art tests pass; docs updated.

## Conflicts

`ArtDownloadService.cs`: WP09 changes one line (the User-Agent) after this; WP11 changes the top-bar derivation call after that.

## Notes

After this the vertical top-bar images (about 1.5 MB, 40 requests a week) are the next candidate to drop (backlog B11): only with
`VisionCorpusTests` unchanged.
