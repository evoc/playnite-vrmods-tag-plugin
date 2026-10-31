using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the PCVR Central game catalog (https://pcvrcentral.com).
    ///
    /// PCVR Central is a hand-curated, community-run catalog of PC VR mods and ports.
    /// The site is a Next.js static site with no public JSON API, but the <c>/mods</c>
    /// page inlines the complete "games with VR mods and conversions" catalog (991
    /// games at time of writing) as a server-rendered RSC payload. Each game object
    /// carries a real display <c>title</c>, a stable <c>slug</c>, and a <c>steamAppId</c>
    /// (87% of games), which this source uses for exact Steam App ID matching.
    ///
    /// One entry per game PCVR Central documents, still from a SINGLE request: the same
    /// inlined catalog carries each game's <c>mods</c> array, so the per-mod framework,
    /// quality and motion-controls labels are read from that one response and unioned onto
    /// the entry (see <c>BuildMetaModFeatures</c>). The frameworks become real category tags;
    /// this source's own <c>PcvrCentral</c> category is a catalog category
    /// (<see cref="VrModCategories.IsCatalogCategory"/>) and adds no tag of its own - the
    /// umbrella "VRMod" tag is what marks the game as having mods. Quality grades and motion
    /// levels become facet tags. One entry, one review row, one decision per game.
    ///

    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A SINGLE request (the catalog page) inlines the whole catalog as Next.js RSC chunks:

    ///      GET https://pcvrcentral.com/mods

    ///      self.__next_f.push([0,"..."]);  self.__next_f.push([1,"..."]);  ...
    ///      // each chunk is a JS-escaped string; the parser unescapes + concatenates them all

    ///  The concatenated payload contains the catalog as a "games" array of game objects:

    ///      "games": [
    ///        {
    ///          "slug": "doom",                     ← deep link (https://pcvrcentral.com/games/doom)
    ///          "title": "DOOM",                   ← display name (falls back to slug if absent)
    ///          "steamAppId": 434120,             ← numeric only when resolvable, else absent / "$undefined"
    ///          "mods": [
    ///            { "slug": "doom-realvr", "framework": "luke-ross",
    ///              "qualityLabel": "excellent", "motionControls": "partial" }
    ///          ]
    ///        }
    ///      ]

    ///  Per game: slug (dedupe + deep link), title (display), steamAppId (exact Steam match).
    ///  Per mod (unioned across a game): framework (uevr-profile / uuvr, reframework, vorpx,
    ///  luke-ross, other), qualityLabel (native-like / excellent / playable / experimental),
    ///  motionControls (full / partial / gamepad-only / none). native-custom / depth3d are not
    ///  frameworks (no injector / not true VR) and contribute no tag. A missing steamAppId is
    ///  fine (falls back to name matching).

    /// NOTE: this is a TEMPORARY FIX — the site exposes no stable API, so the parser
    /// decodes the RSC payload format that may change on any site redeploy.
    /// </summary>
    public class PcvrCentralParser : BaseVrModParser
    {
        /// <summary>The complete "games with VR mods and conversions" catalog page.</summary>
        private const string CatalogUrl = "https://pcvrcentral.com/mods";

        /// <summary>Category applied to every entry (drives the "VRMod PCVR Central" tag).</summary>
        private const string Category = VrModCategories.PcvrCentral;

        /// <summary>Prefix for the per-game deep link (stored in <see cref="VrModEntry.SourceUrl"/>).</summary>
        private const string GameUrlPrefix = "https://pcvrcentral.com/games/";

        /// <summary>
        /// Matches each server-rendered RSC string chunk: <c>self.__next_f.push([N,"...</c>.
        /// The opening double quote (the last char of the match) is the start of the
        /// chunk's JS-escaped string body.
        /// </summary>
        private static readonly Regex RscChunkRegex =
            new Regex("self\\.__next_f\\.push\\(\\[\\d,\\s*\"", RegexOptions.Compiled, ScannedRegexTimeout);

        private string? _lastContentHash;

        /// <summary>Creates a new PcvrCentralParser instance.</summary>
        public PcvrCentralParser() : base("PcvrCentral", "PCVR Central", "https://pcvrcentral.com/mods")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return entry.GameName;
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var content = await FetchStringAsync(CatalogUrl);
            _lastContentHash = ComputeHash(content);
            return ParseCatalog(content);
        }

        /// <summary>
        /// Parses a PCVR Central <c>/mods</c> page into one entry per catalogued game,
        /// deduplicated by slug (falling back to title when a slug is absent).
        /// </summary>
        /// <param name="html">The full <c>/mods</c> page HTML (the RSC payload).</param>
        /// <returns>
        /// One entry per unique game; empty for blank input, a missing games array, or
        /// malformed JSON (fail-safe, never throws).
        /// </returns>
        /// <summary>
        /// What a games array that is malformed, truncated, or of a shape this parser did not expect
        /// produces. Anything outside this list is a fault in this parser, not in the payload, and is
        /// not reported to the user as bad data.
        /// </summary>
        internal static bool IsJsonShapeFailure(Exception ex)
        {
            return ex is JsonException || ex is InvalidOperationException || ex is FormatException;
        }

        public List<VrModEntry> ParseCatalog(string? html)
        {
            var entries = new List<VrModEntry>();
            if (string.IsNullOrWhiteSpace(html))
                return entries;

            string decoded = DecodeRsc(html!);
            if (decoded.Length == 0)
            {
                LogRun("RSC decode produced no content (page markup changed?)");
                return entries;
            }

            string? arrayText = ExtractGamesArray(decoded);
            if (arrayText == null)
            {
                LogRun("No \"games\" array found in the decoded payload (page markup changed?)");
                return entries;
            }

            List<JsonElement> games;
            try
            {
                games = ParseGamesArray(arrayText);
            }
            catch (Exception ex) when (IsJsonShapeFailure(ex))
            {
                // Malformed JSON, or JSON of a shape this parser did not expect: no data rather than
                // a crashed scan. The exception type and message go into the run log because "the
                // page changed" and "our parser is wrong" look identical from the outside.
                string detail = $"{ex.GetType().Name}: {ex.Message}";
                LogRun($"Malformed JSON in the games array - no games parsed ({detail}; start: {Snip(arrayText)})");
                return entries;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            int notObjects = 0;
            int withoutSteamId = 0;
            foreach (var game in games)
            {
                if (!IsObjectElement(game))
                {
                    // The site put a string or a number where a game object belongs. Reading a
                    // property off it is an exception, not a game this plugin could describe, so it
                    // is skipped and counted - one summary line for the run, not one per bad item.
                    skipped++;
                    notObjects++;
                    continue;
                }

                string? slug = GetPropertyString(game, "slug");
                string? title = GetPropertyString(game, "title");
                if (string.IsNullOrWhiteSpace(title))
                    title = slug;
                string? dedupeKey = string.IsNullOrWhiteSpace(slug) ? title : slug;
                if (string.IsNullOrWhiteSpace(dedupeKey))
                {
                    // A game object with neither slug nor title: cannot be matched to anything.
                    skipped++;
                    LogRun($"Game skipped (no title and no slug): {Snip(game.GetRawText())}");
                    continue;
                }
                if (!seen.Add(dedupeKey!.Trim()))
                    continue;

                // Phase 2 "meta mod" (external-references.md, "PCVR Central per-mod data"): one entry
                // per game carrying the UNION of all its mods' features (union-all). Frameworks -> Categories (each
                // non-catalog category becomes a "VRMod {category}" tag), plus every distinct
                // quality grade, motion level and mod status (each a facet tag). The PcvrCentral
                // catalog category is kept for discovery ("PCVR Central documents this game") but
                // contributes no tag. This flips PCVR Central from catalog-only (0 tags applied)
                // to a real tag source while staying one row / one decision per game.
                // Ordered (not a HashSet) so the meta-mod's category list is deterministic:
                // the PcvrCentral catalog category first (discovery identity), then the union of
                // framework categories in first-seen order.
                var metaCategories = new List<string> { Category };
                var metaQualities = new List<string>();
                var metaMotions = new List<string>();
                var metaStatuses = new List<string>();
                BuildMetaModFeatures(game, metaCategories, metaQualities, metaMotions, metaStatuses);

                var entry = new VrModEntry
                {
                    GameName = title!.Trim(),
                    Categories = metaCategories,
                    Qualities = metaQualities,
                    Motions = metaMotions,
                    Statuses = metaStatuses,
                    SourceUrl = string.IsNullOrWhiteSpace(slug) ? null : GameUrlPrefix + slug!.Trim(),
                    // Per-mod feature summary (framework/quality/motion/author) for display in
                    // the review dialog / tooltips — the human-readable detail behind the
                    // aggregated meta-mod tags above.
                    AdditionalInfo = BuildModFeaturesInfo(game)
                };

                // The site sends a numeric steamAppId only for games it can resolve; the rest
                // arrive as the string "$undefined" (or are missing). Those games fall back to
                // name matching, so a missing id is not an error - just count them (one line,
                // not one per game).
                if (game.TryGetProperty("steamAppId", out JsonElement sid)
                    && sid.ValueKind == JsonValueKind.Number
                    && sid.TryGetInt64(out long app))
                {
                    entry.SteamAppId = app.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    withoutSteamId++;
                }

                entries.Add(entry);
            }

            if (notObjects > 0)
                LogRun($"{notObjects} games skipped (not an object in the games array)");
            if (withoutSteamId > 0)
                LogRun($"{withoutSteamId} games without steamid");
            LogRun($"Parsed {entries.Count} games from the /mods catalog ({skipped} skipped)");
            return entries;
        }

        /// <summary>
        /// Concatenates every RSC string chunk in the page into a single decoded string,
        /// which contains the inlined catalog objects.
        /// </summary>
        private static string DecodeRsc(string html)
        {
            var sb = new StringBuilder();
            foreach (Match m in RscChunkRegex.Matches(html))
            {
                int openQuote = m.Index + m.Length - 1;
                int close = FindStringClose(html, openQuote);
                if (close <= openQuote)
                    continue;
                sb.Append(UnescapeJsString(html.Substring(openQuote + 1, close - openQuote - 1)));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns the index of the closing double quote of the JS string that opens at
        /// <paramref name="openQuote"/> (honoring backslash escapes), or -1 if none.
        /// </summary>
        private static int FindStringClose(string s, int openQuote)
        {
            for (int i = openQuote + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\')
                {
                    i++;
                    continue;
                }
                if (c == '"')
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Decodes a JS/JSON-escaped string body (the text between a chunk's quotes) into
        /// the actual payload text. Handles the standard escapes plus JS-only <c>\xHH</c>.
        /// </summary>
        private static string UnescapeJsString(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 >= raw.Length)
                {
                    sb.Append(c);
                    break;
                }

                char e = raw[++i];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '\'': sb.Append('\''); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '0': sb.Append('\0'); break;
                    case '/': sb.Append('/'); break;
                    case 'x':
                        if (i + 2 < raw.Length &&
                            int.TryParse(raw.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int xv))
                        {
                            sb.Append((char)xv);
                            i += 2;
                        }
                        else
                        {
                            sb.Append('\\');
                        }
                        break;
                    case 'u':
                        if (i + 4 < raw.Length &&
                            int.TryParse(raw.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int uv))
                        {
                            sb.Append((char)uv);
                            i += 4;
                        }
                        else
                        {
                            sb.Append('\\');
                            sb.Append(e);
                        }
                        break;
                    default:
                        // Unknown escape: keep both characters literally.
                        sb.Append('\\');
                        sb.Append(e);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Locates the inlined <c>"games":[ ... ]</c> array in the decoded payload and
        /// returns just the bracketed array text, or null if the marker is absent.
        /// </summary>
        private static string? ExtractGamesArray(string decoded)
        {
            int marker = decoded.IndexOf("\"games\":[", StringComparison.Ordinal);
            if (marker < 0)
                return null;
            int open = decoded.IndexOf('[', marker);
            int close = FindMatchingBracket(decoded, open);
            if (open < 0 || close <= open)
                return null;
            return decoded.Substring(open, close - open + 1);
        }

        /// <summary>
        /// From <paramref name="open"/> (index of an opening <c>[</c>), finds the index of
        /// its matching <c>]</c>, tracking string state so brackets inside string literals
        /// are ignored. Returns -1 if unbalanced.
        /// </summary>
        private static int FindMatchingBracket(string s, int open)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        /// <summary>Parses a bracketed array of game objects into element list.</summary>
        private static List<JsonElement> ParseGamesArray(string arrayText)
        {
            var list = new List<JsonElement>();
            using (JsonDocument doc = JsonDocument.Parse(arrayText))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return list;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        /// <summary>
        /// Maps a PCVR Central <c>framework</c> value to the canonical category it enables, or null
        /// when the value is unrecognized or absent. Every case below is the site's OWN label for that
        /// framework, confirmed against the <c>labels.framework</c> and <c>methodExplainer</c>
        /// dictionaries inlined in the catalog payload (see external-references.md, "Framework
        /// ambiguities"): <c>uuvr</c> is UUVR, "a universal injector for Unity games", so it is its own
        /// category and not a UEVR variant (UEVR is the Unreal-engine one); <c>native-custom</c> is the
        /// site's "Dedicated VR mod" - "built for this one game" - which is what
        /// <see cref="VrModCategories.Dedicated"/> means; <c>depth3d</c> is a ReShade depth shader the
        /// site itself calls "not a real VR conversion", so it gets a category of its own instead of
        /// being folded into a real-VR one; <c>other</c> is the site's catch-all for a method that does
        /// not fit its categories. Pure and fail-safe (unknown/absent values map to null, never throw);
        /// case/whitespace tolerant to survive a site redeploy.
        /// </summary>
        internal static string? MapFramework(string? framework)
        {
            if (string.IsNullOrWhiteSpace(framework))
                return null;
            switch (framework!.Trim().ToLowerInvariant())
            {
                case "uevr-profile":
                    return VrModCategories.UeVr;
                case "uuvr":
                    // Unity's injector, not a UEVR variant: tagging it "UEVR" tells the user the
                    // wrong tool (and the wrong engine) for the game in front of them.
                    return VrModCategories.UuVr;
                case "reframework":
                    return VrModCategories.ReFramework;
                case "vorpx":
                    return VrModCategories.VorpX;
                case "luke-ross":
                    return VrModCategories.LukeRoss;
                case "native-custom":
                    // 445 of the site's 1,548 mods: the largest framework group in the catalog, and
                    // until now it produced no category tag at all.
                    return VrModCategories.Dedicated;
                case "depth3d":
                    return VrModCategories.Depth3D;
                case "other":
                    return VrModCategories.Other;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Maps a PCVR Central <c>qualityLabel</c> to the quality grade, or null for
        /// "unknown"/absent/unrecognized. Pure and fail-safe.
        /// </summary>
        internal static string? MapQuality(string? qualityLabel)
        {
            if (string.IsNullOrWhiteSpace(qualityLabel))
                return null;
            switch (qualityLabel!.Trim().ToLowerInvariant())
            {
                case "native-like":
                    return "Native-like";
                case "excellent":
                    return "Excellent";
                case "playable":
                    return "Playable";
                case "experimental":
                    return "Experimental";
                default:
                    return null; // "unknown" / absent / unrecognized
            }
        }

        /// <summary>
        /// Maps a PCVR Central <c>motionControls</c> value to the motion facet, or null for
        /// "unknown"/absent/unrecognized. Pure and fail-safe.
        /// </summary>
        internal static string? MapMotion(string? motionControls)
        {
            if (string.IsNullOrWhiteSpace(motionControls))
                return null;
            switch (motionControls!.Trim().ToLowerInvariant())
            {
                case "full":
                    return VrModCategories.MotionFull;
                case "partial":
                    return VrModCategories.MotionPartial;
                case "gamepad-only":
                    return VrModCategories.MotionGamepadOnly;
                case "none":
                    return VrModCategories.MotionNone;
                default:
                    return null; // "unknown" / absent / unrecognized
            }
        }

        /// <summary>
        /// Maps a PCVR Central per-mod <c>status</c> to the mod-status facet, or null for
        /// "unknown"/absent/unrecognized. Pure and fail-safe. The five values are the site's own
        /// <c>labels.status</c> dictionary (payload key <c>superseded</c> is what the site shows its
        /// users as "Outdated"); the names here keep this plugin's Title-case tag style rather than
        /// the site's sentence case, the same way the motion facet already does.
        ///
        /// "active" is mapped rather than dropped. The source marks 1,527 of its 1,548 mods active, so
        /// the tag will be on almost everything - but a game with NO status tag then means "the source
        /// says nothing about this mod", which is a different fact from "the source says it works".
        /// Folding the majority value away would blur those two cases; a noisy-but-honest tag is
        /// preferable to an ambiguous absence. (Filtering by it is optional; the four problem statuses
        /// - Update Detected / Outdated / Broken / Abandoned - cover 21 mods.)
        /// </summary>
        internal static string? MapStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return null;
            switch (status!.Trim().ToLowerInvariant())
            {
                case "active":
                    return VrModCategories.StatusActive;
                case "update-detected":
                    return VrModCategories.StatusUpdateDetected;
                case "superseded":
                    return VrModCategories.StatusOutdated;
                case "broken":
                    return VrModCategories.StatusBroken;
                case "abandoned":
                    return VrModCategories.StatusAbandoned;
                default:
                    return null; // "unknown" / absent / unrecognized
            }
        }

        /// <summary>
        /// Computes the PCVR Central per-game "meta mod" feature unions (union-all; see
        /// external-references.md, "PCVR Central per-mod data", Phase 2): the union of framework
        /// <b>categories</b>, the union of <b>quality</b> grades, the union of <b>motion</b> levels and
        /// the union of
        /// <b>statuses</b> across all of a game's mods. Contradictions are kept (a game may carry
        /// several) - including statuses, where "Active" plus "Broken" on one game means one working
        /// mod and one broken one, which is information rather than a contradiction to resolve.
        /// Deduplicates by slug+framework (the same key as <see cref="BuildModFeaturesInfo"/>) so the
        /// unions match the displayed per-mod summary. Appends to the caller-supplied <b>ordered</b>
        /// lists (deduplicated case-insensitively, first-seen order preserved) — the caller seeds
        /// <paramref name="categories"/> with the PcvrCentral catalog category first.
        /// </summary>
        private static void BuildMetaModFeatures(
            JsonElement game,
            System.Collections.Generic.List<string> categories,
            System.Collections.Generic.List<string> qualities,
            System.Collections.Generic.List<string> motions,
            System.Collections.Generic.List<string> statuses)
        {
            if (!game.TryGetProperty("mods", out JsonElement mods) || mods.ValueKind != JsonValueKind.Array)
                return;

            var catSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var qualSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var motionSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var statusSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods.EnumerateArray())
            {
                if (!IsObjectElement(mod))
                    continue;

                string? modSlug = GetPropertyString(mod, "slug");
                string? framework = GetPropertyString(mod, "framework");
                if (!modSeen.Add((modSlug ?? "") + "\u0000" + (framework ?? "")))
                    continue;

                string? category = MapFramework(framework);
                if (category != null && catSeen.Add(category))
                    categories.Add(category);
                string? quality = MapQuality(GetPropertyString(mod, "qualityLabel"));
                if (quality != null && qualSeen.Add(quality))
                    qualities.Add(quality);
                string? motion = MapMotion(GetPropertyString(mod, "motionControls"));
                if (motion != null && motionSeen.Add(motion))
                    motions.Add(motion);
                string? status = MapStatus(GetPropertyString(mod, "status"));
                if (status != null && statusSeen.Add(status))
                    statuses.Add(status);
            }
        }

        /// <summary>
        /// Builds the PCVR Central per-mod feature summary for a game: the framework category,
        /// quality, motion-controls level and mod status extracted from the per-game <c>mods</c> array
        /// (deduplicated by slug+framework). Exposed on <see cref="VrModEntry.AdditionalInfo"/> so it
        /// is inspectable WITHOUT changing the catalog tag output (emitting per-mod framework
        /// entries is the deferred behavior change; see external-references.md, "Behavior change and
        /// the two entry models"). It is also the only place the per-mod status is attributable: the
        /// facet tags are a union over a
        /// game's mods, so a tag list cannot say WHICH mod is the broken one - this text can.
        /// Returns null when the game has no parseable mods.
        /// </summary>
        private static string? BuildModFeaturesInfo(JsonElement game)
        {
            if (!game.TryGetProperty("mods", out JsonElement mods) || mods.ValueKind != JsonValueKind.Array)
                return null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var mod in mods.EnumerateArray())
            {
                if (!IsObjectElement(mod))
                    continue;

                string? modSlug = GetPropertyString(mod, "slug");
                string? framework = GetPropertyString(mod, "framework");
                if (!seen.Add((modSlug ?? "") + "\u0000" + (framework ?? "")))
                    continue;

                string? name = GetPropertyString(mod, "name");
                string? author = GetPropertyString(mod, "author");
                string? category = MapFramework(framework);
                string? quality = MapQuality(GetPropertyString(mod, "qualityLabel"));
                string? motion = MapMotion(GetPropertyString(mod, "motionControls"));
                string? status = MapStatus(GetPropertyString(mod, "status"));

                string catText = category
                    ?? (string.IsNullOrWhiteSpace(framework) ? "(no framework)" : framework!);
                var feats = new List<string>();
                if (quality != null)
                    feats.Add("quality: " + quality);
                if (motion != null)
                    feats.Add("motion: " + motion);
                if (status != null)
                    feats.Add("status: " + status);
                string featText = feats.Count > 0 ? string.Join(", ", feats) : "(no quality/motion/status)";
                string label = string.IsNullOrWhiteSpace(name) ? "(unnamed mod)" : name!;
                if (!string.IsNullOrWhiteSpace(author))
                    label += " - " + author;
                lines.Add($"[{catText}] {label} - {featText}");
            }

            if (lines.Count == 0)
                return null;
            return $"PCVR Central: {lines.Count} mod(s)" + "\n" + string.Join("\n", lines);
        }

        /// <summary>
        /// Reads a string property from a game object, returning null when the property is
        /// absent or not a JSON string.
        /// </summary>
        /// <summary>
        /// True when an element from the payload can be read as a property bag. <c>JsonElement</c>
        /// throws rather than returning null when a property is read off a string or a number, and the
        /// payload is scraped, so an element of the wrong kind is the site changing its shape - it is
        /// skipped, not fatal, and never read.
        /// </summary>
        private static bool IsObjectElement(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object;

        private static string? GetPropertyString(JsonElement game, string name)
        {
            if (!game.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.String)
                return null;
            return prop.GetString();
        }
    }
}
