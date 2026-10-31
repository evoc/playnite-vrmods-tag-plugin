# PlayniteVRmods — Agent Guide

> **For humans:** [docs/README.md](docs/README.md) is the user-facing documentation
> (it is shipped inside the installed package and opened by the in-plugin
> Help button).
> **For agents/contributors starting fresh:** this file is the entry point.
> Design, data model, UML: [architecture.md](architecture.md) · Playnite database
> structure: [database.md](database.md) · External URLs, SDK pointers, NuGet
> versions: [external-references.md](external-references.md).

A C# (.NET Framework 4.6.2, WPF) Playnite plugin that scrapes twelve community
VR-mod sources, matches their listings to your Playnite library (Steam App ID
first, fuzzy name match as fallback), applies `VRMod*` tags to matched games,
and — for accepted matches — records the source link in the game's Links
section.

## Repo layout

```text
source/                       # The plugin (Playnite.VRMods.csproj, net462, WPF)
├── VrModTagPlugin.cs        # Main plugin class (MetadataPlugin + lifecycle hooks)
├── VrModMetadataProvider.cs # "Download Metadata" flow: GetTags + GetLinks
├── VrModTagService.cs       # Scan, match, tag apply/remove, source-link reconcile
├── VrModTagSettings.cs      # Settings data class + settings view model (ISettings)
├── VrModNameMatcher.cs      # Fuzzy matching (normalize, no-substring, identifier guard)
├── VrModCategories.cs       # Tag-name vocabulary (single source of truth for tag names)
├── VrModEntry.cs            # One source entry (game name, Steam ID, category, deep link…)
├── IVrModSource.cs          # Data source interface
├── VrModSourceRegistry.cs   # Source registration / factory
├── BaseVrModParser.cs       # Shared HTTP fetch + name-match helpers for parsers
├── VrModHelp.cs             # Help button (opens the docs/README.md shipped in the package)
├── WebUrl.cs                # http(s) URL validation helper
├── HttpHostGate.cs          # Per-host request queue, global in-flight cap, crawl-delay spacing
├── RobotsPolicy.cs          # One robots.txt lookup per host per run → Crawl-delay (pace, not path rules)
├── MatchDecision.cs         # Accepted / Rejected / Pending
├── VrModReviewMatchEntry.cs # Review dialog row model
├── VrModReviewDialog.xaml(.cs)    # Review dialog (filter, sort, columns, decisions)
├── ErrorToBrushConverter.cs # WPF converter (source health → brush)
├── Parsers/                 # One parser per source (11 files, see below)
├── Settings/VrModSettingsView.xaml(.cs)  # Settings page (Sources grid + scan settings)
├── Styles/PlayniteTheme.xaml# Playnite-like WPF styles
├── Properties/AssemblyInfo.cs   # InternalsVisibleTo(tests) only (no version here)
├── icon.png                 # Plugin icon
└── extension.yaml           # Playnite manifest (Name, Id, Version)
```

```text
docs/                         # User manual (shipped in the package, Help button target)
├── README.md               # Manual index (links to the pages below)
├── getting-started.md      # First scan, the review dialog, decisions
├── review.md               # Review dialog reference (columns, filters, menus)
├── settings.md             # Settings page reference (Sources grid, scan settings)
├── tags.md                 # Tag vocabulary and link behavior
├── for-developers.md        # Dev setup (devel extensions), troubleshooting
└── img/                    # Figures referenced by the pages
```

```text

tests/                      # Playnite.VRMods.Tests.csproj (NUnit + NSubstitute)
├── TestHelpers/FakeItemCollection.cs  # Fake IItemCollection + FakeBufferedUpdate + NullLogger
├── Unit/                   # One fixture per class (service, settings, VM, dialog, …)
├── Parsers/                # One fixture per source + snapshots/ recorded fixtures
└── Integration/IntegrationTests.cs

```

Parsers (in the source ranking from external-references.md; `SteamCuratorParser`
is one file, instantiated twice — once per Steam curator):
`PcvrCentralParser`, `UeVrProfilesParser`, `RototRobotVrModParser`,
`MrNlceHubParser`, `CompoundVrParser`, `MrSurvivorHubParser`,
`SteamCuratorParser`, `ReFrameworkParser`, `Flat2VrParser`, `VorpXParser`,
`LukeRossParser`.

## Build & test (Windows only)

