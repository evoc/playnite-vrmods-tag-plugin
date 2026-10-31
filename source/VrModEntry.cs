using System;

namespace Playnite.VRMods
{
    /// <summary>
    /// Represents a single VR mod entry from an external data source.
    /// Each entry corresponds to one game that has a VR mod available.
    ///
    /// Entries are created by parser classes (RototRobotVrModParser, Flat2VrParser, UeVrProfilesParser)
    /// when they fetch and parse external web pages. The entries are then matched against
    /// the user's Playnite library to determine which games should receive VR mod tags.
    ///
    /// Example: A RototRobot entry might contain:
    ///   GameName: "DOOM Eternal VR mod by ModderX"
    ///   Category: "UEVR Profiles"
    ///   SteamAppId: "1234567"
    ///   SourceId: "RototRobot"
    /// </summary>
    public class VrModEntry
    {
        /// <summary>
        /// Maximum allowed length for the game name (200 characters).
        /// Longer names from external sources are truncated to this limit.
        /// This matches Playnite's internal game name limit.
        /// </summary>
        private const int MaxGameNameLength = 200;

        /// <summary>
        /// Length caps for the other free-text fields a source supplies. Every one of them is scraped
        /// from an attacker-chosen page and then carried in the per-source cache (and in anything the
        /// scan persists), so an uncapped field is a storage/memory lever the site controls rather than
        /// this plugin controls. Unlike the deep-link fields - where an unsafe value is DROPPED because
        /// a bad URL is worthless - these are TRUNCATED: the entry still matches its game and still
        /// contributes its tags, it simply carries no more display text than the cap.
        /// </summary>
        private const int MaxAuthorLength = 100;
        private const int MaxAdditionalInfoLength = 400;

        /// <summary>
        /// Cap on a scraped Steam App ID. It is used only for exact comparison against a Playnite
        /// game's Steam App ID, and real Steam App IDs are short numbers; an oversized value is both
        /// a storage lever and a per-comparison cost, so it is cut to a generous fixed width.
        /// </summary>
        private const int MaxSteamAppIdLength = 32;

        /// <summary>
        /// Truncates to <paramref name="maxLength"/> (null stays null - an absent field is not an
        /// empty field, and the duplicate merge treats null as "not yet provided").
        /// </summary>
        private static string? Truncate(string? value, int maxLength)
        {
            if (value == null || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength);
        }

        /// <summary>
        /// The name of the game as reported by the external VR mod source.
        /// May include VR suffixes or author credits (e.g., "DOOM VR mod by Author").
        /// These are stripped during matching by VrModNameMatcher.StripVrSuffixes().
        ///
        /// Values longer than 200 characters are automatically truncated.
        /// Null values are stored as empty strings.
        /// </summary>
        public string GameName
        {
            get => _gameName;
            set
            {
                _gameName = (value ?? string.Empty).Length > MaxGameNameLength
                    ? (value ?? string.Empty).Substring(0, MaxGameNameLength)
                    : value ?? string.Empty;
                _decisionKeyComponent = null;
            }
        }
        private string _gameName = string.Empty;

        /// <summary>
        /// The categories this VR mod belongs to (a mod may have SEVERAL — e.g. a RototRobot
        /// mod that is both "Full Motion Controls" and "UEVR"). Each non-catalog category
        /// contributes a "VRMod {category}" tag; a game accumulates the UNION of all its mods'
        /// categories (see <see cref="VrModCategories.TagNamesFor(VrModEntry)"/>). Empty means the
        /// entry has no category and contributes only its facet tags (6DOF / POV / quality).
        /// </summary>
        public System.Collections.Generic.List<string> Categories { get; set; } = new();

