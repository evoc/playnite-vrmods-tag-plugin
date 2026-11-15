# Conventions (read before changing behavior)

> Part of [AGENTS.md](../AGENTS.md).

**References:** only `Playnite.SDK.dll`, as a direct file reference. **Never**
reference non-SDK Playnite assemblies (`Playnite`, `Playnite.Common`) — the
host provides them and referencing them breaks loading.

**Persistence:**

- Tag changes: mutate `game.TagIds` **and** call
  `api.Database.Games.Update(game)` — in-place mutation alone is NOT
  persisted. Bulk changes: wrap in `using (api.Database.BufferedUpdate())`
  (one commit, no per-item DB events — without it, a reset
  over a large library takes minutes instead of seconds).
- Settings: two-class pattern — `VrModTagSettings` (data; `ObservableObject`;
  parameterless ctor required for `LoadPluginSettings<T>` deserialization) +
  `VrModSettingsViewModel` (implements `ISettings`, wraps the data class).
  **`CancelEdit` must restore values IN PLACE** (`RestoreEditableValues`) —
  swapping the `Settings` reference to the `BeginEdit` clone is a data-loss
  bug (a later Save writes the clone's stale decisions over the file).
  `EndEdit` = save + refresh derived lists. See `VrModTagSettings.cs` for the
  working `BeginEdit`/`CancelEdit`/`EndEdit` pattern.
- **Decisions** (settings `config.json`): one key per (source
  listing, game Id) — `Accepted` / `Rejected` / `Pending`. The key is
  `sourceId||<listing identity>||{gameId:D}` and is built in exactly one
  place, `VrModEntry.MakeDecisionKey(sourceId, gameId)`: `steam:{AppId}`
  when the listing names a Steam App ID, else `url:{host}{path}` when it
  has its own page (scheme, query and fragment dropped, so an http→https
  switch or a tracking parameter is not a new listing), else the display
  name when the source offers nothing else. App ID outranks the URL
  because a slug is usually derived from the very title a site renames.
  The decision covers all categories of that mapping. **A listing rename
  is no longer a re-review** where the listing has an App ID or a page
  of its own; for the sources that publish bare name lists (VorpX,
  MrNlceHub, MrSurvivorHub, LukeRoss, ReFramework) it still is, on
  purpose — nothing in such a response separates a rename from a removal,
  so the previous match is questionable once the name is gone. Settings
  accept only built keys (`Get/Set/ClearDecisionByKey`) and reject an
  empty one, because a decision filed under `""` is one no lookup can read
  again. `VrModReviewMatchEntry` carries the key its row was read from,
  so the dialog's Apply writes to that mapping instead of re-deriving
  a name-shaped guess. Decisions stored before keys carried an identity
  move onto the identity key through `VrModTagSettings.TryRekeyDecision`,
  which runs during a scan (the only place holding an entry whose name
  is the one the old key was built from) and takes the decision, its
  timestamp and its tag baseline with it; if both keys are present
  the identity-shaped one wins and the legacy duplicate is dropped so
  it cannot be counted twice. Measured on this machine's 816 stored
  decisions: 605 (74 %) sit on sources that offer an identity, 211 (26
  %) stay name-shaped by design. Removing a game's VRMod* category tags
  clears its decisions so the match is re-evaluated on the next scan.
- **Clean uninstall:** Playnite has no extension-uninstall hook. The
  uninstall is *detected* in `OnApplicationStopped` by reading Playnite's
  uninstall queue (`extinstalls.json`) and must run **before**
  `_tagService.Dispose()` — the cleanup needs the live database handles
  (never in `Dispose()`; it may run too late). `UninstallCleanup()` = clear
  decisions → `RemoveAllVrModTags()` (tags + owned links, one buffered pass)
  → remove the `VRMod*` tag definitions → delete the settings file.

**Matching:**

- Steam App ID match preferred; normalized name match as fallback.
  An App ID is a *matching* signal for every source that carries
  one, and the *decision identity* only where a listing is
  one game — see **Decisions** above for the identity order.
- A **conflicting** App ID refuses a pairing the names would have
  made (both sides publish one and none is shared); silence on either
  side is never a refusal, so listings from sources that publish
  no App ID and library games with no Steam link match exactly
  as they always did. A refused pairing is still surfaced as a
  **Suggested** row, flagged with the disagreement and ranked last.
- A pairing that scores 1.00 **only** because the two records publish the same
  App ID is never auto-accepted: `MatchScoreFor` (what a pairing is worth)
  and `NameScoreFor` (whether the plugin may act on it) are separate, and
  only an exact name match applies tags without a decision. App-ID matches
  arrive as **Pending** rows in the scan pass and the review pass alike.
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
- **Ownership:** the plugin treats the (source DisplayName, deep-link
  URL) pair as owned and only ever removes links it owns — **foreign
  links (Steam store, Reddit, Epic, …) are never touched.**
- Links follow the decision: added on accept, removed on reject / revert to
  pending / source disabled / per-source Links toggle off / reset —
  reconciled on **every scan** and on **every review save**.

**Scan:**

- **Data-loss guard:** if ANY enabled source failed (network/timeout/parse),
  stale-tag removal is **skipped for that scan** — a failed source's
  games must not lose their tags. An empty catalog is a valid
  answer ("No data"), not a failure, and does not gate removal.
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
- Show dialogs on the UI thread via `UIDispatcher`;
  Playnite SDK is not fully thread-safe.
- Fire-and-forget scan `Task`s must have a `ContinueWith`
  fault observer — unobserved faults are silent.
- WPF targets .NET Framework 4.6.2 (e.g.
  `element.ToolTip = value`, not a static setter).

**Runtime constraints:** HttpClient timeout 30 s; TLS protocol
is Playnite's process-wide choice — the plugin never sets
`ServicePointManager.SecurityProtocol` (Playnite sets it for the whole
process, and a plugin overriding it changes every other plugin's traffic);
game names capped at 200 chars; Levenshtein similarity returns 0.0
above 200 chars; scan interval max 525600 min (365 days, 0 = never) and
cache duration max 43200 min (30 days) (validated in `VerifySettings`).
Transient HTTP failures (transport errors, 5xx, 429) are retried bounded
by `MaxHttpAttempts` (3, doubling backoff 1 s/ 2 s) in `FetchStringAsync`;
definitive 4xx are NOT retried. A server's `Retry-After` (429/503)
replaces that backoff when it asks for ≤ 60 s (`MaxServerRequestedWait`);
a longer ask fails the source for that run after **one** request instead
of retrying against an explicit "not yet". Requests are queued per host
and capped in total by `HttpHostGate` (1 in flight per host, 4 overall).
Each host's `robots.txt` is fetched **once per process** by `RobotsPolicy`
and the `Crawl-delay` it declares for `User-agent: *` becomes the minimum
spacing for that host — capped at `HttpHostGate.MaxCrawlDelay` (30 s).
**`Disallow` is not enforced, on purpose:** several catalog sites disallow
generic crawlers on exactly the pages this plugin must read, so path rules
would silently switch sources off. Scanned text is capped on `VrModEntry`
(`GameName` 200, `Author` 100, `AdditionalInfo` 400, `SteamAppId` 32).

## Logging

- **Info:** scan lifecycle, per-scan summary,
  source failures, settings changes, install events.
- **Debug:** per-entry / per-match detail. (One healthy
  scan on a large library produced ~5,000 per-entry lines
  before this policy — never log per-entry at Info/Warn.)
- **Warn:** anomalies only (source returned 0 entries,
  scan failed, stale-removal skipped, notification failed).
- Every interpolated user-facing value (game name, entry name, tag
  name) goes through `SafeLogText()` — escapes newlines/tabs so one
  odd name cannot forge extra log lines. No telemetry; logs stay
  local (all network calls are GETs to the fixed source hosts).

## ⚠️ Temporary / fragile parsers

The **RototRobot** and **UeVR Profiles** parsers are temporary scraping
implementations over undocumented site structures (HTML `div` structure;
Firebase Firestore REST with the API key scraped from the site's JS bundle).
**PCVR Central** also decodes a server-rendered RSC payload (no stable API).
See the `TEMPORARY FIX` blocks in `source/Parsers/` — replace with stable
feeds when available; expect breakage on site redeploys.
