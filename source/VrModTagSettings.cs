using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Playnite.VRMods
{
    /// <summary>
    /// Data model for plugin settings.
    ///
    /// This class holds all configurable settings and is stored as JSON in
    /// Playnite's config directory (per plugin ID). It is loaded on startup
    /// and saved when the user clicks "Save" in the settings dialog.
    ///
    /// Properties:
    /// - ScanIntervalMinutes: How often to run automatic full scans (default: 10080 = 7 days; 0 = never)
    /// - FuzzyMatchThreshold: Similarity threshold for name-based matching (0.5–1.0, default: 0.8)
    /// - EnableRototRobot: Whether to fetch from RototRobot VRMods-List (default: true)
    /// - EnableFlat2VR: Whether to fetch from Flat2VR Studios (default: true)
    /// - EnableUeVR: Whether to fetch from UeVR Profiles (default: true)
    /// - EnableReFramework: Whether to fetch from REFramework (default: true)
    /// - EnablePcvrCentral: Whether to fetch from PCVR Central (default: true)
    /// - EnableMrNlceHub: Whether to fetch from PCVR Mods Installer Hub (default: true)
    /// - EnableMrSurvivorHub: Whether to fetch from Mr Survivor VR HUB (default: true)
    /// - EnableVorpX: Whether to fetch from VorpX supported-games list (default: false, opt-in)
    /// - EnableLukeRoss: Whether to fetch from Luke Ross R.E.A.L. VR (default: false, opt-in)
    /// - EnableCompoundVR: Whether to fetch from the CompoundVR flat-to-VR catalog (default: true)
    /// - EnableSteamCuratorUEVR: Whether to fetch the Steam Curator "UEVR Compatibility" list (default: true)
    /// - EnableSteamCuratorVorpX: Whether to fetch the Steam Curator "VorpX Games" list (default: true)
    /// - CacheDurationMinutes: How long to cache fetched entries (default: 10)
    ///
    /// Non-serialized properties (marked with [DontSerialize]):
    /// - PreviouslyTaggedGames: List of game IDs that were tagged in the last scan
    /// - SourceStatuses: Per-source scan status (last scan time, result, error, the
    ///   "found"/"applied" counts, and the "Last run" debug log) - in-memory, so it
    ///   resets to "never scanned" when Playnite restarts
    ///
    /// NOTE: user match decisions (MatchDecisions, DecisionTimestamps,
    /// SourceTagsByDecision) ARE persisted to JSON - they survive a Playnite restart,
    /// so previously accepted/rejected matches are re-applied on the next scan.
    ///
    /// The class has a parameterless constructor for deserialization by Playnite's
    /// LoadPluginSettings<T> method.
    ///
    /// Decision management methods:
    /// - GetDecision: Look up user decision for a specific match
    /// - SetDecision: Record user decision for a match
    /// - ClearDecision: Remove a specific decision (reverts to Pending)
    /// - ClearAllDecisions: Remove all decisions (reverts all matches to Pending)
    /// - ClearDecisionsForGame: Remove all per-mapping decisions for a (no-longer-matched) game
    /// </summary>
    public class VrModTagSettings : ObservableObject
    {
        /// <summary>
        /// Parameterless constructor required for LoadPluginSettings deserialization.
        ///
        /// When Playnite loads settings from JSON, it uses this constructor to create
        /// the instance, then populates properties via reflection.
        /// </summary>
        public VrModTagSettings() { }

        /// <summary>
        /// Interval between automatic full scans (in minutes).
        /// Default: 10080 minutes (7 days).
        /// Valid range: 0 (never) or 1–525600 minutes (365 days).
        /// The settings page offers a fixed preset ladder (15 min … 30 days, plus
        /// Never) rather than a free number: the meaningful range is magnitudes,
        /// not exact values.
        /// </summary>
        private int _scanIntervalMinutes = 10080;
        public int ScanIntervalMinutes
        {
            get => _scanIntervalMinutes;
            set => SetValue(ref _scanIntervalMinutes, value);
        }

        /// <summary>
        /// Fuzzy match threshold for name-based game matching.
        ///
        /// Matches with similarity >= this threshold are considered potential matches.
        /// Exact matches (1.0) are always accepted.
        /// Fuzzy matches (threshold to 1.0) go through the review dialog.
        /// Default: 0.8 (80% similarity required).
        /// Valid range: 0.5–1.0; see <see cref="MinFuzzyMatchThreshold"/> for why it does not reach 0.
        /// </summary>
        private float _fuzzyMatchThreshold = DefaultFuzzyMatchThreshold;
        public float FuzzyMatchThreshold
        {
            get => _fuzzyMatchThreshold;
            set
            {
                // Out-of-range numbers reach this setter through more than the settings dialog:
                // Playnite deserializes the saved settings JSON straight into these properties, so a
                // hand-edited, downgraded or half-written settings file can deliver 0.0, 5.0 or NaN.
                // The value is used to decide which library entries get tags and source links, so a
                // threshold of 0 is not a harmless preference — see MinFuzzyMatchThreshold. Clamping
                // here means whatever is in memory is always usable; VerifySettings is what tells the
                // user the number they entered was rejected.
                var clamped = SanitizeFuzzyMatchThreshold(value);
                if (clamped != value)
                    _fuzzyThresholdWasAdjusted = true;
                SetValue(ref _fuzzyMatchThreshold, clamped);
            }
        }

        public const float DefaultFuzzyMatchThreshold = 0.8f;

        /// <summary>
        /// Lowest fuzzy threshold the plugin will match with.
        ///
        /// GetMatchScore returns a 0..1 similarity for any name pair that is not a substring pair and
        /// has no conflicting numeric identifier, so a threshold of 0 accepts essentially every
        /// (entry, game) combination the scan considers: hundreds of tag and source-link writes per
        /// scan, and a review dialog too large to answer. 0.5 keeps the knob useful (loose matches
        /// still surface for review) while staying out of that regime.
        /// </summary>
        public const float MinFuzzyMatchThreshold = 0.5f;

        public const float MaxFuzzyMatchThreshold = 1.0f;

        private bool _fuzzyThresholdWasAdjusted;

        /// <summary>
        /// True when the last assignment to <see cref="FuzzyMatchThreshold"/> had to be moved into the
        /// valid range. Reported once by VerifySettings, and exposed for tests.
        /// </summary>
        internal bool FuzzyMatchThresholdWasAdjusted => _fuzzyThresholdWasAdjusted;

        /// <summary>Forget the pending "your value was out of range" report (called once it has been shown).</summary>
        internal void ClearFuzzyMatchThresholdAdjustment() => _fuzzyThresholdWasAdjusted = false;

        internal static float SanitizeFuzzyMatchThreshold(float value) =>
            // NaN has to be tested for explicitly: every comparison against NaN is false, so a range
            // check alone would pass it through and let it poison every "score >= threshold" test.
            float.IsNaN(value) ? DefaultFuzzyMatchThreshold
                : value < MinFuzzyMatchThreshold ? MinFuzzyMatchThreshold
                : value > MaxFuzzyMatchThreshold ? MaxFuzzyMatchThreshold
                : value;

        /// <summary>
        /// Whether to fetch VR mod data from RototRobot VRMods-List.
        /// Default: true (enabled).
        /// </summary>
        private bool _enableRototRobot = true;
        public bool EnableRototRobot
        {
            get => _enableRototRobot;
            set => SetValue(ref _enableRototRobot, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from Flat2VR Studios.
        /// Default: true (enabled).
        /// </summary>
        private bool _enableFlat2VR = true;
        public bool EnableFlat2VR
        {
            get => _enableFlat2VR;
            set => SetValue(ref _enableFlat2VR, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from UeVR Profiles.
        /// Default: true (enabled).
        /// </summary>
        private bool _enableUeVR = true;
        public bool EnableUeVR
        {
            get => _enableUeVR;
            set => SetValue(ref _enableUeVR, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from REFramework (RE Engine titles).
        /// Default: true (enabled).
        /// </summary>
        private bool _enableReFramework = true;
        public bool EnableReFramework
        {
            get => _enableReFramework;
            set => SetValue(ref _enableReFramework, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the PCVR Central game catalog.
        /// Default: true (enabled).
        /// Catalog-level coverage: reports the games PCVR Central documents (see
        /// <see cref="PcvrCentralParser"/>); produces the single "VRMod PCVR Central" tag.
        /// </summary>
        private bool _enablePcvrCentral = true;
        public bool EnablePcvrCentral
        {
            get => _enablePcvrCentral;
            set => SetValue(ref _enablePcvrCentral, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the Mr. Nlce PCVR Mods Installer Hub.
        /// Default: true (enabled).
        /// Catalog-level coverage: reports the games the hub documents (see
        /// <see cref="MrNlceHubParser"/>); produces the single "VRMod PCVR Mods Hub" tag.
        /// </summary>
        private bool _enableMrNlceHub = true;
        public bool EnableMrNlceHub
        {
            get => _enableMrNlceHub;
            set => SetValue(ref _enableMrNlceHub, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the Mr. Survivor VR HUB.
        /// Default: true (enabled).
        /// Catalog-level coverage: reports the games the hub documents (see
        /// <see cref="MrSurvivorHubParser"/>); produces the single "VRMod Mr Survivor" tag.
        /// </summary>
        private bool _enableMrSurvivorHub = true;
        public bool EnableMrSurvivorHub
        {
            get => _enableMrSurvivorHub;
            set => SetValue(ref _enableMrSurvivorHub, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the VorpX supported-games list.
        /// Default: false (DISABLED by default — opt in manually).
        /// Catalog-level coverage: reports the games VorpX supports (see
        /// <see cref="VorpXParser"/>); produces the single "VRMod VorpX" tag and marks the
        /// games 6DOF/VR-capable.
        /// </summary>
        private bool _enableVorpX;
        public bool EnableVorpX
        {
            get => _enableVorpX;
            set => SetValue(ref _enableVorpX, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the Luke Ross (R.E.A.L. VR) source.
        /// Default: false (DISABLED by default — opt in manually).
        /// Catalog-level coverage over a STATIC list of known R.E.A.L. VR games (the full list
        /// is Patreon-gated; see <see cref="LukeRossParser"/>); produces the single
        /// "VRMod LukeRoss" tag.
        /// </summary>
        private bool _enableLukeRoss;
        public bool EnableLukeRoss
        {
            get => _enableLukeRoss;
            set => SetValue(ref _enableLukeRoss, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the CompoundVR flat-to-VR catalog.
        /// Default: true (enabled).
        /// Catalog-level coverage: reports the flat-to-VR games CompoundVR documents (see
        /// <see cref="CompoundVrParser"/>) and emits the single umbrella "VRMod" tag (CompoundVR is a
        /// catalog, so it contributes no framework tag of its own).
        /// </summary>
        private bool _enableCompoundVR = true;
        public bool EnableCompoundVR
        {
            get => _enableCompoundVR;
            set => SetValue(ref _enableCompoundVR, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the Steam Curator "UEVR Compatibility" list.
        /// Default: true (enabled).
        /// Catalog-level coverage over a Steam curator's playtested UEVR games (see
        /// <see cref="SteamCuratorParser"/>); produces the single "VRMod UEVR" tag. Matched by
        /// Steam App ID (the list carries exact App IDs).
        /// </summary>
        private bool _enableSteamCuratorUEVR = true;
        public bool EnableSteamCuratorUEVR
        {
            get => _enableSteamCuratorUEVR;
            set => SetValue(ref _enableSteamCuratorUEVR, value);
        }

        /// <summary>
        /// Whether to fetch VR mod data from the Steam Curator "VorpX Games" list.
        /// Default: true (enabled).
        /// Catalog-level coverage over a Steam curator's VorpX-supported games (see
        /// <see cref="SteamCuratorParser"/>); produces the single "VRMod VorpX" tag. Matched by
        /// Steam App ID (the list carries exact App IDs).
        /// </summary>
        private bool _enableSteamCuratorVorpX = true;
        public bool EnableSteamCuratorVorpX
        {
            get => _enableSteamCuratorVorpX;
            set => SetValue(ref _enableSteamCuratorVorpX, value);
        }

        /// <summary>
        /// Source IDs whose game Links are disabled (the user unchecked "Links" for
        /// that source in the settings UI).
        ///
        /// Default (empty list) means links are enabled for every source. Controls
        /// whether a saved accepted match from the source contributes a link to the
        /// game's Links (its per-game page, or the source's own website when it has
        /// no per-game page).
        /// </summary>
        private List<string> _disabledLinkSources = new();
        public List<string> DisabledLinkSources
        {
            get => _disabledLinkSources;
            set => SetValue(ref _disabledLinkSources, value);
        }

        /// <summary>
        /// True when game Links should include links for the given source
        /// (default: true — links are enabled unless the source was explicitly
        /// disabled in this list).
        /// </summary>
        public bool IsSourceLinksEnabled(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId))
                return false;
            return !DisabledLinkSources.Any(s => string.Equals(s, sourceId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Duration to cache fetched source entries (in minutes).
        /// Default: 10 minutes.
        /// Valid range: 1–43200 minutes (30 days).
        /// After this duration, entries are re-fetched from sources. The window covers
        /// the time a freshly scanned listing is still fresh enough to tag games that
        /// join the library right after the scan (the single-game scan on install uses
        /// the same cache) — long enough for that, short enough that a stale listing
        /// never tags a game against data that changed since.
        /// </summary>
        private uint _cacheDurationMinutes = 10;
        public uint CacheDurationMinutes
        {
            get => _cacheDurationMinutes;
            set => SetValue(ref _cacheDurationMinutes, value);
        }

        /// <summary>
        /// List of game IDs that were tagged with VRMod tags in the last scan.
        ///
        /// Marked [DontSerialize] — not saved to JSON.
        /// Used during stale tag detection: games in this list that no longer match
        /// any source entry have their VRMod tags removed.
        /// </summary>
        [DontSerialize]
        public List<Guid> PreviouslyTaggedGames { get; set; } = new();

        /// <summary>
        /// Per-source scan status tracking.
        ///
        /// Marked [DontSerialize] — not saved to JSON.
        /// Key: Source ID (lowercase), Value: SourceStatus (last scan time, result, error)
        /// </summary>
        [DontSerialize]
        public Dictionary<string, SourceStatus> SourceStatuses { get; set; } = new();

        /// <summary>
        /// User decisions on matches (Accepted/Rejected/Pending).
        ///
        /// Key format: "{sourceId}||{sourceGameName}||{playniteGameName}" (per-mapping, no category)
        /// Value: "Accepted", "Rejected", or "Pending"
        ///
        /// This dictionary is serialized to JSON and persists across plugin restarts.
        /// When a decision is "Accepted", the match is auto-applied on next scan.
        /// When "Rejected", the match is auto-skipped.
        /// When "Pending" or not present, the match goes through the review dialog.
        /// </summary>
        private Dictionary<string, string> _matchDecisions = new();

        /// <summary>
        /// Guards the three decision dictionaries for OUR code, and the copy-on-write
        /// publication described below. They are plain
        /// <see cref="Dictionary{TKey,TValue}"/>, which is not thread-safe, and they are written
        /// from two different threads: the review dialog writes on Playnite's UI thread
        /// (Apply / Revert buttons) and a scan thread writes when a game stops matching
        /// (<see cref="ClearDecisionsForGame"/>). Reads happen on both as well.
        ///
        /// The lock alone is not enough, and the reason is the shape of Playnite's own access:
        /// <see cref="MatchDecisions"/>, <see cref="DecisionTimestamps"/> and
        /// <see cref="SourceTagsByDecision"/> reach disk on a write that is not ours: Playnite
        /// does not rewrite them when it exits, so the save paths in this plugin are what write
        /// that file. Serialization.GetClone(Settings) also walks them when the
        /// settings page opens — both on a thread that is not a scan thread, through plain property
        /// reads, with no lock of ours on that path. A plain Dictionary enumerated
        /// while another thread adds a key throws <see cref="InvalidOperationException"/>
        /// ("Collection was modified") inside Playnite's settings save; and a map read half-applied is
        /// worse than a crash, because it writes a torn decision set into config.json — data loss with
        /// no symptom.
        ///
        /// So the maps are published copy-on-write: a writer clones the published map, mutates the
        /// clone, and swaps the clone in. An instance that has been handed out through a property
        /// getter is never mutated again, and that is the guarantee a walker needs. Maps a write does
        /// not actually change are not cloned at all (see <see cref="CloneWithout"/> and the early
        /// return in <see cref="EnsureDecisionTimestampUtc"/>) — a steady-state scan, where every match
        /// already has a decision and a timestamp, publishes nothing.
        ///
        /// The getters must keep returning the live published instance. Newtonsoft's default
        /// ObjectCreationHandling reuses what a getter returns and adds into it, so a copy-returning
        /// getter silently discards everything loaded from the plugin's own
        /// ExtensionsData/<id>/config.json. Safety comes from never mutating a published instance, not
        /// from handing out copies.
        /// </summary>
        private readonly object _decisionSync = new();

        private Dictionary<string, string> Decisions() => _matchDecisions ??= new();
        private Dictionary<string, string> Timestamps() => _decisionTimestamps ??= new();
        private Dictionary<string, string> TagsByDecision() => _sourceTagsByDecision ??= new();

        /// <summary>
        /// A private clone of a published map, for a writer to mutate. No other reference exists to
        /// the clone until <see cref="Publish"/> swaps it in, so a mutation can never overlap a read.
        /// </summary>
        private static Dictionary<string, string> WritableCopyOf(Dictionary<string, string> published)
            => new Dictionary<string, string>(published);

        /// <summary>
        /// Publishes cloned maps. A null argument means "this write did not change that map": the
        /// published instance stays where it is. Call only while holding <see cref="_decisionSync"/>.
        /// </summary>
        private void Publish(
            Dictionary<string, string>? decisions,
            Dictionary<string, string>? timestamps,
            Dictionary<string, string>? tags)
        {
            if (decisions != null) _matchDecisions = decisions;
            if (timestamps != null) _decisionTimestamps = timestamps;
            if (tags != null) _sourceTagsByDecision = tags;
        }

        /// <summary>
        /// A clone of <paramref name="published"/> with <paramref name="keys"/> removed, or null when
        /// none of them are present — null means "nothing to publish", so a removal that removes
        /// nothing costs no copy and swaps no instance. The published map is not touched either way.
        /// </summary>
        private static Dictionary<string, string>? CloneWithout(
            Dictionary<string, string> published,
            List<string> keys)
        {
            foreach (var key in keys)
            {
                if (!published.ContainsKey(key))
                    continue;

                var copy = new Dictionary<string, string>(published);
                foreach (var k in keys)
                    copy.Remove(k);
                return copy;
            }

            return null;
        }

        /// <summary>
        /// Gets or sets user decisions on matches (persisted to JSON).
        ///
        /// The getter hands out the published instance, which no writer mutates afterwards (see
        /// <see cref="_decisionSync"/>) — Playnite's serializer and its settings-page clone walk it
        /// safely while a scan writes. The getter initializing the dictionary when null is what
        /// lets deserialization populate the live instance.
        /// The setter publishes the dictionary it is given: from that point it belongs to the
        /// settings object and must not be mutated by the caller.
        /// </summary>
        public Dictionary<string, string> MatchDecisions
        {
            get => _matchDecisions ??= new();
            set => _matchDecisions = value;
        }

        /// <summary>
        /// A private copy of the stored decisions, taken under <see cref="_decisionSync"/>.
        /// Use this whenever code wants to walk the decisions while other threads may be writing
        /// them (settings rows, statistics, the uninstall cleanup): enumerating the live
        /// <see cref="Dictionary{TKey,TValue}"/> while a scan thread adds or removes a key throws
        /// <see cref="InvalidOperationException"/> - and in a plugin, that surfaces as a crashed
        /// settings page.
        /// </summary>
        public Dictionary<string, string> SnapshotMatchDecisions()
        {
            lock (_decisionSync) return new Dictionary<string, string>(Decisions());
        }

        private Dictionary<string, string> _decisionTimestamps = new();

        /// <summary>
        /// Per-decision timestamps (persisted to JSON), keyed by the same key format as
        /// <see cref="MatchDecisions"/> (sourceId||sourceGame||{gameId:D}, per-mapping).
        /// Values are ISO 8601 UTC strings recording when a match was first seen (Pending)
        /// or last decided (Accepted / Rejected). Used by the review dialog's time filter.
        /// Published copy-on-write like <see cref="MatchDecisions"/>; the getter initializing
        /// the dictionary when null is what lets deserialization populate the live instance.
        /// </summary>
        public Dictionary<string, string> DecisionTimestamps
        {
            get => _decisionTimestamps ??= new();
            set => _decisionTimestamps = value;
        }

        private Dictionary<string, string> _sourceTagsByDecision = new();

        /// <summary>
        /// The source's tag set recorded when an ACCEPTED decision was saved, keyed by the same
        /// key format as <see cref="MatchDecisions"/> (sourceId||sourceGame||{gameId:D}, per-mapping).
        /// The value is a newline-joined list of tag names (see
        /// <see cref="VrModCategories.TagNamesFor(System.Collections.Generic.IReadOnlyList{string}, bool?, bool?, bool?, string?)"/>).
        ///
        /// This is the changed-tag baseline: when the source adds/removes a tag (e.g. 6DOF) for
        /// an already-accepted match, the next scan diffs the current source tags against this
        /// stored set and the review dialog flags the change. Only ACCEPTED decisions carry a
        /// baseline — a rejected match is "not the same game", so its source tags are
        /// irrelevant and are never stored.
        /// Published copy-on-write like <see cref="MatchDecisions"/>; the getter initializing
        /// the dictionary when null is what lets deserialization populate the live instance.
        /// </summary>
        public Dictionary<string, string> SourceTagsByDecision
        {
            get => _sourceTagsByDecision ??= new();
            set => _sourceTagsByDecision = value;
        }


        /// <summary>
        /// The decision stored under an already-built key, or <see cref="MatchDecision.Pending"/>
        /// when that mapping has no saved decision. The key shape is one definition:
        /// <see cref="VrModEntry.MakeDecisionKey(string, System.Guid)"/>.
        /// </summary>
        /// <summary>
        /// Guards the key-shaped entry points. <see cref="VrModEntry.MakeDecisionKey(string, Guid)"/>
        /// cannot produce an empty key, so an empty one here means a caller built it by hand (or a
        /// review row reached the dialog without the key it was read from) — storing under "" would
        /// file the decision where no lookup can ever find it again.
        /// </summary>
        private static string RequireDecisionKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException(
                    "A decision key must be built by VrModEntry.MakeDecisionKey; an empty key is unreachable.",
                    nameof(key));
            return key;
        }

        public MatchDecision GetDecisionByKey(string key)
            => TryGetDecisionByKey(RequireDecisionKey(key), out var decision)
                ? decision
                : MatchDecision.Pending;

        public bool TryGetDecisionByKey(string key, out MatchDecision decision)
        {
            RequireDecisionKey(key);
            // Enum.TryParse (not Parse): a corrupt/unparseable stored value degrades to
            // Pending instead of throwing. Stored values are always decision.ToString()
            // names, so this is behaviour-identical for valid data and only differs for
            // corrupt data (where throwing was the bug).
            lock (_decisionSync)
            {
                if (Decisions().TryGetValue(key, out var val)
                    && Enum.TryParse(val, out decision))
                {
                    return true;
                }
            }

            decision = MatchDecision.Pending;
            return false;
        }

        /// <summary>
        /// Decision record for a key the caller already built (see
        /// <see cref="TryGetDecisionByKey"/> for why the key is passed in).
        /// </summary>
        public void SetDecisionByKey(string key, MatchDecision decision, string? sourceTags = null)
        {
            RequireDecisionKey(key);
            var stamp = DateTime.UtcNow.ToString(DecisionTimestampFormat, CultureInfo.InvariantCulture);
            lock (_decisionSync)
            {
                var decisions = WritableCopyOf(Decisions());
                var timestamps = WritableCopyOf(Timestamps());
                decisions[key] = decision.ToString();
                timestamps[key] = stamp;

                // The baseline map is cloned only when this decision really changes it: a decision
                // with a tag baseline adds one, a decision without one only pays for a copy when a
                // stale baseline is sitting there to drop.
                Dictionary<string, string>? tags = null;
                if (sourceTags != null)
                {
                    tags = WritableCopyOf(TagsByDecision());
                    tags[key] = sourceTags;
                }
                else if (TagsByDecision().ContainsKey(key))
                {
                    tags = WritableCopyOf(TagsByDecision());
                    tags.Remove(key);
                }

                Publish(decisions, timestamps, tags);
            }
        }


        /// <summary>
        /// Clears the decision stored under an already-built key (see
        /// <see cref="TryGetDecisionByKey"/> for why the key is passed in).
        /// </summary>
        public void ClearDecisionByKey(string key)
        {
            RequireDecisionKey(key);
            var keys = new List<string> { key };
            lock (_decisionSync)
            {
                Publish(
                    CloneWithout(Decisions(), keys),
                    CloneWithout(Timestamps(), keys),
                    CloneWithout(TagsByDecision(), keys));
            }
        }

        /// <summary>
        /// Applies many decision writes as ONE copy-on-write publish.
        ///
        /// Why this exists: <see cref="SetDecisionByKey"/> clones the published maps because no reader
        /// can take <see cref="_decisionSync"/> (Playnite's own <c>BeginEdit</c> clone and both save
        /// paths walk these maps). That is correct per write and quadratic for a batch: accepting 2 000
        /// review rows on a 5 000-decision store cloned the map 2 000 times - 3,29 s of copying,
        /// measured (dev tools/DecisionCostBench). One clone plus N mutations is O(N).
        ///
        /// Identical outcome to the per-key writes, including the tag-baseline rule (a write with tags
        /// adds one, a write without tags only pays for the baseline map when a stale baseline is
        /// sitting there to drop) and the stamp: every key in one batch shares one timestamp, which is
        /// what a batch is - one moment's worth of deciding.
        /// </summary>
        public void SetDecisionsByKey(IReadOnlyList<(string Key, MatchDecision Decision, string? SourceTags)> writes)
        {
            if (writes.Count == 0)
                return;
            foreach (var write in writes)
                RequireDecisionKey(write.Key);

            var stamp = DateTime.UtcNow.ToString(DecisionTimestampFormat, CultureInfo.InvariantCulture);
            lock (_decisionSync)
            {
                var decisions = WritableCopyOf(Decisions());
                var timestamps = WritableCopyOf(Timestamps());
                Dictionary<string, string>? tags = null;
                foreach (var write in writes)
                {
                    decisions[write.Key] = write.Decision.ToString();
                    timestamps[write.Key] = stamp;
                    if (write.SourceTags != null)
                    {
                        tags ??= WritableCopyOf(TagsByDecision());
                        tags[write.Key] = write.SourceTags;
                    }
                    else if ((tags != null && tags.ContainsKey(write.Key)) || TagsByDecision().ContainsKey(write.Key))
                    {
                        tags ??= WritableCopyOf(TagsByDecision());
                        tags.Remove(write.Key);
                    }
                }

                Publish(decisions, timestamps, tags);
            }
        }

        /// <summary>
        /// Clears many decisions in one publish, on the same argument as
        /// <see cref="SetDecisionsByKey"/>. A removal that removes nothing still costs no copy:
        /// <see cref="CloneWithout"/> returns null for each map none of the keys are present in.
        /// </summary>
        public void ClearDecisionsByKey(IReadOnlyList<string> keys)
        {
            if (keys.Count == 0)
                return;
            foreach (var key in keys)
                RequireDecisionKey(key);

            var keyList = keys as List<string> ?? new List<string>(keys);
            lock (_decisionSync)
            {
                Publish(
                    CloneWithout(Decisions(), keyList),
                    CloneWithout(Timestamps(), keyList),
                    CloneWithout(TagsByDecision(), keyList));
            }
        }

        /// <summary>
        /// Moves one mapping's stored data - the decision, its timestamp and its tag baseline - from
        /// a key built out of a listing's display name to the same mapping's key built out of the
        /// listing's stable identity (<see cref="VrModEntry.DecisionKeyComponent"/>).
        ///
        /// This is how decisions stored before identity-shaped keys stay alive. It runs while the
        /// listing still has the name the stored key was built from - during a scan, for the entry
        /// that carries that name - which is the only moment a legacy key can be recognised at all.
        /// Afterwards the mapping survives the source renaming the listing, because the stored key no
        /// longer contains the name. A mapping whose listing has no stable identity is never touched:
        /// its key stays name-shaped and behaves as it always did.
        ///
        /// Returns false (and drops the legacy entry rather than duplicating it) when the identity key
        /// already carries a decision, so the identity-shaped one always wins over a legacy duplicate.
        /// </summary>
        public bool TryRekeyDecision(string legacyKey, string identityKey)
        {
            RequireDecisionKey(legacyKey);
            RequireDecisionKey(identityKey);
            if (string.Equals(legacyKey, identityKey, StringComparison.Ordinal))
                return false;

            lock (_decisionSync)
            {
                var decisions = Decisions();
                if (!decisions.TryGetValue(legacyKey, out var storedValue))
                    return false;

                var timestamps = Timestamps();
                var tags = TagsByDecision();
                var legacyKeys = new List<string> { legacyKey };

                if (decisions.ContainsKey(identityKey))
                {
                    // Two keys for one mapping: keep the identity-shaped decision the user made
                    // under the listing's current identity, and drop the stale legacy duplicate so
                    // it cannot be counted twice in the "matched" statistics.
                    Publish(CloneWithout(decisions, legacyKeys), CloneWithout(timestamps, legacyKeys),
                        CloneWithout(tags, legacyKeys));
                    return false;
                }

                var newDecisions = WritableCopyOf(decisions);
                newDecisions[identityKey] = storedValue;
                newDecisions.Remove(legacyKey);

                Dictionary<string, string>? newTimestamps = null;
                if (timestamps.TryGetValue(legacyKey, out var stamp))
                {
                    newTimestamps = WritableCopyOf(timestamps);
                    newTimestamps[identityKey] = stamp;
                    newTimestamps.Remove(legacyKey);
                }

                Dictionary<string, string>? newTags = null;
                if (tags.TryGetValue(legacyKey, out var tagBaseline))
                {
                    newTags = WritableCopyOf(tags);
                    newTags[identityKey] = tagBaseline;
                    newTags.Remove(legacyKey);
                }

                Publish(newDecisions, newTimestamps, newTags);
                return true;
            }
        }

        /// <summary>
        /// Clears all match decisions (reverts all matches to Pending).
        ///
        /// Used by the "Reset all decisions" action in the review dialog.
        /// After this call, all matches will go through the review dialog on next scan.
        /// </summary>
        public void ClearAllDecisions()
        {
            lock (_decisionSync)
            {
                // A published map is frozen, so clearing means publishing three empty maps.
                // Clear()ing the instance Playnite might be holding right now is exactly what the
                // publication rule forbids.
                Publish(
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>());
            }
        }

        /// <summary>
        /// Clears all per-mapping decisions for a specific Playnite game.
        ///
        /// Used when a game is no longer matched: every per-mapping decision keyed on
        /// that game's stable Id (the 3rd key segment) is cleared so the game is
        /// re-evaluated on the next scan. Keyed by Id (not name) so a store duplicate with
        /// the same display name is NOT cleared when a sibling duplicate goes stale.
        /// </summary>
        /// <param name="gameId">The Playnite game Id.</param>
        public void ClearDecisionsForGame(Guid gameId)
        {
            var idText = gameId.ToString("D");
            List<string>? keysToRemove = null;
            lock (_decisionSync)
            {
                keysToRemove = Decisions()
                    .Where(kvp =>
                    {
                        var parts = kvp.Key.Split(DecisionKeySeparators, StringSplitOptions.None);
                        // Per-mapping key: sourceId||sourceGame||gameId. A no-longer-matched game
                        // drops ALL of its per-mapping decisions (keyed by game Id, not tag/name).
                        if (parts.Length != 3) return false;
                        return string.Equals(parts[2], idText, StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in keysToRemove)
                {
                    // Collect-then-publish: the key scan reads the published map, the removal
                    // happens in a clone of it.
                    Publish(
                        CloneWithout(Decisions(), keysToRemove),
                        CloneWithout(Timestamps(), keysToRemove),
                        CloneWithout(TagsByDecision(), keysToRemove));
                    break;
                }
            }
        }

        /// <summary>
        /// Clears every decision belonging to any of the given games in ONE walk of the stored
        /// decisions and ONE publish.
        ///
        /// Why: <see cref="ClearDecisionsForGame"/> finds a game's keys by walking the whole decision
        /// map, so calling it once per game - which is exactly what the stale-tag sweep does, one call
        /// per game that lost its VRMod tags - costs O(games x stored decisions). This costs
        /// O(stored decisions + games): one walk, a key collected when its game-Id part is one of the
        /// requested Ids.
        ///
        /// Same rules as the single-game clear, because a bulk clear that behaved differently would
        /// just move the bug somewhere quieter: keys match on the third part of a
        /// sourceId||sourceGame||gameId key, OrdinalIgnoreCase, so a same-name store duplicate keeps
        /// its own decisions; a legacy name-shaped or two-part key is never touched; timestamps and tag
        /// baselines go with the decision; and when none of the games has a stored decision nothing is
        /// cloned and nothing is published.
        /// </summary>
        /// <param name="gameIds">The Playnite game Ids whose decisions all go.</param>
        public void ClearDecisionsForGames(IReadOnlyList<Guid> gameIds)
        {
            if (gameIds.Count == 0)
                return;

            var idTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gameId in gameIds)
                idTexts.Add(gameId.ToString("D"));

            List<string>? keysToRemove = null;
            lock (_decisionSync)
            {
                foreach (var kvp in Decisions())
                {
                    var parts = kvp.Key.Split(DecisionKeySeparators, StringSplitOptions.None);
                    if (parts.Length != 3)
                        continue;
                    if (!idTexts.Contains(parts[2]))
                        continue;
                    keysToRemove ??= new List<string>();
                    keysToRemove.Add(kvp.Key);
                }

                if (keysToRemove == null)
                    return;

                Publish(
                    CloneWithout(Decisions(), keysToRemove),
                    CloneWithout(Timestamps(), keysToRemove),
                    CloneWithout(TagsByDecision(), keysToRemove));
            }
        }

        /// <summary>
        /// Every stored decision that points at one Playnite game, as
        /// (sourceId, sourceGameName, decision) - the inverse of
        /// <see cref="ClearDecisionsForGame"/>.
        ///
        /// This exists because per-game link reconciliation used to ask
        /// <c>GetDecision(sourceId, entry.GameName, game.Id)</c> for EVERY cached catalog entry,
        /// once per game: with the ~3 100 entries this plugin actually fetches, that is 3 100
        /// key-building lookups per reconciled game, and it ran on Playnite's UI thread. Walking
        /// the stored decisions instead costs what the user actually recorded (hundreds in total,
        /// a handful per game) and the same amount whether the catalog has 300 or 3 000 entries.
        /// Entries a decision points at that are no longer cached are simply absent, exactly as
        /// before. A stored <see cref="MatchDecision.Pending"/> is reported (callers that only own
        /// links for a settled match filter it out, as the old loop did).
        /// </summary>
        public List<(string SourceId, string SourceGame, MatchDecision Decision)> GetDecisionsForGame(Guid gameId)
        {
            var idText = gameId.ToString("D");
            var result = new List<(string, string, MatchDecision)>();
            lock (_decisionSync)
            {
                foreach (var kvp in Decisions())
                {
                    var parts = kvp.Key.Split(DecisionKeySeparators, StringSplitOptions.None);
                    if (parts.Length != 3) continue;                    // legacy name-keyed or malformed
                    if (!string.Equals(parts[2], idText, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Enum.TryParse(kvp.Value, out MatchDecision decision))
                        result.Add((parts[0], parts[1], decision));
                }
            }
            return result;
        }

        /// <summary>
        /// Removes legacy (name-keyed) decision entries — a one-time data cleanup that runs
        /// on every settings load and is a no-op once the file is clean.
        ///
        /// Before the game-Id key format, decisions were keyed by Playnite game NAME
        /// (sourceId||sourceGame||gameName, plus an even older 4-segment format). The Id-keyed
        /// code can never look such keys up, so they are inert — but they still count toward
        /// the "Games with VR mods" statistics. Removing them makes those stats honest again;
        /// the affected mappings simply come back for review once (no migration is performed
        /// on purpose — the saved decisions are re-made in the review dialog).
        /// </summary>
        /// <returns>The number of legacy keys removed (0 if nothing was legacy).</returns>
        public int RemoveLegacyNameKeyedDecisions()
        {
            List<string> legacyKeys;
            lock (_decisionSync)
            {
                legacyKeys = Decisions().Keys
                    .Concat(Timestamps().Keys)
                    .Concat(TagsByDecision().Keys)
                    .Distinct(StringComparer.Ordinal)
                    .Where(IsLegacyDecisionKey)
                    .ToList();
                Publish(
                    CloneWithout(Decisions(), legacyKeys),
                    CloneWithout(Timestamps(), legacyKeys),
                    CloneWithout(TagsByDecision(), legacyKeys));
            }
            return legacyKeys.Count;
        }

        /// <summary>
        /// True for keys that are NOT in the current per-mapping format
        /// (sourceId||sourceGame||{gameId:D}) — i.e. exactly 3 segments whose 3rd parses as a
        /// game Id. Matches the legacy name-keyed 3-segment keys, the older 4-segment keys,
        /// and anything malformed.
        /// </summary>
        private static bool IsLegacyDecisionKey(string key)
        {
            var parts = key.Split(DecisionKeySeparators, StringSplitOptions.None);
            if (parts.Length != 3) return true;
            return !Guid.TryParse(parts[2], out _);
        }

        /// <summary>
        /// The format used to store per-decision timestamps in <see cref="DecisionTimestamps"/>
        /// (ISO 8601 round-trip, full precision, UTC — the "O" format).
        /// </summary>
        private const string DecisionTimestampFormat = "O";
        private static readonly string[] DecisionKeySeparators = { "||" };
        private static readonly string[] LineSeparators = { "\n" };

        /// <summary>
        /// Returns the stored decision timestamp (UTC) for the given match, or null if none recorded.
        /// </summary>
        public DateTime? GetDecisionTimestampUtc(string key)
        {
            RequireDecisionKey(key);
            lock (_decisionSync)
                return Timestamps().TryGetValue(key, out var s) ? TryParseDecisionTimestampUtc(s) : null;
        }

        /// <summary>
        /// Returns the stored source-tag baseline for the given decision key as a list of tag
        /// names, or null when no baseline is recorded (e.g. the match was never accepted while
        /// this feature was present). Used by the changed-tag detection.
        /// </summary>
        public List<string>? GetSourceTags(string key)
        {
            lock (_decisionSync)
            {
                if (!TagsByDecision().TryGetValue(key, out var s) || string.IsNullOrEmpty(s))
                    return null;
                return s.Split(LineSeparators, StringSplitOptions.None).ToList();
            }
        }

        /// <summary>
        /// Returns the stored decision timestamp (UTC) for the given match, or (if none recorded)
        /// stores <paramref name="nowUtc"/> and returns it. This is the idempotent first-seen /
        /// last-decided upsert used during scans: the first scan to see a match records the time;
        /// later scans read it back unchanged, and a decision recorded via
        /// <see cref="SetDecision"/> keeps its own (decision) timestamp.
        /// </summary>
        public DateTime EnsureDecisionTimestampUtc(string key, DateTime nowUtc)
        {
            RequireDecisionKey(key);
            // One critical section rather than a read followed by a write: two scan threads asking
            // about the same match must not both conclude they were the first to see it.
            lock (_decisionSync)
            {
                if (Timestamps().TryGetValue(key, out var stored)
                    && TryParseDecisionTimestampUtc(stored) is { } existing)
                {
                    // The common case in a steady-state scan: nothing to change, so nothing is
                    // cloned and the published map stays the instance every walker is reading.
                    return existing;
                }

                var timestamps = WritableCopyOf(Timestamps());
                timestamps[key] = nowUtc.ToString(DecisionTimestampFormat, CultureInfo.InvariantCulture);
                Publish(null, timestamps, null);
                return nowUtc;
            }
        }

        private static DateTime? TryParseDecisionTimestampUtc(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t
                : null;
        }
    }

    /// <summary>
    /// Tracks the scan status of a single data source.
    ///
    /// This class is not serialized to JSON. Instead, it is stored in
    /// VrModTagSettings.SourceStatuses and updated during each scan.
    ///
    /// Displayed in the settings UI to show the user when each source
    /// was last scanned and whether the scan succeeded.
    /// </summary>
    public class SourceStatus
    {
        /// <summary>
        /// UTC timestamp of the last successful scan for this source.
        /// </summary>
        public DateTime LastScanTime { get; set; }

        /// <summary>
        /// True if the last scan succeeded, false if it failed.
        /// </summary>
        public bool LastResult { get; set; }

        /// <summary>
        /// Number of entries the source returned on the last scan.
        /// 0 is anomalous (healthy sources always return entries) and is surfaced as a
        /// "no data" health state, distinct from success (green) and failure (red).
        /// </summary>
        public int LastEntryCount { get; set; }

        /// <summary>
        /// Error message from the last failed scan (null if scan succeeded).
        /// </summary>
        public string? LastError { get; set; }

        /// <summary>
        /// Change identifier from the last fetch (used to detect source updates).
        /// Computed as SHA-256 hash of the fetched content by each parser.
        /// </summary>
        public string? LastChangeId { get; set; }

        /// <summary>
        /// Display name of the source (e.g., "RototRobot VRMods-List").
        /// Used in the settings UI.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// How many entries this source returned on the last scan carry at least one VRMod tag
        /// (a category/facet tag, per <c>VrModEntry.AppliedTagNames</c>) - i.e. what the run
        /// FOUND, independent of matching/review/apply. 0 when the last scan failed or returned
        /// no such entries. In-memory, set during the fetch phase.
        /// </summary>
        public int LastTagCount { get; set; }

        /// <summary>
        /// How many entries this source returned on the last scan carry a source deep link
        /// (<c>VrModEntry.SourceUrl</c>) - i.e. what the run FOUND, independent of
        /// matching/review/apply. 0 when the last scan failed or returned no such entries.
        /// In-memory, set during the fetch phase.
        /// </summary>
        public int LastLinkCount { get; set; }

        /// <summary>
        /// How many tags this source's last scan APPLIED to its matched games (the apply
        /// phase), as opposed to <see cref="LastTagCount"/> (what the run found). 0 when
        /// the last scan applied nothing (e.g. a catalog source, which carries no tags of
        /// its own, or a run that matched nothing). In-memory, set during the apply phase.
        /// </summary>
        public int LastTagCountApplied { get; set; }

        /// <summary>
        /// How many source deep links this source's last scan ENSURED on its accepted
        /// matches (added if missing, kept if present), as opposed to
        /// <see cref="LastLinkCount"/> (what the run found). 0 when the last scan ensured
        /// none (e.g. no accepted matches yet, links disabled, or a failed run). In-memory,
        /// set during the apply phase.
        /// </summary>
        public int LastLinkCountApplied { get; set; }

        /// <summary>
        /// The multiline debug log of this source's last run (shown in the settings "Last run"
        /// view): one line per web request (URL, HTTP status code, body size, elapsed time),
        /// per per-game extraction problem (a row that failed to parse, a missing field, ...),
        /// and the run's milestones (cache hit vs fetch, entries found, tags/links applied,
        /// and the error when the run failed). In-memory like the rest of this record
        /// (SourceStatuses is [DontSerialize]): empty until the first scan this Playnite run.
        /// </summary>
        public List<string> LastRunLog { get; set; } = new List<string>();
    }

    /// <summary>
    /// ViewModel for the plugin settings UI.
    ///
    /// This class bridges the data model (VrModTagSettings) and the XAML settings view.
    /// It implements Playnite's ISettings interface, which requires:
    /// - BeginEdit(): Creates a working copy of settings for editing
    /// - CancelEdit(): Restores the working copy to the saved state
    /// - EndEdit(): Saves the working copy and persists to disk
    /// - VerifySettings(): Validates settings before saving
    ///
    /// The ViewModel also manages:
    /// - Source rows (merged enable checkbox + website link + scan status, bound to settings UI)
    /// - Decision counters (the Accepted count feeds the "Games with VR mods" statistic)
    /// - Command for scanning now
    ///
    /// Settings changes trigger:
    /// - CacheDurationMinutes: Logs the new value (cache uses it on next fetch)
    /// - ScanIntervalMinutes: Restarts the scheduled scan timer
    /// - EnableRototRobot/EnableFlat2VR/EnableUeVR: Syncs to registry and refreshes cache
    /// </summary>
    public class VrModSettingsViewModel : ObservableObject, ISettings
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly VrModTagPlugin _plugin;
        private readonly VrModSourceRegistry _registry;
        private VrModTagSettings? _editingClone;
        private readonly object _scanLock = new();
        private bool _isScanning;

        private VrModTagSettings _settings = null!;
        /// <summary>
        /// Current settings data.
        /// Set in BeginEdit(), EndEdit(), and CancelEdit().
        /// Property change events trigger source row updates.
        /// </summary>
        public VrModTagSettings Settings
        {
            get => _settings;
            set
            {
                if (_settings != null)
                    _settings.PropertyChanged -= OnSettingsPropertyChanged;
                if (value != null)
                {
#pragma warning disable CS8601 // Possible null reference assignment (SDK ObservableObject generic ref parameter)
                    SetValue(ref _settings, value);
#pragma warning restore CS8601
                    _settings!.PropertyChanged += OnSettingsPropertyChanged;
                    UpdateSourceRows();
                }
            }
        }

        /// <summary>
        /// True if a scan is currently in progress.
        /// Used to disable the "Scan &amp; Review VR Mods" button during scans.
        /// </summary>
        public bool IsScanning => _isScanning;

        /// <summary>
        /// One row per registered source (bound to the settings UI).
        /// Each row merges the enable checkbox, a clickable website link, and the
        /// last scan status for that source.
        /// </summary>
        public ObservableCollection<VrModSourceRowViewModel> SourceRows { get; } = new();

        private int _acceptedCount;

        /// <summary>
        /// Number of Accepted decisions — the games currently tagged with a VR mod.
        /// Drives the "Games with VR mods" statistic.
        /// </summary>
        public int AcceptedCount
        {
            get => _acceptedCount;
            private set => SetValue(ref _acceptedCount, value);
        }

        /// <summary>
        /// Command for the "Full Scan" button: runs a full scan that auto-applies the matches
        /// (accepted re-applied, pending tagged) and shows no dialog. Enabled only when not
        /// currently scanning.
        /// </summary>
        public ICommand FullScanCommand { get; }

        /// <summary>
        /// Command for the "Review VR Mods" button: opens the review dialog for every matched
        /// game (new + accepted + rejected) from the in-memory source cache, WITHOUT rescanning —
        /// so the user can review or change their decisions. Only when no source data is cached
        /// yet (cold start) does it run a scan first. Enabled only when not currently scanning.
        /// </summary>
        public ICommand ReviewCommand { get; }

        /// <summary>
        /// Command for the "Help" button: opens the README that ships inside the installed
        /// extension package, so the documentation always matches the installed version.
        /// </summary>
        public ICommand HelpCommand { get; }

        /// <summary>
        /// Constructs the settings ViewModel.
        ///
        /// Steps:
        /// 1. Stores references to the plugin and source registry
        /// 2. Initializes the command (Scan &amp; Review VR Mods)
        /// 3. Loads saved settings from disk (or creates defaults)
        /// 4. Refreshes decision lists from saved decisions
        /// </summary>
        /// <param name="plugin">The plugin instance (for saving settings).</param>
        /// <param name="registry">The source registry (for source status).</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Saved settings are user-owned files: corrupt, locked, or written by a newer version. Default settings beat a settings dialog that will not open.")]
        public VrModSettingsViewModel(VrModTagPlugin plugin, VrModSourceRegistry registry)
        {
            _plugin = plugin;
            _registry = registry;
            FullScanCommand = new RelayCommand(ExecuteFullScan, () => !IsScanning);
            ReviewCommand = new RelayCommand(ExecuteReview, () => !IsScanning);
            HelpCommand = new RelayCommand(() => VrModHelp.OpenManual());

            // A corrupted saved-settings file (e.g. an interrupted write) makes LoadPluginSettings
            // throw. Without this guard the exception would escape the plugin constructor and the
            // extension would fail to load at all; fall back to defaults instead so the user can
            // still open the settings (and save a fresh file) to recover.
            VrModTagSettings? savedSettings = null;
            try
            {
                savedSettings = _plugin.LoadPluginSettings<VrModTagSettings>();
            }
            catch (Exception ex)
            {
                Logger.Warn($"VRMod: saved settings could not be read ({ex.Message}); starting with default settings.");
                savedSettings = null;
            }
            if (savedSettings != null)
            {
                var removedLegacy = savedSettings.RemoveLegacyNameKeyedDecisions();
                if (removedLegacy > 0)
                {
                    Logger.Info($"VRMod: removed {removedLegacy} legacy name-keyed decision(s) from saved settings; "
                        + "those mappings are pending review again (decisions are now keyed by game Id).");
                }
                Settings = savedSettings;
            }
            else
            {
                Settings = new VrModTagSettings();
            }

            RefreshDecisionLists();

            // Reflect the persisted (or default) enable state on the registry BEFORE any scan.
            // Every source is constructed enabled (BaseVrModParser ctor), and SyncSettingsToRegistry
            // is otherwise only called on Save / live checkbox change — so without this, the startup
            // scan (and the scheduled timer) would fetch sources the user has disabled, including the
            // opt-in VorpX / Luke Ross sources that default to disabled. Syncing here makes
            // GetEnabledSources() correct from the very first scan.
            SyncSettingsToRegistry();
        }

        /// <summary>
        /// Read-only view of the source registry, whose per-source IsEnabled the scan gate
        /// (GetEnabledSources) reads. Exposed for tests and the settings grid.
        /// </summary>
        internal VrModSourceRegistry SourceRegistry => _registry;

        /// <summary>
        /// Handles property change events from the Settings data model.
        ///
        /// When any setting property changes, this method delegates to OnSettingsChanged
        /// to update the source status list and sync changes to the registry.
        /// </summary>
        /// <param name="sender">The settings object that changed.</param>
        /// <param name="e">Property change event arguments.</param>
        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnSettingsChanged(e.PropertyName);
        }

        /// <summary>
        /// Creates a working copy of settings for editing.
        ///
        /// Called when the settings dialog opens. Takes a rollback snapshot of
        /// the settings; the live settings object (shared with the tag service)
        /// is edited in place. "Cancel" rolls the editable values back to the
        /// snapshot; "Save" persists the live object as-is.
        /// </summary>
        public void BeginEdit()
        {
            _editingClone = Serialization.GetClone(Settings);
            // SourceStatuses is [DontSerialize], so the clone starts with an empty
            // dictionary. Share the live dictionary by reference so the snapshot
            // reflects the real scan statuses instead of "Never scanned".
            _editingClone.SourceStatuses = Settings.SourceStatuses;
        }

        /// <summary>
        /// Rolls the user-editable settings back to their BeginEdit values.
        ///
        /// Called when the user clicks "Cancel" in the settings dialog.
        /// The restore happens IN PLACE on the live settings object: the tag
        /// service holds this exact instance, so replacing it would desync the
        /// service's live decisions from what this page displays and persists —
        /// saving afterwards would then overwrite newer decisions with a stale
        /// snapshot. Decision data is never restored here: it is owned by the
        /// tag service and is not edited on this page.
        /// Guards against a missing snapshot to prevent inconsistent state.
        /// </summary>
        public void CancelEdit()
        {
            if (_editingClone == null)
                return;
            RestoreEditableValues(_editingClone);
            _editingClone = null;
            RefreshDecisionLists();
        }

        /// <summary>
        /// Copies the user-editable fields (scan interval, fuzzy threshold, cache
        /// duration, source enable flags, Links toggles) from a settings snapshot
        /// back onto the live settings object, in place.
        /// </summary>
        private void RestoreEditableValues(VrModTagSettings snapshot)
        {
            Settings.ScanIntervalMinutes = snapshot.ScanIntervalMinutes;
            Settings.FuzzyMatchThreshold = snapshot.FuzzyMatchThreshold;
            Settings.CacheDurationMinutes = snapshot.CacheDurationMinutes;
            Settings.EnableRototRobot = snapshot.EnableRototRobot;
            Settings.EnableFlat2VR = snapshot.EnableFlat2VR;
            Settings.EnableUeVR = snapshot.EnableUeVR;
            Settings.EnableReFramework = snapshot.EnableReFramework;
            Settings.EnablePcvrCentral = snapshot.EnablePcvrCentral;
            Settings.EnableMrNlceHub = snapshot.EnableMrNlceHub;
            Settings.EnableMrSurvivorHub = snapshot.EnableMrSurvivorHub;
            Settings.EnableVorpX = snapshot.EnableVorpX;
            Settings.EnableLukeRoss = snapshot.EnableLukeRoss;
            Settings.EnableCompoundVR = snapshot.EnableCompoundVR;
            Settings.EnableSteamCuratorUEVR = snapshot.EnableSteamCuratorUEVR;
            Settings.EnableSteamCuratorVorpX = snapshot.EnableSteamCuratorVorpX;
            Settings.DisabledLinkSources = new List<string>(snapshot.DisabledLinkSources);
        }

        /// <summary>
        /// Saves the current settings and persists to disk.
        ///
        /// Called when the user clicks "Save" in the settings dialog.
        /// Steps:
        /// 1. Syncs source enable/disable settings to the registry
        /// 2. Saves settings to disk (calls _plugin.SavePluginSettings)
        /// 3. Refreshes decision lists from saved decisions
        ///
        /// The persisted object is the live settings instance shared with the
        /// tag service, whose decisions are always current — so saving here can
        /// never overwrite newer decisions made via the review dialog, a scan,
        /// or a reset.
        /// </summary>
        public void EndEdit()
        {
            SyncSettingsToRegistry();
            _plugin.SavePluginSettings(Settings);
            RefreshDecisionLists();
        }

        /// <summary>
        /// Validates settings before saving.
        ///
        /// Checks:
        /// - ScanIntervalMinutes: 0 (never) or 1–525600 minutes (365 days)
        /// - CacheDurationMinutes: 1–43200 minutes (30 days)
        /// - FuzzyMatchThreshold: 0.5–1.0 (lower values match nearly the whole library)
        ///
        /// Returns false with error messages if validation fails.
        /// Called by Playnite when the user clicks "Save" — prevents invalid settings from being saved.
        /// </summary>
        /// <param name="errors">List of validation error messages (empty if valid).</param>
        /// <returns>True if settings are valid, false otherwise.</returns>
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (Settings.ScanIntervalMinutes < 0)
                errors.Add("Scan interval must not be negative (0 = never).");
            if (Settings.ScanIntervalMinutes > 525600)
                errors.Add("Scan interval must not exceed 525600 minutes (365 days).");
            if (Settings.CacheDurationMinutes < 1)
                errors.Add("Cache duration must be at least 1 minute.");
            if (Settings.CacheDurationMinutes > 43200)
                errors.Add("Cache duration must not exceed 43200 minutes (30 days).");
            // The setter clamps, so Settings.FuzzyMatchThreshold is always in range by the time this
            // runs; what still has to be reported is that the entered value was out of range at all.
            // Saying so here is what keeps "it quietly used a different number" from being the
            // behaviour — and returning false keeps the out-of-range number from being persisted.
            if (Settings.FuzzyMatchThresholdWasAdjusted)
            {
                errors.Add(
                    $"Fuzzy match threshold must be between {VrModTagSettings.MinFuzzyMatchThreshold:0%} and {VrModTagSettings.MaxFuzzyMatchThreshold:0%}. "
                    + $"The value you entered is out of that range and was adjusted to {Settings.FuzzyMatchThreshold:0%}. "
                    + "A threshold below 50% matches almost every game in the library, which means bulk tag and link writes you would have to undo by hand.");
                Settings.ClearFuzzyMatchThresholdAdjustment();
            }
            return errors.Count == 0;
        }

        /// <summary>
        /// Syncs source enable/disable settings from the data model to the registry.
        ///
        /// Called in EndEdit() to ensure the registry reflects the user's settings
        /// selections. The registry's IsEnabled property controls whether each source
        /// is fetched during scans.
        ///
        /// Mapping:
        /// - "RototRobot" → EnableRototRobot
        /// - "Flat2VR" → EnableFlat2VR
        /// - "UeVR" → EnableUeVR
        /// - Other sources → unchanged
        /// </summary>
        private void SyncSettingsToRegistry()
        {
            foreach (var source in _registry.Sources)
            {
                var id = source.SourceId.ToLowerInvariant();
                source.IsEnabled = id switch
                {
                    "rototrobot" => Settings.EnableRototRobot,
                    "flat2vr" => Settings.EnableFlat2VR,
                    "uevr" => Settings.EnableUeVR,
                    "reframework" => Settings.EnableReFramework,
                    "pcvrcentral" => Settings.EnablePcvrCentral,
                    "mrnlcehub" => Settings.EnableMrNlceHub,
                    "mrsurvivorhub" => Settings.EnableMrSurvivorHub,
                    "vorpx" => Settings.EnableVorpX,
                    "lukeross" => Settings.EnableLukeRoss,
                    "compoundvr" => Settings.EnableCompoundVR,
                    "steamcuratoruevr" => Settings.EnableSteamCuratorUEVR,
                    "steamcuratorvorpx" => Settings.EnableSteamCuratorVorpX,
                    _ => source.IsEnabled
                };
            }
        }

        /// <summary>
        /// Executes the "Scan &amp; Review VR Mods" command: runs a scan on a background thread
        /// (to avoid blocking the UI) and opens the review dialog for EVERY matched game
        /// (new + accepted + rejected), so the user can review or change their decisions.
        ///
        /// Guards against concurrent scans using _scanLock, and updates the source status
        /// list and decision lists on the UI thread after the scan completes.
        /// </summary>
        private void ExecuteFullScan() => RunScanOnBackground(() => _plugin.RunFullScanApplyOnly());

        /// <summary>
        /// Executes the "Review VR Mods" command: opens the review dialog for EVERY matched
        /// game (new + accepted + rejected) from the in-memory source cache, WITHOUT rescanning
        /// (the startup or an earlier scan populated the cache). Only on a cold start (no
        /// cached data) does it run a scan first. Runs on a background thread so the UI is not
        /// blocked while the dialog is open (same guard/refresh behavior as the scan path).
        /// </summary>
        private void ExecuteReview() => RunScanOnBackground(() => _plugin.RunReviewFromCache());

        /// <summary>
        /// Runs a blocking scan on a background thread (the scan methods block the caller in
        /// ActivateGlobalProgress / while the review dialog is open) and, when it returns,
        /// refreshes the source rows and decision lists on the UI thread. Guards against
        /// overlapping scans with <see cref="_scanLock"/> / <see cref="_isScanning"/>.
        /// </summary>
        private void RunScanOnBackground(System.Action scanAction)
        {
            lock (_scanLock)
            {
                if (_isScanning)
                    return;
                _isScanning = true;
            }

            CommandManager.InvalidateRequerySuggested();
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    scanAction();
                }
                finally
                {
                    lock (_scanLock)
                    {
                        _isScanning = false;
                    }
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        UpdateSourceRows();
                        RefreshDecisionLists();
                        CommandManager.InvalidateRequerySuggested();
                    });
                }
            });
        }

        /// <summary>
        /// Updates the SourceRows ObservableCollection.
        ///
        /// Clears the existing list and adds a VrModSourceRowViewModel for each
        /// registered source. Each row reads the enable state, website, and last
        /// scan status live from the registry and settings.
        ///
        /// Called when the settings instance changes or after a scan completes.
        /// Not called on every source enable toggle — the row's StatusText is
        /// computed live and the checkbox writes through to Settings directly.
        /// </summary>
        private void UpdateSourceRows()
        {
            SourceRows.Clear();
            foreach (var source in _registry.Sources)
            {
                var row = new VrModSourceRowViewModel(source, Settings);
                row.OnSourceDisabled = OnSourceRowDisabled;
                row.OnLinksToggled = OnSourceLinksToggled;
                SourceRows.Add(row);
            }
        }

        /// <summary>
        /// Handles a source's "Links" checkbox toggling in the settings UI. Reconciles that
        /// source's links across the library immediately: unchecking removes the source's links,
        /// checking restores them for saved accepted matches (needs the source's cached entries;
        /// a cold cache restores nothing until the next scan).
        /// </summary>
        private void OnSourceLinksToggled(string sourceId)
        {
            _plugin.TagService.ReconcileSourceLinksForSource(sourceId);
        }

        /// <summary>
        /// Handles a source being disabled in the settings UI (its row's IsEnabled went true→false).
        /// Asks whether to remove the VRMod tags that came from that source so the library stays clean;
        /// on Yes, removes them via the tag service. Decisions are left in place so re-enabling the
        /// source re-applies the tags automatically on the next scan.
        /// </summary>
        private void OnSourceRowDisabled(string sourceId)
        {
            var source = _registry.Sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            var sourceName = source?.DisplayName ?? sourceId;
            // Themed Playnite dialog (a native MessageBox is jarring here): "Remove tags" is the
            // default action, "Cancel" keeps the tags. Closing the dialog (X / Esc) also counts
            // as Cancel (ShowMessage returns null).
            var chosen = API.Instance.Dialogs.ShowMessage(
                "Remove the VRMod tags that came from \"" + sourceName + "\"?\n\nSay \"Remove tags\" to clean your library of tags that came from this source. Tags shared with other sources are kept.",
                "Source disabled",
                MessageBoxImage.Question,
                new List<MessageBoxOption>
                {
                    new MessageBoxOption(RemoveTagsOptionTitle, isDefault: true),
                    new MessageBoxOption(CancelOptionTitle, isCancel: true),
                });
            if (!ShouldRemoveTagsForSource(chosen?.Title))
                return;

            _plugin.TagService.RemoveTagsForSource(sourceId);
        }

        /// <summary>Button text for the "remove the source's tags" option in the source-disabled confirmation.</summary>
        internal const string RemoveTagsOptionTitle = "Remove tags";

        /// <summary>Button text for the cancel option in the source-disabled confirmation.</summary>
        internal const string CancelOptionTitle = "Cancel";

        /// <summary>
        /// Pure, testable mapping from the chosen option's title to whether the source's tags
        /// should be removed. An unknown or null title (dialog closed without a pick) maps to
        /// "keep the tags" (no removal).
        /// </summary>
        internal static bool ShouldRemoveTagsForSource(string? chosenOptionTitle)
        {
            return string.Equals(chosenOptionTitle, RemoveTagsOptionTitle, StringComparison.Ordinal);
        }

        /// <summary>
        /// Handles source-specific setting changes (EnableRototRobot, etc.).
        ///
        /// When a source enable/disable setting changes, syncs to the registry so
        /// new scans use the updated setting. The source rows do NOT need to be
        /// rebuilt: each row's enable checkbox writes through to Settings and its
        /// status text is computed live.
        /// </summary>
        /// <param name="settingName">The setting that changed.</param>
        private void SourceSettingChanged(string settingName)
        {
            if (settingName == nameof(Settings.EnableRototRobot) ||
                settingName == nameof(Settings.EnableFlat2VR) ||
                settingName == nameof(Settings.EnableUeVR) ||
                settingName == nameof(Settings.EnableReFramework) ||
                settingName == nameof(Settings.EnablePcvrCentral) ||
                settingName == nameof(Settings.EnableMrNlceHub) ||
                settingName == nameof(Settings.EnableMrSurvivorHub) ||
                settingName == nameof(Settings.EnableVorpX) ||
                settingName == nameof(Settings.EnableLukeRoss))
            {
                SyncSettingsToRegistry();
            }
        }

        /// <summary>
        /// Entry point for settings change notifications from the data model.
        ///
        /// Delegates to SourceSettingChanged for source-specific settings.
        /// Other settings (ScanIntervalMinutes, CacheDurationMinutes, etc.) are
        /// handled by VrModTagPlugin.OnSettingsChanged (which restarts timer, etc.).
        /// </summary>
        /// <param name="settingName">The setting that changed.</param>
        public void OnSettingsChanged(string settingName)
        {
            SourceSettingChanged(settingName);
        }

        /// <summary>
        /// Refreshes the decision counters from settings.
        ///
        /// Called in EndEdit() and after each scan to keep the statistics in sync
        /// with the current decisions stored in settings.
        /// </summary>
        public void RefreshDecisionLists()
        {
            var accepted = 0;
            foreach (var kvp in Settings.SnapshotMatchDecisions())
            {
                if (Enum.TryParse(kvp.Value, out MatchDecision decision) && decision == MatchDecision.Accepted)
                    accepted++;
            }
            AcceptedCount = accepted;
        }

        /// <summary>
        /// Refreshes this page after the tag service's decisions changed out of
        /// band (review dialog saved, reset, auto-scan): rebuilds the per-source
        /// rows (match counts) and the decision totals. This page shares its
        /// settings object with the tag service, so no reload is needed — only a
        /// re-read of the live data.
        /// </summary>
        public void RefreshAfterDecisionsChanged()
        {
            UpdateSourceRows();
            RefreshDecisionLists();
        }
    }
}