        /// <summary>
        /// Legacy single-category alias (one category per mod). Setting it replaces <see
        /// cref="Categories"/> with that single category (or clears it), so pre-existing
        /// single-category writers keep compiling and stay consistent with <see
        /// cref="Categories"/>. Multi-category code sets <see cref="Categories"/> directly.
        /// Reading returns the first category (or null when there are none). Not persisted —
        /// <see cref="Categories"/> is the source of truth.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Category
        {
            get => Categories.Count > 0 ? Categories[0] : string.Empty;
            set
            {
                if (string.IsNullOrEmpty(value))
                    Categories = new System.Collections.Generic.List<string>();
                else
                    Categories = new System.Collections.Generic.List<string> { value! };
            }
        }

        /// <summary>
        /// Whether this VR mod provides 6DOF (six-degree-of-freedom) VR, as an
        /// orthogonal capability facet (independent of the primary category).
        ///
        /// Null (default) means the source did not declare a 6DOF capability for
        /// this entry. True means the mod is 6DOF (e.g. REFramework provides
        /// "generic 6DOF VR support for all games"); false means it is
        /// explicitly not 6DOF (e.g. a 3DOF/head-tracked-only mod).
        ///
        /// When true and the entry matches a game, the additional
        /// "VRMod 6DOF" tag is applied alongside the primary category tag and
        /// the umbrella "VRMod" tag.
        /// </summary>
        public bool? Is6DOF { get; set; }

        /// <summary>
        /// Whether this VR mod is a first-person experience, as an orthogonal capability facet
        /// (independent of the primary category). Null (default) means the source did not
        /// declare a first-person perspective for this entry; true means it does (and a
        /// "VRMod First Person" facet tag is applied on accept).
        /// </summary>
        public bool? IsFirstPerson { get; set; }

        /// <summary>
        /// Whether this VR mod is a third-person experience, as an orthogonal capability facet
        /// (independent of the primary category). Null (default) means the source did not
        /// declare a third-person perspective for this entry; true means it does (and a
        /// "VRMod Third Person" facet tag is applied on accept).
        /// </summary>
        public bool? IsThirdPerson { get; set; }

        /// <summary>
        /// Optional quality/grade(s) of the VR experience (e.g. "Excellent", "Native-like",
        /// "Playable", "Experimental"). Each value contributes a "VRMod {quality}" facet tag
        /// (orthogonal to the category and 6DOF facets). A collection because a single game
        /// can have several mods graded differently (the PCVR Central meta-mod keeps the union
        /// of all its mods' grades — union-all; see external-references.md, "The model: orthogonal
        /// dimensions, union-all"). Empty = no quality tag.
        /// </summary>
        public System.Collections.Generic.List<string> Qualities { get; set; } = new();

        /// <summary>
        /// Optional motion-controls level(s) (e.g. "Full Motion", "Partial Motion", "Gamepad
        /// Only", "No Motion"), as an orthogonal capability facet (independent of the category
        /// and the Quality facet). A collection for the same union-all reason as
        /// <see cref="Qualities"/>: one game's mods can declare different motion levels (the
        /// PCVR Central meta-mod keeps the union). Empty = no motion tag.
        /// </summary>
        public System.Collections.Generic.List<string> Motions { get; set; } = new();

        /// <summary>
        /// Optional mod-status value(s) as the source currently reports them (e.g. "Active",
        /// "Update Detected", "Outdated", "Broken", "Abandoned"). An orthogonal facet: what the
        /// source says about the mod itself - does it still work with the game's current build, has
        /// a newer mod superseded it, was it dropped - independent of <see cref="Qualities"/> (how
        /// good a working mod is) and <see cref="Motions"/> (what it captures). A collection for the
        /// same union-all reason as those two: one game's mods can be in different states, and the
        /// whole set is the useful information. "Active" together with "Broken" means a working mod
        /// and a broken one both exist to choose between - not that one mod both works and does not.
        /// A parser fills this only when the source states it (empty = no status tag), and only
        /// through its mapping method so an unrecognised status never becomes a tag.
        /// </summary>
        public System.Collections.Generic.List<string> Statuses { get; set; } = new();