- **.NET Framework 4.6.2 + WPF → Windows-only.** .NET SDK 8 (`global.json` pins
  `8.0.421`; any 8.0.4xx with xx ≥ 421 works). Internet access to nuget.org for
  restore (PlayniteSDK 6.15.0, NUnit, NSubstitute, …). No Playnite install is
  needed to build or test.

```text

dotnet build    # root Playnite.VRMods.sln builds plugin + tests; must be 0 errors AND 0 warnings
dotnet test     # runs the full suite (same solution)
```

(The root `Playnite.VRMods.sln` references both `source/Playnite.VRMods.csproj`
and `tests/Playnite.VRMods.Tests.csproj`, so the bare commands work from the
repository root. `dotnet build source/Playnite.VRMods.csproj` still works to
build just the plugin.) Bare commands build **Debug**; everything that has to
match CI here adds `-c Release` (that is the configuration whose analyzers are
on, and the warning gates below are run against it).

- **The build must stay warning-free.** Fix warnings; do not suppress them
  unless the SDK forces it (then `#pragma warning disable` + reason comment).
  A plain `dotnet build`/`dotnet test` does NOT fail on a warning, so the
  release job checks them by name: `Code analysis (release gate)` in
  `.github/workflows/release.yml` fails on any `: warning CA` line.
- **Version:** the git tag (`vX.Y.Z`) is the version. GitVersion injects it
  into the assembly at build time (`GitVersion.MsBuild` package; config in
  `GitVersion.yml`). `Version` in `source/extension.yaml` is the fallback for
  local builds — `tools/Build-Release.ps1` uses it unless `-Version` is
  passed. (`Properties/AssemblyInfo.cs` carries no version — only
  `InternalsVisibleTo`.)
- **Release packaging (`.pext`)** — `tools/Build-Release.ps1`. Requirements it
  enforces: Release
  publish; **`Playnite.SDK.dll` must NOT be in the package** (the host
  provides it — Playnite's `VerifyExtensionPackage` rejects packages
  containing it); manifest + module + icon present; no `Playnite`/
  `Playnite.Common` assembly references; `docs/` (the user manual incl. its images) is shipped
  into the package (csproj `<Content>` copy) so the installed extension's
  Help button opens a version-matched manual (Playnite extracts every package file into
  the extension dir and wipes the whole dir on uninstall, so no manifest
  declaration is needed). The 10.x package extension is `.pext` (not `.ext`
  — that is 9.x). Install: double-click or
  `Playnite.DesktopApp.exe --installext <path>`; applied on next startup.
