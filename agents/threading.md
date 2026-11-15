# Threading contract

> Part of [AGENTS.md](../AGENTS.md).

Three kinds of thread touch this plugin: **Playnite's UI thread**
(its database collection events, the settings page, notification
clicks), **thread-pool threads** (every scan, the new-game drain),
and **timer threads** (the scan interval, the new-game debounce).

**What may hold what:**

- `_newGameLock` (`VrModTagService`) is a **leaf lock**: held only for
  HashSet and queue work on that class's own fields, never while calling
  Playnite (`Database.*`), the UI dispatcher, `Timer.Change`, network,
  or logging. Both paths that used to enumerate `Database.Games` inside
  it (`SeedKnownGames`, `NotifyLibraryUpdated`) now snapshot first and
  merge under the lock. Measured reason, not style: that walk is Playnite's
  code over a LiteDB result set, and Playnite raises its library events
  on Playnite's own thread. With a 20-row walk at 60 ms per row, the event
  thread waited **1 131 ms** behind that lock before the change and **< 400
  ms** after it (`tests/Unit/VrModThreadingContractTests.cs`); on a 3 000-game
  library the wait is seconds, on the thread Playnite needs for its own UI.
  Take it with **`using (NewGameLock())`**, never `lock (_newGameLock)`: the
  scope records the hold per thread, which is what makes the hop guard below
  able to see it. A raw `lock` statement on the field still locks, but it
  is invisible to the guard, so the guard's promise only covers holds taken
  this way (`tests/Unit/VrModUiHopGuardTests.cs` checks the scope really
  is the same mutual exclusion the ten `lock` statements it replaced had).
- The single-scan gate `_scanInFlight` is an `Interlocked` flag,
  not a lock, and is never taken while `_newGameLock` is held.
- The service's only UI hops (`RunOnUiThread`, required because Playnite's
  per-game `Links` is an `ObservableCollection` bound to a WPF `CollectionView`)
  run from scan threads that hold neither lock, and they carry **only the
  collection mutation**: link reconciliation computes a `LinkPlan` on the
  calling thread (catalog match, stored decisions, desired links) and hops
  for the `game.Links` edits. It used to marshal the whole method, which
  meant the catalog walk and one decision lookup per cached entry, on
  Playnite's UI thread, per game. Before marshaling, `RunOnUiThread` calls
  `EnsureUiHopIsSafe()`, which refuses a hop from a thread holding the new-game
  lock: it logs `VRMod Tag Service: refused to wait on Playnite's UI thread`
  and throws `InvalidOperationException`. That prefix is what to grep for in
  `extensions.log`; the message itself names no file, because whoever reads
  that log has an installed extension, not a checkout. Why that is a rule and
  not a caution: Playnite runs a progress action on a worker task and shows
  its window with `ShowDialog` (`ProgressViewViewModel.ActivateProgress` →
  `window.CreateAndOpenDialog(this)`), so the UI thread *does* pump while a
  worker waits in `Dispatcher.Invoke` — and what it pumps includes Playnite's
  library-changed handler, which in this plugin takes `_newGameLock`. A worker
  holding that lock and waiting on the dispatcher is then waiting on a UI
  thread waiting on it, and neither side can advance: the lock is released only
  when the hop returns. `Dispatcher.Invoke` carries no timeout, and Playnite's
  own watchdog deliberately stays quiet about it ("Dispatcher.Wait may be
  waiting for the UI thread to become available … we don't want to report this
  as an error"), so a hang there surfaces as a library that stops responding,
  not as a failure anyone downstream can act on. This is the shape that
  once stalled a new-game drain for the whole 15 s timeout the hop carried;
  the hop no longer carries a timeout, so it fails in our own code instead.
- `_decisionSync` (`VrModTagSettings`) is a **leaf lock** over the
  three persisted decision dictionaries. They are plain `Dictionary<>`,
  written from the UI thread (review dialog Apply/Revert, the settings
  page's Links toggle) and from scan threads (`ClearDecisionsForGame`
  when a game stops matching), and read from both. Every accessor
  in that class takes it, and anything that wants to *enumerate*
  decisions calls `SnapshotMatchDecisions()` instead of walking
  `MatchDecisions` - enumerating a `Dictionary` while another thread
  inserts a key throws, which in a plugin is a settings page that
  crashes. The lock cannot reach the walks that matter, and that
  is the whole problem here. Three readers take those dictionaries
  without it: `VrModSettingsViewModel.BeginEdit()`, which runs
  `Serialization.GetClone(Settings)` the moment the Add-ons settings
  page opens; the plugin's own saves - `EndEdit()` (the page's
  Save button) and the review dialog's save, the latter inside
  `RunGlobalProgressFunction`, so on the progress-dialog thread rather
  than the UI thread; and Playnite's `SavePluginSettings` whenever it
  decides to persist the plugin. Playnite does *not* rewrite plugin
  settings when it exits - only those two paths write the file - but
  every one of them walks the maps through plain property getters, on a
  thread that is not a scan thread, and a scan thread is exactly where
  decisions get written. So the maps are **published copy-on-write** -
  a writer clones the published map, mutates the clone, and swaps the
  clone in, and an instance that has been handed out through a getter
  is never mutated again. Maps a write does not actually change are
  not cloned, so a steady-state scan (every match already decided,
  every timestamp already stored) publishes nothing. Measured on this
  machine, at the live library's 816 stored decisions: an already-known
  timestamp lookup stays 1,5 µs, a first-seen match costs 89 µs (so
  a first-ever scan pays ~75 ms for 850 new matches, against 4,4-6,2
  s for the scan itself), and a 100-decision review-dialog apply pays
  ~13 ms. What a walk can never get is the torn single map: enumerating
  one while it is being written threw `IndexOutOfRangeException`
  / "destination array is not long enough" on the code before this
  rule, in `tests/Unit/VrModSettingsPublicationTests.cs` run against
  it. The getter keeps returning the live published instance on purpose:
  Newtonsoft's default `ObjectCreationHandling` reuses and fills what
  a getter returns, so a copy-returning getter silently throws away
  everything loaded from `ExtensionsData/<plugin id>/config.json` -
  that is a test in the same file, not a hunch.