        /// <summary>
        /// Legacy single-grade alias (one grade per mod). Setting it replaces <see cref="Qualities"/>
        /// with that single grade (or clears it), so pre-existing single-grade writers (and unit
        /// tests) keep compiling and stay consistent with <see cref="Qualities"/>. Multi-grade code
        /// (the PCVR Central meta-mod) sets <see cref="Qualities"/> directly. Reading returns the first
        /// grade, or null when there are none (matching the old nullable <c>Quality</c> property).
        /// Not persisted — <see cref="Qualities"/> is the source of truth.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Quality
        {
            get => Qualities.Count > 0 ? Qualities[0] : null;
            set
            {
                if (string.IsNullOrEmpty(value))
                    Qualities = new System.Collections.Generic.List<string>();
                else
                    Qualities = new System.Collections.Generic.List<string> { value! };
            }
        }

        /// <summary>
        /// Legacy single motion-level alias (one level per mod). Setting it replaces <see cref="Motions"/>
        /// with that single level (or clears it), mirroring <see cref="Quality"/>. Multi-level code sets
        /// <see cref="Motions"/> directly. Reading returns the first level, or null when there are none.
        /// Not persisted — <see cref="Motions"/> is the source of truth.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Motion
        {
            get => Motions.Count > 0 ? Motions[0] : null;
            set
            {
                if (string.IsNullOrEmpty(value))
                    Motions = new System.Collections.Generic.List<string>();
                else
                    Motions = new System.Collections.Generic.List<string> { value! };
            }
        }

        /// <summary>
        /// Steam App ID for this game, if available from the source.
        /// Used for exact matching against Playnite games' Steam links.
        /// Example: "1234567" for https://store.steampowered.com/app/1234567/
        /// Null if the source does not provide this information.
        /// Values longer than <see cref="MaxSteamAppIdLength"/> are truncated: a value that long
        /// cannot be a real App ID, and it is attacker-chosen text.
        /// </summary>
        public string? SteamAppId
        {
            get => _steamAppId;
            set
            {
                _steamAppId = Truncate(value, MaxSteamAppIdLength);
                _decisionKeyComponent = null;
            }
        }
        private string? _steamAppId;

        /// <summary>
        /// The ID of the source this entry came from (e.g., "RototRobot", "Flat2VR").
        /// Set automatically by VrModTagService when entries are fetched.
        /// Used for per-source decision tracking and caching.
        /// </summary>
        public string? SourceId { get; set; }

        /// <summary>
        /// Per-game deep link(s) to this entry's mod/game page on its source, when the
        /// source provides one (e.g., the PCVR Central per-game page). A collection
        /// because one merged game can own several distinct deep links (the
        /// duplicate-listing merge unions them, mirroring the Categories/Qualities/
        /// Motions union-all pattern; each contributes its own Playnite link). Empty
        /// when the source has no per-entry page; the review dialog then falls back
        /// to the source's website.
        ///
        /// SECURITY: every assigned value is filtered through <see cref="WebUrl.IsSafeLinkUrl"/>
        /// and only absolute http/https URLs survive. These URLs are written into the user's
        /// library as Playnite <c>Link</c>s, and Playnite opens a Link with the OS shell
        /// (<c>Process.Start(UseShellExecute = true)</c>) without validating the scheme itself, so a
        /// scraped <c>file:</c>, <c>jhtml:</c>, <c>javascript:</c>, <c>steam:</c> or app-URI value
        /// must never reach <c>Link.Url</c>. Read this property (not <see cref="SourceUrl"/>) when
        /// constructing a Link.
        /// </summary>
        public System.Collections.Generic.List<string> SourceUrls
        {
            get => _sourceUrls;
            set
            {
                _sourceUrls = SafeSourceUrls(value);
                _decisionKeyComponent = null;
            }
        }
        private System.Collections.Generic.List<string> _sourceUrls = new();