- **Releasing (GitHub Actions)** — `.github/workflows/release.yml` runs on
  every push to main on `windows-latest` (net462/WPF builds only on
  Windows): computes the next version from the Conventional Commits since
  the last tag (config in `GitVersion.yml`: `feat:`→minor, `fix:`/`perf:`
  →patch, `<type>!:` or `BREAKING CHANGE:`→major; first push without any
  tag→1.0.0), creates+pushes the tag `v<version>`, runs the full test suite,
  packages with `tools/Build-Release.ps1 -Version <version>`, and attaches
  `dist/VRMod Tags-<version>.pext` to the GitHub Release. **Every push to
  main is a release** — no prereleases; feature branches are built
  locally, never released. Procedure: merge to main with Conventional
  Commit messages — that's the whole procedure; the commit prefix decides
  the bump (a wrong prefix silently under-bumps the release; the
  `commitlint` PR gate in `.github/workflows/commitlint.yml` catches that).
  Manual re-runs: "Run workflow" with an optional explicit version.
  Users install from the repo's Releases page —
  [github.com/evoc/playnite-vrmods-tag-plugin](https://github.com/evoc/playnite-vrmods-tag-plugin).
  The stable latest-asset URL is
  `https://github.com/evoc/playnite-vrmods-tag-plugin/releases/latest/download/VRMod%20Tags-<version>.pext`
  (asset name `VRMod Tags-<version>.pext`; the space is `%20` in a URL).

## Test suite invariants

- No `Thread.Sleep` / wall-clock dependence (time-based tests use fixed
  `DateTime` values). No shared mutable static state that tests assert on.
  Per-test `SetUp` isolation. `[Test]` + `[TestCase]` data-driven style.
- `VrModTagService` is fully injectable (`IPlayniteAPI` via NSubstitute,
  `FakeItemCollection<Game|Tag>` for the database) — test the service, not
  the plugin.
- **Coverage boundary (intentional):** `VrModTagPlugin` orchestration
  (startup scan, scheduled-scan timer, scan-progress UI, notifications) is NOT
  unit-tested — the plugin ctor constructs the real service and registry
  (no injection), so a unit test would trigger real network fetches. That
  path is covered by the integration tests that drive the service directly,
  by one plugin-ctor test (corrupt settings), and by live verification.
  **Do NOT add unit tests that construct `VrModTagPlugin` with the real
  registry.**
- Parsers are tested against recorded fixtures in `tests/Parsers/snapshots/`
  (plus page-token and snapshot tests for paginated sources). The UeVR test
  hits the real Firestore API.

## Conventions (read before changing behavior)

**References:** only `Playnite.SDK.dll`, as a direct file reference. **Never**
reference non-SDK Playnite assemblies (`Playnite`, `Playnite.Common`) — the
host provides them and referencing them breaks loading.

**Persistence:**

- Tag changes: mutate `game.TagIds` **and** call
  `api.Database.Games.Update(game)` — in-place mutation alone is NOT
  persisted. Bulk changes: wrap in `using (api.Database.BufferedUpdate())`
  (one commit, no per-item DB events — without it, a reset over a large
  library takes minutes instead of seconds).
- Settings: two-class pattern — `VrModTagSettings` (data; `ObservableObject`;
  parameterless ctor required for `LoadPluginSettings<T>` deserialization) +
  `VrModSettingsViewModel` (implements `ISettings`, wraps the data class).
  **`CancelEdit` must restore values IN PLACE** (`RestoreEditableValues`) —
  swapping the `Settings` reference to the `BeginEdit` clone is a data-loss
  bug (a later Save writes the clone's stale decisions over the file).
  `EndEdit` = save + refresh derived lists. See `VrModTagSettings.cs` for the
  working `BeginEdit`/`CancelEdit`/`EndEdit` pattern.
- **Decisions** (settings `config.json`): one key per
  (source listing, game Id) — `Accepted` / `Rejected` /
  `Pending`. The key is `sourceId||<listing identity>||{gameId:D}` and is built in
  exactly one place, `VrModEntry.MakeDecisionKey(sourceId, gameId)`:
  `steam:{AppId}` when the listing names a Steam App ID, else
  `url:{host}{path}` when it has its own page (scheme, query and fragment dropped, so an
  http→https switch or a tracking parameter is not a new listing), else the display name
  when the source offers nothing else. App ID outranks the URL because a slug is usually
  derived from the very title a site renames. The decision covers all categories of that
  mapping. **A listing rename is no longer a re-review** where the listing has an App ID or
  a page of its own; for the sources that publish bare name lists (VorpX, MrNlceHub,
  MrSurvivorHub, LukeRoss, ReFramework) it still is, on purpose — nothing in such a
  response separates a rename from a removal, so the previous match is questionable once
  the name is gone. Settings accept only built keys (`Get/Set/ClearDecisionByKey`) and
  reject an empty one, because a decision filed under `""` is one no lookup can read
  again. `VrModReviewMatchEntry` carries the key its row was read from, so the dialog's
  Apply writes to that mapping instead of re-deriving a name-shaped guess.
  Decisions stored before keys carried an identity move onto the identity key through
  `VrModTagSettings.TryRekeyDecision`, which runs during a scan (the only place holding an
  entry whose name is the one the old key was built from) and takes the decision, its
  timestamp and its tag baseline with it; if both keys are present the identity-shaped one
  wins and the legacy duplicate is dropped so it cannot be counted twice. Measured on this
  machine's 816 stored decisions: 605 (74 %) sit on sources that offer an identity, 211
  (26 %) stay name-shaped by design. Removing a game's VRMod* category tags
  clears its decisions so the match is re-evaluated on the next scan.
- **Clean uninstall:** Playnite has no extension-uninstall hook. The
  uninstall is *detected* in `OnApplicationStopped` by reading Playnite's
  uninstall queue (`extinstalls.json`) and must run **before**
  `_tagService.Dispose()` — the cleanup needs the live database handles
  (never in `Dispose()`; it may run too late). `UninstallCleanup()` = clear
  decisions → `RemoveAllVrModTags()` (tags + owned links, one buffered pass)
  → remove the `VRMod*` tag definitions → delete the settings file.

**Matching:**

- Steam App ID match preferred; normalized name match as fallback. An App ID is a
  *matching* signal for every source that carries one, and the *decision identity* only
  where a listing is one game — see **Decisions** above for the identity order.
- **No substring matching** — a proper substring scores -1 (kills
  DOOM / DOOM Eternal). On top, a numeric/roman **identifier guard**:
  different edition identifiers score -1 (*Street Fighter 6* vs *SF V*,
  *DMC 5* vs *DMC HD*); equal values still match (*GTA 5* ≡ *GTA V*).
- Fuzzy threshold is user-configurable (default 0.8). Score 1.0 = accepted;
  ≥ threshold = `Pending` (review); below = silently skipped.
- **All tag names start with `VRMod` and come from `VrModCategories`** — the
  single source of truth; no hardcoded tag strings elsewhere.

**Source links (game Links section):**

- Only **saved (Accepted) matches** get a link; pending/rejected get none.
- Deep link when the parser provides one, otherwise the source's website.
- **Ownership:** the plugin treats the
  (source DisplayName, deep-link URL) pair as owned and only ever removes
  links it owns — **foreign links (Steam store, Reddit, Epic, …) are never
  touched.**
- Links follow the decision: added on accept, removed on reject / revert to
  pending / source disabled / per-source Links toggle off / reset —
  reconciled on **every scan** and on **every review save**.

**Scan:**

- **Data-loss guard:** if ANY enabled source failed (network/timeout/parse),
  stale-tag removal is **skipped for that scan** — a failed source's games
  must not lose their tags. An empty catalog is a valid answer ("No data"),
  not a failure, and does not gate removal.
- Auto-apply vs review is selected by the `reviewCallback` argument:
  `RunFullScanAsync` / `ScanGameAsync` with `reviewCallback == null`
  auto-apply pending matches; with a callback, they are collected and shown
  in the review dialog (the user's Save applies them). Startup/auto-scan
  passes a callback (bell notification); single-game install and "Scan Now"
  behavior follows the same signature — see `VrModTagPlugin.cs`.
- Indeterminate checkboxes in the review dialog count as **Rejected**.

**UI / threading:**

- Review-dialog `Owner` must be a **shown** window
  (`Dialogs.GetCurrentAppWindow()`); `Dialogs.CreateWindow(...)` returns an
  unshown window and setting it as `Owner` throws.
- Show dialogs on the UI thread via `UIDispatcher`; Playnite SDK is not fully
  thread-safe.
- Fire-and-forget scan `Task`s must have a `ContinueWith` fault observer —
  unobserved faults are silent.
- WPF targets .NET Framework 4.6.2 (e.g. `element.ToolTip = value`, not a
  static setter).

**Runtime constraints:** HttpClient timeout 30 s; TLS protocol is Playnite's
process-wide choice — the plugin never sets
`ServicePointManager.SecurityProtocol` (Playnite sets it for the whole
process, and a plugin overriding it changes every other plugin's traffic);
game names capped at 200 chars; Levenshtein similarity returns 0.0
above 200 chars; scan interval max 525600 min (365 days, 0 = never) and cache duration max 43200 min (30 days)
(validated in `VerifySettings`). Transient HTTP failures (transport errors,
5xx, 429) are retried bounded by `MaxHttpAttempts` (3, doubling backoff 1 s/
2 s) in `FetchStringAsync`; definitive 4xx are NOT retried. A server's
`Retry-After` (429/503) replaces that backoff when it asks for ≤ 60 s
(`MaxServerRequestedWait`); a longer ask fails the source for that run after
**one** request instead of retrying against an explicit "not yet". Requests are
queued per host and capped in total by `HttpHostGate` (1 in flight per host, 4
overall). Each host's `robots.txt` is fetched **once per process** by
`RobotsPolicy` and the `Crawl-delay` it declares for `User-agent: *` becomes the
minimum spacing for that host — capped at `HttpHostGate.MaxCrawlDelay` (30 s).
**`Disallow` is not enforced, on purpose:** several catalog sites disallow
generic crawlers on exactly the pages this plugin must read, so path rules would
silently switch sources off. Scanned text is capped on `VrModEntry` (`GameName`
200, `Author` 100, `AdditionalInfo` 400, `SteamAppId` 32).

## Logging

- **Info:** scan lifecycle, per-scan summary, source failures, settings
  changes, install events.
- **Debug:** per-entry / per-match detail. (One healthy scan on a large
  library produced ~5,000 per-entry lines before this policy — never log
  per-entry at Info/Warn.)
- **Warn:** anomalies only (source returned 0 entries, scan failed,
  stale-removal skipped, notification failed).
- Every interpolated user-facing value (game name, entry name, tag name) goes
  through `SafeLogText()` — escapes newlines/tabs so one odd name cannot
  forge extra log lines. No telemetry; logs stay local (all network calls
  are GETs to the fixed source hosts).

## ⚠️ Temporary / fragile parsers

The **RototRobot** and **UeVR Profiles** parsers are temporary scraping
implementations over undocumented site structures (HTML `div` structure;
Firebase Firestore REST with the API key scraped from the site's JS bundle).
**PCVR Central** also decodes a server-rendered RSC payload (no stable API).
See the `TEMPORARY FIX` blocks in `source/Parsers/` — replace with stable
feeds when available; expect breakage on site redeploys.

## Docs & screenshots: keep in sync with the GUI

**General rule:** a change to the user-facing GUI — the **settings page**
(`Settings/VrModSettingsView.xaml`) or the **review dialog**
(`VrModReviewDialog.xaml`) — is **not done** until the **documentation and its
screenshots** are updated to match the new UI.

- **Docs:** update the affected `docs/` page(s) — `review.md` / `settings.md`
  (and the `README.md` index if a page is added/renamed) — to describe the new
  controls, hotkeys, tooltips, columns, and filters.
- **Screenshots:** re-capture the `docs/img/` figures those pages reference so
  they show the **current** UI, not the old one — otherwise the change ships a
  stale screenshot to every user (the manual, incl. images, is packaged with the
  `.pext`).
- A GUI change with no doc/screenshot update is **incomplete**: leave it as open
  work, do not mark it done.

## Links stay inside this repo

This repo is the artifact: it is cloned, it is packaged into the `.pext`, and it
is meant to be published. So every link and every path in it resolves to something
a clone will have.

- A relative link may walk up from `docs/` to the root - `docs/for-developers.md`
  links `../AGENTS.md` and `../architecture.md` - because that stays inside the
  repo. What is broken is a target above the repository root: on someone else's
  machine there is nothing there, and a link that leaves the repo reads as intact
  to whoever wrote it.
- The same holds for paths in prose and in code comments. They break at the
  moment someone else checks the repo out, and they describe one machine's
  directory layout in a repo meant for everyone. Name an external tool by what it
  does - "an out-of-repo GUI harness that instantiates this real dialog" - not by
  where it happens to sit on one developer's disk.
- Naming a document is not linking to it. `[architecture.md](architecture.md)`
  resolves nowhere in a repo that has no `architecture.md`, and looks fine while
  reading. Either write a link that resolves, or use inline code for a bare
  reference.

## Threading contract

Three kinds of thread touch this plugin: **Playnite's UI thread** (its database
collection events, the settings page, notification clicks), **thread-pool
threads** (every scan, the new-game drain), and **timer threads** (the scan
interval, the new-game debounce).

**What may hold what:**

- `_newGameLock` (`VrModTagService`) is a **leaf lock**: held only for HashSet and
  queue work on that class's own fields, never while calling Playnite
  (`Database.*`), the UI dispatcher, `Timer.Change`, network, or logging. Both
  paths that used to enumerate `Database.Games` inside it (`SeedKnownGames`,
  `NotifyLibraryUpdated`) now snapshot first and merge under the lock. Measured
  reason, not style: that walk is Playnite's code over a LiteDB result set, and
  Playnite raises its library events on Playnite's own thread. With a 20-row walk
  at 60 ms per row, the event thread waited **1 131 ms** behind that lock before
  the change and **< 400 ms** after it (`tests/Unit/VrModThreadingContractTests.cs`);
  on a 3 000-game library the wait is seconds, on the thread Playnite needs for
  its own UI.
  Take it with **`using (NewGameLock())`**, never `lock (_newGameLock)`: the scope
  records the hold per thread, which is what makes the hop guard below able to see
  it. A raw `lock` statement on the field still locks, but it is invisible to the
  guard, so the guard's promise only covers holds taken this way
  (`tests/Unit/VrModUiHopGuardTests.cs` checks the scope really is the same
  mutual exclusion the ten `lock` statements it replaced had).
- The single-scan gate `_scanInFlight` is an `Interlocked` flag, not a lock, and is
  never taken while `_newGameLock` is held.
- The service's only UI hops (`RunOnUiThread`, required because Playnite's per-game
  `Links` is an `ObservableCollection` bound to a WPF `CollectionView`) run from
  scan threads that hold neither lock, and they carry **only the collection
  mutation**: link reconciliation computes a `LinkPlan` on the calling thread
  (catalog match, stored decisions, desired links) and hops for the `game.Links`
  edits. It used to marshal the whole method, which meant the catalog walk and one
  decision lookup per cached entry, on Playnite's UI thread, per game.
  Before marshaling, `RunOnUiThread` calls `EnsureUiHopIsSafe()`, which refuses a hop
  from a thread holding the new-game lock (a log line and an
  `InvalidOperationException`).
  Why that is a rule and not a caution: Playnite runs a progress action on a worker
  task and shows its window with `ShowDialog` (`ProgressViewViewModel.ActivateProgress`
  → `window.CreateAndOpenDialog(this)`), so the UI thread *does* pump while a worker
  waits in `Dispatcher.Invoke` — and what it pumps includes Playnite's library-changed
  handler, which in this plugin takes `_newGameLock`. A worker holding that lock and
  waiting on the dispatcher is then waiting on a UI thread waiting on it, and neither
  side can advance: the lock is released only when the hop returns. `Dispatcher.Invoke`
  carries no timeout, and Playnite's own watchdog deliberately stays quiet about it
  ("Dispatcher.Wait may be waiting for the UI thread to become available … we don't
  want to report this as an error"), so a hang there surfaces as a library that stops
  responding, not as a failure anyone downstream can act on. This is the shape that
  once stalled a new-game drain for the whole 15 s timeout the hop carried; the hop
  no longer carries a timeout, so it fails in our own code instead.
- `_decisionSync` (`VrModTagSettings`) is a **leaf lock** over the three persisted
  decision dictionaries. They are plain `Dictionary<>`, written from the UI thread
  (review dialog Apply/Revert, the settings page's Links toggle) and from scan
  threads (`ClearDecisionsForGame` when a game stops matching), and read from both.
  Every accessor in that class takes it, and anything that wants to *enumerate*
  decisions calls `SnapshotMatchDecisions()` instead of walking `MatchDecisions` -
  enumerating a `Dictionary` while another thread inserts a key throws, which in a
  plugin is a settings page that crashes.
  The lock cannot reach the walks that matter, and that is the whole problem here.
  Three readers take those dictionaries without it: `VrModSettingsViewModel.BeginEdit()`,
  which runs `Serialization.GetClone(Settings)` the moment the Add-ons settings page
  opens; the plugin's own saves - `EndEdit()` (the page's Save button) and the review
  dialog's save, the latter inside `RunGlobalProgressFunction`, so on the progress-dialog
  thread rather than the UI thread; and Playnite's `SavePluginSettings` whenever it
  decides to persist the plugin. Playnite does *not* rewrite plugin settings when it exits -
  only those two paths write the file - but every one of
  them walks the maps through plain property getters, on a thread that is not a scan
  thread, and a scan thread is exactly where decisions get written.
  So the maps are **published copy-on-write** - a writer clones the
  published map, mutates the clone, and swaps the clone in, and an instance that has
  been handed out through a getter is never mutated again. Maps a write does not
  actually change are not cloned, so a steady-state scan (every match already
  decided, every timestamp already stored) publishes nothing. Measured on this
  machine, at the live library's 816 stored decisions: an already-known timestamp
  lookup stays 1,5 µs, a first-seen match costs 89 µs (so a first-ever scan pays
  ~75 ms for 850 new matches, against 4,4-6,2 s for the scan itself), and a
  100-decision review-dialog apply pays ~13 ms. What a walk can never get is the
  torn single map: enumerating one while it is being written threw
  `IndexOutOfRangeException` / "destination array is not long enough" on the code
  before this rule, in `tests/Unit/VrModSettingsPublicationTests.cs` run against it.
  The getter keeps returning the live published instance on purpose: Newtonsoft's
  default `ObjectCreationHandling` reuses and fills what a getter returns, so a
  copy-returning getter silently throws away everything loaded from
  `ExtensionsData/<plugin id>/config.json` - that is a test in the same file, not a
  hunch.

**Entry points:** `RunScanWithProgress`, `RunShowGamesScan`, `RunFullScanApplyOnly`
and `RunReviewFromCache` all block their caller (title-bar progress window, modal
review dialog, `GetAwaiter().GetResult()`), so they must not run on Playnite's UI
thread. Until now that rule was a comment. `VrModTagPlugin.RunScanEntryPoint`
enforces it: a caller on the UI thread is moved to the thread pool with a warning
naming the entry point, and **that call returns before the scan finishes** - a
caller that needs the result has to be off the UI thread (the settings page
already is, through `RunScanOnBackground`).

**Progress results are read, not assumed.** `ActivateGlobalProgress` returns a result; the review dialog's own scan/cleanup/save call sites used to drop it, so a cancelled or failed operation looked identical to a completed one (the status line said "Scan completed." and the window closed itself), and Playnite's two extension guides both tell the caller to inspect that result. `VrModReviewDialog.ProgressFailureText(operation, result)` and `VrModReviewDialog.ReviewDialogMayClose(result)` are the decision half, kept static and unit-tested (`tests/Unit/VrModProgressOutcomeTests.cs`) because a failure must never be a result this dialog closes itself on. What counts as "not completed" is exactly an `Error`, a `Canceled`, or an explicit `Result == false`: the SDK documents the three properties but never says what a successful action leaves in `Result`, Playnite's own wrappers build the result different ways, and its next API generation drops `Result` altogether — so a rule that required `Result == true` would reject successful scans. Both of the dialog's progress windows are currently created with `Cancelable: false`, so a cancellation there can only come from Playnite closing the window; the rule covers it anyway, and the tag-removal action already threads `a.CancelToken` into the service, which is the plumbing a cancelable Save would need.

**Matching cost:** per-game catalog matching goes through a cache-versioned index
(`VrModTagService.GetCachedEntriesForGame`) instead of scoring every cached entry
per game. The index visits only entries that could still reach the fuzzy threshold,
using two bounds derived from the matcher itself - the length band
(`VrModNameMatcher.CanReachSimilarity`) and a character-count bound on the longest
common subsequence a Levenshtein similarity of at least the threshold implies. Both
are checked against the exhaustive walk in `tests/Unit/VrModCatalogIndexTests.cs`,
because a prune that silently drops a real match is the whole risk of the design.

**Accepted residual:** a walker that reads the three decision properties one after
another can straddle one write and pair a decision from the new maps with the previous
map's timestamps or tag baselines. That is not a torn map and it does not crash: the file
is still valid JSON, `EnsureDecisionTimestampUtc` re-records a missing timestamp on the
next scan, and a lost tag baseline only means `ComputeTagDelta` reports "no change" for
that one match until the decision is accepted again. Closing it properly would mean
Playnite reading one bundle object instead of three properties, which is not ours to
change. `VrModSettingsViewModel._scanLock` was listed here as a lock held across service
calls; it is not - it guards the `_isScanning` flag check-and-set only, and is released
before the scan task starts.

## Live testing (any machine)

Building/testing needs no Playnite. For live end-to-end verification you need
a Playnite 10.x install:

1. `dotnet publish source\Playnite.VRMods.csproj -c Release -o <some-dir>`
2. Add `<some-dir>` to Playnite's external-extensions list
   (Settings → **For developers** → External extensions; config key
   `DevelExtenions` — a typo in Playnite itself, **do not "fix"** it). The
   value must be a JSON **array of objects**:
   `[{"Selected": true, "Item": "<some-dir>"}]` — a plain string array fails
   deserialization and Playnite won't start.
3. Restart Playnite; the log shows `Loaded ... Playnite.VRMods.dll`.

**Reaching the settings page (live Playnite):** press **F9** to open the
Add-ons window; the plugin's settings page is under **Extensions settings →
Metadata Sources → VRMod Tags** (it lands there because the plugin is a
`MetadataProvider`). From that page, **Full Scan** re-scans every enabled
source without a dialog, and **Review VR Mods** scans then opens the review
dialog for every matched game.

Dev extensions shadow installed extensions with the same manifest Id, so a
dev entry is safe while a release is installed and reversible. Prove which copy a
running instance loaded by re-opening each candidate `Playnite.VRMods.dll` with
`FileShare.None`: the loaded one throws a sharing violation, the shadowed one
stays free. To tell which **build** a folder holds, read the dll's ProductVersion,
because GitVersion writes the full commit into it (`…+Branch.main.Sha.<sha>`). A file
hash alone is not enough: MSBuild can leave a build output untouched across a
repeat `dotnet publish`, so bytes on disk can predate the commit they are
attributed to.

Machine-specific dev setup (launch targets, live library state, GUI-driving notes)
is **not in this repo**.

## CI gates and supply chain

Two workflows, both SHA-pinned (a tag is a moving ref anyone can re-point; a SHA
pin is what makes "the action we ran" mean something). `commitlint.yml` runs at
`permissions: contents: read`; `release.yml` runs at `contents: write` because it
pushes the version tag it creates:

- `.github/workflows/release.yml` (push to main), in the order the steps run:
  vulnerable-package gate (`dotnet restore`, then
  `dotnet list package --vulnerable --include-transitive --format json`, and a
  non-empty finding list fails the release — the bare command prints findings
  and exits 0, so the verdict is read from the JSON) -> resolve version + tag ->
  code-analysis gate -> test gate -> `Build-Release.ps1` -> GitHub release with
  the `.pext` attached. There is no format gate here; that gate is on the pull
  request. Version from Conventional Commits via GitVersion
  (`GitVersion.MsBuild` pinned to exactly `6.8.2`: a `6.*` range lets an
  upstream major silently change how the *version number itself* is computed).
- `.github/workflows/commitlint.yml` (pull request): commit-message gate plus
  the `dotnet format whitespace|style --verify-no-changes` gate (`--verify-changes`
  is not a flag in SDK 8; `--verify-no-changes` is the real one).

`.github/dependabot.yml` opens weekly PRs for `github-actions` and `nuget`
(both projects). `SECURITY.md` is the vulnerability-report channel.

Analyzers: `source/Playnite.VRMods.csproj` sets `EnableNETAnalyzers` with
`AnalysisLevel=latest` + `AnalysisMode=All`. On `net462` the SDK leaves analyzers
**off** by default (it enables them for `net5+`), so this has to be set explicitly:
the plugin parses twelve attacker-chosen websites and the framework's own security
rules have to actually run on it. `AnalysisMode=Security` is not the posture — it
switches off the style/performance and design families, and those families have
named real weaknesses here (`CA1848`, `CA1307`, `CA1051`, `CA1034`).

### Code-analysis policy: clean before pushing

**No branch is ready to merge, and nothing is ready to push, while the plugin
build reports a code-analysis warning.** The gate is the `Release` build of
`source/Playnite.VRMods.csproj`: 0 warnings, 0 errors. Run it before pushing:

```text
dotnet build source/Playnite.VRMods.csproj -c Release -v n                        # what CI runs
dotnet build source/Playnite.VRMods.csproj -c Release -p:TreatWarningsAsErrors=true
```

Both must come back clean. `dotnet build` alone does not fail on a warning, so
`.github/workflows/release.yml` carries a `Code analysis (release gate)` step that
fails the release on any `: warning CA` line — that step is the backstop, not the
policy, and it is not a licence to push a build that fails it.

How a warning may legitimately stop being a warning:

- **Fix it.** Suppression is the last resort, and where the SDK forces it,
  `#pragma warning disable` plus a reason comment at the site.
- **Exclude a family only with a judgement written down.** Exclusions live in
  `.editorconfig` as `dotnet_diagnostic.CAxxxx.severity = suggestion` lines and
  each one carries a sentence saying why that family is off for this plugin — a
  reason, not a count. No exclusion enters `.editorconfig` without it.
- **A broad `catch` (`CA1031`) is not a silenced family.** It is acceptable only
  where being broad is the contract — Playnite's UI handlers, its extension seam,
  settings persistence, best-effort uninstall cleanup — and each such site carries
  a `[SuppressMessage]` with its reason. Everywhere else, catch a named failure
  vocabulary (`IsRequestPlumbingFailure` for fetch plumbing, `IsJsonShapeFailure`
  for JSON reads, `IsPathOrFileFailure` for settings and uninstall-queue files),
  each pinned by a test that states what it absorbs and what it must rethrow.

The full sweep — every family, exclusions included — is a **work list, not a gate**,
and it is a local command: `.editorconfig` severities win over `-p:AnalysisLevel`,
so it needs those lines commented out, and they must be back in place before
anything is committed.

```text
dotnet build source/Playnite.VRMods.csproj -c Release -p:AnalysisLevel=latest-All --no-incremental
```

The sweep's output is not a record to keep here: what is fixed is in the code, what
is excluded is in `.editorconfig` with its reason, and what is still open is open
work, not a number in a guide.