**Entry points:** `RunScanWithProgress`, `RunShowGamesScan`,
`RunFullScanApplyOnly` and `RunReviewFromCache` all block their caller
(title-bar progress window, modal review dialog, `GetAwaiter().GetResult()`),
so they must not run on Playnite's UI thread. Until now that rule
was a comment. `VrModTagPlugin.RunScanEntryPoint` enforces it: a
caller on the UI thread is moved to the thread pool with a warning
naming the entry point, and **that call returns before the scan
finishes** - a caller that needs the result has to be off the UI
thread (the settings page already is, through `RunScanOnBackground`).

**Progress results are read, not assumed.** `ActivateGlobalProgress` returns
a result; the review dialog's own scan/cleanup/save call sites used to
drop it, so a cancelled or failed operation looked identical to a completed
one (the status line said "Scan completed." and the window closed itself),
and Playnite's two extension guides both tell the caller to inspect that
result. `VrModReviewDialog.ProgressFailureText(operation, result)` and
`VrModReviewDialog.ReviewDialogMayClose(result)` are the decision half, kept
static and unit-tested (`tests/Unit/VrModProgressOutcomeTests.cs`) because
a failure must never be a result this dialog closes itself on. What counts
as "not completed" is exactly an `Error`, a `Canceled`, or an explicit
`Result == false`: the SDK documents the three properties but never says what a
successful action leaves in `Result`, Playnite's own wrappers build the result
different ways, and its next API generation drops `Result` altogether — so a
rule that required `Result == true` would reject successful scans. Both of the
dialog's progress windows are currently created with `Cancelable: false`, so
a cancellation there can only come from Playnite closing the window; the rule
covers it anyway, and the tag-removal action already threads `a.CancelToken`
into the service, which is the plumbing a cancelable Save would need.

**Matching cost:** per-game catalog matching goes through a cache-versioned
index (`VrModTagService.GetCachedEntriesForGame`) instead of scoring
every cached entry per game. The index visits only entries that could
still reach the fuzzy threshold, using two bounds derived from the matcher
itself - the length band (`VrModNameMatcher.CanReachSimilarity`) and a
character-count bound on the longest common subsequence a Levenshtein
similarity of at least the threshold implies. Both are checked against
the exhaustive walk in `tests/Unit/VrModCatalogIndexTests.cs`, because a
prune that silently drops a real match is the whole risk of the design.

**Accepted residual:** a walker that reads the three decision properties
one after another can straddle one write and pair a decision from the
new maps with the previous map's timestamps or tag baselines. That is
not a torn map and it does not crash: the file is still valid JSON,
`EnsureDecisionTimestampUtc` re-records a missing timestamp on the next scan,
and a lost tag baseline only means `ComputeTagDelta` reports "no change" for
that one match until the decision is accepted again. Closing it properly would
mean Playnite reading one bundle object instead of three properties, which
is not ours to change. `VrModSettingsViewModel._scanLock` was listed here
as a lock held across service calls; it is not - it guards the `_isScanning`
flag check-and-set only, and is released before the scan task starts.