        /// <summary>
        /// Keeps only safe http(s) URLs (see <see cref="WebUrl.IsSafeLinkUrl"/>), case-insensitively
        /// de-duplicated. A rejected value is dropped rather than truncated: the entry still matches
        /// and still tags its game, it simply owns no deep link, so the source's own website becomes
        /// the fallback link.
        /// </summary>
        private static System.Collections.Generic.List<string> SafeSourceUrls(
            System.Collections.Generic.IEnumerable<string>? urls)
        {
            var safe = new System.Collections.Generic.List<string>();
            if (urls == null)
                return safe;
            foreach (var url in urls)
            {
                if (!WebUrl.IsSafeLinkUrl(url))
                    continue;
                if (safe.Find(existing => string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)) != null)
                    continue;
                safe.Add(url!);
            }
            return safe;
        }

        /// <summary>
        /// Legacy single deep-link alias (one URL per entry). Setting it replaces
        /// <see cref="SourceUrls"/> with that single URL (or clears it), so pre-existing
        /// single-URL writers (parsers, tests) keep compiling and stay consistent with
        /// <see cref="SourceUrls"/>. Reading returns the first URL (or null when there
        /// are none). Not persisted — <see cref="SourceUrls"/> is the source of truth.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourceUrl
        {
            get => SourceUrls.Count > 0 ? SourceUrls[0] : null;
            set
            {
                if (string.IsNullOrEmpty(value))
                    SourceUrls = new System.Collections.Generic.List<string>();
                else
                    SourceUrls = new System.Collections.Generic.List<string> { value! };
            }
        }

        /// <summary>
        /// Name of the mod author/creator, if available from the source.
        /// Example: "ModderX" from "DOOM VR mod by ModderX".
        /// Extracted during parsing but not currently used for matching.
        /// Capped at <see cref="MaxAuthorLength"/> (scraped text, stored per entry).
        /// </summary>
        public string? Author
        {
            get => _author;
            set => _author = Truncate(value, MaxAuthorLength);
        }
        private string? _author;

        /// <summary>
        /// Additional information about this VR mod entry (PCVR Central's mod-feature list, a
        /// curator's note). Capped at <see cref="MaxAdditionalInfoLength"/>: it is the longest
        /// free-text field a source writes, and it is attacker-chosen.
        /// </summary>
        public string? AdditionalInfo
        {
            get => _additionalInfo;
            set => _additionalInfo = Truncate(value, MaxAdditionalInfoLength);
        }
        private string? _additionalInfo;

        /// <summary>
        /// The full set of tag names this entry would apply to its game if accepted: the
        /// category tag plus every set facet (6DOF, First/Third Person, quality). Single
        /// source of truth (see <see cref="VrModCategories.TagNamesFor(System.Collections.Generic.IReadOnlyList{string}, bool?, bool?, bool?, string?)"/>)
        /// shared by the tag service, the review-dialog preview, and the changed-tag
        /// detection.
        /// </summary>
        public System.Collections.Generic.List<string> AppliedTagNames
        {
            get => VrModCategories.TagNamesFor(this);
        }

