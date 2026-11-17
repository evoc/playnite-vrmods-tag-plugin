using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the UnflatVR directory (https://unflatvr.com) — a hand-curated catalogue of
    /// flat-screen games that gained VR through a mod, one page per mod plus a per-game hub.
    ///
    /// UnflatVR is a CATALOG source (like PCVR Central and CompoundVR): every emitted entry carries
    /// the single catalog category <see cref="VrModCategories.UnflatVr"/>, which contributes only the
    /// umbrella "VRMod" tag. What it brings over the other catalogues is an exact Steam App ID for
    /// most of its games and the freshest data in the source set.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  One request returns the WHOLE catalogue. UnflatVR is a server-rendered static site with no
    ///  data API, but its "Steam library check" page embeds the complete catalogue as an inline JSON
    ///  island, which is what the page's own client script reads:
    ///
    ///      GET https://unflatvr.com/steam-library-check/
    ///
    ///      &lt;script type="application/json" id="slc-data"&gt;{"g":[ … ]}&lt;/script&gt;
    ///
    ///  verbatim from the page as measured on 2026-11-15:
    ///
    ///      {"g":[
    ///        {"n":["0 A.D."],"a":"","h":"../vr/0-a-d/",
    ///         "v":[["0 A.D. VR","../game/quest/0-ad-vr-sgtbilko76/","Quest","C"]]},
    ///        {"n":["Duke Nukem 3D"],"a":"434050","h":"../vr/duke-nukem-3d/",
    ///         "v":[["EDuke VR","../game/pcvr/eduke-vr-obesecatlord/","PCVR","B"],
    ///              ["EDuke VR (Steam Frame)","../game/frame/eduke-vr-steam-frame-obesecatlord/","Steam Frame","B"]]}
    ///      ]}
    ///
    ///  Fields this parser reads per hub:
    ///    - n  = the game's name(s). An ARRAY: the shape lets a hub carry alias spellings, though on
    ///           2026-11-15 none of the 715 hubs carried more than one. Only n[0] becomes the entry's
    ///           game name — an entry has one name and the matcher has no alias input.
    ///    - a  = the Steam App ID as a string, "" when the site cannot resolve the game.
    ///    - h  = the per-game hub URL, site-relative.
    ///    - v  = the hub's mods, each [mod name, listing URL (site-relative), platform, UFVR grade].
    ///
    ///  Content measured on 2026-11-15 (the numbers the source ranking in external-references.md
    ///  quotes): 715 hubs, 879 mods, 602 hubs carrying an App ID.
    ///
    ///  PLATFORM COVERAGE (owner decision 2026-11-15, task 216: a standalone port is worth showing):
    ///    - a "PCVR" mod is the flat PC game gaining VR on the PC, so it earns no tag of its own — the
    ///      umbrella "VRMod" already says this game has a VR mod,
    ///    - a "Quest" or "Steam Frame" mod is a port that runs on a standalone headset; the game keeps
    ///      its place in the list and earns "VRMod Quest" / "VRMod SteamFrame" next to the umbrella, so
    ///      a library can be filtered for "playable in VR, but not on my PC". The site spells the frame
    ///      platform "Steam Frame"; the tag drops the space.
    ///    - a platform string outside the measured vocabulary {"PCVR","Quest","Steam Frame"} is skipped
    ///      and named on the run log, so the day the site adds a fourth platform the loss is visible in
    ///      the source's "Last run" view instead of silent.
    ///  Measured platform vocabulary on 2026-11-15: PCVR 713 mods, Quest 134, Steam Frame 32. One hub
    ///  can carry several platforms (Duke Nukem 3D has a PCVR mod and a Steam Frame port), so a game can
    ///  earn both headset tags at once.
    ///
    ///  WHAT PHASE 1 DOES NOT READ (the follow-on phase is approved, task 216):
    ///  UnflatVR also states, per mod, a Status (Active…), a Controls level (Full Motion…), a Cost, a
    ///  Game-Files size, its own 0–100 grade rubric, and a link to the mod's real home (Nexus or
    ///  GitHub) — and it groups some games under framework pages (UEVR, REFramework, R.E.A.L. VR,
    ///  MonakaVR, RAI PAL). None of that is in the island, and nothing else carries it either: a
    ///  game's own hub page lists none of its mods (measured over 6 hubs: 0 mod links, 0 spec lines),
    ///  so every one of those fields lives only in the body of the mod's own page. Those 893 pages are
    ///  enumerable — robots.txt declares sitemap-listings.xml, with a lastmod per URL — but 703 of
    ///  them lastmod on the same day, so a "refetch only what changed" pass still re-reads ~78 % of
    ///  the site. The approved follow-on instead fetches the pages of the games that really matched
    ///  the library: measured at 131 hubs / 162 mod pages against a 3,045-game library, where a full
    ///  harvest of the PCVR mods alone would cost 713.
    ///  The URLs those pages live at are already carried per entry, uncapped, as
    ///  <see cref="VrModEntry.ListingUrls"/> — the deep-link cap only limits what becomes a library
    ///  Link — so the follow-on fetches them straight off the entries it matched, without a second
    ///  pass over the island.
    ///  Its A/B/C/D grade is not mapped onto the plugin's quality labels, and will not be in the
    ///  follow-on either: their grade scores install-help and review-video presence, our quality
    ///  labels describe what the VR conversion looks like. Different axes; it is shown in
    ///  <see cref="VrModEntry.AdditionalInfo"/> instead, where it reads as what it is.
    ///
    ///  FAILURE MODE: an HTTP failure throws (the base class' contract). A page that arrives but has
    ///  no <c>id="slc-data"</c> island is the site's own layout change: the run log says so in as
    ///  many words and the fetch returns no entries, which the tag service reports as "no data"
    ///  rather than as a broken library.
    /// ────────────────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class UnflatVrParser : BaseVrModParser
    {
        /// <summary>The one page that carries the whole catalogue island.</summary>
        public const string CatalogUrl = "https://unflatvr.com/steam-library-check/";

        /// <summary>The site's platform token for a mod that adds VR to the flat PC game on the PC.</summary>
        internal const string PlatformPcvr = "PCVR";

        /// <summary>The site's platform token for a standalone Meta Quest port.</summary>
        internal const string PlatformQuestSite = "Quest";

        /// <summary>
        /// The site's platform token for a standalone Steam Frame port. The site spells it with a
        /// space; the tag it earns, <see cref="VrModCategories.SteamFrame" />, drops it. The two
        /// spellings meet in <see cref="TryMapPlatform" /> and nowhere else.
        /// </summary>
        internal const string PlatformSteamFrameSite = "Steam Frame";

        /// <summary>
        /// The host the catalogue is served from. A deep link resolved out of the island has to be on
        /// this host (or its <c>www.</c> variant, see <see cref="BaseVrModParser.SameRegisteredHost"/>):
        /// an href in scraped text is not a statement about where this source's game lives.
        /// </summary>
        internal const string HostOfSourceSite = "unflatvr.com";

        /// <summary>
        /// Deep links kept per entry. The hub page first (it is this game's page on the source, and
        /// what PCVR Central links to), then the mod listings. Past this many the surplus listings
        /// stop becoming library links — the plugin writes every deep link into the game's Link list,
        /// and a game with a dozen mods would fill it — but they are not discarded: every one of them
        /// stays on the entry as <see cref="VrModEntry.ListingUrls"/>, and their names, grades and
        /// platforms are also in <see cref="VrModEntry.AdditionalInfo"/> for the review dialog.
        /// </summary>
        internal const int MaxDeepLinksPerEntry = 5;

        private string? _lastContentHash;

        /// <summary>Creates a new UnflatVrParser instance.</summary>
        public UnflatVrParser() : base("UnflatVR", "UnflatVR", "https://unflatvr.com")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return StripVrSuffixes(entry.GameName);
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var page = await FetchStringAsync(CatalogUrl);
            var result = ParseCatalog(page);

            _lastContentHash = ComputeHash(BuildHashLines(result.Entries));

            var platforms = string.Join(", ", result.ModsByPlatform
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => $"{kvp.Key} {kvp.Value}"));
            var note = result.UnknownPlatforms.Count > 0
                ? $" · unknown platform(s) skipped: {string.Join(", ", result.UnknownPlatforms)}"
                : string.Empty;
            LogRun($"parsed {result.Hubs} game hub(s) → {result.Entries.Count} game(s) tagged " +
                   $"({result.ModsTotal} mod(s): {platforms}; {result.HubsNoAppId} hub(s) without a Steam App ID){note}");
            return result.Entries;
        }

        /// <summary>
        /// Reads the catalogue island out of a fetched page. Public so a test can feed a recorded page
        /// (or a fragment) directly, and so the island can be pointed at without the network.
        /// </summary>
        /// <param name="html">The full <c>/steam-library-check/</c> page.</param>
        /// <returns>The entries to match and the counts the run log reports.</returns>
        public (List<VrModEntry> Entries, int Hubs, int ModsTotal, Dictionary<string, int> ModsByPlatform, int HubsNoAppId, List<string> UnknownPlatforms) ParseCatalog(string? html)
        {
            var entries = new List<VrModEntry>();
            var unknownPlatforms = new List<string>();
            var hubs = 0;
            var modsTotal = 0;
            var modsByPlatform = new Dictionary<string, int>(StringComparer.Ordinal);
            var hubsNoAppId = 0;

            var island = ExtractIsland(html);
            if (island == null)
            {
                // Not "the site has no games": the island is the only thing this parser reads, so its
                // absence is the layout change itself. Say it plainly — this line is what the operator
                // sees in the source's "Last run" dialog.
                LogRun("no catalogue island (a <script id=\"slc-data\"> element) in the fetched page — " +
                       "UnflatVR changed its page layout or the Steam-library-check feature; nothing parsed");
                return (entries, hubs, modsTotal, modsByPlatform, hubsNoAppId, unknownPlatforms);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(island);
            }
            catch (JsonException ex)
            {
                LogRun($"catalogue island is not valid JSON ({ex.GetType().Name}: {Snip(ex.Message, 160)}) — nothing parsed");
                return (entries, hubs, modsTotal, modsByPlatform, hubsNoAppId, unknownPlatforms);
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("g", out JsonElement games)
                    || games.ValueKind != JsonValueKind.Array)
                {
                    LogRun("catalogue island has no \"g\" array — UnflatVR changed the island's shape; nothing parsed");
                    return (entries, hubs, modsTotal, modsByPlatform, hubsNoAppId, unknownPlatforms);
                }

                foreach (JsonElement hub in games.EnumerateArray())
                {
                    if (hub.ValueKind != JsonValueKind.Object)
                        continue;
                    hubs++;

                    var gameName = ReadFirstGameName(hub);
                    if (string.IsNullOrEmpty(gameName))
                    {
                        LogRun($"hub with no game name: {Snip(hub.GetRawText(), 120)}");
                        continue;
                    }

                    var appId = ReadAppId(hub);
                    if (appId == null)
                        hubsNoAppId++;

                    // The hub's own page, then its mods' pages, all resolved off the catalogue URL
                    // rather than cut out of the text by hand: the site writes site-relative hrefs and
                    // a base-URI resolve is what says what they mean.
                    var links = new List<string>();
                    var hubLink = ResolveSiteUrl(ReadString(hub, "h"));
                    if (hubLink != null)
                        links.Add(hubLink);

                    // "UnflatVR" is a catalog category, so it mints no tag of its own: what a game gets
                    // from this source is the umbrella tag, plus one headset tag per platform the site
                    // lists a mod for.
                    var categories = new List<string> { VrModCategories.UnflatVr };
                    var mods = new List<string>();
                    var modUrls = new List<string>();
                    var anyKnownPlatform = false;
                    foreach (JsonElement mod in EnumerateMods(hub))
                    {
                        var text = ReadModText(mod);
                        if (text == null)
                            continue;
                        modsTotal++;

                        if (!TryMapPlatform(text.Value.Platform, unknownPlatforms, out string? modCategory))
                            continue;   // a platform nobody measured: named on the run log, never tagged

                        // A PCVR mod maps to no category of its own, so "did this hub say anything the
                        // plugin reads" cannot be inferred from the category list - it needs its own flag.
                        anyKnownPlatform = true;
                        mods.Add($"{text.Value.Name} ({text.Value.Grade}, {text.Value.Platform})");
                        modsByPlatform[text.Value.Platform] =
                            modsByPlatform.TryGetValue(text.Value.Platform, out int seen) ? seen + 1 : 1;
                        if (modCategory != null && !categories.Contains(modCategory))
                            categories.Add(modCategory);

                        var modLink = ResolveSiteUrl(text.Value.Url);
                        if (modLink != null)
                        {
                            // Every listing URL the site publishes for this game is kept, uncapped, so a
                            // later phase can fetch each mod's own page; only the first few of them become
                            // library Links.
                            modUrls.Add(modLink);
                            if (links.Count < MaxDeepLinksPerEntry)
                                links.Add(modLink);
                        }
                    }

                    // A hub whose mods all sit on platforms nobody measured says nothing about this
                    // game, so it is not a match candidate: better no row than a row that claims a VR
                    // mod in a language this plugin never read.
                    if (!anyKnownPlatform)
                        continue;

                    entries.Add(new VrModEntry
                    {
                        GameName = gameName!,
                        Categories = categories,
                        SteamAppId = appId,
                        SourceUrls = links,
                        ListingUrls = modUrls,
                        AdditionalInfo = BuildInfo(mods),
                    });
                }
            }

            return (entries, hubs, modsTotal, modsByPlatform, hubsNoAppId, unknownPlatforms);
        }

        /// <summary>
        /// The JSON text inside the page's <c>&lt;script type="application/json" id="slc-data"&gt;</c>
        /// element, or null when the page has none.
        ///
        /// The id is matched before the element is closed so the attribute order cannot matter, and the
        /// body is taken to the FIRST <c>&lt;/script&gt;</c>: JSON cannot contain that sequence (a
        /// script body may not contain one even as a string literal), so a non-greedy match cannot
        /// overrun the island into the scripts that follow it.
        /// </summary>
        internal static string? ExtractIsland(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;
            var m = MatchScanned(html!, "<script[^>]*\\bid=\"slc-data\"[^>]*>(.*?)</script>",
                RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string? ReadFirstGameName(JsonElement hub)
        {
            if (!hub.TryGetProperty("n", out JsonElement names))
                return null;
            if (names.ValueKind == JsonValueKind.String)
                return NullWhenBlank(names.GetString());
            if (names.ValueKind != JsonValueKind.Array)
                return null;
            foreach (JsonElement name in names.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String)
                    continue;
                var value = NullWhenBlank(name.GetString());
                if (value != null)
                    return value;   // n[0] is the name the site puts in its own headings; the rest are aliases
            }
            return null;
        }

        /// <summary>
        /// The Steam App ID the site asserts for this game, or null when it asserts none. The island
        /// writes an empty string for "unresolved", and a non-numeric value would be a claim about a
        /// different kind of identifier, so neither becomes an App ID.
        /// </summary>
        private static string? ReadAppId(JsonElement hub)
        {
            var raw = ReadString(hub, "a");
            if (raw == null)
                return null;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0
                ? id.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        private static IEnumerable<JsonElement> EnumerateMods(JsonElement hub)
        {
            if (!hub.TryGetProperty("v", out JsonElement mods) || mods.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (JsonElement mod in mods.EnumerateArray())
                yield return mod;
        }

        /// <summary>
        /// One mod row: the island writes it as a positional array
        /// <c>[name, url, platform, grade]</c>, not an object. The tuple is null when the row is not an
        /// array of four strings, so a shape change on the site surfaces as a countable loss.
        /// </summary>
        private static (string Name, string Url, string Platform, string Grade)? ReadModText(JsonElement mod)
        {
            if (mod.ValueKind != JsonValueKind.Array)
                return null;

            var parts = new List<string>(4);
            foreach (JsonElement part in mod.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.String)
                    return null;
                parts.Add(part.GetString() ?? string.Empty);
            }
            if (parts.Count < 4)
                return null;

            var name = parts[0].Trim();
            if (name.Length == 0)
                return null;
            return (name, parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
        }

        /// <summary>
        /// Whether the plugin knows what to say about a mod's platform, and which category tag that
        /// platform earns: a PCVR mod earns none (the umbrella already says the game has a VR mod), a
        /// Quest port earns <see cref="VrModCategories.Quest" />, a Steam Frame port earns
        /// <see cref="VrModCategories.SteamFrame" />. A platform outside the vocabulary measured on
        /// 2026-11-15 returns false and is recorded once, because tagging a game off an unread
        /// platform string — or silently skipping the row — are both worse than a named loss.
        /// </summary>
        private static bool TryMapPlatform(string platform, List<string> unknownPlatforms, out string? category)
        {
            if (platform.Equals(PlatformPcvr, StringComparison.OrdinalIgnoreCase))
            {
                category = null;
                return true;
            }
            if (platform.Equals(PlatformQuestSite, StringComparison.OrdinalIgnoreCase))
            {
                category = VrModCategories.Quest;
                return true;
            }
            if (platform.Equals(PlatformSteamFrameSite, StringComparison.OrdinalIgnoreCase))
            {
                category = VrModCategories.SteamFrame;
                return true;
            }
            category = null;
            if (unknownPlatforms.Find(known => string.Equals(known, platform, StringComparison.OrdinalIgnoreCase)) == null)
                unknownPlatforms.Add(platform);
            return false;
        }

        /// <summary>
        /// Resolves one site-relative href from the island against the catalogue URL, and refuses
        /// anything that is not a plain http(s) URL on a named host (the same gate every value written
        /// into a library Link passes; <see cref="VrModEntry.SourceUrls"/> filters again on assignment).
        /// </summary>
        private static string? ResolveSiteUrl(string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
                return null;
            if (!Uri.TryCreate(CatalogUrl, UriKind.Absolute, out Uri? baseUri))
                return null;
            if (!Uri.TryCreate(relative!.Trim(), UriKind.RelativeOrAbsolute, out Uri? relativeUri))
                return null;
            if (!Uri.TryCreate(baseUri, relativeUri, out Uri? absolute))
                return null;
            var url = absolute!.ToString();
            // Not just "is it a safe URL": the island's hrefs are scraped text, and every value here
            // becomes a Link in the user's library that Playnite opens with the OS shell. A row that
            // points off UnflatVR's own host is not this source's page, whatever its scheme says, so it
            // is dropped here rather than filtered silently by the entry's own setter.
            if (!WebUrl.IsSafeLinkUrl(url))
                return null;
            return SameRegisteredHost(HostOfSourceSite, absolute.Host) ? url : null;
        }

        /// <summary>
        /// The display line the review dialog shows for this game: every mod the site lists for it,
        /// with the site's own grade and the platform it runs on. Grades are named as the site's
        /// ("UFVR grade") because they are not the plugin's quality labels — see the class doc — and
        /// the platform is spelled as the site spells it because that is what the row came from.
        /// </summary>
        private static string? BuildInfo(List<string> mods)
        {
            if (mods.Count == 0)
                return null;
            var sb = new StringBuilder();
            sb.Append("UFVR mods: ").Append(string.Join(", ", mods));
            return sb.ToString();
        }

        /// <summary>
        /// The content this parse published, as one string to hash into <see cref="ChangeIdentifier"/>.
        /// The listing URLs belong here and not only the names: a mod page can move while its name,
        /// grade and platform stay identical, and a hash that missed that would let the service skip
        /// the fetch (see <c>VrModTagService.HasSourceChanged</c>) and leave a stale Link in the
        /// user's library for the rest of the session. Internal so the test assembly
        /// (<c>InternalsVisibleTo</c>) can pin that property; nothing else calls it.
        /// </summary>
        internal static string BuildHashLines(List<VrModEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (VrModEntry e in entries)
            {
                sb.Append(e.GameName).Append('|')
                  .Append(e.SteamAppId ?? string.Empty).Append('|')
                  .Append(e.AdditionalInfo ?? string.Empty).Append('|')
                  .Append(string.Join(",", e.ListingUrls)).Append('\n');
            }
            return sb.ToString();
        }

        private static string? ReadString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            return NullWhenBlank(value.GetString());
        }

        private static string? NullWhenBlank(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}
