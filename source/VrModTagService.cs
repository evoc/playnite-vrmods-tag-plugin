using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Playnite.VRMods
{
    /// <summary>
    /// Core service that orchestrates VR mod tag application across the Playnite library.
    ///
    /// This is the central business logic class of the plugin. It:
    /// 1. Fetches and caches VR mod data from external sources (RototRobot, Flat2VR, UeVR)
    /// 2. Builds indexed lookups for fast game name matching
    /// 3. Matches VR mod entries against the user's Playnite library using Steam IDs and fuzzy name matching
    /// 4. Applies or removes VRMod tags based on match results and user decisions
    /// 5. Manages a decision system (Accepted/Rejected/Pending) for persistent match preferences
    /// 6. Removes stale tags from games that no longer match any VR mod source
    ///
    /// The service is created once during plugin initialization and lives for the duration
    /// of the Playnite session. It implements IDisposable to clean up its cache on shutdown.
    ///
    /// Matching pipeline (for each VR mod entry from each source):
    ///   1. Check Steam App ID match (exact, preferred)
    ///   2. Check normalized name match (Levenshtein similarity >= threshold)
    ///   3. Check user's decision: Accepted -> apply, Rejected -> skip, Pending -> review or auto-apply
    ///   4. Apply category tag and umbrella "VRMod" tag to matched games
    ///
    /// Caching:
    ///   - Source entries are cached per-source by fetch timestamp (configurable duration)
    ///   - Game index (name/Steam/normalized) is rebuilt each scan for consistency
    ///   - Tags are cached per-scan to avoid redundant database lookups
    /// </summary>
    public class VrModTagService : IDisposable
    {
        private readonly IPlayniteAPI _api;
        private readonly VrModTagSettings _settings;
        private readonly ILogger _logger;
        private readonly VrModSourceRegistry _registry;

        /// <summary>
        /// Hard wall-clock budget for one scan run: all sources, all per-request retries, all
        /// pagination pages. One source can already spend <c>MaxHttpAttempts</c> x the 30 s client
        /// timeout, and the Steam Curator source walks up to <c>MaxCuratorPages</c> pages of one
        /// host, so a slow or hostile set of sources can otherwise hold a scan open for many minutes
        /// with no way out. When the budget fires the run is cancelled, and every source it cancelled
        /// counts as failed so no stale-tag cleanup runs on the strength of a partial scan.
        /// </summary>
        public static readonly TimeSpan DefaultScanWallClockBudget = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Wall-clock budget applied to this service's scan runs (see
        /// <see cref="DefaultScanWallClockBudget"/>). Internal setter is the unit-test seam: the
        /// 10-minute production default cannot be observed in a test, a 300 ms one can.
        /// </summary>
        internal TimeSpan ScanWallClockBudget { get; set; } = DefaultScanWallClockBudget;

        /// <summary>
        /// Cancellation source of the scan currently fetching sources (its own budget plus any caller
        /// token), or null when no scan is fetching. Written/cleared only by
        /// <see cref="FetchAllEnabledSourcesAsync"/>; read by <see cref="CancelCurrentScan"/>.
        /// </summary>
        private System.Threading.CancellationTokenSource? _activeScanCts;

        private const int HashBucketChars = 3;

        /// <summary>
        /// Candidate budget for one listing's suggestion search. The posting list of the listing's
        /// rarest word is not walked when more games than this carry that word: a full scan runs over
        /// a few thousand listings, and a common word ("game", "vr") would otherwise turn the search
        /// into a cross product. Measured against the live catalog and this library: 0 of 1 122
        /// listings reach it, so in practice it is only the guard.
        /// </summary>
        internal const int SuggestionPostingCap = 40;

        /// <summary>
        /// How many suggestion rows one listing may put in front of a person per review pass. A
        /// listing can name a family the library holds several entries of; showing every one turns a
        /// review into an inventory. Candidates past the cap are counted in the log, not hidden.
        /// </summary>
        internal const int SuggestionRowsPerEntry = 2;

        private readonly ConcurrentDictionary<string, (DateTime Fetched, List<VrModEntry> Entries)> _sourceCache = new();
        private bool _isDisposed;


        // Normalized name -> EVERY game whose normalized name is that. One normalized name can
        // belong to several Game rows when the same title is imported from multiple stores
        // (e.g. one Epic + one Steam "Alien: Isolation"); matching must return all of them so
        // each store duplicate is independently matched, tagged, and decided (by game Id).
        private ConcurrentDictionary<string, List<Game>>? _gameNameCache;
        private Dictionary<Guid, Game>? _gameIdCache;
        private ConcurrentDictionary<string, Game>? _steamCache;
        private ConcurrentDictionary<string, string>? _normalizedGameCache;
        /// <summary>
        /// Normalized source-catalog entry names, rebuilt per scan. A catalog entry name is one of a
        /// few thousand strings that is compared against every game in the batch, so normalizing it
        /// once per scan instead of once per (entry, game) pair is the difference between a few
        /// milliseconds and a few seconds per scan.
        /// </summary>
        private Dictionary<string, string>? _normalizedEntryCache;
        private Dictionary<string, Tag>? _tagCache;
        private Dictionary<Guid, Tag>? _tagIdCache;
        private List<(string SourceName, string EntryName)>? _lastUnmatchedEntries;

        /// <summary>
        /// Normalized WORD -> every game whose name carries it, rebuilt per
        /// <see cref="BuildGameIndex"/>.
        ///
        /// The name index buckets by the first <see cref="HashBucketChars"/> normalized characters and
        /// only scores inside a bucket, so two names that agree everywhere except at the front never
        /// meet: "morrowind" sits in bucket <c>mor</c>, "the elder scrolls iii: morrowind goty
        /// edition" in bucket <c>the</c>. Measured on the live catalog against this library, 17 of the
        /// 108 name-containment candidates are unreachable under that rule whatever the score says.
        /// This index is the recall fix, and it feeds suggestions only (see
        /// <see cref="FindSuggestedGames"/>): the auto-apply path still scores inside the buckets.
        /// </summary>
        private ConcurrentDictionary<string, List<Game>>? _gameTokenIndex;

        /// <summary>
        /// <c>"sourceId||listing identity"</c> -> the games a person has confirmed as that listing,
        /// built per <see cref="BuildGameIndex"/> from the saved decisions. Read by
        /// <see cref="FindGamesByIndex"/> so a mapping somebody confirmed once stays a match whatever
        /// the listing is called next, including a pairing the name rules would never produce.
        /// </summary>
        private Dictionary<string, List<(Guid GameId, string DecisionKey)>>? _decidedListingGames;

        /// <summary>
        /// Suggestions the last review pass found (source name, listing name, game name, reason).
        /// Never applied — no tag, no link, no decision. Kept so the run summary can say what a scan
        /// that matched nothing still found worth a look, instead of only listing what it failed on.
        /// </summary>
        private List<(string SourceName, string EntryName, string GameName, string Reason)>? _lastSuggestions;

        /// <summary>
        /// The candidates the last review pass found for listings the matching rules could not assert,
        /// or null when no review pass has run. Pairings for a person to decide, not matches: nothing
        /// here has been applied to the library. Surfaced for the last-run summary and for tests.
        /// </summary>
        public IReadOnlyList<(string SourceName, string EntryName, string GameName, string Reason)>? LastSuggestions => _lastSuggestions;

        // Display names (lowercased) carried by more than one Game row this scan. A row whose
        // name is in this set is a store duplicate; the review grid disambiguates those rows
        // with a " (Store)" suffix. Rebuilt each BuildGameIndex.
        private HashSet<string> _duplicateGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Playnite game SourceId -> store display name (GameSource.Name, e.g. "Steam"/"Epic"),
        // for the review grid's duplicate disambiguation. Rebuilt each BuildGameIndex.
        private Dictionary<Guid, string> _sourceDisplayName = new Dictionary<Guid, string>();

        // Count of already-accepted matches whose source tags changed since the last accept,
        // computed during RunFullScanAsync. Surfaced to the plugin so the auto-scan notification
        // can tell the user their accepted matches picked up tag changes (6DOF added/removed, etc.).
        private int _changedAcceptedCount;

        // 0 = idle, 1 = a tag-mutating scan (RunFullScanAsync / ScanGameAsync) is in progress.
        // Scans mutate shared state (the tag/index caches) and the non-concurrent SourceStatuses
        // dictionary, so they must never overlap. A coinciding trigger (e.g. the timer/startup
        // scan vs. a manual one) is skipped, not queued — scans are idempotent, so the in-flight
        // one yields the correct state and the next trigger (timer/manual) catches up.
        private int _scanInFlight;

        // New-game detection (OnLibraryUpdated): the set of game Ids known at seed time
        // (SeedKnownGames, called from OnApplicationStarted) plus everything seen since. A game
        // added to the library after seeding is "new" and must be scanned for VR mod tags without
        // waiting for the next full scan. Guarded by _newGameLock (touched from the Playnite
        // OnLibraryUpdated thread and from scan-completion threads).
        //
        // LOCK ORDER: _newGameLock is a LEAF. It is held only for HashSet/queue work on
        // this class's own fields - never while calling Playnite (Database.*), the UI dispatcher, a
        // Timer.Change, network, or logging. Anything Playnite owns is read OUTSIDE the lock and the
        // result merged in afterwards, so a wait inside Playnite's code can never turn into a wait on
        // this lock from another thread. The single-scan gate (_scanInFlight) is an Interlocked flag,
        // not a lock, and is never acquired while _newGameLock is held. The only UI hops in this class
        // (RunOnUiThread) are made from scan threads that hold neither.
        //
        // Take this lock with `using (NewGameLock())`, never `lock (_newGameLock)`: NewGameLock()
        // records the hold per thread, and EnsureUiHopIsSafe - the guard that stops a lock holder
        // from waiting on Playnite's UI thread - only sees holds made through it.
        private readonly object _newGameLock = new object();

        /// <summary>
        /// How many times the current thread holds <c>_newGameLock</c> (0 = not held). ThreadStatic
        /// because the question a UI hop has to answer is per-thread: "is the thread about to wait
        /// on Playnite's dispatcher already holding this lock?". Maintained only by
        /// <see cref="NewGameLock"/> and its release.
        /// </summary>
        [ThreadStatic] private static int _newGameLockDepth;
        private HashSet<Guid>? _knownGameIds;
        // New game Ids awaiting a batch scan. Populated by NotifyLibraryUpdated; drained (and
        // scanned) by TryDrainNewGames, which also runs after EVERY scan completes — so a new game
        // detected while a scan held the single-scan gate is still scanned, never skipped.
        private readonly HashSet<Guid> _pendingNewGameIds = new HashSet<Guid>();

        // Playnite's per-game add/remove events. OnLibraryUpdated is a poor new-game trigger:
        // in Playnite 10 it is raised from exactly one call site (MainViewModelBase.UpdateEmulation
        // Library, the emulator/ROM import), so a game added through "Add game → Manually…", a store
        // integration, the "from installed" wizard or an edit never reaches NotifyLibraryUpdated.
        // Database.Games.ItemCollectionChanged IS raised for every added game, whoever added it,
        // which is what actually finds newly imported moddable games.
        private bool _libraryEventsSubscribed;

        // One add operation can raise the event several times (a wizard import adds dozens of games,
        // Playnite then updates sorting names and metadata per game). Pending Ids are batched, so the
        // burst costs ONE source fetch + ONE scan instead of one per game.
        internal const int DefaultNewGameDebounceMs = 5000;
        private readonly int _newGameDebounceMs;
        private Timer? _newGameDebounceTimer;

        /// <summary>
        /// Above this many games in one debounced batch, handle the batch with ONE entry-driven
        /// full library scan instead of one per-game scan. The per-game path walks the entire source
        /// catalog once per game; the full scan walks it once for the whole library. Measured with
        /// the test harness (3000 catalog entries, matching only, no network): a per-game batch costs
        /// ~0.9 s fixed plus ~80 ms per game (25 s for 300 games), a full scan costs 0.75 s for a
        /// 300-game library and 1.5 s for a 3000-game library. The crossover is a handful of games;
        /// this cut-off is deliberately above it so small batches — the Add Game dialog, a single
        /// import — stay narrowly scoped to the games that actually changed.
        /// </summary>
        internal const int MaxPerGameScanBatch = 20;

        /// <summary>
        /// Acquires the single-scan gate; returns a lease that releases it on disposal, or null if
        /// a scan is already in progress (the caller should then skip). See <see cref="_scanInFlight"/>.
        /// </summary>
        private ScanGateLease? TryBeginScan()
        {
            if (Interlocked.Exchange(ref _scanInFlight, 1) == 1)
            {
                _logger.Warn("VRMod Tag Service: a scan is already in progress; skipping this one (concurrent scans are serialized to protect the shared tag/index state). ");
                return null;
            }
            return new ScanGateLease(this);
        }

        private void ReleaseScanGate()
        {
            Interlocked.Exchange(ref _scanInFlight, 0);
            // New games may have been detected while this scan held the gate (their batch scan was
            // gate-skipped, or OnLibraryUpdated fired mid-scan). They must still be scanned, so
            // drain after the gate frees. No-op when nothing is pending.
            TryDrainNewGames();
        }

        /// <summary>Releases the single-scan gate exactly once, even on a throwing scan.</summary>
        private sealed class ScanGateLease : IDisposable
        {
            private readonly VrModTagService _owner;
            private bool _released;
            public ScanGateLease(VrModTagService owner) => _owner = owner;
            public void Dispose()
            {
                if (_released) return;
                _released = true;
                _owner.ReleaseScanGate();
            }
        }

        public VrModTagService(IPlayniteAPI api, VrModTagSettings settings, ILogger logger, VrModSourceRegistry registry,
            int newGameDebounceMs = DefaultNewGameDebounceMs)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _registry = registry;
            _newGameDebounceMs = newGameDebounceMs > 0 ? newGameDebounceMs : DefaultNewGameDebounceMs;
        }

        public VrModTagSettings Settings => _settings;

        /// <summary>
        /// The registered data sources (read-only). Exposed so the metadata provider can look up a
        /// source's display name for the source links it offers.
        /// </summary>
        public VrModSourceRegistry SourceRegistry => _registry;

        /// <summary>
        /// The number of already-accepted matches whose source tags changed since the last accept,
        /// from the most recent full scan. The auto-scan notification uses this to tell the user
        /// their accepted matches picked up tag changes (e.g. 6DOF added or removed).
        /// </summary>
        public int ChangedAcceptedCount => _changedAcceptedCount;
        private TimeSpan CacheDuration => TimeSpan.FromMinutes(_settings.CacheDurationMinutes);

        private string SafeLogText(string text)
        {
            var safe = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return safe;
        }



        /// <returns>The entries, plus a flag reporting whether a network fetch happened
        /// (false = served from the in-memory cache, so no politeness delay is needed).</returns>
        private async Task<(List<VrModEntry> Entries, bool Fetched)> GetEntriesAsync(IVrModSource source)
        {
            var cacheKey = source.SourceId;
            if (_sourceCache.TryGetValue(cacheKey, out var cached) &&
                (DateTime.UtcNow - cached.Fetched) < CacheDuration)
            {
                _logger.Info($"VRMod Tag Service: Cache hit for {source.DisplayName}.");
                return (cached.Entries, false);
            }

            _logger.Info($"VRMod Tag Service: Cache miss or expired. Fetching from {source.DisplayName}...");
            var entries = await source.FetchEntriesAsync();
            var entryList = entries.ToList();
            // Uniform guarantee for EVERY source: one entry per game, union of features
            // (BaseVrModParser.MergeDuplicateGames). A source that lists the same game more
            // than once (e.g. once per category — the RototRobot site does) would otherwise
            // yield duplicate entries sharing one per-mapping decision key, whose changed-tag
            // baseline ping-pongs between their tag sets (spurious "Changed" rows). Parsers
            // may already merge (RototRobot does, so its parse output is already one entry
            // per game); for an already-merged list this is a no-op.
            var mergedList = BaseVrModParser.MergeDuplicateGames(entryList);
            if (mergedList.Count < entryList.Count)
            {
                var mergedCount = entryList.Count - mergedList.Count;
                entryList = mergedList;
                var line = $"Merged {mergedCount} duplicate game listing(s) (same game listed more than once) into one entry per game (union of features)";
                if (source.RunLog is { } runLog && runLog.Count < BaseVrModParser.MaxRunLogLines)
                    runLog.Add(line);
            }
            // Entries are the only place scraped category/quality/motion text gets turned into tag
            // names, so the vocabulary check happens once, here, before anything downstream can use it.
            DropUnknownFeatureValues(entryList, source);
            _sourceCache[cacheKey] = (DateTime.UtcNow, entryList);
            InvalidateEntryIndex();
            _logger.Info($"VRMod Tag Service: {source.DisplayName} returned {entryList.Count} entries (cached).");
            return (entryList, true);
        }

        /// <summary>
        /// Drops category / quality / motion values that are not in the plugin's tag vocabulary (see
        /// <see cref="VrModCategories.IsKnownTagValue"/>), and records what was dropped in the source's
        /// run log.
        ///
        /// Those three lists are what tag names are built from ("VRMod {value}"), and they are filled
        /// from text a website wrote. Every parser today maps that text onto a known value before
        /// returning it, so this changes nothing about the current sources — it is the guard that keeps
        /// the next parser, or a source that renames a category on its site, from writing that text into
        /// the user's library as a tag. It runs here, at the one place every source's entries enter the
        /// service, rather than in each of the places that later builds a tag name. Dropped values are
        /// reported instead of skipped quietly: a source renaming its categories on its site would
        /// otherwise look like games quietly losing their category tags.
        /// </summary>
        private void DropUnknownFeatureValues(List<VrModEntry> entries, IVrModSource source)
        {
            var dropped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                foreach (var value in VrModCategories.UnknownTagValues(entry))
                    dropped.Add(value);
            if (dropped.Count == 0)
                return;

            foreach (var entry in entries)
            {
                entry.Categories.RemoveAll(v => !VrModCategories.IsKnownTagValue(v));
                entry.Qualities.RemoveAll(v => !VrModCategories.IsKnownTagValue(v));
                entry.Motions.RemoveAll(v => !VrModCategories.IsKnownTagValue(v));
                entry.Statuses.RemoveAll(v => !VrModCategories.IsKnownTagValue(v));
            }

            // The dropped text is untrusted page content, so it is logged the way every other scraped
            // string is: control characters escaped, and only a sample of it.
            var sample = string.Join(", ", dropped.Take(8).Select(SafeLogText));
            var more = dropped.Count > 8 ? $" (+{dropped.Count - 8} more)" : "";
            var line = $"Ignored {dropped.Count} value(s) outside the tag vocabulary: {sample}{more}";
            _logger.Warn($"VRMod Tag Service: {line} (source '{source.DisplayName}').");
            if (source.RunLog is { } runLog && runLog.Count < BaseVrModParser.MaxRunLogLines)
                runLog.Add(line);
        }

        /// <summary>
        /// Fetches entries from all enabled sources in parallel with per-source isolation: a source
        /// that throws (network error, timeout, parse failure) is marked red in the source
        /// statuses and contributes nothing; the other sources proceed.
        ///
        /// Returns the combined entries and how many sources failed. The failure count is the
        /// callers' gate for stale-tag removal: while any source failed this scan, we cannot tell
        /// whether a game is genuinely no longer listed or was simply covered by a source that
        /// failed, so removing tags would be data loss. Callers must skip stale removal whenever
        /// the failure count is non-zero.
        /// </summary>
        /// <summary>
        /// The non-catalog category tag names an entry yields (e.g. "VRMod UEVR"), joined for a
        /// scan log. A multi-category entry lists all of its category tags.
        /// </summary>
        private static string EntryCategoryTags(VrModEntry entry)
            => string.Join(", ", entry.Categories.Where(c => !VrModCategories.IsCatalogCategory(c)).Select(VrModCategories.TagNameFor));

        /// <summary>
        /// Cancels the scan that is currently fetching sources: its in-flight requests, its retries
        /// and its pagination stop at the next opportunity. No-op when no scan is fetching.
        /// Wired to the progress window's Cancel button (Playnite's
        /// <c>GlobalProgressActionArgs.CancelToken</c>) and available to tests.
        /// </summary>
        public void CancelCurrentScan()
        {
            _activeScanCts?.Cancel();
        }

        /// <summary>
        /// Fetches every enabled source concurrently.
        /// <paramref name="cancellationToken"/> is caller cancellation for this run (the manual scan
        /// passes the progress window's Cancel token). It is combined with
        /// <see cref="ScanWallClockBudget"/>: whichever fires first cancels the run, and every
        /// source's <c>ScanToken</c> is set to the combined token so its own request retries and
        /// pagination loops stop too.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "One source failing - including a bug inside that source's parser - is recorded against that source and must not end the scan.")]
        private async Task<(ConcurrentDictionary<string, ConcurrentBag<VrModEntry>> Entries, int FailedSources, System.Collections.Concurrent.ConcurrentBag<string> SucceededSourceIds)> FetchAllEnabledSourcesAsync(string trigger, System.Threading.CancellationToken cancellationToken = default)
        {
            var allEntries = new ConcurrentDictionary<string, ConcurrentBag<VrModEntry>>();
            var failedSources = 0;
            var succeededSourceIds = new System.Collections.Concurrent.ConcurrentBag<string>();
            var sourcesList = _registry.GetEnabledSources().ToList();

            // Cancellable + bounded scan run: caller token (progress-window Cancel) combined with the
            // wall-clock budget, handed to every source so their retries/pagination stop as well.
            using var scanCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCts.CancelAfter(ScanWallClockBudget);
            _activeScanCts = scanCts;
            foreach (var source in sourcesList)
                source.ScanToken = scanCts.Token;

            // One fresh in-memory debug log per source for this run (the settings "Last run"
            // view). The parsers append every web request they make (URL, HTTP status code,
            // body size, elapsed time) and every per-game extraction problem; the service
            // appends the run's milestones. UpdateSourceStatus copies the list into
            // SourceStatus.LastRunLog when the run completes.
            var runLogs = new Dictionary<string, List<string>>();
            foreach (var source in sourcesList)
            {
                var log = new List<string> { $"Run started ({trigger})" };
                runLogs[source.SourceId] = log;
                source.RunLog = log;
            }

            var fetchTasks = sourcesList.Select(async source =>
            {
                var runLog = runLogs[source.SourceId];
                try
                {
                    var needFetch = HasSourceChanged(source);
                    if (needFetch)
                    {
                        _logger.Info($"VRMod Tag Service: Fetching from {source.DisplayName} (source changed).");
                    }
                    else
                    {
                        _logger.Info($"VRMod Tag Service: Source {source.DisplayName} unchanged. Using cached data.");
                    }

                    var (sourceEntries, fetched) = await GetEntriesAsync(source);
                    runLog.Add(fetched
                        ? $"Fetched {sourceEntries.Count} entries"
                        : "Cache hit - entries served from the in-memory cache (no fetch this run)");
                    // Found counts (the "Last run" view): what this run's entries carry, independent
                    // of matching/review/apply — an entry with at least one applicable tag, and an
                    // entry with a source deep link.
                    int tagsFound = 0;
                    int linksFound = 0;
                    foreach (var entry in sourceEntries)
                    {
                        entry.SourceId = source.SourceId;
                        if (entry.AppliedTagNames.Count > 0)
                            tagsFound++;
                        if (!string.IsNullOrWhiteSpace(entry.SourceUrl))
                            linksFound++;
                        var bag = allEntries.GetOrAdd(source.SourceId, _ => new ConcurrentBag<VrModEntry>());
                        bag.Add(entry);
                    }
                    succeededSourceIds.Add(source.SourceId);
                    UpdateSourceStatus(source, true, null, sourceEntries.Count, runLog, tagsFound, linksFound);
                    // Politeness pause only when this source actually hit the network; a
                    // cache hit did no I/O and must not add latency to the scan.
                    if (fetched)
                        await Task.Delay(500);
                }
                catch (System.OperationCanceledException ex)
                {
                    // Cancelled with the scan (user Cancel or the wall-clock budget). Counted as a
                    // failed source ON PURPOSE: the caller then skips stale-tag removal and link
                    // reconciliation rather than acting on a half-finished scan.
                    System.Threading.Interlocked.Increment(ref failedSources);
                    bool cancelledScan = scanCts.IsCancellationRequested;
                    runLog.Add(cancelledScan ? "CANCELLED: scan was cancelled" : $"FAILED: {ex.Message}");
                    _logger.Info($"VRMod Tag Service: {source.DisplayName} {(cancelledScan ? "cancelled with the scan" : "failed")}: {ex.Message}");
                    UpdateSourceStatus(source, false, cancelledScan ? "Scan cancelled." : ex.Message, 0, runLog);
                }
                catch (Exception ex)
                {
                    System.Threading.Interlocked.Increment(ref failedSources);
                    runLog.Add($"FAILED: {ex.Message}");
                    _logger.Error($"VRMod Tag Service: Error fetching from {source.DisplayName}: {ex.Message}");
                    UpdateSourceStatus(source, false, ex.Message, 0, runLog);
                }
            });

            try
            {
                await Task.WhenAll(fetchTasks);
            }
            finally
            {
                // The token belongs to this run only: a source keeps no reference to a disposed
                // cancellation source between scans (its ScanToken goes back to "no cancellation").
                foreach (var source in sourcesList)
                    source.ScanToken = System.Threading.CancellationToken.None;
                _activeScanCts = null;
            }

            return (allEntries, failedSources, succeededSourceIds);
        }

        public void RefreshCache()
        {
            _sourceCache.Clear();
            InvalidateEntryIndex();
            _logger.Info("VRMod Tag Service: Cache cleared.");
        }

        public void ClearSourceCache(string sourceId)
        {
            _sourceCache.TryRemove(sourceId, out _);
            InvalidateEntryIndex();
            _logger.Info($"VRMod Tag Service: Cache cleared for source '{sourceId}'.");
        }

        public bool HasSourceChanged(IVrModSource source)
        {
            var currentId = source.ChangeIdentifier;
            if (string.IsNullOrEmpty(currentId))
                return true;

            if (_settings.SourceStatuses == null)
                return true;

            _settings.SourceStatuses.TryGetValue(source.SourceId.ToLowerInvariant(), out var status);
            var storedId = status?.LastChangeId;
            return currentId != storedId;
        }

        public void UpdateSourceStatus(IVrModSource source, bool success, string? error = null, int entryCount = 0, List<string>? runLog = null, int tagsFound = 0, int linksFound = 0)
        {
            if (_settings.SourceStatuses == null)
                _settings.SourceStatuses = new Dictionary<string, SourceStatus>();

            var key = source.SourceId.ToLowerInvariant();
            if (!_settings.SourceStatuses.ContainsKey(key))
                _settings.SourceStatuses[key] = new SourceStatus { DisplayName = source.DisplayName };

            var status = _settings.SourceStatuses[key];
            status.LastScanTime = DateTime.UtcNow;
            status.LastResult = success;
            status.LastError = error;
            status.LastEntryCount = entryCount;
            status.LastChangeId = source.ChangeIdentifier;
            // The found counts (what this run's entries carry: at least one applicable tag / a
            // source deep link). A failed or empty scan reports 0 for both.
            status.LastTagCount = tagsFound;
            status.LastLinkCount = linksFound;
            // Keep the run's debug log (the settings "Last run" view). The service passes the
            // run's list; a null (e.g. a direct test call) leaves the previous log untouched.
            if (runLog != null)
                status.LastRunLog = new List<string>(runLog);
        }

        /// <summary>
        /// Appends the apply-phase milestone line to a source's run log (the "Last run" view)
        /// and records the applied counts for the dialog's "found (applied)" boxes. Called
        /// once per successfully-fetched source - also with 0/0, so a run that applied
        /// nothing (e.g. every match still pending review) says so explicitly instead of the
        /// line being absent. Sources that failed the fetch have no line - the FAILED line is
        /// their terminal line. No-op when the source has no status (defensive; a failed fetch
        /// never reaches here).
        /// </summary>
        private void RecordSourceRunCounts(string sourceId, int tagCount, int linkCount)
        {
            if (_settings.SourceStatuses == null)
                return;
            var key = sourceId.ToLowerInvariant();
            if (_settings.SourceStatuses.TryGetValue(key, out var status))
            {
                status.LastTagCountApplied = tagCount;
                status.LastLinkCountApplied = linkCount;
                status.LastRunLog.Add($"Apply: {tagCount} tag(s) applied, {linkCount} source link(s) ensured");
            }
        }

        /// <summary>
        /// Per-source tag/link counts accumulated during one scan's apply-loop, for the "last
        /// run log" dialog. <see cref="Tags"/> is the category/facet tags applied;
        /// <see cref="AcceptedGames"/> is the set of games with a saved-accepted match (the
        /// source-link candidates, deduped by game so a source listing a game twice counts once).
        /// </summary>
        private sealed class SourceRunStats
        {
            public int Tags;
            public readonly HashSet<Guid> AcceptedGames = new HashSet<Guid>();
        }

        private void BuildGameIndex()
        {
            _gameNameCache = new ConcurrentDictionary<string, List<Game>>();
            _gameIdCache = new Dictionary<Guid, Game>();
            _steamCache = new ConcurrentDictionary<string, Game>();
            _normalizedGameCache = new ConcurrentDictionary<string, string>();
            _normalizedEntryCache = new Dictionary<string, string>(StringComparer.Ordinal);
            _tagCache = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
            _tagIdCache = new Dictionary<Guid, Tag>();
            _gameTokenIndex = new ConcurrentDictionary<string, List<Game>>(StringComparer.Ordinal);
            _duplicateGameNames = BuildDuplicateGameNames();
            _sourceDisplayName = BuildSourceDisplayNameMap();

            foreach (var game in _api.Database.Games)
            {
                var normalizedName = NormalizeForMatchFast(game.Name);
                var list = _gameNameCache.GetOrAdd(normalizedName, _ => new List<Game>());
                list.Add(game);
                _gameIdCache[game.Id] = game;
                _normalizedGameCache.TryAdd(game.Id.ToString(), normalizedName);

                if (game.Links != null)
                {
                    foreach (var link in game.Links)
                    {
                        if (link.Url.StartsWith("https://store.steampowered.com/app/", StringComparison.Ordinal))
                        {
                            var appId = link.Url.Substring("https://store.steampowered.com/app/".Length).TrimEnd('/');
                            _steamCache.TryAdd(appId, game);
                        }
                    }
                }

                // Word -> game. A name can carry the same word twice ("Warling Warling"); the posting
                // list holds the game once so a candidate is never classified twice.
                foreach (var token in VrModNameMatcher.TokenizeForMatch(game.Name).Distinct())
                {
                    var posting = _gameTokenIndex.GetOrAdd(token, _ => new List<Game>());
                    if (!posting.Contains(game))
                        posting.Add(game);
                }
            }

            _decidedListingGames = BuildDecidedListingIndex();
        }

        /// <summary>
        /// The saved decisions indexed by LISTING instead of by game: <c>"sourceId||identity"</c> ->
        /// the games somebody confirmed as that listing. The settings store a decision under a key
        /// that already carries both halves (<see cref="VrModNameMatcher.MakeDecisionKey"/>: source id,
        /// the listing's identity component, the game id), so this is that key cut at its last
        /// separator, and the game id is what the game-id cache resolves it back to.
        ///
        /// Accepted decisions only. A Rejected mapping is a refusal to match and a Pending one is a
        /// question, neither of which is evidence that a game is a listing. Rebuilt per index build
        /// because a decision made in the review dialog has to reach the next scan's matching.
        /// </summary>
        private Dictionary<string, List<(Guid GameId, string DecisionKey)>> BuildDecidedListingIndex()
        {
            var index = new Dictionary<string, List<(Guid GameId, string DecisionKey)>>(StringComparer.Ordinal);
            foreach (var stored in _settings.SnapshotMatchDecisions())
            {
                if (!Enum.TryParse(stored.Value, out MatchDecision decision) || decision != MatchDecision.Accepted)
                    continue;

                var cut = stored.Key.LastIndexOf("||", StringComparison.Ordinal);
                if (cut <= 0 || cut + 2 >= stored.Key.Length)
                    continue;
                var listingPart = stored.Key.Substring(0, cut);
                if (!Guid.TryParse(stored.Key.Substring(cut + 2).Trim('{', '}'), out var gameId))
                    continue;

                if (!index.TryGetValue(listingPart, out var games))
                    index[listingPart] = games = new List<(Guid, string)>();
                games.Add((gameId, stored.Key));
            }
            return index;
        }

        /// <summary>
        /// The listing half of a decision key, in both shapes a stored key can have: the listing's
        /// stable identity component (what this plugin writes today) and its raw display name (what it
        /// wrote before listings carried a stable identity — which is what sits in the settings file of
        /// anybody upgrading from that version). A saved decision is only worth having if the scan can
        /// FIND it, and the scan does not get to choose which version wrote the file.
        /// </summary>
        private static List<string> StoredListingKeys(VrModEntry entry)
        {
            var sourceId = entry.SourceId ?? string.Empty;
            var identity = entry.DecisionKeyComponent;
            var name = entry.GameName ?? string.Empty;
            var keys = new List<string>(2) { $"{sourceId}||{identity}" };
            if (identity != name)
                keys.Add($"{sourceId}||{name}");
            return keys;
        }

        /// <summary>
        /// Whether somebody already decided this exact listing/game pair, under either key shape (see
        /// <see cref="StoredListingKeys"/>). Used to keep a decided pair out of the suggestion list:
        /// an Accepted pair is already a row through the restored mapping, a Rejected one is not
        /// asked about again.
        /// </summary>
        private bool HasStoredDecision(VrModEntry entry, Guid gameId)
        {
            foreach (var listingKey in StoredListingKeys(entry))
            {
                if (_settings.TryGetDecisionByKey($"{listingKey}||{gameId:D}", out _))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The display names (lowercased) carried by more than one Game row this scan — i.e.
        /// store duplicates of the same title. The review grid disambiguates a row whose name is
        /// in this set with a " (Store)" suffix; unique names stay plain.
        /// </summary>
        private HashSet<string> BuildDuplicateGameNames()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in _api.Database.Games)
            {
                var name = game.Name ?? string.Empty;
                counts.TryGetValue(name, out var count);
                counts[name] = count + 1;
            }
            var dupes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in counts)
                if (kv.Value > 1)
                    dupes.Add(kv.Key);
            return dupes;
        }

        /// <summary>
        /// Maps each Playnite game SourceId to its store display name (GameSource.Name, e.g.
        /// "Steam"/"Epic") for the review grid's duplicate disambiguation. Empty when the
        /// database exposes no sources.
        /// </summary>
        private Dictionary<Guid, string> BuildSourceDisplayNameMap()
        {
            var map = new Dictionary<Guid, string>();
            var sources = _api.Database.Sources;
            if (sources == null)
                return map;
            foreach (var source in sources)
            {
                if (source != null)
                    map[source.Id] = source.Name ?? string.Empty;
            }
            return map;
        }

        /// <summary>
        /// The display text for a game in the review grid: the name as-is, with the store
        /// source in brackets ("Alien: Isolation (Steam)") only when the same name is shared by
        /// more than one game (a multi-store duplicate). A unique name is shown unchanged.
        /// </summary>
        private string GetGameDisplayName(Game game)
        {
            if (game == null)
                return string.Empty;
            if (!_duplicateGameNames.Contains(game.Name))
                return game.Name;
            if (game.SourceId != Guid.Empty && _sourceDisplayName.TryGetValue(game.SourceId, out var sourceName) && !string.IsNullOrEmpty(sourceName))
                return $"{game.Name} ({sourceName})";
            return game.Name;
        }

        /// <summary>
        /// Fast single-pass name normalization: strip VR suffixes, lowercase, map '-'/'_'
        /// to ' ', drop '.', trim. Produces the SAME output as
        /// <see cref="VrModNameMatcher.Normalize"/> for realistic (ASCII) names — locked by
        /// the <c>NormalizeForMatchFast_MatchesCanonicalNormalize</c> differential test.
        /// Deliberately a single StringBuilder pass (no intermediate string allocations)
        /// because it runs in hot loops (per game in BuildGameIndex, per entry in
        /// FindGameByIndex). Do NOT delegate to <see cref="VrModNameMatcher.Normalize"/>
        /// (adds allocations to the hot path) or replace with it (changes the hot path's
        /// per-char lowercasing). The differential test is the intended "consolidation":
        /// it locks the two to agreement without a perf/behaviour regression.
        /// </summary>
        internal static string NormalizeForMatchFast(string name)
        {
            name = VrModNameMatcher.StripVrSuffixes(name);
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (ch == '-' || ch == '_') sb.Append(' ');
                else if (ch == '.') { }
                else sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString().Trim();
        }

        private string GetNormalizedGameName(Game game)
        {
            if (_normalizedGameCache != null && _normalizedGameCache.TryGetValue(game.Id.ToString(), out var cached))
                return cached;
            var normalized = NormalizeForMatchFast(game.Name);
            _normalizedGameCache?.TryAdd(game.Id.ToString(), normalized);
            return normalized;
        }

        /// <summary>
        /// Normalized catalog entry name, memoized for the current scan (see
        /// <see cref="_normalizedEntryCache">_normalizedEntryCache</see>). Falls back to normalizing
        /// directly when no scan is in progress.
        /// </summary>
        private string GetNormalizedEntryName(string entryName)
        {
            var cache = _normalizedEntryCache;
            if (cache == null) return NormalizeForMatchFast(entryName);
            if (!cache.TryGetValue(entryName, out var normalized))
            {
                normalized = NormalizeForMatchFast(entryName);
                cache[entryName] = normalized;
            }
            return normalized;
        }

        /// <summary>
        /// The entry's category tags as they read in a log line. Only called where a match is
        /// actually logged: building this joins LINQ allocations per entry, and a scan walks every
        /// entry of every source for every game, most of which are pruned without ever being logged.
        /// </summary>
        private static string CategoryTagSummary(VrModEntry entry)
        {
            var tags = EntryCategoryTags(entry);
            return tags.Length == 0 ? "no category tags on this entry" : tags;
        }

        /// <summary>
        /// Returns EVERY Playnite game this source entry matches, or an empty list when none.
        ///
        /// A source entry matches a game when they are the same game: identical normalized name
        /// (exact, or the best fuzzy name) OR the same Steam App ID. When several Game rows share
        /// that (best) normalized name — i.e. the same title imported from multiple stores, e.g.
        /// one Epic + one Steam "Alien: Isolation" — ALL of them are returned, so each store
        /// duplicate is independently matched, tagged, and decided (by game Id). Only identical
        /// normalized names are grouped; a different title is never pulled in.
        /// </summary>
        internal List<Game> FindGamesByIndex(VrModEntry entry)
        {
            var results = new List<Game>();

            // A mapping somebody confirmed is a match, whatever the listing is called next. This is
            // what a review is FOR: after one Accept the tags and the deep link survive a rename on
            // the site, and a pairing the name rules would never produce (a listing named "Morrowind"
            // against "The Elder Scrolls III: Morrowind GOTY Edition") becomes a match on every scan.
            // Read from the index the previous BuildGameIndex froze, so a decision made during this
            // scan's review dialog takes effect at the next scan.
            foreach (var listingKey in StoredListingKeys(entry))
            {
                if (_decidedListingGames == null || !_decidedListingGames.TryGetValue(listingKey, out var confirmed))
                    continue;
                foreach (var (gameId, _) in confirmed)
                {
                    if (_gameIdCache != null && _gameIdCache.TryGetValue(gameId, out var decidedGame))
                        AddUnique(results, decidedGame);
                }
            }

            // Steam App ID match (unique per game).
            if (_steamCache != null && !string.IsNullOrEmpty(entry.SteamAppId)
                && _steamCache.TryGetValue(entry.SteamAppId!, out var steamGame))
            {
                results.Add(steamGame);
            }

            var normalizedEntry = NormalizeForMatchFast(entry.GameName);
            if (string.IsNullOrEmpty(normalizedEntry) || _gameNameCache == null)
                return results;

            // Exact normalized name: every game sharing it (store duplicates).
            if (_gameNameCache.TryGetValue(normalizedEntry, out var exactGames))
            {
                foreach (var g in exactGames)
                    AddUnique(results, g);
                return results;
            }

            // No exact name: fuzzy. Take the best-scoring normalized name and return every game
            // sharing THAT name (the user rule: list all games with the best name when the name
            // occurs multiple times). Different names are never grouped.
            var entryBucket = normalizedEntry.Substring(0, Math.Min(HashBucketChars, normalizedEntry.Length));
            string? bestKey = null;
            var bestScore = 0.0f;
            foreach (var kvp in _gameNameCache)
            {
                var gameBucket = kvp.Key.Substring(0, Math.Min(HashBucketChars, kvp.Key.Length));
                if (entryBucket != gameBucket)
                    continue;

                var score = VrModNameMatcher.GetMatchScore(normalizedEntry, kvp.Key, _settings.FuzzyMatchThreshold);
                if (score < 0)
                    continue;

                var isBetter = score > bestScore
                    || (bestKey != null && score == bestScore && string.CompareOrdinal(kvp.Key, bestKey) < 0);
                if (isBetter)
                {
                    bestKey = kvp.Key;
                    bestScore = score;
                }

                if (score < _settings.FuzzyMatchThreshold)
                {
                    foreach (var g in kvp.Value)
                        _logger.Debug($"VRMod Tag Service: No match for '{SafeLogText(entry.GameName)}' against '{SafeLogText(g.Name)}' (score: {score:F2})");
                }
            }

            if (bestKey != null && bestScore >= _settings.FuzzyMatchThreshold && _gameNameCache.TryGetValue(bestKey, out var bestGames))
            {
                foreach (var g in bestGames)
                    AddUnique(results, g);
                _logger.Debug($"VRMod Tag Service: matched '{SafeLogText(entry.GameName)}' to {results.Count} game(s) sharing normalized name '{bestKey}' (score: {bestScore:F2})");
            }

            return results;
        }

        /// <summary>
        /// Listings this library has a game for that the matching rules cannot ASSERT: the name rules
        /// say the two names are the same product family, or say something interesting and stop there.
        ///
        /// Suggestions are the answer to the question whole-string similarity cannot answer. A listing
        /// "Morrowind" against "The Elder Scrolls III: Morrowind GOTY Edition" is 0.20 similar — the
        /// substring rule refuses it outright, and the buckets never even compare it. What is true
        /// about that pair is a fact about words, not about a ratio: every word of the listing name
        /// appears in the game name, in order, and the words left over are one series prefix plus
        /// "GOTY Edition". <see cref="VrModNameMatcher.CompareTokenRuns"/> reads those facts and this
        /// method turns the verdicts into rows for a person to decide, with the leftover words named.
        ///
        /// What is deliberately NOT a suggestion:
        /// <list type="number">
        ///   <item><description>a pair the ordinary path already produces — a suggestion is only
        ///   interesting when it is new, and callers only ask for entries with no match at all;</description></item>
        ///   <item><description>a pair whose leftovers are a series ordinal the other name does not
        ///   carry — <see cref="TokenRunKind.DifferentProduct"/> is a refusal, and it is the same
        ///   judgement the substring rule was written to make ("doom" is not "DOOM 3"), now arrived at
        ///   with a reason attached instead of a -1;</description></item>
        ///   <item><description>a pair that merely shares a stretch of words — one name's words have
        ///   to be the whole of the other's run. On the live catalog this filter is the difference
        ///   between 127 review rows and 496, and what it drops is "Assassin's Creed Valhalla" against
        ///   "Valhalla Hills": two games that happen to share a word;</description></item>
        ///   <item><description>a pair somebody already rejected — they said no to it, and it is not
        ///   brought back to ask again.</description></item>
        /// </list>
        ///
        /// Nothing here is applied: no tag, no link, no decision. A suggestion becomes a match only
        /// through a saved decision, which the first block of <see cref="FindGamesByIndex"/> then
        /// carries on every later scan.
        /// </summary>
        /// <param name="entry">A catalog listing that found no match by name or App ID.</param>
        /// <returns>Ranked candidates, at most <see cref="SuggestionRowsPerEntry"/>; empty when the library has nothing to say.</returns>
        internal List<VrModMatchSuggestion> FindSuggestedGames(VrModEntry entry)
        {
            var suggestions = new List<VrModMatchSuggestion>();
            var index = _gameTokenIndex;
            if (index == null)
                return suggestions;   // no BuildGameIndex has run yet: nothing to search

            var entryTokens = VrModNameMatcher.TokenizeForMatch(entry.GameName);
            if (entryTokens.Count == 0)
                return suggestions;

            // Reach the candidates through the rarest word the listing uses. "Skyrim VR Mod List" is
            // found by "skyrim", not by "mod" or "list": the common words post to most of the
            // library, and a posting list that big is not a candidate set, it is the library.
            //
            // A word the library does not have at all is skipped rather than treated as "rarest with
            // zero postings": it proves only that the listing says something the library does not, and
            // the pair the feature exists to surface is exactly the one whose extra word is new here —
            // 'Brothers: A Tale of Two Sons Remake' against the library's 'Brothers: A Tale of Two
            // Sons'. Measured over 1 122 live listing names against this 3 045-game library: 638 of them
            // carry at least one word the library lacks, and taking the rarest PRESENT word instead of
            // the bare rarest raises the review rows the rules can justify from 127 to 145. Nothing is
            // suggested only when the library shares not one word with the listing.
            string? rarestToken = null;
            var rarestPosting = int.MaxValue;
            foreach (var token in entryTokens)
            {
                if (!index.TryGetValue(token, out var posting) || posting.Count == 0)
                    continue;
                if (posting.Count < rarestPosting)
                {
                    rarestPosting = posting.Count;
                    rarestToken = token;
                }
            }
            if (rarestToken == null || rarestPosting == 0)
                return suggestions;
            if (rarestPosting > SuggestionPostingCap)
            {
                _logger.Debug($"VRMod Tag Service: suggestion search skipped for '{SafeLogText(entry.GameName)}' "
                    + $"(rarest word '{rarestToken}' is carried by {rarestPosting} games).");
                return suggestions;
            }

            var candidates = new List<VrModMatchSuggestion>();
            var decidedCount = 0;
            // A pair the ordinary matching path already produces is not a suggestion: it is already a
            // row, and the same game twice in one dialog reads as two facts about it. Callers only
            // ask for entries with no match at all, so in a scan this set is empty — the filter is
            // here so the rule is a property of the method rather than of how it happens to be called.
            var alreadyMatched = new HashSet<Guid>(FindGamesByIndex(entry).Select(g => g.Id));
            foreach (var game in index[rarestToken])
            {
                if (alreadyMatched.Contains(game.Id))
                    continue;

                var run = VrModNameMatcher.CompareTokenRuns(entry.GameName, game.Name);
                if (run.Kind != TokenRunKind.SameProduct && run.Kind != TokenRunKind.NeedsALook)
                    continue;
                if (!run.SourceIsCarriedByGame && !run.GameIsCarriedBySource)
                    continue;

                if (HasStoredDecision(entry, game.Id))
                {
                    // Somebody already answered this question. A Rejected pair is not asked again; an
                    // Accepted pair is already a row of its own (the saved decision restores it as a
                    // match), and listing it twice — once Accepted, once Pending — would read as two
                    // facts about the same game.
                    decidedCount++;
                    continue;
                }

                candidates.Add(new VrModMatchSuggestion(game, entry.GameName, DescribeTokenRun(run),
                    run.Kind, VrModNameMatcher.ComputeSimilarity(VrModNameMatcher.Normalize(entry.GameName),
                        VrModNameMatcher.Normalize(game.Name))));
            }

            if (candidates.Count == 0)
                return suggestions;

            // Re-release wording first: that is the tier where the rules are closest to certain, and
            // it is the one a person can accept without reading. Then by how much of the name agrees.
            var ranked = candidates
                .OrderBy(c => c.Kind == TokenRunKind.SameProduct ? 0 : 1)
                .ThenByDescending(c => c.DisplayScore)
                .ThenBy(c => c.Game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (ranked.Count > SuggestionRowsPerEntry)
            {
                _logger.Debug($"VRMod Tag Service: '{SafeLogText(entry.GameName)}' has {ranked.Count} candidate "
                    + $"game(s); showing the first {SuggestionRowsPerEntry}.");
            }
            if (decidedCount > 0)
            {
                _logger.Debug($"VRMod Tag Service: '{SafeLogText(entry.GameName)}' skipped {decidedCount} candidate(s) "
                    + "somebody already decided.");
            }

            suggestions.AddRange(ranked.Take(SuggestionRowsPerEntry));
            return suggestions;
        }

        /// <summary>
        /// One line, in plain words, saying what the token rules saw about a pair and naming the
        /// words that are left over. Written to be read in a tooltip under a 90-pixel status column,
        /// so it states the agreement first and the disagreement second.
        ///
        /// English wording, composed here rather than localized: it is generated from the pair's own
        /// tokens, and the review dialog's own action labels ("Show all", "Bulk Accept") are English
        /// in the same window.
        /// </summary>
        private static string DescribeTokenRun(TokenRunResult run)
        {
            // Number-and-verb agreement, because this sentence is read in a tooltip by a person
            // deciding whether to accept a pairing: "every words ... appears" reads like a machine
            // error rather than a finding.
            var shared = run.SharedTokenCount == 1 ? "every word" : $"all {run.SharedTokenCount} words";
            var verb = run.SharedTokenCount == 1 ? "appears" : "appear";
            var parts = run.SourceIsCarriedByGame
                ? new List<string> { $"{shared} of the listing name {verb} in the game name, in order" }
                : new List<string> { $"{shared} of the game name {verb} in the listing name, in order" };

            var gameAdds = DescribeTokens(run.ExtraGameTokens);
            var listingAdds = DescribeTokens(run.ExtraSourceTokens);
            if (gameAdds.Length > 0)
                parts.Add($"the game name adds {gameAdds}");
            if (listingAdds.Length > 0)
                parts.Add($"the listing adds {listingAdds}");

            parts.Add(run.Kind == TokenRunKind.SameProduct
                ? "what is added is re-release or packaging wording, not another title"
                : "what is added is title wording the name rules cannot judge");
            return string.Join("; ", parts);
        }

        /// <summary>Up to six leftover words, then a count of the rest.</summary>
        private static string DescribeTokens(IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0)
                return string.Empty;
            var shown = string.Join(" ", tokens.Take(6));
            return tokens.Count <= 6 ? $"'{shown}'" : $"'{shown}' (+{tokens.Count - 6} more)";
        }

        private static void AddUnique(List<Game> list, Game game)
        {
            foreach (var g in list)
                if (g.Id == game.Id)
                    return;
            list.Add(game);
        }

        /// <summary>
        /// Entry-driven scan: walk every enabled source catalog ONCE and match it against the whole
        /// library. Cheaper per game than scanning game by game (see <see cref="ScanGamesAsync"/>),
        /// which is why a large queued batch is routed here.
        /// </summary>
        /// <param name="reviewCallback">Receives the pending matches; null auto-applies them.</param>
        /// <param name="reviewAll">Also collect already-decided matches (the Scan &amp; Review dialog).</param>
        /// <param name="cancellationToken">Cancel before any library write.</param>
        /// <returns>True when the scan ran; false when another scan holds the single-scan gate or no source is enabled. Callers that must not lose the work retry on false.</returns>
        public async Task<bool> RunFullScanAsync(System.Action<List<VrModReviewMatchEntry>>? reviewCallback = null, bool reviewAll = false, System.Threading.CancellationToken cancellationToken = default)
        {
            // Never run two tag-mutating scans at once (see TryBeginScan): a coinciding trigger
            // returns here without touching state. The lease releases the gate on disposal
            // (finally) — on every return path, including a throwing scan.
            using var _ = TryBeginScan();
            if (_ == null) return false;

            _logger.Info("VRMod Tag Service: Starting full library scan.");
            // Reset per-scan diagnostics so counts don't accumulate across scans in the same process.
            _lastUnmatchedEntries = null;
            _lastSuggestions = null;
            _changedAcceptedCount = 0;
            BuildGameIndex();

            var enabledSources = _registry.GetEnabledSources().ToList();
            if (enabledSources.Count == 0)
            {
                _logger.Warn("VRMod Tag Service: No sources enabled. Skipping scan.");
                return false;
            }

            var (allEntries, failedSources, succeededSourceIds) = await FetchAllEnabledSourcesAsync("full scan", cancellationToken);

            // A cancelled scan stops here instead of going on to rewrite library entries: the fetch
            // set is incomplete, and tags/links written from half a scan are what a user cannot
            // easily undo. Whatever earlier scans applied stays applied.
            cancellationToken.ThrowIfCancellationRequested();

            var currentMatchedGames = new HashSet<Guid>();
            var pendingMatches = new List<VrModReviewMatchEntry>();
            // In reviewAll mode (the "Scan & Review VR Mods" button) this also collects the already-decided
            // matches (accepted + rejected) so the dialog shows every matched game, not just the
            // pending ones. Tag application and stale-removal below are unchanged.
            var reviewMatches = new List<VrModReviewMatchEntry>();
            var statsBySource = new ConcurrentDictionary<string, (int TotalEntries, int Matches, int Rejected)>();
            // Per-source last-run tag/link counts for the "last run log" dialog:
            // tags actually applied this scan + the accepted matches that drive source links.
            var runStatsBySource = new Dictionary<string, SourceRunStats>();
            using var buffer = _api.Database.BufferedUpdate();

            // The exact VR tags each auto-applied game should end up with this scan (category +
            // facets + umbrella). Tracked so the post-loop reconcile can remove facets/categories
            // a still-matched game no longer has (a full scan must not just accumulate tags).
            var expectedTagsByGame = new Dictionary<Guid, HashSet<Guid>>();
            void AddExpectedTags(Guid gameId, IEnumerable<Guid> tagIds)
            {
                if (!expectedTagsByGame.TryGetValue(gameId, out var set))
                    set = expectedTagsByGame[gameId] = new HashSet<Guid>();
                foreach (var tagId in tagIds)
                    set.Add(tagId);
            }

            void BumpRunStats(string srcId, int tags, Guid gameId, bool accepted)
            {
                if (!runStatsBySource.TryGetValue(srcId, out var st))
                    runStatsBySource[srcId] = st = new SourceRunStats();
                st.Tags += tags;
                if (accepted)
                    st.AcceptedGames.Add(gameId);
            }

            foreach (var kvp in allEntries)
            {
                var sourceId = kvp.Key;

                foreach (var entry in kvp.Value)
                {
                    var tagName = EntryCategoryTags(entry);
                    var games = FindGamesByIndex(entry);
                    if (games.Count == 0)
                    {
                        var stats = statsBySource.GetOrAdd(entry.SourceId!, s => (0, 0, 0));
                        stats.TotalEntries++;
                        statsBySource[entry.SourceId!] = stats;
                        _lastUnmatchedEntries ??= new List<(string SourceName, string EntryName)>();
                        _lastUnmatchedEntries.Add((entry.SourceId ?? "unknown", entry.GameName));
                        _logger.Debug($"VRMod Tag Service: No match found for '{SafeLogText(entry.GameName)}' (source: {entry.SourceId ?? "unknown"})");

                        // No match — but the name rules may still have something to report. Always
                        // collected: LogScanSummary tells a user who never opens a dialog that these
                        // pairings exist. Only the review-everything pass turns them into rows, since
                        // the pending-match dialog is a dialog about matches and its Bulk Accept acts
                        // on every row in it — a row the rules merely suspect does not belong under a
                        // button that means "this is the game".
                        foreach (var suggestion in FindSuggestedGames(entry))
                        {
                            _lastSuggestions ??= new List<(string SourceName, string EntryName, string GameName, string Reason)>();
                            _lastSuggestions.Add((entry.SourceId ?? "unknown", entry.GameName,
                                suggestion.Game.Name, suggestion.Reason));
                            if (reviewAll)
                            {
                                reviewMatches.Add(MakeReviewMatch(sourceId, entry, suggestion.Game,
                                    suggestion.DisplayScore, MatchDecision.Pending, suggestion.Reason));
                            }
                        }
                        continue;
                    }

                    var entryStats = statsBySource.GetOrAdd(sourceId, s => (0, 0, 0));
                    entryStats.TotalEntries++;

                    // A source entry can match every store duplicate of the same game (e.g. one
                    // Epic + one Steam "Alien: Isolation"). Handle each matched game so every
                    // store instance is tagged, counted, and decided (by game Id) independently.
                    foreach (var game in games)
                    {
                        var decision = GetEntryDecision(sourceId, entry, game.Id);

                        switch (decision)
                        {
                            case MatchDecision.Accepted:
                                var appliedAccepted = ApplyEntryTags(game, entry);
                                AddExpectedTags(game.Id, appliedAccepted);
                                BumpRunStats(sourceId, appliedAccepted.Count, game.Id, true);
                                currentMatchedGames.Add(game.Id);
                                entryStats.Matches++;
                                statsBySource[sourceId] = entryStats;
                                _logger.Debug($"VRMod Tag Service: Auto-applied (accepted) '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}'");
                                // Count an already-accepted match whose source tags changed since the last
                                // accept (independent of reviewAll: the auto-scan shows only pending rows
                                // but still reports how many accepted matches changed).
                                CountChangedAcceptedMatch(sourceId, entry, game);
                                if (reviewAll)
                                    reviewMatches.Add(MakeReviewMatch(sourceId, entry, game, VrModNameMatcher.GetMatchScore(entry.GameName, game.Name, _settings.FuzzyMatchThreshold), MatchDecision.Accepted));
                                break;
                            case MatchDecision.Rejected:
                                entryStats.Rejected++;
                                statsBySource[sourceId] = entryStats;
                                _logger.Debug($"VRMod Tag Service: Auto-skipped (rejected) '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}'");
                                if (reviewAll)
                                    reviewMatches.Add(MakeReviewMatch(sourceId, entry, game, VrModNameMatcher.GetMatchScore(entry.GameName, game.Name, _settings.FuzzyMatchThreshold), MatchDecision.Rejected));
                                break;
                            case MatchDecision.Pending:
                                var score = VrModNameMatcher.GetMatchScore(entry.GameName, game.Name, _settings.FuzzyMatchThreshold);
                                if (score >= _settings.FuzzyMatchThreshold)
                                {
                                    if (reviewCallback == null)
                                    {
                                        var appliedPending = ApplyEntryTags(game, entry);
                                        AddExpectedTags(game.Id, appliedPending);
                                        BumpRunStats(sourceId, appliedPending.Count, game.Id, false);
                                        currentMatchedGames.Add(game.Id);
                                        entryStats.Matches++;
                                        statsBySource[sourceId] = entryStats;
                                        _logger.Debug($"VRMod Tag Service: Auto-applied (pending, no review) '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}' ({tagName})");
                                    }
                                    else
                                    {
                                        var status = score >= 1.0f ? MatchDecision.Accepted : MatchDecision.Pending;
                                        var match = MakeReviewMatch(sourceId, entry, game, score, status);
                                        pendingMatches.Add(match);
                                        if (reviewAll)
                                            reviewMatches.Add(match);
                                        entryStats.Matches++;
                                        statsBySource[sourceId] = entryStats;
                                    }
                                }
                                else
                                {
                                    _logger.Debug($"VRMod Tag Service: Skipped Pending match for '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}' (score: {score:F2} < threshold: {_settings.FuzzyMatchThreshold:F2})");
                                }
                                break;
                        }
                    }
                }
            }

            if (currentMatchedGames.Count > 0)
            {
                var umbrellaTag = EnsureTag(VrModCategories.TagUmbrella);
                foreach (var gameId in currentMatchedGames)
                {
                    // O(1) via the per-scan game-id index (a database scan would be O(games)).
                    if (_gameIdCache != null && _gameIdCache.TryGetValue(gameId, out var game))
                        AddTagToGame(game, umbrellaTag);
                    if (expectedTagsByGame.TryGetValue(gameId, out var expectedSet))
                        expectedSet.Add(umbrellaTag.Id);
                }
            }

            // Keep VRMod tags on games that still have a pending (review) match as
            // well as the auto-accepted ones; only games with no current match
            // (stale) or rejected matches should lose their tags. In review mode
            // currentMatchedGames holds only auto-accepted games while the pending
            // matches are collected separately in pendingMatches, so passing
            // currentMatchedGames alone would strip every pending game's tags — a
            // data loss if the user then cancels the review dialog.
            var keepGameIds = new HashSet<Guid>(currentMatchedGames);
            foreach (var m in pendingMatches)
                keepGameIds.Add(m.PlayniteGame.Id);
            if (failedSources > 0)
            {
                _logger.Warn($"VRMod Tag Service: {failedSources} source(s) failed this scan; skipping stale tag removal (previously applied tags are preserved).");
            }
            else
            {
                RemoveStaleTags(keepGameIds);
                // A full scan must also clean up facets/categories a still-matched game no
                // longer has (e.g. 6DOF dropped from a source entry) — RemoveStaleTags only
                // handles games that dropped out of every match entirely.
                ReconcileMatchedGameTags(expectedTagsByGame);

                // Maintain source links for every currently matched game: add links for
                // saved accepted matches, remove plugin links that are no longer desired.
                foreach (var gameId in keepGameIds)
                {
                    if (_gameIdCache == null || !_gameIdCache.TryGetValue(gameId, out var game))
                        continue;
                    if (ReconcileSourceLinksCore(game))
                        _api.Database.Games.Update(game);
                }
            }

            // Vocabulary rename (see MigrateLegacyTagNames). Deliberately outside the failed-source guard:
            // a rename preserves the claim a tag carries and reads no source data, so a source being
            // unreachable is no reason to leave a game holding the retired spelling.
            MigrateLegacyTagNames();

            // In reviewAll mode the callback gets every match (accepted + rejected + pending) so
            // the review dialog shows all matched games; otherwise just the pending ones.
            var matchesForReview = reviewAll ? reviewMatches : pendingMatches;
            if (reviewCallback != null && matchesForReview.Count != 0)
            {
                _logger.Debug($"VRMod Tag Service: Full scan: invoking reviewCallback with {matchesForReview.Count} match(es) (reviewAll={reviewAll}), thread={System.Environment.CurrentManagedThreadId}");
                reviewCallback(matchesForReview);
                _logger.Debug("VRMod Tag Service: Full scan: reviewCallback returned");
            }

            // Record each successfully-fetched source's apply counts as its run-log line (the
            // "Last run" view) - also 0/0, so a run that applied nothing says so explicitly.
            // Links are only counted when no source failed (the link reconcile is skipped then)
            // and links are on.
            foreach (var sourceId in succeededSourceIds)
            {
                int tags = runStatsBySource.TryGetValue(sourceId, out var st) ? st.Tags : 0;
                int links = failedSources == 0 && _settings.IsSourceLinksEnabled(sourceId) && st != null
                    ? st.AcceptedGames.Count
                    : 0;
                RecordSourceRunCounts(sourceId, tags, links);
            }

            LogScanSummary(statsBySource, currentMatchedGames, pendingMatches);

            _logger.Info($"VRMod Tag Service: Full scan complete. {currentMatchedGames.Count} game(s) tagged, {pendingMatches.Count} pending review.");
            return true;
        }

        /// <summary>
        /// Builds the full review-match list (accepted + rejected + pending) purely from the
        /// in-memory source cache — NO network fetch, NO tag application, NO stale-tag removal
        /// and NO link reconciliation. This is the "Review VR Mods" path: present the review
        /// dialog from what is already known, without running a scan.
        ///
        /// Rows follow the same collection rules as a full scan with <c>reviewAll</c>
        /// (see <see cref="ReviewRowStatus"/>): accepted and rejected mappings become rows with
        /// their decision; a pending mapping becomes a row only when its score is at or above
        /// the threshold (an exact 1.00 match as Accepted). Decisions are read from the same
        /// store a scan reads, so the rows show exactly what a scan would present.
        ///
        /// Returns <see langword="null"/> when no enabled source has cached data (e.g. a cold
        /// start before the startup scan populated the cache), so the caller can fall back to a
        /// real scan; an empty list means data is cached but nothing matches.
        /// </summary>
        public List<VrModReviewMatchEntry>? BuildReviewMatchesFromCache()
        {
            BuildGameIndex();

            var enabledSources = _registry.GetEnabledSources().ToList();
            if (enabledSources.Count == 0)
            {
                _logger.Info("VRMod Tag Service: Review from cache: no enabled sources; nothing to show.");
                return null;
            }

            var matches = new List<VrModReviewMatchEntry>();
            var haveData = false;

            foreach (var source in enabledSources)
            {
                // Whatever the cache holds (fresh or expired): this is the last known data.
                // Freshness only gates whether a scan RE-fetches — the dialog shows what is
                // known now; Full Scan is the refresh action.
                if (!_sourceCache.TryGetValue(source.SourceId, out var cached))
                    continue;
                haveData = true;
                var sourceId = source.SourceId;

                foreach (var entry in cached.Entries)
                {
                    var games = FindGamesByIndex(entry);
                    if (games.Count == 0)
                    {
                        // The same candidates a full scan would report: this dialog is where a
                        // suggestion is meant to be read and decided.
                        foreach (var suggestion in FindSuggestedGames(entry))
                        {
                            _lastSuggestions ??= new List<(string SourceName, string EntryName, string GameName, string Reason)>();
                            _lastSuggestions.Add((sourceId, entry.GameName, suggestion.Game.Name, suggestion.Reason));
                            matches.Add(MakeReviewMatch(sourceId, entry, suggestion.Game,
                                suggestion.DisplayScore, MatchDecision.Pending, suggestion.Reason));
                        }
                        continue;
                    }

                    foreach (var game in games)
                    {
                        var score = VrModNameMatcher.GetMatchScore(entry.GameName, game.Name, _settings.FuzzyMatchThreshold);
                        var status = ReviewRowStatus(GetEntryDecision(sourceId, entry, game.Id), score, _settings.FuzzyMatchThreshold);
                        if (status == null)
                            continue;
                        matches.Add(MakeReviewMatch(sourceId, entry, game, score, status.Value));
                    }
                }
            }

            if (!haveData)
            {
                _logger.Info("VRMod Tag Service: Review from cache: no cached source data (cold start). Caller should run a scan.");
                return null;
            }

            _logger.Info($"VRMod Tag Service: Review from cache: built {matches.Count} review match(es) from the in-memory source cache (no scan).");
            return matches;
        }

        /// <summary>
        /// The review-row status for a match built from a stored decision (the full "show all"
        /// review list): Accepted and Rejected mappings become rows with their decision; a
        /// Pending mapping becomes a row only when the score is at or above the threshold — an
        /// exact match (1.00) as Accepted, anything else as Pending. A Pending mapping below
        /// the threshold produces no row (null). Pure and testable.
        /// </summary>
        /// <param name="decision">The stored decision for the mapping (Pending = never decided).</param>
        /// <param name="score">The match score (0–1).</param>
        /// <param name="threshold">The fuzzy-match threshold (0–1).</param>
        /// <returns>The row status, or null for no row.</returns>
        internal static MatchDecision? ReviewRowStatus(MatchDecision decision, float score, float threshold)
        {
            switch (decision)
            {
                case MatchDecision.Accepted:
                    return MatchDecision.Accepted;
                case MatchDecision.Rejected:
                    return MatchDecision.Rejected;
                default: // Pending: reviewable only at or above the threshold.
                    if (score < threshold)
                        return null;
                    return score >= 1.0f ? MatchDecision.Accepted : MatchDecision.Pending;
            }
        }

        /// <summary>
        /// The decision key of one (source listing, Playnite game) mapping, built from the listing's
        /// stable identity rather than its current display name (see
        /// <see cref="VrModEntry.DecisionKeyComponent"/>). Every place in this service that reads,
        /// writes or clears a mapping's decision goes through it, so a listing that keeps its App ID
        /// or its page URL keeps its decision across a rename on the site.
        /// </summary>
        private static string EntryDecisionKey(string sourceId, VrModEntry entry, Guid gameId)
            => entry.MakeDecisionKey(sourceId, gameId);

        /// <summary>
        /// Decision lookup for a source entry, including the one-time carry-forward of a decision that
        /// was stored before decision keys carried a listing identity: when the identity-shaped key is
        /// empty but the entry's own name still matches a stored key, that stored decision (with its
        /// timestamp and tag baseline) is moved onto the identity key and returned.
        ///
        /// A scan is the only place that can recognise a legacy key, because it is the only place that
        /// holds an entry whose name is the one the legacy key was built from. Doing it here means the
        /// next time the site renames that listing there is nothing left to lose.
        /// </summary>
        private bool TryGetEntryDecision(string sourceId, VrModEntry entry, Guid gameId,
            out MatchDecision decision)
        {
            var identityKey = EntryDecisionKey(sourceId, entry, gameId);
            if (_settings.TryGetDecisionByKey(identityKey, out decision))
                return true;

            if (entry.DecisionKeyComponent == entry.GameName)
            {
                // This listing offers no identity of its own, so the key the settings already tried
                // IS the name key: there is no legacy shape left to look for.
                decision = MatchDecision.Pending;
                return false;
            }

            var legacyKey = VrModNameMatcher.MakeDecisionKey(sourceId, entry.GameName, gameId);
            if (!_settings.TryGetDecisionByKey(legacyKey, out decision))
            {
                decision = MatchDecision.Pending;
                return false;
            }

            _settings.TryRekeyDecision(legacyKey, identityKey);
            return true;
        }

        /// <summary>
        /// <see cref="TryGetEntryDecision"/> for callers that only need the decision.
        /// </summary>
        private MatchDecision GetEntryDecision(string sourceId, VrModEntry entry, Guid gameId)
            => TryGetEntryDecision(sourceId, entry, gameId, out var decision)
                ? decision
                : MatchDecision.Pending;

        /// <summary>
        /// Builds a <see cref="VrModReviewMatchEntry"/> for the review dialog from a matched
        /// entry. Centralizes the source lookup + field mapping so the scan's per-decision
        /// collection (accepted / rejected / pending) stays consistent.
        /// </summary>
        /// <param name="sourceId">The source that produced the entry.</param>
        /// <param name="entry">The source entry.</param>
        /// <param name="game">The matched Playnite game.</param>
        /// <param name="category">The VR mod category.</param>
        /// <param name="score">The match score (0-1).</param>
        /// <param name="status">The decision to record (Accepted / Rejected / Pending).</param>
        private VrModReviewMatchEntry MakeReviewMatch(string sourceId, VrModEntry entry, Game game, float score, MatchDecision status,
            string? suggestionReason = null)
        {
            var source = _registry.Sources.FirstOrDefault(s => s.SourceId == sourceId);
            // Per-mapping decision key (NO category): the decision is a property of the
            // source-game→game mapping, not the tags — one yes/no covers all of a mod's
            // categories. Keyed by the game's stable Id so store duplicates of the same name
            // each carry their own decision, and by the listing's identity (not its name) so the
            // mapping survives the site renaming the listing.
            var key = EntryDecisionKey(sourceId, entry, game.Id);
            var decidedAtUtc = _settings.EnsureDecisionTimestampUtc(key, DateTime.UtcNow);
            // The persisted (saved-to-JSON) decision + its save time, so the review dialog can
            // tell an already-saved mapping from a new (not-yet-saved) one. Pending means there is
            // no saved decision (the first-seen timestamp is not a saved decision).
            var persisted = _settings.TryGetDecisionByKey(key, out var storedDecision) ? storedDecision : MatchDecision.Pending;
            MatchDecision? persistedDecision = persisted == MatchDecision.Pending ? null : persisted;
            DateTime? savedAtUtc = persisted == MatchDecision.Pending ? null : _settings.GetDecisionTimestampUtc(key);
            // Changed-tag detection: only an already-ACCEPTED decision has a stored baseline
            // (a rejected match is "not the same game", a pending one has no saved decision),
            // so only then is there anything to diff the current source tags against.
            bool tagsChanged = false;
            string tagDeltaText = string.Empty;
            if (persistedDecision == MatchDecision.Accepted)
            {
                tagsChanged = ComputeTagDelta(_settings.GetSourceTags(key),
                    VrModCategories.TagNamesFor(entry), out tagDeltaText);
            }
            return new VrModReviewMatchEntry
            {
                SourceId = sourceId,
                SourceName = source?.DisplayName ?? sourceId,
                SourceGameName = entry.GameName,
                DecisionKey = key,
                SourceUrl = entry.SourceUrl ?? source?.WebsiteUrl,
                PlayniteGame = game,
                GameDisplayName = GetGameDisplayName(game),
                Score = score,
                Categories = entry.Categories,
                Is6DOF = entry.Is6DOF,
                IsFirstPerson = entry.IsFirstPerson,
                IsThirdPerson = entry.IsThirdPerson,
                Qualities = entry.Qualities,
                Motions = entry.Motions,
                ModStatuses = entry.Statuses,
                Status = status,
                DecidedAtUtc = decidedAtUtc,
                PersistedDecision = persistedDecision,
                SavedAtUtc = savedAtUtc,
                TagsChanged = tagsChanged,
                TagDeltaText = tagDeltaText,
                Reason = suggestionReason ?? string.Empty,
                IsSuggestion = suggestionReason != null
            };
        }

        /// <summary>
        /// Counts an already-accepted match whose source tags changed since the last accept into
        /// <see cref="ChangedAcceptedCount"/>. Only an accepted match has a stored baseline to diff
        /// against (a rejected match is "not the same game", a pending one has no saved decision).
        /// Kept separate from <see cref="MakeReviewMatch"/> (which builds the review row) so the
        /// auto-scan — which shows only pending rows — can still report how many accepted matches
        /// changed without collecting the accepted rows themselves.
        /// </summary>
        private void CountChangedAcceptedMatch(string sourceId, VrModEntry entry, Game game)
        {
            var key = EntryDecisionKey(sourceId, entry, game.Id);
            if (ComputeTagDelta(_settings.GetSourceTags(key), VrModCategories.TagNamesFor(entry), out _))
                _changedAcceptedCount++;
        }

        /// <summary>
        /// Computes the changed-tag delta for an already-accepted match: diffs the entry's
        /// current source tags (<paramref name="currentTags"/>) against the baseline stored when
        /// the match was last accepted (<paramref name="storedTags"/>). Returns true (and fills
        /// <paramref name="deltaText"/>) when the source added or removed a tag since the last
        /// accept. A missing baseline (<c>null</c> — the decision predates this feature) is
        /// reported as no change, so pre-existing decisions are not flagged. Pure function, no
        /// instance state, for direct unit testing.
        /// </summary>
        internal static bool ComputeTagDelta(List<string>? storedTags, IReadOnlyCollection<string> currentTags, out string deltaText)
        {
            if (storedTags == null)
            {
                deltaText = string.Empty;
                return false;
            }
            // Both sides go through CanonicalTagName: a baseline stored before the plugin renamed one of
            // its own tag names carries the same claim, and diffing it literally would put every old
            // decision back in front of the user as "changed".
            var storedNames = storedTags.Select(VrModCategories.CanonicalTagName).ToList();
            var currentNames = currentTags.Select(VrModCategories.CanonicalTagName).ToList();
            var added = currentNames.Where(c => !storedNames.Contains(c)).ToList();
            var removed = storedNames.Where(s => !currentNames.Contains(s)).ToList();
            if (added.Count == 0 && removed.Count == 0)
            {
                deltaText = string.Empty;
                return false;
            }
            var parts = new List<string>();
            if (added.Count > 0)
                parts.Add("+ " + string.Join("+ ", added));
            if (removed.Count > 0)
                parts.Add("- " + string.Join("- ", removed));
            deltaText = string.Join("  ", parts);
            return true;
        }

        public async Task ScanGameAsync(Game game, System.Action<List<VrModReviewMatchEntry>>? reviewCallback = null, System.Threading.CancellationToken cancellationToken = default)
        {
            // Same single-scan gate as RunFullScanAsync (see TryBeginScan): a coinciding trigger
            // returns here without touching state; the lease releases the gate on disposal.
            using var _ = TryBeginScan();
            if (_ == null) return;

            _logger.Info($"VRMod Tag Service: Scanning game '{SafeLogText(game.Name)}'...");
            BuildGameIndex();

            var enabledSources = _registry.GetEnabledSources().ToList();
            if (enabledSources.Count == 0)
                return;

            var (allEntries, failedSources, _) = await FetchAllEnabledSourcesAsync("single-game scan", cancellationToken);

            using (var buffer = _api.Database.BufferedUpdate())
            {
                ScanGameCore(game, allEntries, failedSources, reviewCallback);
            }
        }

        /// <summary>
        /// Scans a BATCH of new games in ONE gated scan: the single-scan gate is acquired once, all
        /// enabled sources are fetched (or cache-served) once, and the per-game match/apply core
        /// runs for every game. This is the OnLibraryUpdated path — a library update can add
        /// several games at once, and scanning them in one batch guarantees EACH game gets its
        /// tags (a per-game ScanGameAsync loop would be gate-skipped after the first).
        /// </summary>
        /// <param name="games">The new games to scan.</param>
        /// <returns>True if the games were scanned; false if the scan was not run (empty batch,
        /// a scan already held the single-scan gate, or no sources are enabled) — the caller
        /// (the new-game drain) uses this to re-queue the batch so it is not lost.</returns>
        public async Task<bool> ScanGamesAsync(IEnumerable<Game> games, System.Threading.CancellationToken cancellationToken = default)
        {
            var gameList = (games ?? Enumerable.Empty<Game>()).Where(g => g != null).ToList();
            if (gameList.Count == 0)
                return false;

            using var _ = TryBeginScan();
            if (_ == null) return false;

            _logger.Info($"VRMod Tag Service: Scanning {gameList.Count} new game(s)...");
            BuildGameIndex();

            var enabledSources = _registry.GetEnabledSources().ToList();
            if (enabledSources.Count == 0)
            {
                _logger.Warn("VRMod Tag Service: No sources enabled. Skipping new-game scan.");
                return false;
            }

            // Fetch (or cache-serve) ALL enabled sources ONCE for the whole batch — the per-game
            // core below then matches each game against these shared entries.
            var (allEntries, failedSources, _) = await FetchAllEnabledSourcesAsync("new-game scan", cancellationToken);

            // One buffered-update window for the whole batch (one flush at the end), like a full
            // scan; each game's tags/links are applied inside it.
            using (var buffer = _api.Database.BufferedUpdate())
            {
                foreach (var game in gameList)
                    ScanGameCore(game, allEntries, failedSources, null);
            }

            _logger.Info($"VRMod Tag Service: New-game scan complete for {gameList.Count} game(s).");
            return true;
        }

        /// <summary>
        /// The per-game match/apply core shared by <see cref="ScanGameAsync"/> and
        /// <see cref="ScanGamesAsync"/>: matches the game against the (already-fetched) source
        /// entries, applies/keeps the matched VRMod tags, removes stale ones, and reconciles
        /// source links. The caller owns the single-scan gate and the database buffered-update
        /// window. reviewCallback null = auto-apply (no review dialog); otherwise pending
        /// matches are collected and handed to it.
        /// </summary>
        private void ScanGameCore(Game game, ConcurrentDictionary<string, ConcurrentBag<VrModEntry>> allEntries, int failedSources, System.Action<List<VrModReviewMatchEntry>>? reviewCallback)
        {
            var currentMatchedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var has6DOF = false;
            var qualityTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var motionTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var statusTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingMatches = new List<VrModReviewMatchEntry>();

            var normalizedGame = GetNormalizedGameName(game);
            var threshold = _settings.FuzzyMatchThreshold;

            foreach (var kvp in allEntries)
            {
                var sourceId = kvp.Key;

                foreach (var entry in kvp.Value)
                {
                    // Entries the user has never reviewed are the overwhelming majority of this loop,
                    // and almost all of them cannot reach the threshold with this game's name: skip
                    // those on the length bound before doing any name work.
                    // Entries that DO carry a stored decision are never pruned: a decision can come
                    // from a Steam App ID match, where the two names are unrelated by design, and
                    // pruning those would silently drop a decision the user made.
                    if (!TryGetEntryDecision(sourceId, entry, game.Id, out var decision))
                    {
                        var normalizedEntry = GetNormalizedEntryName(entry.GameName);
                        if (!VrModNameMatcher.CanReachSimilarity(normalizedEntry, normalizedGame, threshold))
                            continue;
                        decision = MatchDecision.Pending;
                    }

                    switch (decision)
                    {
                        case MatchDecision.Accepted:
                            foreach (var cat in entry.Categories) currentMatchedCategories.Add(cat);
                            if (entry.Is6DOF == true) has6DOF = true;
                            foreach (var q in entry.Qualities) qualityTags.Add(q);
                            foreach (var m in entry.Motions) motionTags.Add(m);
                            foreach (var s in entry.Statuses) statusTags.Add(s);
                            _logger.Info($"VRMod Tag Service: Auto-applied (accepted) '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}' ({CategoryTagSummary(entry)})");
                            break;
                        case MatchDecision.Rejected:
                            _logger.Info($"VRMod Tag Service: Auto-skipped (rejected) '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}'");
                            break;
                        case MatchDecision.Pending:
                            var score = VrModNameMatcher.GetMatchScoreNormalized(GetNormalizedEntryName(entry.GameName), normalizedGame, threshold);
                            if (score >= threshold)
                            {
                                if (reviewCallback == null)
                                {
                                    foreach (var cat in entry.Categories) currentMatchedCategories.Add(cat);
                                    if (entry.Is6DOF == true) has6DOF = true;
                                    foreach (var q in entry.Qualities) qualityTags.Add(q);
                                    foreach (var m in entry.Motions) motionTags.Add(m);
                                    foreach (var s in entry.Statuses) statusTags.Add(s);
                                    _logger.Info($"VRMod Tag Service: Auto-applied (pending, no review) '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}' ({CategoryTagSummary(entry)})");
                                }
                                else
                                {
                                    var src = _registry.Sources.FirstOrDefault(s => s.SourceId == sourceId);
                                    pendingMatches.Add(new VrModReviewMatchEntry
                                    {
                                        SourceId = sourceId,
                                        SourceName = src?.DisplayName ?? sourceId,
                                        SourceGameName = entry.GameName,
                                        DecisionKey = entry.MakeDecisionKey(sourceId, game.Id),
                                        SourceUrl = entry.SourceUrl ?? src?.WebsiteUrl,
                                        PlayniteGame = game,
                                        GameDisplayName = GetGameDisplayName(game),
                                        Score = score,
                                        Categories = entry.Categories,
                                        Is6DOF = entry.Is6DOF,
                                        Qualities = entry.Qualities,
                                        Motions = entry.Motions,
                                        ModStatuses = entry.Statuses,
                                        Status = score >= 1.0f ? MatchDecision.Accepted : MatchDecision.Pending
                                    });
                                }
                            }
                            else
                            {
                                // "Pending" is also what GetDecision answers for every entry nobody
                                // has ever decided about, so this branch runs against the entire catalog
                                // for every game scanned. A -1 score is the matcher's "cannot reach the
                                // threshold by any edit distance" prune, not a near miss: one renamed
                                // game logged 3173 of these, and Playnite rotates extension logs at a
                                // few MB, which would evict the lines that actually matter.
                                if (score > 0f)
                                    _logger.Debug($"VRMod Tag Service: Skipped Pending match for '{SafeLogText(entry.GameName)}' -> '{SafeLogText(game.Name)}' (score: {score:F2} < threshold: {_settings.FuzzyMatchThreshold:F2})");
                            }
                            break;
                    }
                }
            }

            if (currentMatchedCategories.Count > 0)
            {
                var umbrellaTag = EnsureTag(VrModCategories.TagUmbrella);
                foreach (var cat in currentMatchedCategories)
                {
                    if (VrModCategories.IsCatalogCategory(cat))
                        continue;
                    var tagName = VrModCategories.TagNameFor(cat);
                    var tag = EnsureTag(tagName);
                    AddTagToGame(game, tag);
                }
                if (has6DOF)
                    AddTagToGame(game, EnsureTag(VrModCategories.Tag6DOF));
                foreach (var q in qualityTags)
                    AddTagToGame(game, EnsureTag(VrModCategories.TagNameForQuality(q)));
                foreach (var m in motionTags)
                    AddTagToGame(game, EnsureTag(VrModCategories.TagNameForMotion(m)));
                foreach (var s in statusTags)
                    AddTagToGame(game, EnsureTag(VrModCategories.TagNameForStatus(s)));
                AddTagToGame(game, umbrellaTag);
            }

            var existingVrTagIds = (game.TagIds ?? new List<Guid>()).ToList();
            var currentTagIds = new HashSet<Guid>();
            foreach (var cat in currentMatchedCategories)
            {
                if (VrModCategories.IsCatalogCategory(cat))
                    continue;
                var tag = EnsureTag(VrModCategories.TagNameFor(cat));
                currentTagIds.Add(tag.Id);
            }
            if (currentMatchedCategories.Count > 0)
            {
                var umbrellaTagForRemoval = EnsureTag(VrModCategories.TagUmbrella);
                currentTagIds.Add(umbrellaTagForRemoval.Id);
                if (has6DOF)
                    currentTagIds.Add(EnsureTag(VrModCategories.Tag6DOF).Id);
                foreach (var q in qualityTags)
                    currentTagIds.Add(EnsureTag(VrModCategories.TagNameForQuality(q)).Id);
                foreach (var m in motionTags)
                    currentTagIds.Add(EnsureTag(VrModCategories.TagNameForMotion(m)).Id);
                foreach (var s in statusTags)
                    currentTagIds.Add(EnsureTag(VrModCategories.TagNameForStatus(s)).Id);
            }

            if (failedSources > 0)
            {
                _logger.Warn($"VRMod Tag Service: {failedSources} source(s) failed this scan; skipping stale tag removal for '{SafeLogText(game.Name)}' (previously applied tags are preserved).");
            }
            else
            {
                var changed = false;
                foreach (var tagId in existingVrTagIds)
                {
                    var tag = GetCachedOrDbTag(tagId);
                    if (tag != null && VrModCategories.IsPluginTagName(tag.Name) && !currentTagIds.Contains(tagId))
                    {
                        game.TagIds?.Remove(tagId);
                        changed = true;
                        _logger.Info($"VRMod Tag Service: Removed stale tag '{SafeLogText(tag.Name)}' from '{SafeLogText(game.Name)}'");
                    }
                }
                // Maintain source links for this game (add for saved accepted matches,
                // remove plugin links that are no longer desired).
                if (ReconcileSourceLinksCore(game))
                    changed = true;
                if (changed)
                    _api.Database.Games.Update(game);
            }

            if (reviewCallback != null && pendingMatches.Count != 0)
            {
                reviewCallback(pendingMatches);
            }

            _logger.Info($"VRMod Tag Service: Game scan complete for '{SafeLogText(game.Name)}'.");
        }

        /// <summary>
        /// Takes <c>_newGameLock</c> and records the hold on the calling thread; disposing the
        /// return value releases it. Use as <c>using (NewGameLock()) { ... }</c> - the one
        /// sanctioned way to take this lock, because <see cref="EnsureUiHopIsSafe"/> can only see
        /// holds made here. Semantics are identical to a <c>lock</c> statement: Monitor.Enter/Exit
        /// in a try/finally, reentrant on the same thread (the depth counts nested holds).
        /// </summary>
        internal IDisposable NewGameLock()
        {
            Monitor.Enter(_newGameLock);
            _newGameLockDepth++;
            return new NewGameLockRelease(_newGameLock);
        }

        /// <summary>
        /// True when the calling thread currently holds <c>_newGameLock</c> through
        /// <see cref="NewGameLock"/>.
        /// </summary>
        internal static bool CurrentThreadHoldsNewGameLock => _newGameLockDepth > 0;

        /// <summary>
        /// The invariant a UI hop depends on: a thread that holds <c>_newGameLock</c> must not wait
        /// on Playnite's UI thread. That pair deadlocks - the waiting side cannot release the lock
        /// because it is parked in the dispatcher wait, while the UI thread (which pumps Playnite's
        /// library-change events, and those take this lock, see <see cref="NotifyLibraryUpdated"/>)
        /// cannot run while it is blocked behind that wait. Playnite's progress window is opened with
        /// <c>ShowDialog</c>, so the UI thread does pump during a scan - and what it pumps can be
        /// exactly the handler that needs this lock. This is the shape that once stalled a drain for
        /// the whole timeout the hop used to carry, and the hop no longer carries a timeout.
        ///
        /// It fails here instead of hanging: the log names the cause, and the exception ends the
        /// caller's progress action, which Playnite reports as a failed operation that the review
        /// dialog now surfaces (see <see cref="VrModReviewDialog.ProgressFailureText"/>). Only holds
        /// taken through <see cref="NewGameLock"/> are visible - a raw <c>lock (_newGameLock)</c>
        /// bypasses this, which is why the field's comment forbids it.
        /// </summary>
        internal void EnsureUiHopIsSafe()
        {
            if (_newGameLockDepth == 0)
                return;
            var message = "VRMod Tag Service: refused to wait on Playnite's UI thread while holding "
                + "the new-game lock (lock-order inversion: the UI thread pumps library-change "
                + "handlers that take that lock). See the Threading contract in AGENTS.md.";
            _logger.Error(message);
            throw new InvalidOperationException(message);
        }

        /// <summary>Releases the new-game hold recorded by <see cref="NewGameLock"/>.</summary>
        private sealed class NewGameLockRelease : IDisposable
        {
            private readonly object _monitor;
            private bool _released;

            public NewGameLockRelease(object monitor)
            {
                _monitor = monitor;
            }

            public void Dispose()
            {
                if (_released)
                    return;
                _released = true;
                _newGameLockDepth--;
                Monitor.Exit(_monitor);
            }
        }

        /// <summary>
        /// Seeds the known-game set with every game currently in the library. Called from
        /// OnApplicationStarted (before the startup full scan runs): games present at seed time
        /// are covered by that full scan, so only games added to the library AFTER seeding are
        /// "new" and routed through <see cref="NotifyGamesAdded"/> / <see cref="NotifyLibraryUpdated"/>.
        /// Seeding is also where the library change events are subscribed: the baseline must exist
        /// before the first event can be judged against it.
        /// </summary>
        public void SeedKnownGames()
        {
            // The library walk happens OUTSIDE _newGameLock (see the lock-order note at the field):
            // enumerating Playnite's games is Playnite's work, over a LiteDB result set, with a
            // duration this class cannot bound. Holding our own lock across it would make every
            // library-change event thread - which arrives on Playnite's thread, and on a big library
            // that walk is seconds long - wait for it.
            var ids = SnapshotGameIds();
            using (NewGameLock())
                _knownGameIds = ids;
            SubscribeToLibraryChanges();
            _logger.Info("VRMod Tag Service: Seeded known games (new-game detection baseline).");
        }

        /// <summary>
        /// Reads the current library's game Ids into a local set. Exists so the only walks of
        /// <c>Database.Games</c> that happen near new-game state happen OUTSIDE that state's lock.
        /// A game added during the walk is not lost: Playnite raises <c>ItemCollectionChanged</c>
        /// for it, and the next drain re-reads the library.
        /// </summary>
        private HashSet<Guid> SnapshotGameIds()
        {
            var ids = new HashSet<Guid>();
            foreach (var g in _api.Database.Games)
                if (g != null && g.Id != Guid.Empty)
                    ids.Add(g.Id);
            return ids;
        }

        /// <summary>
        /// Subscribes to Playnite's per-game add/remove events. This is the trigger that actually
        /// sees a new game: Playnite raises OnLibraryUpdated only after an emulator/ROM library
        /// import, so manual adds, store-integration imports and "from installed" adds are invisible
        /// to it. Idempotent; paired with <see cref="UnsubscribeFromLibraryChanges"/> on dispose.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Attaching the handler only faults when Playnite's collection is already torn down (an unload racing startup). The full scan still covers those games, so the subscribe failure is logged and the service keeps working on its other triggers.")]
        public void SubscribeToLibraryChanges()
        {
            using (NewGameLock())
            {
                if (_libraryEventsSubscribed)
                    return;
                _libraryEventsSubscribed = true;
            }

            try
            {
                _api.Database.Games.ItemCollectionChanged += OnGamesItemCollectionChanged;
                _api.Database.Games.ItemUpdated += OnGamesItemUpdated;
                // Logged because "did Playnite ever call us?" is otherwise unanswerable from a
                // live session: without this line a missing tag looks like a matcher bug instead
                // of a missing event.
                _logger.Debug("VRMod Tag Service: subscribed to library collection changes.");
            }
            catch (Exception ex)
            {
                // Attach only faults if Playnite's collection is already torn down (plugin unload
                // racing startup). The full scan still covers those games, so a failed subscribe is
                // logged and the service keeps working on its other triggers.
                using (NewGameLock())
                    _libraryEventsSubscribed = false;
                _logger.Error($"VRMod Tag Service: could not subscribe to library change events: {ex}");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Detaching from a collection Playnite has already destroyed throws, and the handler dies with that collection anyway. Nothing the plugin can still do about it, so it is logged at debug level.")]
        public void UnsubscribeFromLibraryChanges()
        {
            using (NewGameLock())
            {
                if (!_libraryEventsSubscribed)
                    return;
                _libraryEventsSubscribed = false;
            }

            try
            {
                _api.Database.Games.ItemCollectionChanged -= OnGamesItemCollectionChanged;
                _api.Database.Games.ItemUpdated -= OnGamesItemUpdated;
            }
            catch (Exception ex)
            {
                // Playnite's collection is already gone (plugin unload during shutdown): the handler
                // dies with it, so a failed detach is not a fault worth reporting.
                _logger.Debug($"VRMod Tag Service: library change event detach failed: {ex}");
            }

            _newGameDebounceTimer?.Dispose();
            _newGameDebounceTimer = null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "An event handler that throws unwinds Playnite's own database write, so the game the user just added would be added by Playnite and never reported to us. Anything the handler faults on is deferred to the next scan instead.")]
        private void OnGamesItemCollectionChanged(object? sender, ItemCollectionChangedEventArgs<Game> args)
        {
            try
            {
                // Debug-level and one line per event: the only direct evidence, in a real Playnite
                // session, of which add paths reach the plugin at all.
                _logger.Debug($"VRMod Tag Service: library collection changed (+{args.AddedItems?.Count ?? 0} / -{args.RemovedItems?.Count ?? 0}).");
                NotifyGamesAdded(args.AddedItems);
            }
            catch (Exception ex)
            {
                _logger.Error($"VRMod Tag Service: new-game event handler faulted: {ex}");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Same contract as the add handler: a handler that throws unwinds Playnite's own write, which would corrupt the very edit the user just made. A rename we miss is picked up by the next full scan.")]
        private void OnGamesItemUpdated(object? sender, ItemUpdatedEventArgs<Game> args)
        {
            try
            {
                var renamed = new List<Game>();
                if (args.UpdatedItems != null)
                {
                    foreach (var update in args.UpdatedItems)
                    {
                        // Playnite raises this for every edit: playtime, completion status, icon,
                        // links, sorting name. Only the name can change which VR mod entry a game
                        // matches, and "Add game → Manually…" is the case that matters most: Playnite
                        // inserts a placeholder game (named "New Game") the moment the dialog opens —
                        // that add is what reaches the collection-changed handler — and the user's
                        // Save is an Update that renames it. Without this path the finished title is
                        // only matched again by the next scheduled full scan.
                        var game = update.NewData;
                        var oldName = update.OldData?.Name;
                        if (game == null || game.Id == Guid.Empty || string.IsNullOrWhiteSpace(game.Name))
                            continue;
                        if (oldName != null && string.Equals(oldName, game.Name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        renamed.Add(game);
                    }
                }

                if (renamed.Count == 0)
                    return;

                _logger.Debug($"VRMod Tag Service: library update event, {renamed.Count} game(s) renamed.");
                NotifyGamesRenamed(renamed);
            }
            catch (Exception ex)
            {
                _logger.Error($"VRMod Tag Service: game update event handler faulted: {ex}");
            }
        }

        /// <summary>
        /// A game Playnite says was just added to the library: mark it known (so the diff path does
        /// not report it a second time) and queue it for the fast per-game scan. Removals are not
        /// interesting here; a removed game's tags and decisions are handled by the reconcile path.
        /// </summary>
        public void NotifyGamesAdded(IEnumerable<Game>? addedGames)
        {
            if (addedGames == null)
                return;

            var newIds = new List<Guid>();
            using (NewGameLock())
            {
                // Playnite raises collection events while it is still starting up, before
                // OnApplicationStarted has seeded. With no baseline the event is the only evidence
                // there is, and it is authoritative about who was added, so those games are queued
                // anyway instead of being swallowed as "already known".
                _knownGameIds ??= new HashSet<Guid>();
                foreach (var game in addedGames)
                {
                    if (game == null || game.Id == Guid.Empty)
                        continue;
                    if (_knownGameIds.Add(game.Id))
                    {
                        _pendingNewGameIds.Add(game.Id);
                        newIds.Add(game.Id);
                    }
                }
            }

            if (newIds.Count == 0)
                return;

            _logger.Info($"VRMod Tag Service: Library changed: {newIds.Count} new game(s) queued for a VR mod scan.");
            ScheduleNewGameDrain();
        }

        /// <summary>
        /// A game whose name Playnite just changed. Matching keys off the name, so the new title has
        /// to be matched again. These games are already in the known-game baseline, so they cannot go
        /// through <see cref="NotifyGamesAdded"/> (which drops anything already known). Batched and
        /// debounced exactly like adds: one rename or a hundred cost a single source fetch.
        /// </summary>
        public void NotifyGamesRenamed(IEnumerable<Game>? renamedGames)
        {
            if (renamedGames == null)
                return;

            var queued = new List<Guid>();
            using (NewGameLock())
            {
                _knownGameIds ??= new HashSet<Guid>();
                foreach (var game in renamedGames)
                {
                    if (game == null || game.Id == Guid.Empty)
                        continue;
                    _knownGameIds.Add(game.Id);
                    if (_pendingNewGameIds.Add(game.Id))
                        queued.Add(game.Id);
                }
            }

            if (queued.Count == 0)
                return;

            _logger.Info($"VRMod Tag Service: Library changed: {queued.Count} renamed game(s) queued for a VR mod scan.");
            ScheduleNewGameDrain();
        }

        /// <summary>
        /// Runs <see cref="TryDrainNewGames"/> once the add burst has settled. Re-arming the one-shot
        /// timer on every event slides the deadline, so a bulk import drains as a single batch.
        /// </summary>
        private void ScheduleNewGameDrain()
        {
            Timer? timer;
            using (NewGameLock())
            {
                _newGameDebounceTimer ??= new Timer(_ => TryDrainNewGames(), null, Timeout.Infinite, Timeout.Infinite);
                timer = _newGameDebounceTimer;
            }

            try
            {
                timer?.Change(_newGameDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Unsubscribe/dispose raced this schedule. The batch is not lost: the pending Ids
                // stay queued and the next scan completion (ReleaseScanGate) drains them.
            }
        }

        /// <summary>
        /// Called from OnLibraryUpdated (Playnite fires it after a library update adds games AND
        /// downloads their "normal" metadata). Detects games added since the last seed/notify,
        /// marks them known, and schedules a batch scan for them. None are lost even if a scan
        /// holds the single-scan gate: they are kept pending and drained when that scan
        /// completes (see <see cref="TryDrainNewGames"/>).
        /// </summary>
        public void NotifyLibraryUpdated()
        {
            // Snapshot first, diff under the lock - the same leaf-lock rule as SeedKnownGames: the
            // walk of Playnite's collection must not happen while _newGameLock is held, because this
            // runs on Playnite's thread and the drain path on a timer thread wants the same lock.
            var currentIds = SnapshotGameIds();

            List<Guid> newIds;
            using (NewGameLock())
            {
                if (_knownGameIds == null)
                {
                    // Unseeded (defensive; OnApplicationStarted seeds before OnLibraryUpdated can
                    // fire). Adopt all current games as known so we never mass-scan; report nothing.
                    _knownGameIds = currentIds;
                    return;
                }

                newIds = new List<Guid>();
                foreach (var id in currentIds)
                {
                    if (_knownGameIds.Add(id))
                        newIds.Add(id);
                }
                foreach (var id in newIds)
                    _pendingNewGameIds.Add(id);
            }

            if (newIds.Count == 0)
                return;

            _logger.Info($"VRMod Tag Service: Library updated: {newIds.Count} new game(s) queued for a VR mod scan.");
            TryDrainNewGames();
        }

        /// <summary>
        /// Scans the pending new games (if any): snapshots the pending Ids, resolves them to live
        /// Game objects, and runs ONE gated batch scan (sources fetched once). Called from
        /// NotifyLibraryUpdated and after EVERY scan completes (see <see cref="ReleaseScanGate"/>),
        /// so a new game detected while a scan held the gate is still scanned. No-op when nothing
        /// is pending.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "A faulted drain re-queues its batch for the next scan; letting it escape would end the drain loop.")]
        private void TryDrainNewGames()
        {
            List<Guid> batch;
            using (NewGameLock())
            {
                if (_pendingNewGameIds.Count == 0)
                    return;
                batch = new List<Guid>(_pendingNewGameIds);
                _pendingNewGameIds.Clear();
            }

            var games = new List<Game>();
            foreach (var g in _api.Database.Games)
                if (batch.Contains(g.Id))
                    games.Add(g);
            if (games.Count == 0)
                return;

            // Background: the drain may run on a scan-completion thread; ScanGamesAsync acquires
            // the (just-freed) gate and fetches sources, so keep it off the caller's thread. If
            // the batch could not be scanned (a scan still holds the gate, or none was possible),
            // the batch is put back so the NEXT trigger (scan completion / library update) drains
            // it — a new game is never lost, only deferred.
            _ = Task.Run(async () =>
            {
                try
                {
                    // A bulk rename (a metadata-provider pass, a library import, pasting a curated VR
                    // list) arrives here as one batch, and per-game scans are the wrong shape for it
                    // (see MaxPerGameScanBatch): one catalog walk per game. Past that count the batch
                    // goes through the entry-driven full scan, which walks the catalog once.
                    var useFullScan = games.Count > MaxPerGameScanBatch;
                    if (useFullScan)
                        _logger.Info($"VRMod Tag Service: {games.Count} new/renamed game(s) queued at once; running one full library scan instead of {games.Count} per-game scans.");

                    var scanned = useFullScan
                        ? await RunFullScanAsync(null, false)
                        : await ScanGamesAsync(games);
                    if (!scanned)
                        RequeueNewGames(batch);
                }
                catch (Exception ex)
                {
                    _logger.Error($"VRMod Tag Service: new-game drain scan faulted: {ex}");
                    RequeueNewGames(batch);
                }
            });
        }

        /// <summary>
        /// Returns a just-taken batch of pending new-game Ids to the pending set (after a scan
        /// attempt that did not run, e.g. gate-skipped), so a later trigger drains them.
        /// </summary>
        private void RequeueNewGames(IEnumerable<Guid> ids)
        {
            using (NewGameLock())
            {
                foreach (var id in ids)
                    _pendingNewGameIds.Add(id);
            }
        }

        /// <summary>
        /// Every entry currently held in the per-source cache. A view of state rather than an
        /// operation, so it is exposed as a property.
        /// </summary>
        public IEnumerable<VrModEntry> AllCachedEntries
        {
            get
            {
                foreach (var kvp in _sourceCache)
                {
                    foreach (var entry in kvp.Value.Entries)
                    {
                        yield return entry;
                    }
                }
            }
        }

        private static readonly string[] DecisionKeySeparators = { "||" };

        public IEnumerable<VrModEntry> GetVrModEntriesForGame(Game game, IEnumerable<VrModEntry> allEntries)
        {
            var normalizedGame = GetNormalizedGameName(game);

            foreach (var entry in allEntries)
            {
                var normalizedEntry = NormalizeForMatchFast(entry.GameName);

                if (VrModNameMatcher.GetMatchScore(normalizedEntry, normalizedGame, _settings.FuzzyMatchThreshold) >= _settings.FuzzyMatchThreshold)
                {
                    yield return entry;
                }
            }
        }

        public List<MetadataNameProperty> GetVrTagsForGame(Game game)
        {
            var tags = new List<MetadataNameProperty>();
            var tagIds = game.TagIds ?? new List<Guid>();
            var tagLookup = _tagIdCache ?? new Dictionary<Guid, Tag>();

            foreach (var tagId in tagIds)
            {
                if (!tagLookup.TryGetValue(tagId, out var tag))
                {
                    tag = _api.Database.Tags.FirstOrDefault(t => t.Id == tagId);
                    if (tag != null)
                        tagLookup[tagId] = tag;
                }
                if (tag != null && VrModCategories.IsPluginTagName(tag.Name))
                {
                    tags.Add(new MetadataNameProperty(tag.Name));
                }
            }

            return tags;
        }

        public List<MetadataNameProperty> ComputeTagsForGame(Game game)
        {
            var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tags = new List<MetadataNameProperty>();
            var allEntries = AllCachedEntries.ToList();

            if (allEntries.Count == 0)
                return tags;

            var normalizedGame = GetNormalizedGameName(game);

            foreach (var entry in allEntries)
            {
                var normalizedEntry = NormalizeForMatchFast(entry.GameName);

                if (VrModNameMatcher.GetMatchScore(normalizedEntry, normalizedGame, _settings.FuzzyMatchThreshold) >= _settings.FuzzyMatchThreshold)
                {
                    // One category tag per (non-catalog) category — a mod may have several.
                    foreach (var category in entry.Categories)
                    {
                        var tagName = VrModCategories.TagNameFor(category);
                        if (!VrModCategories.IsCatalogCategory(category) && tagNames.Add(tagName))
                        {
                            tags.Add(new MetadataNameProperty(tagName));
                        }
                    }
                    if (entry.Is6DOF == true && tagNames.Add(VrModCategories.Tag6DOF))
                    {
                        tags.Add(new MetadataNameProperty(VrModCategories.Tag6DOF));
                    }
                    foreach (var q in entry.Qualities)
                    {
                        var qTag = VrModCategories.TagNameForQuality(q);
                        if (tagNames.Add(qTag))
                            tags.Add(new MetadataNameProperty(qTag));
                    }
                    foreach (var m in entry.Motions)
                    {
                        var mTag = VrModCategories.TagNameForMotion(m);
                        if (tagNames.Add(mTag))
                            tags.Add(new MetadataNameProperty(mTag));
                    }
                    foreach (var s in entry.Statuses)
                    {
                        var sTag = VrModCategories.TagNameForStatus(s);
                        if (tagNames.Add(sTag))
                            tags.Add(new MetadataNameProperty(sTag));
                    }
                }
            }

            return tags;
        }

        private Tag EnsureTag(string tagName)
        {
            if (_tagCache != null && _tagCache.TryGetValue(tagName, out var cached))
                return cached;

            var tag = _api.Database.Tags.FirstOrDefault(t => t.Name == tagName);
            if (tag == null)
                tag = _api.Database.Tags.Add(tagName);

            _tagCache?.Add(tagName, tag);
            if (_tagIdCache != null && !_tagIdCache.ContainsKey(tag.Id))
                _tagIdCache[tag.Id] = tag;
            return tag;
        }

        private Tag? GetCachedOrDbTag(Guid tagId)
        {
            if (_tagIdCache != null && _tagIdCache.TryGetValue(tagId, out var cached))
                return cached;
            return _api.Database.Tags.FirstOrDefault(t => t.Id == tagId);
        }

        private void AddTagToGame(Game game, Tag tag)
        {
            if (game.TagIds == null)
                game.TagIds = new List<Guid>();
            if (!game.TagIds.Contains(tag.Id))
            {
                game.TagIds.Add(tag.Id);
                // In-place list mutation is not tracked by the database —
                // Update() is required for the change to persist to games.db.
                _api.Database.Games.Update(game);
            }
        }

        /// <summary>
        /// Applies the primary category tag plus any orthogonal capability facet
        /// tags (e.g. the 6DOF tag) for a matched entry to a game. Returns the ids of
        /// every tag it applied (the category tag plus any facet tags), so a caller can
        /// track the exact tag set a scan expects on each game and reconcile away stale
        /// facets/categories on later scans (see ReconcileMatchedGameTags).
        /// </summary>
        private List<Guid> ApplyEntryTags(Game game, VrModEntry entry)
        {
            // Apply every tag this entry's own set names (the category tag and any facets).
            // A catalog category contributes none of these (see IsCatalogCategory), so a
            // catalog match applies no tag of its own - only the umbrella, which the caller
            // adds at the game level. TagNamesFor is the single source of truth shared with
            // the review-dialog preview and the changed-tag detection, so all three stay in
            // lockstep.
            var applied = new List<Guid>();
            foreach (var name in VrModCategories.TagNamesFor(entry))
            {
                var tag = EnsureTag(name);
                AddTagToGame(game, tag);
                applied.Add(tag.Id);
            }
            return applied;
        }

        private void LogScanSummary(ConcurrentDictionary<string, (int TotalEntries, int Matches, int Rejected)> statsBySource, HashSet<Guid> matchedGames, List<VrModReviewMatchEntry> pendingMatches)
        {
            _logger.Info("VRMod Tag Service: === Scan Summary ===");

            int totalEntries = 0, totalMatches = 0, totalRejected = 0;
            foreach (var kvp in statsBySource)
            {
                var sourceId = kvp.Key;
                var total = kvp.Value.TotalEntries;
                var matches = kvp.Value.Matches;
                var rejected = kvp.Value.Rejected;
                var source = _registry.Sources.FirstOrDefault(s => s.SourceId == sourceId);
                var displayName = source?.DisplayName ?? sourceId;
                _logger.Info($"VRMod Tag Service:   [{displayName}] Total: {total}, Matches: {matches}, Rejected: {rejected}");
                totalEntries += total;
                totalMatches += matches;
                totalRejected += rejected;
            }
            _logger.Info($"VRMod Tag Service: Total entries scanned: {totalEntries}, Total matches: {totalMatches}, Total rejected: {totalRejected}");

            if (pendingMatches.Count != 0)
            {
                var accepted = pendingMatches.Count(m => m.Status == MatchDecision.Accepted);
                var stillPending = pendingMatches.Count(m => m.Status == MatchDecision.Pending);
                _logger.Info($"VRMod Tag Service: Pending review: {pendingMatches.Count} (auto-accepted: {accepted}, still pending: {stillPending})");
            }

            if (matchedGames.Count > 0)
            {
                _logger.Info($"VRMod Tag Service: Tagged games: {matchedGames.Count}");
                foreach (var gameId in matchedGames)
                {
                    // O(1) via the per-scan game-id index (a database scan would be O(games)).
                    if (_gameIdCache != null && _gameIdCache.TryGetValue(gameId, out var game))
                        _logger.Debug($"VRMod Tag Service:   - {SafeLogText(game.Name)}");
                }
            }

            if (_lastUnmatchedEntries != null && _lastUnmatchedEntries.Count > 0)
            {
                var topSources = _lastUnmatchedEntries
                    .GroupBy(e => e.SourceName)
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .ToList();

                _logger.Info($"VRMod Tag Service: {_lastUnmatchedEntries.Count} source entries did not match any game:");
                foreach (var src in topSources)
                {
                    _logger.Info($"VRMod Tag Service:   [{src.Key}] {src.Count()} unmatched: {string.Join(", ", src.Take(3).Select(e => $"\"{SafeLogText(e.EntryName)}\""))}");
                }
                if (_lastUnmatchedEntries.Count > 15)
                {
                    _logger.Info($"VRMod Tag Service:   ... and {_lastUnmatchedEntries.Count - 15} more");
                }
            }

            if (_lastSuggestions != null && _lastSuggestions.Count > 0)
            {
                // A scan that matched nothing is not a scan that found nothing. These pairings are not
                // applied and not counted as matches; they are the review dialog's to decide, and the
                // log is where a user who never opens that dialog can find out they exist.
                _logger.Info($"VRMod Tag Service: {_lastSuggestions.Count} listing(s) have a candidate game the name rules will not assert "
                    + "— open 'Review all matches' to decide them:");
                foreach (var s in _lastSuggestions.Take(10))
                {
                    _logger.Info($"VRMod Tag Service:   [{s.SourceName}] '{SafeLogText(s.EntryName)}' ~ '{SafeLogText(s.GameName)}' — {s.Reason}");
                }
                if (_lastSuggestions.Count > 10)
                {
                    _logger.Info($"VRMod Tag Service:   ... and {_lastSuggestions.Count - 10} more");
                }
            }

            // Surface enabled sources that returned 0 entries (anomalous — a healthy source always
            // returns entries). Such a source is invisible to the per-source stats above because it
            // contributed no entries, so flag it explicitly here.
            var noDataSources = _registry.GetEnabledSources().Where(s =>
                _settings.SourceStatuses != null
                && _settings.SourceStatuses.TryGetValue(s.SourceId.ToLowerInvariant(), out var st)
                && st.LastResult && st.LastEntryCount == 0);
            foreach (var source in noDataSources)
                _logger.Warn($"VRMod Tag Service: Source {source.DisplayName} returned 0 entries (no data). It may have changed or be temporarily unavailable.");

            _logger.Info("VRMod Tag Service: === End Summary ===");
        }

        private void RemoveStaleTags(HashSet<Guid> currentMatches)
        {
            var vrRelatedTags = _api.Database.Tags.Where(t => VrModCategories.IsPluginTagName(t.Name)).ToList();
            if (vrRelatedTags.Count == 0)
                return;

            var vrTagIds = new HashSet<Guid>(vrRelatedTags.Select(t => t.Id));
            if (_tagIdCache != null)
            {
                foreach (var tagId in _tagIdCache.Keys)
                {
                    vrTagIds.Add(tagId);
                }
            }

            var gamesToUpdate = new List<Game>();
            var staleGames = new List<Game>();
            foreach (var game in _api.Database.Games)
            {
                if (game.TagIds == null)
                    continue;

                if (currentMatches.Contains(game.Id))
                    continue;

                var tagsToRemove = game.TagIds.Where(id => vrTagIds.Contains(id)).ToList();
                foreach (var tagId in tagsToRemove)
                {
                    game.TagIds.Remove(tagId);
                    _logger.Info($"VRMod Tag Service: Removed stale VRMod tag from '{SafeLogText(game.Name)}' (no longer matched)");
                }
                if (tagsToRemove.Count > 0)
                {
                    // The game dropped out of every match - drop ALL its per-mapping decisions
                    // (keyed by this game's Id, so a same-name store duplicate is NOT cleared) and
                    // its stale source links. Deferred to one bulk clear below, not per game:
                    // ClearDecisionsForGame locates a game's keys by walking the whole decision map,
                    // so one call per stale game is O(games x stored decisions).
                    staleGames.Add(game);
                    gamesToUpdate.Add(game);
                }
            }

            // Decisions go first, then link reconciliation - the order the per-game loop had. A game's
            // links are partly kept alive by its own recorded decisions (BuildLinkPlan walks
            // GetDecisionsForGame), so reconciling before the clear would re-earn a link for a decision
            // that is about to be deleted. Cross-game order does not matter: a clear touches only keys
            // carrying that game's Id, and a reconcile reads only its own game's decisions.
            _settings.ClearDecisionsForGames(staleGames.Select(g => g.Id).ToList());
            foreach (var game in staleGames)
                ReconcileSourceLinksCore(game);

            // Persist after enumeration finishes (calling Update mid-foreach invalidates the enumerator).
            foreach (var game in gamesToUpdate)
                _api.Database.Games.Update(game);
        }

        /// <summary>
        /// Reconciles the VRMod tags on games that are still matched this scan: any VRMod tag that
        /// the scan no longer justifies (a facet like 6DOF dropped from a source entry, or a
        /// category the source no longer lists the game under) is removed. Only auto-applied games
        /// are touched (the ones in expectedTagsByGame), and only after a clean scan (no failed
        /// sources) — see RunFullScanAsync. Decisions are not touched (facets are not decisions).
        /// </summary>
        private void ReconcileMatchedGameTags(Dictionary<Guid, HashSet<Guid>> expectedTagsByGame)
        {
            if (expectedTagsByGame.Count == 0)
                return;

            // Every VRMod-owned tag id, so we only ever touch tags this extension manages.
            var vrTagIds = new HashSet<Guid>();
            foreach (var t in _api.Database.Tags)
            {
                if (t.Name != null && VrModCategories.IsPluginTagName(t.Name))
                    vrTagIds.Add(t.Id);
            }
            if (_tagIdCache != null)
            {
                foreach (var tagId in _tagIdCache.Keys)
                {
                    vrTagIds.Add(tagId);
                }
            }

            foreach (var kvp in expectedTagsByGame)
            {
                if (_gameIdCache == null || !_gameIdCache.TryGetValue(kvp.Key, out var game))
                    continue;
                if (game.TagIds == null)
                    continue;

                var removedAny = false;
                foreach (var tagId in game.TagIds.ToList())
                {
                    if (vrTagIds.Contains(tagId) && !kvp.Value.Contains(tagId))
                    {
                        game.TagIds.Remove(tagId);
                        removedAny = true;
                        _logger.Info($"VRMod Tag Service: Removed VRMod tag from '{SafeLogText(game.Name)}' (no longer listed by a source this scan)");
                    }
                }
                if (removedAny)
                    _api.Database.Games.Update(game);
            }
        }

        /// <summary>
        /// Rewrites the tag names this plugin used to mint into the spelling it mints today, on the games
        /// it tagged. <see cref="VrModCategories.TagValueAliases"/> is the list; today it holds one rename:
        /// "VRMod Full Motion Controls" (RototRobot's category) into "VRMod Full Motion" (PCVR Central's
        /// motion level) - two tags on one game for one claim, measured on a real library as 54 games with
        /// the new name, 37 with the old, 22 carrying both.
        ///
        /// Runs on every full scan and does nothing when no game carries an old name, so it is self-healing
        /// (a library restored from an older backup gets folded again) instead of a versioned one-shot. Only
        /// games carrying the umbrella "VRMod" tag - the marker this plugin applies - are touched, and only
        /// the rename itself: a game that already has the current tag simply loses the old one.
        /// </summary>
        /// <returns>The number of games whose tags were rewritten.</returns>
        public int MigrateLegacyTagNames()
        {
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in VrModCategories.TagValueAliases)
            {
                var legacyName = $"{VrModCategories.TagUmbrella} {alias.Key}";
                var canonicalName = VrModCategories.TagNameFor(alias.Key);
                if (!legacyName.Equals(canonicalName, StringComparison.Ordinal))
                    renames[legacyName] = canonicalName;
            }
            if (renames.Count == 0)
                return 0;

            var legacyTags = _api.Database.Tags
                .Where(t => t.Name != null && renames.ContainsKey(t.Name))
                .ToList();
            if (legacyTags.Count == 0)
                return 0;

            var replacementById = new Dictionary<Guid, (string LegacyName, string CanonicalName, Tag ReplacementTag)>();
            foreach (var tag in legacyTags)
            {
                var legacyName = tag.Name!;
                var canonicalName = renames[legacyName];
                replacementById[tag.Id] = (legacyName, canonicalName, EnsureTag(canonicalName));
            }
            var umbrellaTag = EnsureTag(VrModCategories.TagUmbrella);

            // O(1) per game through the per-scan index when a scan built one; a database walk otherwise.
            var games = _gameIdCache != null ? _gameIdCache.Values.ToList() : _api.Database.Games.ToList();
            var changedGames = 0;
            foreach (var game in games)
            {
                if (game.TagIds == null || !game.TagIds.Contains(umbrellaTag.Id))
                    continue;

                var rewritten = false;
                foreach (var tagId in game.TagIds.ToList())
                {
                    if (!replacementById.TryGetValue(tagId, out var rename))
                        continue;
                    game.TagIds.Remove(tagId);
                    if (!game.TagIds.Contains(rename.ReplacementTag.Id))
                        game.TagIds.Add(rename.ReplacementTag.Id);
                    rewritten = true;
                    _logger.Info($"VRMod Tag Service: Replaced '{SafeLogText(rename.LegacyName)}' with '{SafeLogText(rename.CanonicalName)}' on '{SafeLogText(game.Name)}'");
                }

                if (rewritten)
                {
                    changedGames++;
                    _api.Database.Games.Update(game);
                }
            }

            if (changedGames > 0)
                _logger.Info($"VRMod Tag Service: legacy tag rename applied to {changedGames} game(s)");
            return changedGames;
        }

        /// <summary>
        /// Removes the VRMod tags that came from a source the user just disabled, so the library
        /// stays clean. A game that had this source as its ONLY source loses all its VRMod tags;
        /// a game shared with other sources loses only the category tags whose category this source
        /// was the sole accepted source for (facets like 6DOF and the umbrella tag are kept — a
        /// still-matched game keeps them). Decisions are intentionally NOT touched here: an accepted
        /// decision stays (dormant while the source is disabled) so re-enabling the source re-applies
        /// the tags automatically on the next scan; X-only games have theirs cleared naturally by the
        /// next scan's stale-tag pass (RemoveStaleTags).
        /// </summary>
        /// <param name="sourceId">The source the user disabled.</param>
        /// <returns>The number of games whose tags were changed.</returns>
        public int RemoveTagsForSource(string sourceId)
        {
            var source = _registry.Sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return 0;
            if (source.IsEnabled)
            {
                _logger.Info($"VRMod Tag Service: RemoveTagsForSource('{source.DisplayName}'): source is still enabled; nothing to clean up.");
                return 0;
            }

            // Group every recorded decision by Playnite game Id. The decision key is
            // sourceId||sourceGame||{gameId:D} (per-mapping); the game Id is parts[2].
            var byGame = new Dictionary<string, List<(string SourceId, string Key, string Decision)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _settings.SnapshotMatchDecisions())
            {
                var parts = kvp.Key.Split(DecisionKeySeparators, StringSplitOptions.None);
                if (parts.Length < 3)
                    continue;
                if (!byGame.TryGetValue(parts[2], out var list))
                {
                    list = new List<(string, string, string)>();
                    byGame[parts[2]] = list;
                }
                list.Add((parts[0], kvp.Key, kvp.Value));
            }

            // Precompute the VR tag id set and a name->id map so per-game removal needs no per-tag DB lookups.
            var vrTagIds = new HashSet<Guid>();
            var nameToTagId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _api.Database.Tags)
            {
                if (string.IsNullOrEmpty(t.Name))
                    continue;
                nameToTagId[t.Name] = t.Id;
                if (VrModCategories.IsPluginTagName(t.Name))
                    vrTagIds.Add(t.Id);
            }

            // The tag ids an accepted decision's stored baseline resolves to (its category tags + facets).
            List<Guid> BaselineTagIds(string key)
            {
                var ids = new List<Guid>();
                var baseline = _settings.GetSourceTags(key);
                if (baseline == null)
                    return ids;
                foreach (var name in baseline)
                {
                    if (name.Length > 0 && nameToTagId.TryGetValue(name, out var id))
                        ids.Add(id);
                }
                return ids;
            }

            var gamesToUpdate = new List<Game>();
            foreach (var game in _api.Database.Games)
            {
                if (!byGame.TryGetValue(game.Id.ToString("D"), out var gameDecisions) || gameDecisions.Count == 0)
                    continue;

                var xDecisions = gameDecisions.Where(d => string.Equals(d.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (xDecisions.Count == 0)
                    continue;

                // Tags this source still backs via an accepted decision (from the stored baselines).
                var xTagIds = new HashSet<Guid>();
                foreach (var d in xDecisions.Where(d => string.Equals(d.Decision, "Accepted", StringComparison.OrdinalIgnoreCase)))
                    foreach (var id in BaselineTagIds(d.Key))
                        xTagIds.Add(id);

                // Tags still backed by an accepted decision from ANOTHER source (so a shared tag survives).
                var otherTagIds = new HashSet<Guid>();
                var hasOtherAccepted = false;
                foreach (var d in gameDecisions)
                {
                    if (string.Equals(d.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(d.Decision, "Accepted", StringComparison.OrdinalIgnoreCase))
                        continue;
                    hasOtherAccepted = true;
                    foreach (var id in BaselineTagIds(d.Key))
                        otherTagIds.Add(id);
                }

                var tagIds = game.TagIds;
                var removedCount = 0;
                if (tagIds != null && tagIds.Count > 0 && !hasOtherAccepted)
                {
                    // X was the game's only source → clean-wipe every VRMod tag.
                    foreach (var tagId in tagIds.ToList())
                    {
                        if (vrTagIds.Contains(tagId))
                        {
                            tagIds.Remove(tagId);
                            removedCount++;
                        }
                    }
                }
                else if (tagIds != null && tagIds.Count > 0)
                {
                    // Shared game → remove only the tags X backed that no other source still backs.
                    foreach (var tagId in tagIds.ToList())
                    {
                        if (xTagIds.Contains(tagId) && !otherTagIds.Contains(tagId))
                        {
                            tagIds.Remove(tagId);
                            removedCount++;
                        }
                    }
                }

                // A disabled source contributes no links — remove this source's links too.
                var linksChanged = ReconcileSourceLinksCore(game);

                if (removedCount > 0 || linksChanged)
                {
                    _logger.Info($"VRMod Tag Service: Removed {removedCount} VRMod tag(s) and stale source link(s) from '{SafeLogText(game.Name)}' (source '{source.DisplayName}' disabled).");
                    gamesToUpdate.Add(game);
                }
            }

            // Persist after enumeration finishes (calling Update mid-foreach invalidates the enumerator).
            foreach (var game in gamesToUpdate)
                _api.Database.Games.Update(game);

            return gamesToUpdate.Count;
        }

        /// <summary>
        /// Removes every VRMod tag from every game in the library (the "Reset and remove tags"
        /// option of the review dialog's reset confirmation). This is a clean wipe: after it, the
        /// library has no VRMod tags at all, matching the cleared decisions. Unlike
        /// <see cref="RemoveTagsForSource"/> it is not gated on a disabled source — it is called
        /// when the user explicitly asked for a full reset.
        /// </summary>
        /// <returns>The number of games whose tags were changed.</returns>
        public int RemoveAllVrModTags()
        {
            return RemoveAllVrModTags(null, CancellationToken.None);
        }

        /// <summary>
        /// Removes every VRMod tag from every game, reporting progress per game so a long reset
        /// (hundreds/thousands of games) can show a progress bar instead of a frozen UI. The
        /// per-game work (tag removal + source-link reconciliation) is pure in-memory mutation;
        /// the actual <c>Games.Update</c> calls are issued once, after the loop, inside a
        /// <see cref="IDatabase.BufferedUpdate"/> so they commit — and notify the UI — once.
        /// </summary>
        /// <param name="onProgress">Optional <c>(current, total, gameName)</c> called once per game, before its work.</param>
        /// <param name="cancellationToken">When cancelled, remaining games are skipped (the work done
        /// so far still commits).</param>
        /// <returns>The number of games changed.</returns>
        public int RemoveAllVrModTags(System.Action<int, int, string>? onProgress, System.Threading.CancellationToken cancellationToken = default)
        {
            return RemoveAllVrModTags(null, onProgress, cancellationToken);
        }

        /// <summary>
        /// Removes every VRMod tag, reporting progress per game. When <paramref name="games"/> is
        /// supplied, only those games are touched — the review dialog passes its matched games so a
        /// reset on a big library is fast. A game carries a VRMod tag only if it was matched (tags
        /// are applied on accept / during the scan), so scoping to the matches misses nothing; a
        /// match row without the tag is a cheap no-op. When <c>null</c>, the whole library is
        /// scanned (the <see cref="UninstallCleanup"/> path, a full wipe). The per-game work (tag
        /// removal + source-link reconciliation) is pure in-memory mutation; the
        /// <c>Games.Update</c> calls are issued once, after the loop, inside a
        /// <see cref="IDatabase.BufferedUpdate"/> so they commit — and notify the UI — once.
        /// </summary>
        /// <param name="games">The games to scope the removal to, or <c>null</c> for the whole library.</param>
        /// <param name="onProgress">Optional <c>(current, total, gameName)</c> called once per game, before its work.</param>
        /// <param name="cancellationToken">When cancelled, remaining games are skipped (the work done so far still commits).</param>
        /// <returns>The number of games changed.</returns>
        public int RemoveAllVrModTags(System.Collections.Generic.IEnumerable<Game>? games, System.Action<int, int, string>? onProgress = null, System.Threading.CancellationToken cancellationToken = default)
        {
            // Precompute the VR tag id set (tags whose name starts with the umbrella prefix) so
            // per-game removal needs no per-tag DB lookups.
            var vrTagIds = new HashSet<Guid>();
            foreach (var t in _api.Database.Tags)
            {
                if (string.IsNullOrEmpty(t.Name))
                    continue;
                if (VrModCategories.IsPluginTagName(t.Name))
                    vrTagIds.Add(t.Id);
            }

            // Scope the loop: the supplied games (the review dialog's matches) or the whole library.
            // De-duplicated (by reference) so a game matched by several sources is touched once.
            var scope = games == null
                ? _api.Database.Games.ToList()
                : games.Where(g => g != null).Distinct().ToList();
            var total = scope.Count;
            var gamesToUpdate = new List<Game>();
            var current = 0;
            foreach (var game in scope)
            {
                current++;
                onProgress?.Invoke(current, total, game.Name);
                if (cancellationToken.IsCancellationRequested)
                    break;

                var removedCount = 0;
                var tagIds = game.TagIds;
                if (tagIds != null)
                {
                    foreach (var tagId in tagIds.ToList())
                    {
                        if (vrTagIds.Contains(tagId))
                        {
                            tagIds.Remove(tagId);
                            removedCount++;
                        }
                    }
                }

                // Decisions were cleared before this call, so no source link is desired anymore —
                // reconcile (removes every plugin-owned link) in the same pass as the tag removal.
                var linkChanged = ReconcileSourceLinksCore(game);

                if (removedCount > 0)
                    _logger.Info($"VRMod Tag Service: Removed {removedCount} VRMod tag(s) from '{SafeLogText(game.Name)}' (all decisions reset).");

                if (removedCount > 0 || linkChanged)
                    gamesToUpdate.Add(game);
            }

            // Persist after the loop. Buffered so the per-game updates commit — and notify the UI — once.
            using (_api.Database.BufferedUpdate())
            {
                foreach (var game in gamesToUpdate)
                    _api.Database.Games.Update(game);
            }

            return gamesToUpdate.Count;
        }

        /// <summary>
        /// Complete uninstall cleanup: clears all match decisions, removes every VRMod tag
        /// (from the games and from the tag collection) and every plugin-owned source link,
        /// so the shared library has no trace of the plugin. Called only when Playnite queued
        /// this plugin for uninstall (see <c>VrModTagPlugin</c> Dispose) — never on a normal
        /// shutdown.
        /// </summary>
        public void UninstallCleanup()
        {
            // Clear decisions first: RemoveAllVrModTags then reconciles the source links
            // against the cleared decisions and removes every plugin-owned link.
            _settings.ClearAllDecisions();
            RemoveAllVrModTags();

            // Remove the tag definitions too, so no orphaned VRMod* tags remain in the user's
            // tag collection (nothing references them anymore). Each Remove is persisted
            // directly by the collection, no Update needed.
            var vrTags = new List<Tag>();
            foreach (var t in _api.Database.Tags)
            {
                if (t.Name != null && VrModCategories.IsPluginTagName(t.Name))
                    vrTags.Add(t);
            }
            foreach (var t in vrTags)
                _api.Database.Tags.Remove(t);

            if (vrTags.Count > 0)
                _logger.Info($"VRMod Tag Service: Removed {vrTags.Count} VRMod tag definition(s) from the tags collection (uninstall cleanup).");
        }

        #region Source link reconciliation

        /// <summary>
        /// Whether the user saved an Accepted decision for this match for this game.
        /// Pending (unreviewed) and rejected matches return false — this is the
        /// "saved matches only" gate for source links.
        /// </summary>
        public bool IsSavedAccepted(Game game, VrModEntry entry)
        {
            var sourceId = entry.SourceId ?? string.Empty;
            if (sourceId.Length == 0)
                return false;

            return GetEntryDecision(sourceId, entry, game.Id) == MatchDecision.Accepted;
        }

        /// <summary>
        /// Display name used as the text for the source link (falls back to the source ID
        /// if the source is not registered).
        /// </summary>
        public string GetSourceDisplayName(VrModEntry entry)
        {
            var displayName = _registry.Sources.FirstOrDefault(s => s.SourceId == entry.SourceId)?.DisplayName;
            return string.IsNullOrEmpty(displayName) ? (entry.SourceId ?? "Source") : displayName!;
        }

        /// <summary>
        /// The source's own website. Used as the link target when the parser does not
        /// provide a per-game deep link. Empty if the source is not registered.
        /// </summary>
        public string GetSourceWebsiteUrl(VrModEntry entry)
        {
            var websiteUrl = _registry.Sources.FirstOrDefault(s => s.SourceId == entry.SourceId)?.WebsiteUrl;
            return websiteUrl ?? string.Empty;
        }

        /// <summary>
        /// The link target for an entry's source link: the per-game deep link if the parser
        /// provided one, otherwise the source's own website. Empty if neither is available.
        /// </summary>
        public string GetSourceLinkUrl(VrModEntry entry)
        {
            // Defense in depth on top of VrModEntry.SourceUrls validation: this value is written
            // into the library as a Playnite Link, and Playnite opens a Link with the OS shell
            // without checking the scheme, so an unsafe scraped URL must never leave here.
            if (WebUrl.IsSafeLinkUrl(entry.SourceUrl))
                return entry.SourceUrl!;
            var website = GetSourceWebsiteUrl(entry);
            return WebUrl.IsSafeLinkUrl(website) ? website : string.Empty;
        }

        /// <summary>
        /// Whether the source for the entry is registered and enabled.
        /// </summary>
        public bool IsSourceEnabled(string? sourceId)
        {
            var source = _registry.Sources.FirstOrDefault(s => s.SourceId == sourceId);
            return source != null && source.IsEnabled;
        }

        /// <summary>
        /// Reconciles the plugin-owned source links on this game: adds links for saved
        /// accepted matches (source enabled and its per-source "Links" toggle on), and
        /// removes plugin-owned links that are no longer desired (match rejected or
        /// reverted to pending, source disabled, or the per-source "Links" toggle off).
        /// Links the plugin could not have generated are never touched.
        /// Persists the game if its Links changed.
        /// </summary>
        public void ReconcileSourceLinks(Game game)
        {
            if (ReconcileSourceLinksCore(game))
                _api.Database.Games.Update(game);
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the Playnite UI (Dispatcher) thread, marshaling when the
        /// caller is on a background thread. Playnite's per-game collections (notably
        /// <c>Game.Links</c>, an <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>)
        /// are bound to WPF <c>CollectionView</c>s, so mutating them off the UI thread throws
        /// <see cref="System.NotSupportedException"/> ("...changes to its SourceCollection from a
        /// thread different from the Dispatcher thread"). The scan and reset run on a background
        /// thread (Playnite's global-progress pattern), so their collection mutations must be
        /// marshaled here. When already on the UI thread — or with no UI (unit tests) — runs inline.
        ///
        /// Before marshaling it runs <see cref="EnsureUiHopIsSafe"/>: this hop is only safe from a
        /// thread that holds no service lock.
        /// </summary>
        private T RunOnUiThread<T>(Func<T> work)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return work();
            EnsureUiHopIsSafe();
            return dispatcher.Invoke<T>(work);
        }

        /// <summary>
        /// The core of <see cref="ReconcileSourceLinks(Game)"/> without persistence:
        /// mutates <c>game.Links</c> in place and returns whether anything changed.
        /// Callers that batch persistence (scans, source-disable, reset, stale-tag
        /// cleanup) use this and persist the game themselves.
        ///
        /// "Owned" links — the ones this plugin may remove — are the (name, url) pairs it
        /// could have generated for this game: deep links from entries that currently
        /// match the game or that have an Accepted/Rejected decision recorded for it, plus
        /// the website link of every registered source.
        /// </summary>
        internal bool ReconcileSourceLinksCore(Game game)
        {
            if (game == null)
                return false;

            // Plan on whichever thread asked for it; hop to Playnite's UI thread only for the
            // one thing that needs it (mutating game.Links). Until now the hop carried the whole
            // method: a catalog walk plus a decision lookup per cached entry, on the UI thread,
            // per game.
            var plan = BuildLinkPlan(game);
            return RunOnUiThread(() => ApplyLinkPlan(game, plan));
        }

        /// <summary>
        /// Everything a reconcile needs EXCEPT the mutation of <c>game.Links</c>: the catalog walk,
        /// the stored decisions for this game, the source website keys, the desired link set.
        /// Runs on the calling (scan) thread. It touches no WPF-bound collection, so none of it
        /// belongs on Playnite's UI thread - which is where all of it used to run.
        /// </summary>
        private LinkPlan BuildLinkPlan(Game game)
        {
            // Match through the catalog index: this used to copy every cached entry and fuzzy-score
            // all of it, per reconciled game.
            var gameEntries = GetCachedEntriesForGame(game);

            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in gameEntries)
                AddOwnedLink(owned, entry);
            // An entry no longer matching the game can still own a link if a decision for it is
            // recorded (the match was saved before the source renamed it). Walk the decisions this
            // game actually has instead of asking every cached entry whether it owns one: same
            // answer - same Ordinal key semantics, same last-write-wins entry - and the cost is
            // what the user recorded (a handful per game) instead of one key-building lookup per
            // catalog entry. Keyed by game Id so a same-name store duplicate's links stay
            // independent.
            foreach (var (decisionSourceId, decisionGameName, decision) in _settings.GetDecisionsForGame(game.Id))
            {
                if (decision != MatchDecision.Accepted && decision != MatchDecision.Rejected)
                    continue;
                var entry = FindCachedEntry(decisionSourceId, decisionGameName);
                if (entry == null)
                    continue;
                AddOwnedLink(owned, entry);
            }
            foreach (var source in _registry.Sources)
            {
                if (!string.IsNullOrEmpty(source.WebsiteUrl))
                    owned.Add(OwnedKey(source.DisplayName, source.WebsiteUrl));
            }

            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var desiredUrls = new List<(string Name, string Url)>();
            foreach (var entry in gameEntries)
            {
                var sourceId = entry.SourceId ?? string.Empty;
                if (sourceId.Length == 0)
                    continue;
                if (!IsSourceEnabled(sourceId))
                    continue;
                if (!IsSavedAccepted(game, entry))
                    continue;
                if (!_settings.IsSourceLinksEnabled(sourceId))
                    continue;

                var url = GetSourceLinkUrl(entry);
                if (url.Length == 0)
                    continue;
                var key = OwnedKey(GetSourceDisplayName(entry), url);
                if (desired.Contains(key))
                    continue;
                desired.Add(key);
                desiredUrls.Add((GetSourceDisplayName(entry), url));
            }

            return new LinkPlan(owned, desired, desiredUrls);
        }

        /// <summary>
        /// The only part of a reconcile that must run on Playnite's UI thread: mutating
        /// <c>game.Links</c>, an ObservableCollection bound to a WPF CollectionView. Everything it
        /// needs was decided off the UI thread by <see cref="BuildLinkPlan"/>.
        /// </summary>
        private bool ApplyLinkPlan(Game game, LinkPlan plan)
        {
            var owned = plan.Owned;
            var desired = plan.Desired;
            var desiredUrls = plan.DesiredUrls;

            var changed = false;
            var links = game.Links;
            if (links != null)
            {
                for (var i = links.Count - 1; i >= 0; i--)
                {
                    var link = links[i];
                    var key = OwnedKey(link.Name ?? string.Empty, link.Url ?? string.Empty);
                    if (owned.Contains(key) && !desired.Contains(key))
                    {
                        links.RemoveAt(i);
                        changed = true;
                        _logger.Info($"VRMod Tag Service: Removed source link '{link.Name}' from '{SafeLogText(game.Name)}' (no longer a saved accepted match).");
                    }
                }
            }
            foreach (var (name, url) in desiredUrls)
            {
                if (links == null || links.Any(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (links == null)
                {
                    links = new ObservableCollection<Link>();
                    game.Links = links;
                }
                links.Add(new Link(name, url));
                changed = true;
                _logger.Info($"VRMod Tag Service: Added source link '{name}' to '{SafeLogText(game.Name)}'.");
            }
            return changed;
        }

        /// <summary>
        /// The computed half of a link reconcile: what the plugin owns, what it wants, and the
        /// links that realize it. Built on the calling thread, applied on the UI thread.
        /// </summary>
        private sealed class LinkPlan
        {
            public LinkPlan(
                HashSet<string> owned,
                HashSet<string> desired,
                List<(string Name, string Url)> desiredUrls)
            {
                Owned = owned;
                Desired = desired;
                DesiredUrls = desiredUrls;
            }

            public HashSet<string> Owned { get; }
            public HashSet<string> Desired { get; }
            public List<(string Name, string Url)> DesiredUrls { get; }
        }

        /// <summary>
        /// Indexes over the current <c>_sourceCache</c> contents, built once per cache generation
        /// instead of once per game:
        ///
        /// - <c>BySourceAndName</c>: (sourceId, entry name) -> entry, so a stored decision can find
        ///   the entry it points at. Keys compare Ordinal because the decision keys they serve were
        ///   built Ordinal out of these very strings.
        /// - <c>ByKeyLength</c>: normalized-name length -> candidates, the blocking index behind
        ///   <see cref="GetCachedEntriesForGame"/>. Each candidate carries the match key the matcher
        ///   would have derived from it and a character histogram, both computed once for the whole
        ///   catalog instead of once per (game, entry) pair.
        ///
        /// Dictionary is not thread-safe, so an index is built into locals and published with
        /// volatile writes together with the cache version it was built for; every mutation of
        /// <c>_sourceCache</c> bumps that version. A reader whose version no longer matches the
        /// published index rebuilds it, so the worst case is one wasted rebuild - never a stale
        /// entry, and never a half-built map being observed.
        /// </summary>
        private CatalogIndex? _catalogIndex;
        private int _catalogIndexVersion = -1;
        private int _sourceCacheVersion;

        private sealed class CatalogCandidate
        {
            public CatalogCandidate(VrModEntry entry, string matchKey, int[] letterCounts)
            {
                Entry = entry;
                MatchKey = matchKey;
                LetterCounts = letterCounts;
            }

            public VrModEntry Entry { get; }
            public string MatchKey { get; }
            public int[] LetterCounts { get; }
        }

        private sealed class CatalogIndex
        {
            public Dictionary<string, Dictionary<string, VrModEntry>> BySourceAndName { get; } =
                new Dictionary<string, Dictionary<string, VrModEntry>>(StringComparer.Ordinal);
            public Dictionary<int, List<CatalogCandidate>> ByKeyLength { get; } =
                new Dictionary<int, List<CatalogCandidate>>();
            public int MinKeyLength { get; private set; } = int.MaxValue;
            public int MaxKeyLength { get; private set; }

            public void Add(int keyLength, CatalogCandidate candidate)
            {
                if (!ByKeyLength.TryGetValue(keyLength, out var bucket))
                    ByKeyLength[keyLength] = bucket = new List<CatalogCandidate>();
                bucket.Add(candidate);
                if (keyLength < MinKeyLength) MinKeyLength = keyLength;
                if (keyLength > MaxKeyLength) MaxKeyLength = keyLength;
            }
        }

        private CatalogIndex GetCatalogIndex()
        {
            var version = Volatile.Read(ref _sourceCacheVersion);
            var index = Volatile.Read(ref _catalogIndex);
            if (index == null || Volatile.Read(ref _catalogIndexVersion) != version)
            {
                index = BuildCatalogIndex();
                Volatile.Write(ref _catalogIndex, index);
                Volatile.Write(ref _catalogIndexVersion, version);
            }
            return index;
        }

        private CatalogIndex BuildCatalogIndex()
        {
            var index = new CatalogIndex();
            foreach (var cached in _sourceCache)
            {
                foreach (var entry in cached.Value.Entries)
                {
                    var sourceId = entry.SourceId ?? string.Empty;
                    if (sourceId.Length == 0)
                        continue;
                    if (!index.BySourceAndName.TryGetValue(sourceId, out var byName))
                        index.BySourceAndName[sourceId] = byName = new Dictionary<string, VrModEntry>(StringComparer.Ordinal);
                    byName[entry.GameName] = entry;    // last write wins, as the old catalog scan did

                    // The same two-step normalization GetMatchScore(string, string, ..) performs on
                    // its inputs, applied once per entry instead of once per game-entry pair.
                    var matchKey = VrModNameMatcher.Normalize(NormalizeForMatchFast(entry.GameName));
                    index.Add(matchKey.Length, new CatalogCandidate(entry, matchKey, LetterHistogram(matchKey)));
                }
            }
            return index;
        }

        private VrModEntry? FindCachedEntry(string sourceId, string entryName)
        {
            var index = GetCatalogIndex();
            return index.BySourceAndName.TryGetValue(sourceId, out var byName) && byName.TryGetValue(entryName, out var entry)
                ? entry
                : null;
        }

        /// <summary>
        /// The cached catalog entries that match one game, without walking the catalog per game.
        /// <see cref="GetVrModEntriesForGame(Game, IEnumerable{VrModEntry})"/> scores every entry it
        /// is handed, which for a reconciled library meant ~3 100 normalizations and ~3 100 edit
        /// distances per game. This asks the index for the entries that could possibly score up to
        /// the threshold, and only scores those.
        ///
        /// The blocking rules are derivations of the matcher's own prunes, so they can only skip
        /// candidates the matcher would have rejected anyway:
        ///
        /// - Length: similarity is <c>1 - distance / maxLen</c> and edit distance is never smaller
        ///   than the difference in length, so a pair can reach the threshold only when both lengths
        ///   lie in <c>[threshold * L, L / threshold]</c> (the same bound as
        ///   <see cref="VrModNameMatcher.CanReachSimilarity"/>). Buckets outside it are not visited.
        /// - Character counts: <c>distance >= maxLen - LCS(a, b)</c>, so reaching the threshold needs
        ///   <c>LCS >= threshold * maxLen</c>. The per-character sum of <c>min(count_a, count_b)</c>
        ///   over-estimates LCS (non-letters are pooled into one bucket, which can only raise it), so
        ///   a candidate a whole character short of the requirement cannot score up to it.
        ///
        /// Survivors are scored with exactly the strings <see cref="VrModNameMatcher.GetMatchScore"/>
        /// would have built, thresholds and all, and the margin on both bounds is one character to
        /// keep float rounding out of a decision that changes a match.
        /// </summary>
        internal List<VrModEntry> GetCachedEntriesForGame(Game game)
        {
            var threshold = _settings.FuzzyMatchThreshold;
            var index = GetCatalogIndex();
            var gameKey = VrModNameMatcher.Normalize(GetNormalizedGameName(game));
            var gameCounts = LetterHistogram(gameKey);
            var result = new List<VrModEntry>();

            var low = Math.Max(index.MinKeyLength, (int)Math.Floor(threshold * gameKey.Length) - 1);
            var high = threshold > 0f
                ? Math.Min(index.MaxKeyLength, (int)Math.Ceiling(gameKey.Length / threshold) + 1)
                : index.MaxKeyLength;
            for (var length = low; length <= high; length++)
            {
                if (!index.ByKeyLength.TryGetValue(length, out var bucket))
                    continue;
                foreach (var candidate in bucket)
                {
                    var maxLen = Math.Max(length, gameKey.Length);
                    if (LetterOverlapUpperBound(gameCounts, candidate.LetterCounts) < threshold * maxLen - 1)
                        continue;
                    if (VrModNameMatcher.GetMatchScoreNormalized(candidate.MatchKey, gameKey, threshold) >= threshold)
                        result.Add(candidate.Entry);
                }
            }
            return result;
        }

        // Histogram buckets: a-z plus one bucket for everything else (digits, spaces, accents,
        // punctuation). Pooling the non-letters can only raise the sum, which keeps it an upper bound.
        private const int LetterHistogramBuckets = 27;

        private static int[] LetterHistogram(string key)
        {
            var counts = new int[LetterHistogramBuckets];
            foreach (var ch in key)
            {
                if (ch >= 'a' && ch <= 'z') counts[ch - 'a']++;
                else counts[26]++;
            }
            return counts;
        }

        private static int LetterOverlapUpperBound(int[] a, int[] b)
        {
            var sum = 0;
            for (var i = 0; i < LetterHistogramBuckets; i++)
                sum += Math.Min(a[i], b[i]);
            return sum;
        }

        private void InvalidateEntryIndex()
        {
            Interlocked.Increment(ref _sourceCacheVersion);
            Volatile.Write(ref _catalogIndex, null);
        }

        /// <summary>
        /// Reconciles one source's links across the whole library, used when the source's
        /// "Links" toggle changes in the settings UI.
        ///
        /// Unchecking the toggle removes every link the source owns (its website link and any
        /// per-game deep links present in the cache). Checking it restores links for saved
        /// accepted matches - only when the source's entries are cached; with a cold cache
        /// nothing is restored (the next scan adds the links). Stale-link cleanup (links whose
        /// match was rejected) happens on the next scan or review save, not here.
        /// </summary>
        /// <param name="sourceId">The source whose Links toggle changed.</param>
        public void ReconcileSourceLinksForSource(string sourceId)
        {
            var source = _registry.Sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return;

            var sourceEntries = AllCachedEntries
                .Where(e => string.Equals(e.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // URLs this source owns: its website plus any per-game deep links in the cache.
            var ownedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(source.WebsiteUrl))
                ownedUrls.Add(source.WebsiteUrl);
            foreach (var entry in sourceEntries)
            {
                // A merged game can own several distinct deep links — the source owns all of them.
                foreach (var url in entry.SourceUrls)
                    if (!string.IsNullOrEmpty(url))
                        ownedUrls.Add(url);
            }

            var linksEnabled = _settings.IsSourceLinksEnabled(sourceId);

            // Games that can gain a link back (toggle checked): saved accepted matches of this source.
            var restoreByGame = new Dictionary<Guid, List<(string Name, string Url)>>();
            if (linksEnabled && source.IsEnabled)
            {
                foreach (var entry in sourceEntries)
                {
                    var games = FindGamesByIndex(entry);
                    foreach (var game in games)
                    {
                        if (GetEntryDecision(sourceId, entry, game.Id) != MatchDecision.Accepted)
                            continue;
                        var url = GetSourceLinkUrl(entry);
                        if (url.Length == 0)
                            continue;
                        if (!restoreByGame.TryGetValue(game.Id, out var list))
                            restoreByGame[game.Id] = list = new List<(string Name, string Url)>();
                        list.Add((GetSourceDisplayName(entry), url));
                    }
                }
            }

            var gamesToUpdate = new List<Game>();
            foreach (var game in _api.Database.Games)
            {
                // Cheap pre-filter on the calling thread, and deliberately nothing that reads what
                // is INSIDE game.Links: that collection is bound to a WPF view, so both reading and
                // writing its contents happen on Playnite's UI thread, in ApplySourceLinkToggle.
                // (Until this method hopped, the whole loop ran wherever it was called from.)
                var links = game.Links;
                if ((links == null || links.Count == 0) && !restoreByGame.ContainsKey(game.Id))
                    continue;
                if (RunOnUiThread(() => ApplySourceLinkToggle(game, source.DisplayName, ownedUrls, linksEnabled, restoreByGame)))
                    gamesToUpdate.Add(game);
            }

            foreach (var game in gamesToUpdate)
                _api.Database.Games.Update(game);
        }

        /// <summary>
        /// The UI-thread half of <see cref="ReconcileSourceLinksForSource"/> for one game: remove the
        /// links the source owns when its Links toggle went off, restore the saved accepted matches
        /// when it came back on. Returns true if any link moved.
        /// </summary>
        private bool ApplySourceLinkToggle(
            Game game,
            string sourceDisplayName,
            HashSet<string> ownedUrls,
            bool linksEnabled,
            Dictionary<Guid, List<(string Name, string Url)>> restoreByGame)
        {
            var links = game.Links;
            var hasOwned = links != null && links.Any(l =>
                string.Equals(l.Name, sourceDisplayName, StringComparison.OrdinalIgnoreCase)
                && l.Url != null
                && ownedUrls.Contains(l.Url));
            if (!hasOwned && !restoreByGame.ContainsKey(game.Id))
                return false;

            var changed = false;
            if (!linksEnabled && links != null)
            {
                for (var i = links.Count - 1; i >= 0; i--)
                {
                    var link = links[i];
                    if (!string.Equals(link.Name, sourceDisplayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (link.Url == null || !ownedUrls.Contains(link.Url))
                        continue;
                    links.RemoveAt(i);
                    changed = true;
                    _logger.Info($"VRMod Tag Service: Removed '{link.Name}' link for '{SafeLogText(game.Name)}' (source links disabled).");
                }
            }
            if (linksEnabled && restoreByGame.TryGetValue(game.Id, out var restore))
            {
                foreach (var (name, url) in restore)
                {
                    if (links != null && links.Any(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (links == null)
                    {
                        links = new ObservableCollection<Link>();
                        game.Links = links;
                    }
                    links.Add(new Link(name, url));
                    changed = true;
                    _logger.Info($"VRMod Tag Service: Restored '{name}' link for '{SafeLogText(game.Name)}' (source links re-enabled).");
                }
            }
            return changed;
        }
        private void AddOwnedLink(HashSet<string> owned, VrModEntry entry)
        {
            var url = GetSourceLinkUrl(entry);
            if (url.Length > 0)
                owned.Add(OwnedKey(GetSourceDisplayName(entry), url));
        }

        private static string OwnedKey(string name, string url) => name + "\u0000" + url;

        #endregion

        public void FetchAndMatchEntries(Game game, out List<VrModEntry> allEntries, out IEnumerable<VrModEntry> matches)
        {
            allEntries = new List<VrModEntry>();

            foreach (var source in _registry.GetEnabledSources())
            {
                if (!source.IsEnabled)
                    continue;
                if (_sourceCache.TryGetValue(source.SourceId, out var cached))
                {
                    allEntries.AddRange(cached.Entries);
                }
            }

            matches = GetVrModEntriesForGame(game, allEntries);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the scan state. Overridable so a subclass cleans up in its own Dispose(bool)
        /// override instead of shadowing <see cref="Dispose()"/>.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                UnsubscribeFromLibraryChanges();

                // A scan that is still running when the service goes away is stopped here: the token
                // its in-flight requests observe is this object's to release, and releasing it also
                // gives the cancel path in CancelActiveScan nothing left to cancel.
                var activeScan = _activeScanCts;
                _activeScanCts = null;
                activeScan?.Dispose();
            }

            _sourceCache.Clear();
            InvalidateEntryIndex();
            _isDisposed = true;
        }
    }
}