        /// <summary>
        /// The identity half of a decision key (see
        /// <see cref="VrModNameMatcher.MakeDecisionKey(string, string, System.Guid)"/>): what this
        /// listing <em>is</em>, as far as its source can say, rather than what it is currently
        /// called. A decision keyed on the display name is unreachable the moment the site renames
        /// its listing - the pair goes back through review and the deep link it owned is dropped -
        /// so the key uses the most stable thing the listing offers:
        ///
        /// <list type="number">
        ///   <item><description><c>steam:{AppId}</c> when the listing names a Steam App ID. That is
        ///   per-listing here because every App-ID-bearing source of this plugin publishes one row
        ///   per game (a decision is one "this game has a VR mod at this source" statement, and
        ///   PCVR Central aggregates a game page's whole mod list into one entry).</description></item>
        ///   <item><description><c>url:{host}{path}</c> when the listing has its own page: scheme,
        ///   query and fragment are dropped, so an <c>http</c> to <c>https</c> switch or a tracking
        ///   parameter does not change the identity. The URL is already capped and scheme-checked by
        ///   <see cref="WebUrl.IsSafeLinkUrl"/>, which is why no separate length cap is added
        ///   here.</description></item>
        ///   <item><description>the display name when the source offers nothing else (VorpX, MrNlceHub,
        ///   MrSurvivorHub, LukeRoss and ReFramework publish bare name lists, where nothing in the
        ///   response separates a rename from a removal). A rename of one of those re-opens review on
        ///   purpose: the previous match is questionable once the listing name is gone, and re-matching
        ///   from scratch is the safer outcome.</description></item>
        /// </list>
        ///
        /// The identity is preferred over the URL because a Steam App ID survives a site rewriting its
        /// own slugs, and because a slug is frequently derived from the very title that gets renamed.
        ///
        /// Cached because the hot scan loop asks for it while deciding whether a candidate pair is even
        /// worth scoring. The setters of the three fields it is derived from clear the cache; the two
        /// identity fields are set while an entry is being built, before any scan reads it, so the only
        /// concurrency case is two threads computing the same value independently.
        /// </summary>
        public string DecisionKeyComponent
        {
            get
            {
                var urls = SourceUrls;
                var cached = _decisionKeyComponent;
                // The cache is valid only for the URL collection it was computed from: one parser
                // merges deep links into an entry it already built (BaseVrModParser.MergeDuplicate-
                // Games adds to SourceUrls in place), which is exactly the case a name-shaped identity
                // computed earlier would otherwise freeze. A different list instance or a different
                // count means the identity has to be rebuilt; the name and App ID setters clear the
                // cache outright. A racing pair of readers can each recompute the same string, which
                // is harmless.
                if (cached != null && ReferenceEquals(_decisionKeySourceUrls, urls)
                    && _decisionKeySourceUrlCount == urls.Count)
                    return cached;

                cached = ComputeDecisionKeyComponent();
                _decisionKeyComponent = cached;
                _decisionKeySourceUrls = urls;
                _decisionKeySourceUrlCount = urls.Count;
                return cached;
            }
        }
        private string? _decisionKeyComponent;
        private System.Collections.Generic.List<string>? _decisionKeySourceUrls;
        private int _decisionKeySourceUrlCount;

        /// <summary>
        /// The settings key of the decision "this Playnite game is this source listing": the
        /// listing's <see cref="DecisionKeyComponent"/> plus the game's stable Id, joined by
        /// <see cref="VrModNameMatcher.MakeDecisionKey(string, string, System.Guid)"/>. One definition,
        /// used by the scan, the review dialog and the cached-entry lookup; anything that reads or
        /// writes a mapping's decision builds the key here so all three agree.
        /// </summary>
        /// <param name="sourceId">The source that published the listing.</param>
        /// <param name="gameId">The matched Playnite game's Id.</param>
        public string MakeDecisionKey(string sourceId, Guid gameId)
            => VrModNameMatcher.MakeDecisionKey(sourceId, DecisionKeyComponent, gameId);

        private string ComputeDecisionKeyComponent()
        {
            var appId = SteamAppId;
            if (!string.IsNullOrWhiteSpace(appId))
                return "steam:" + appId!.Trim();

            var url = SourceUrl;
            if (url != null && System.Uri.TryCreate(url, System.UriKind.Absolute, out var parsed)
                && !string.IsNullOrEmpty(parsed.Host))
            {
                var path = (parsed.AbsolutePath ?? string.Empty).TrimEnd('/');
                return "url:" + parsed.Host.ToLowerInvariant() + path;
            }

            return GameName;
        }

        /// <summary>
        /// Returns a human-readable string showing the game name and mod categories.
        /// Used for logging and display purposes.
        /// </summary>
        /// <returns>String in format "GameName [Cat1, Cat2]".</returns>
        public override string ToString()
        {
            return $"{GameName} [{string.Join(", ", Categories)}]";
        }
    }
}
