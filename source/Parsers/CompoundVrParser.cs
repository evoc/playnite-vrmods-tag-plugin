using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the CompoundVR "flat-to-VR intelligence" catalog (https://compoundvr.com).
    ///
    /// CompoundVR is a hand-curated catalog of games that can be played in VR on a PC, with a
    /// per-game tier (S/A/B/C/D/F), the VR "route" (how VR is enabled), and a freshness
    /// timestamp. It is a CATALOG source (like PCVR Central): every emitted entry carries the
    /// single catalog category <see cref="VrModCategories.CompoundVr"/>, which contributes only
    /// the umbrella "VRMod" tag (the per-game framework is descriptive, not the source's
    /// identity).
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A SINGLE request returns every game: the index page is a static (Astro) site whose
    ///  <c>/games/</c> page server-renders ALL game cards inline (no per-game fetch needed):
    ///
    ///      GET https://compoundvr.com/games/
    ///
    ///  Each game is one anchor, one card:
    ///
    ///      &lt;a class="game-card" href="/games/cyberpunk-2077/"
    ///         data-sort-tier="0"
    ///         data-sort-updated="1790640000000"            ← epoch-milliseconds (freshness)
    ///         data-route="Multi-Route Coverage"
    ///         data-tech="Full VR Mod|RED4ext|...|OpenXR"    ← pipe-separated tech tags
    ///         data-platforms="PCVR" ...&gt;
    ///          ...
    ///          &lt;h3 class="game-card__title"&gt;Cyberpunk 2077 VR&lt;/h3&gt;
    ///          &lt;span class="tier-badge tier--s"&gt; S &lt;/span&gt;
    ///          ...
    ///      &lt;/a&gt;
    ///
    ///  Fields this parser reads per card:
    ///    - slug    = href="/games/{slug}/"        → deep link + (no App ID exists anywhere on
    ///                                                  the site, so matching is by NAME only).
    ///    - title   = the &lt;h3 class="game-card__title"&gt; text (HTML-entity decoded — titles
    ///                                                  contain &amp;#39; etc.).
    ///    - tier    = the "tier--{letter}" class (S/A/B/C/D/F).
    ///    - updated = data-sort-updated (epoch ms).
    ///    - tech    = data-tech (pipe-separated) — drives the SCOPE FILTER below.
    ///
    ///  SCOPE FILTER (only cards that "have a VR mod" are emitted):
    ///  CompoundVR mixes several kinds of cards. In scope = a game that GAINS VR through a mod:
    ///    - FLAT-TO-VR card  → EMITTED (a flat PC game with a VR mod).
    ///    - EMULATOR card    → EMITTED (a flat/console title made VR-playable by a mod — the same
    ///                          "gains VR via mod" case, so it is in scope, NOT excluded).
    ///  Out of scope = a game that is ALREADY a natively-VR product (needs no mod):
    ///    - NATIVE-VR card   → EXCLUDED (its data-tech carries one of the unambiguous
    ///                          <see cref="NativeScopeTokens"/>).
    ///  NOTE: render/modding APIs (OpenXR, SteamVR, VorpX, UEVR, VRChat) are NOT exclusion signals
    ///  — many flat-to-VR mods use them. The ambiguous "native vr" quality label is also NOT an
    ///  exclusion. A card with no listed token is emitted (when in doubt, tag it — more tags
    ///  than too few).
    ///  <see cref="NativeScopeTokens"/> is the tested vocabulary from the source at check time
    ///  (2026-10-09); if the site adds new natively-VR words, extend it.
    ///
    ///  ── TEMPORARY FIX (2026-10-09) ────────────────────────────────────────────────────────
    ///  This is a scrape of an undocumented static-site layout (class names, data-* attributes,
    ///  and the scope-token vocabulary). Any of these can change on a site redeploy; the doc
    ///  block above describes exactly what is expected so the fix is local.
    /// ────────────────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class CompoundVrParser : BaseVrModParser
    {
        /// <summary>The single index page that carries every game card.</summary>
        private const string IndexUrl = "https://compoundvr.com/games/";

        /// <summary>
        /// data-tech tokens that unambiguously mark a card as a NATIVELY-VR product (already VR,
        /// no mod needed → excluded). Compared case-insensitively against each lower-cased,
        /// trimmed data-tech token. Deliberately narrow: render/modding APIs (OpenXR, SteamVR,
        /// VorpX, UEVR, VRChat) and the ambiguous "native vr" quality label are NOT here — a card
        /// is excluded only on an explicit natively-VR-product signal (when in doubt, emit it).
        /// </summary>
        private static readonly string[] NativeScopeTokens =
        {
            "official vr",
            "official vr mode",
            "official vr support",
            "official vr port",
            "official hybrid vr",
            "official hybrid",
            "official psvr mode",
            "official psvr support",
            "direct vr port",
            "vr only",
            "standalone vr",
            "standalone",
            "standalone port",
            "standalone quest support",
            "quest exclusive",
            "oculus exclusive",
            "mixed reality",
        };

        private string? _lastContentHash;

        /// <summary>Creates a new CompoundVrParser instance.</summary>
        public CompoundVrParser() : base("CompoundVR", "CompoundVR", "https://compoundvr.com")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        private static readonly string[] GameCardOpenMarkup = new[] { "<a class=\"game-card\"" };

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return StripVrSuffixes(entry.GameName);
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var html = await FetchStringAsync(IndexUrl);
            var (entries, total, excluded) = ParseCards(html);

            _lastContentHash = ComputeHash(string.Join("\n", BuildHashLines(entries)));

            LogRun($"parsed {total} card(s) → {entries.Count} in scope " +
                   $"({excluded} natively-VR product(s) excluded)");
            return entries;
        }

        /// <summary>
        /// Parses the index page into one in-scope entry per card (flat-to-VR mods and
        /// emulator/retro titles that gain VR via a mod). Public so unit tests can feed a
        /// recorded page fragment directly.
        /// </summary>
        /// <param name="html">The full <c>/games/</c> index page.</param>
        /// <returns>The in-scope entries plus the total and the natively-VR-excluded count (for the run log).</returns>
        public (List<VrModEntry> Entries, int Total, int Excluded) ParseCards(string? html)
        {
            var entries = new List<VrModEntry>();
            var total = 0;
            var excluded = 0;
            if (string.IsNullOrWhiteSpace(html))
                return (entries, total, excluded);

            // Each card begins with the marker; the first split element is the page prefix
            // before the first card, so start at index 1.
            var blocks = html!.Split(GameCardOpenMarkup, StringSplitOptions.None);
            for (var b = 1; b < blocks.Length; b++)
            {
                var block = blocks[b];
                total++;

                var title = UnescapeHtml(FirstCapture(block,
                    "<h3 class=\"game-card__title\"[^>]*>([^<]*)</h3>"));
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var slug = FirstCapture(block, "href=\"(/games/[^/]+/)\"");
                var tech = FirstCapture(block, "data-tech=\"([^\"]*)\"");
                var updated = FirstCapture(block, "data-sort-updated=\"(\\d+)\"");
                var tier = FirstCapture(block, "tier-badge tier--([a-z])");
                tier = tier.Length > 0 ? char.ToUpperInvariant(tier[0]).ToString() : "";

                // Scope filter: only cards that "have a VR mod" are emitted. A natively-VR
                // product (unambiguous token) needs no mod and is excluded; everything else
                // (flat-to-VR mods, emulator/retro titles made VR-playable) is emitted.
                if (ContainsAny(tech, NativeScopeTokens))
                {
                    excluded++;
                    continue;
                }

                var deepLink = slug.Length > 0 ? "https://compoundvr.com" + slug : WebsiteUrl;
                var additional = new StringBuilder();
                if (tier.Length > 0)
                    additional.Append("Tier ").Append(tier);
                var updatedDate = UpdatedDate(updated);
                if (updatedDate.Length > 0)
                    additional.Append(additional.Length > 0 ? " · " : "").Append("updated ").Append(updatedDate);

                entries.Add(new VrModEntry
                {
                    GameName = title.Trim(),
                    Categories = new List<string> { VrModCategories.CompoundVr },
                    SourceUrls = new List<string> { deepLink },
                    AdditionalInfo = additional.Length > 0 ? additional.ToString() : null,
                });
            }

            return (entries, total, excluded);
        }

        /// <summary>
        /// True when any lower-cased data-tech token of <paramref name="tech"/> is in
        /// <paramref name="tokens"/>.
        /// </summary>
        private static bool ContainsAny(string tech, string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(tech))
                return false;
            foreach (var raw in tech.Split('|'))
            {
                var t = raw.Trim().ToLowerInvariant();
                foreach (var token in tokens)
                    if (t == token)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Formats an epoch-milliseconds freshness value as yyyy-MM-dd (UTC), or "" when absent.
        /// </summary>
        private static string UpdatedDate(string epochMs)
        {
            if (!long.TryParse(epochMs, out var ms) || ms <= 0)
                return "";
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms)
                   .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static List<string> BuildHashLines(IEnumerable<VrModEntry> entries)
        {
            var lines = new List<string>();
            foreach (var e in entries)
                lines.Add(e.GameName + "|" + (e.SourceUrls.Count > 0 ? e.SourceUrls[0] : ""));
            return lines;
        }

        private static string FirstCapture(string text, string pattern)
        {
            var m = MatchScanned(text, pattern);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// Decodes the HTML entities that appear in the source (titles carry &amp;#39; etc.).
        /// </summary>
        private static string UnescapeHtml(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return s.Replace("&amp;", "&")
                     .Replace("&lt;", "<")
                     .Replace("&gt;", ">")
                     .Replace("&quot;", "\"")
                     .Replace("&#39;", "'")
                     .Replace("&apos;", "'")
                     .Replace("&nbsp;", " ");
        }
    }
}
