using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for RototRobot's VRMods-List project.
    ///
    /// Fetches VR mod data from RototRobot's GitHub-hosted site. This is the largest and
    /// most comprehensive source, covering 6 categories of VR mods:
    /// - Full Motion Controls (full-body/infrared motion capture)
    /// - UEVR Profiles (Unreal Engine VR profiles for 6DOF)
    /// - Motion Gesture (gesture-based VR mods)
    /// - Head Tracked (head-tracking VR mods)
    /// - Injector (injectable VR tools)
    /// - Emulators (VR emulator support)
    ///
    /// Parsing strategy:
    /// 1. Primary: Parse the rendered HTML page (https://rototrobot.github.io/VRMods-List/)
    ///    which contains game items with rich data (names, Steam App IDs, screenshots)
    /// 2. Fallback: Parse the raw markdown (https://raw.githubusercontent.com/...)
    ///    which uses header-based sections with game names listed under each category header
    ///
    /// HTML structure (as of 2026-08):
    ///   &lt;div class="game-item" data-tags="motion 1st uevr"&gt;
    ///     &lt;span class="title-main"&gt;Game Name&lt;/span&gt;
    ///     &lt;span class="title-sub"&gt;by Author&lt;/span&gt;
    ///     &lt;span class="data-secondary-link" data-url="https://store.steampowered.com/app/123/..."&gt;
    ///   &lt;/div&gt;
    ///
    /// Category mapping (from data-tags to internal categories):
    /// - "motion" → "Full Motion Controls" (VRMod Full Motion Controls tag)
    /// - "uevr" → "UEVR Profiles" (VRMod UEVR Profiles tag)
    /// - "gesture" → "Motion Gesture" (VRMod Motion Gesture tag)
    /// - "3dof" → "Head Tracked" (VRMod Head Tracked tag)
    /// - "tool" → "Injector" (VRMod Injector tag)
    /// - "emulator" → "Emulators" (VRMod Emulators tag)
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  TWO requests, primary then fallback. PRIMARY: the rendered GitHub-Pages site, one
    ///  <c>&lt;div class="game-item"&gt;</c> per mod (rich data: name, author, Steam link, tags):
    ///
    ///      GET https://rototrobot.github.io/VRMods-List/
    ///
    ///      &lt;div class="game-item" data-tags="motion 1st uevr"&gt;
    ///        &lt;span class="title-main"&gt;Game Name&lt;/span&gt;
    ///        &lt;span class="title-sub"&gt;by Author&lt;/span&gt;
    ///        &lt;span class="data-secondary-link"
    ///              data-url="https://store.steampowered.com/app/123/..."&gt;
    ///      &lt;/div&gt;
    ///
    ///  Per game-item: title from the title-main span; Steam App ID from the data-url
    ///  /app/&lt;id&gt;/ link; category from the FIRST data-tags tag mapped by the TagToCategory
    ///  table (motion → Full Motion Controls, uevr → UEVR Profiles, gesture → Motion
    ///  Gesture, 3dof → Head Tracked, tool → Injector, emulator → Emulators). FALLBACK
    ///  (rendered site down): the raw Markdown index — category headers (e.g. "## UEVR
    ///  Profiles") each followed by game-name lines:
    ///
    ///      GET https://raw.githubusercontent.com/RototRobot/VRMods-List/gh-pages/index.md
    ///
    ///  If the site's tag vocabulary changes, the tag → category mapping silently
    ///  misclassifies games (see the TEMPORARY FIX note below).
    ///
    /// ──────────────────────────────────────────────────────────────────
    ///  TEMPORARY FIX (2026-08-30) — replace when the site stabilizes
    /// ──────────────────────────────────────────────────────────────────
    ///  This parser was rewritten because the live site changed its HTML
    ///  structure (from H2/H3 headings + div.game-card to div.game-item
    ///  with data-tags attributes). The old structure is no longer
    ///  served. The raw markdown fallback URL is currently a 404.
    ///
    ///  The tag-to-category mapping (data-tags → category) is inferred
    ///  from the site's tag vocabulary and may not match the site's
    ///  future intent. If the site adds new tags or changes the tag
    ///  scheme, this mapping will silently misclassify games.
    ///
    ///  TODO: Coordinate with the RototRobot project to get a stable
    ///  structured data feed (JSON, CSV, or API) so this parser does
    ///  not depend on HTML structure.
    /// ──────────────────────────────────────────────────────────────────
    /// </summary>
    public class RototRobotVrModParser : BaseVrModParser
    {
        /// <summary>
        /// URL for the rendered HTML page with game items.
        /// This is the primary source for parsing, providing rich structured data.
        /// </summary>
        private const string RenderedUrl = "https://rototrobot.github.io/VRMods-List/";

        /// <summary>
        /// URL for the raw markdown file.
        /// Used as a fallback if the rendered HTML is unavailable.
        /// Format: header-based sections (# Category) followed by game name lines.
        /// </summary>
        private const string RawUrl = "https://raw.githubusercontent.com/RototRobot/VRMods-List/gh-pages/index.md";

        /// <summary>
        /// SHA-256 hash of the last fetched content.
        /// Used as the ChangeIdentifier to detect when the source has been updated.
        /// Computed during FetchEntriesAsync and returned by ChangeIdentifier property.
        /// </summary>
        private string? _lastContentHash;

        /// <summary>
        /// Maps data-tags values to internal category names.
        /// A game can have multiple tags (e.g. "motion 1st uevr"), and the first
        /// matching tag determines its primary category.
        /// Order matters: more specific tags are checked first.
        /// </summary>
        private static readonly (string Tag, string Category)[] TagToCategory =
        {
            ("motion", VrModCategories.FullMotionControls),
            ("uevr", VrModCategories.UeVr),
            ("gesture", VrModCategories.MotionGesture),
            ("3dof", VrModCategories.HeadTracked),
            // "tool"/injector and emulator entries both resolve to the generic "Dedicated"
            // framework category (a game-specific VR mod / emulator VR mod).
            ("tool", VrModCategories.Dedicated),
            ("emulator", VrModCategories.Dedicated)
        };

        /// <summary>
        /// Maps raw markdown category headers to internal category names.
        /// The source uses slightly different naming than our tag names.
        /// </summary>
        private static readonly Dictionary<string, string> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Full Motion Controls", VrModCategories.FullMotionControls },
            { "UEVR Profiles", VrModCategories.UeVr },
            { "Motion Gesture Games", VrModCategories.MotionGesture },
            { "Head Tracked Games", VrModCategories.HeadTracked },
            // Raw source headers that map to the generic "Dedicated" framework category.
            { "Injector tools", VrModCategories.Dedicated },
            { "VR Emulators", VrModCategories.Dedicated }
        };

        /// <summary>
        /// Creates a new RototRobotVrModParser instance.
        /// Initializes the base class with source ID "RototRobot" and display name "RototRobot VRMods-List".
        /// </summary>
        public RototRobotVrModParser() : base("RototRobot", "RototRobot VRMods-List", "https://rototrobot.github.io/VRMods-List/")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        private static readonly char[] TagSeparatorChars = new[] { ' ' };

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var entries = new List<VrModEntry>();
            bool renderedParsed = false;

            try
            {
                // Primary: fetch and parse rendered HTML
                var html = await FetchStringAsync(RenderedUrl);
                _lastContentHash = ComputeHash(html);
                entries = ParseRenderedHtml(html);
                renderedParsed = true;
            }
            catch (Exception ex) when (IsRequestPlumbingFailure(ex))
            {
                // Fetch failed (see the FAILED line the fetch already logged) — fall through
                // to the raw markdown fallback below. A failure that is not request plumbing is a
                // fault in this parser and is not papered over by fetching the other way.
            }

            if (!renderedParsed || entries.Count == 0)
            {
                // Fallback: fetch and parse raw markdown. A fetch that SUCCEEDED but parsed 0
                // entries is a markup change, not an error - the old code silently returned
                // "no data" then; the raw fetch is the documented fallback for exactly that.
                LogRun(renderedParsed
                    ? "Rendered page parsed 0 entries (markup change?) - falling back to the raw markdown"
                    : "Rendered page fetch failed - falling back to the raw markdown");
                try
                {
                    var raw = await FetchStringAsync(RawUrl);
                    _lastContentHash = ComputeHash(raw);
                    entries = ParseRawMarkdown(raw);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to fetch RototRobot data: {ex.Message}", ex);
                }
            }

            return entries;
        }

        /// <summary>
        /// Parses game entries from the rendered HTML page.
        ///
        /// The rendered page has a structure of:
        ///   &lt;div class="game-item" data-tags="motion 1st uevr"&gt;
        ///     &lt;span class="title-main"&gt;Game Name&lt;/span&gt;
        ///     &lt;span class="title-sub"&gt;by Author&lt;/span&gt;
        ///     &lt;span class="data-secondary-link" data-url="https://store.steampowered.com/app/123/..."&gt;
        ///   &lt;/div&gt;
        ///
        /// This method uses regex to find game-item elements, extract the game name
        /// from title-main, the author from title-sub, and the Steam App ID from
        /// the data-secondary-link's data-url attribute.
        ///
        /// Category is determined by the data-tags attribute: the first matching
        /// tag from TagToCategory determines the category.
        ///
        /// This method is virtual to allow subclasses to override for custom parsing logic.
        /// </summary>
        /// <param name="html">The rendered HTML page content.</param>
        /// <returns>List of VR mod entries parsed from the HTML.</returns>
        public virtual List<VrModEntry> ParseRenderedHtml(string html)
        {
            var entries = new List<VrModEntry>();

            // Find all game-item elements
            // Pattern: <div class="game-item" data-tags="..."> ... </div>
            // We match up to the next game-item or the details section
            var itemPattern = new Regex(
                @"<div\s+class=""game-item""\s+data-tags=""([^""]*)""[^>]*>([\s\S]*?)(?=<div\s+class=""game-item""|<div\s+class=""details-|$)",
                RegexOptions.Singleline, ScannedRegexTimeout);

            var matches = itemPattern.Matches(html);

            int skipped = 0;
            foreach (Match match in matches)
            {
                var tags = match.Groups[1].Value.Trim();
                var content = match.Groups[2].Value;

                // Extract game name from title-main span
                var nameMatch = MatchScanned(content, @"<span\s+class=""title-main"">([^<]+)</span>");
                if (!nameMatch.Success)
                {
                    // A game item whose name span is missing: the item's markup changed.
                    skipped++;
                    LogRun($"Game item skipped (no <span class=\"title-main\">): {Snip(content)}");
                    continue;
                }

                var name = nameMatch.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    LogRun($"Game item skipped (empty title-main): {Snip(content)}");
                    continue;
                }

                // Extract author from title-sub span
                var authorMatch = MatchScanned(content, @"<span\s+class=""title-sub"">([^<]+)</span>");
                var author = authorMatch.Success ? authorMatch.Groups[1].Value.Trim() : null;

                // Extract Steam App ID from data-secondary-link's data-url
                var steamMatch = MatchScanned(content, @"store\.steampowered\.com/app/(\d+)/");
                var steamAppId = steamMatch.Success ? steamMatch.Groups[1].Value : null;

                // Determine the categories from data-tags (a mod may be several —
                // e.g. "motion 1st uevr" → both Full Motion Controls AND UEVR).
                var categories = MapTagsToCategories(tags);
                // Capability facets from the same data-tags: "1st" (first person), "3rd"
                // (third person), and "6dof" (6DOF). These are orthogonal to the categories.
                var tagArray = tags.Split(TagSeparatorChars, StringSplitOptions.RemoveEmptyEntries);

                entries.Add(new VrModEntry
                {
                    GameName = name,
                    Categories = categories,
                    SteamAppId = steamAppId,
                    Author = author,
                    IsFirstPerson = HasTag(tagArray, "1st") ? (bool?)true : null,
                    IsThirdPerson = HasTag(tagArray, "3rd") ? (bool?)true : null,
                    Is6DOF = HasTag(tagArray, "6dof") ? (bool?)true : null
                });
            }

            var parsedCount = entries.Count;
            // The site lists some games in several categories (same game, several data-tags
            // sets): merge the duplicates so the result is one entry per game carrying the
            // UNION of the occurrences' features (shared helper, see the service chokepoint
            // that applies it to every source).
            entries = BaseVrModParser.MergeDuplicateGames(entries);
            LogRun($"Parsed {parsedCount} entries from the rendered HTML ({skipped} items skipped)" +
                (entries.Count < parsedCount
                    ? $"; merged {parsedCount - entries.Count} duplicate listing(s) (same game in several categories) into one entry per game"
                    : string.Empty));
            return entries;
        }

        /// <summary>
        /// Maps the data-tags attribute value to the SET of internal categories it carries.
        /// A mod may be several (e.g. "motion 1st uevr" → both Full Motion Controls AND UEVR).
        /// Each matching category token contributes its category, in priority order, deduped
        /// (e.g. "tool emulator" → a single "Dedicated"). Falls back to a single
        /// "Full Motion Controls" if no known category tag is found (preserves the legacy
        /// default). The facet tokens (1st/3rd/6dof) are NOT categories and are ignored here.
        /// </summary>
        /// <param name="tags">The raw data-tags value (e.g., "motion 1st uevr").</param>
        /// <returns>The mapped category names (one per matching category token).</returns>
        public static List<string> MapTagsToCategories(string tags)
        {
            var tagArray = tags.Split(TagSeparatorChars, StringSplitOptions.RemoveEmptyEntries);
            var categories = new List<string>();
            foreach (var (tag, category) in TagToCategory)
            {
                if (Array.Exists(tagArray, t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!categories.Contains(category))
                        categories.Add(category);
                }
            }
            if (categories.Count == 0)
                categories.Add("Full Motion Controls");
            return categories;
        }

        /// <summary>
        /// Whether the tag array contains the given token (case-insensitive). Used to read the
        /// capability facets ("1st" / "3rd" / "6dof") out of a data-tags value.
        /// </summary>
        private static bool HasTag(string[] tagArray, string token)
        {
            return Array.Exists(tagArray, t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Parses game entries from raw markdown format.
        ///
        /// The raw markdown uses a simple structure:
        ///   # Category Name
        ///   Game Name 1
        ///   Game Name 2 VR mod by Author
        ///   # Next Category
        ///
        /// This method iterates through lines, tracking the current category (set by # headers)
        /// and collecting game names under each category. VR suffixes are stripped from game names.
        ///
        /// Lines that start with "#", "|", or are empty are skipped.
        ///
        /// This method is virtual to allow subclasses to override for custom parsing logic.
        /// </summary>
        /// <param name="markdown">The raw markdown file content.</param>
        /// <returns>List of VR mod entries parsed from the markdown.</returns>
        public virtual List<VrModEntry> ParseRawMarkdown(string markdown)
        {
            var entries = new List<VrModEntry>();
            var lines = markdown.Split('\n');
            string? currentCategory = null;
            var loggedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    var headerText = trimmed.Substring(2).Trim();
                    if (CategoryMap.TryGetValue(headerText, out var mappedCategory))
                    {
                        currentCategory = mappedCategory;
                    }
                    else
                    {
                        // A category header the parser does not know: every game under it is
                        // silently dropped. Name the header (once) so a site update that adds
                        // a new category is visible in the run log.
                        currentCategory = null;
                        if (loggedHeaders.Add(headerText))
                            LogRun($"Unknown category header \"{headerText}\" - games under it are skipped");
                    }
                }
                else if (!string.IsNullOrEmpty(currentCategory) && !trimmed.StartsWith("#", StringComparison.Ordinal) && !trimmed.StartsWith("|", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(trimmed))
                {
                    var name = StripVrSuffixes(trimmed).Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        entries.Add(new VrModEntry
                        {
                            GameName = name,
                            Categories = new List<string> { currentCategory! }
                        });
                    }
                }
            }

            var parsedCount = entries.Count;
            // Same duplicate-merge as the rendered path: the raw markdown can list one game
            // under several category headers.
            entries = BaseVrModParser.MergeDuplicateGames(entries);
            LogRun($"Parsed {parsedCount} entries from the raw markdown" +
                (entries.Count < parsedCount
                    ? $"; merged {parsedCount - entries.Count} duplicate listing(s) (same game in several categories) into one entry per game"
                    : string.Empty));
            return entries;
        }

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return StripVrSuffixes(entry.GameName);
        }

        /// <summary>
        /// Computes a SHA-256 hash of the given content string.
        /// Used to generate ChangeIdentifiers for detecting source content changes.
        /// Delegates to the inherited BaseVrModParser.ComputeHash method.
        /// </summary>
        /// <param name="content">The string content to hash.</param>
        /// <returns>Base64-encoded SHA-256 hash of the content.</returns>
        protected static new string ComputeHash(string content) => BaseVrModParser.ComputeHash(content);
    }
}
