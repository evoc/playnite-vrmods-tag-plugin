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
    ///    - route   = data-route — drives the CLASSIFICATION below.
    ///    - platforms = data-platforms (pipe-separated) — drives the PLATFORM COVERAGE below.
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
    ///  CLASSIFICATION (owner decision 2026-11-17): is this card a mod at all?
    ///  data-route is the site's own answer to that question. Its /methodology/ page defines four
    ///  "flat-to-VR" route types; the list page filters on five, and 223 of 562 cards (measured
    ///  2026-11-17) carry the value "Native VR" that the methodology never mentions. So the two
    ///  fields disagree often enough that neither is safe alone, and <see cref="ClassifyCard"/>
    ///  ranks them: a named mod tool outranks any route label.
    ///    - Mod      = a <see cref="ModProofTokens"/> token in data-tech, or data-route in
    ///                 {Full VR Mod, Framework Only, Multi-Route Coverage}.
    ///    - NotMod   = data-route in {Native VR, Official Hybrid} AND no mod-proof token.
    ///    - Unknown  = everything else: an unseen route value, or a missing data-route.
    ///  <b>NotMod is the only reject in this parser.</b> Unknown maps forward everywhere Mod does,
    ///  the same "when in doubt, emit it" rule as the scope filter above, and for the same reason:
    ///  it is what stops a site-side vocabulary change from silently deleting a user's mod.
    ///  Since the owner read the affected cards on 2026-11-17, NotMod also stops the CARD: a
    ///  NotMod card is not offered by this source at all, so it contributes no umbrella tag, no
    ///  headset tag and no link. Measured 2026-11-18 it removes 213 of the 564 recorded cards (15 of
    ///  the 35-card fixture; 213 of 565 live), 183 the site labels "Native VR", 30 "Official
    ///  Hybrid" with no mod tool named on them — the second figure is the parked question, closed
    ///  by reading those 30 cards, and the pair is why the run log names both reasons separately.
    ///  What the gate does NOT do is decide the library tag: each
    ///  source computes its own matching and the tag is the union of what the sources emit, so a
    ///  skip here means only "this source stops justifying the game". A game another source still
    ///  lists keeps every tag it had — verified by the service tests, and the reason the gate is
    ///  allowed to exist at all.
    ///
    ///  PLATFORM COVERAGE (owner decision 2026-11-17): a card whose data-platforms
    ///  carries the token "Quest" earns <see cref="VrModCategories.Quest"/>, and only on a card
    ///  that does not classify as NotMod — the question the tag answers is "did the modding scene
    ///  make this game reachable on Quest", not "does a Quest build exist". Comparison is
    ///  token-exact (split on '|'), never substring. Every other platform token is deliberately
    ///  unmapped, and the reason is recorded here so the next reader does not re-add it:
    ///    - PCVR                  → the bucket this plugin deliberately does not subdivide.
    ///    - PSVR, PSVR2           → console store games, not mods: none of the 124 / 80 cards
    ///                              carries a mod route, and their own tech labels name
    ///                              PlayStation Move / DualShock 4 / PS4 Pro Enhanced, which
    ///                              do not exist on PC.
    ///    - Pico                  → 2 of 39 cards are mods (the rest are Pico-store games and
    ///                              PSP emulator packages), all 39 also list Quest and none
    ///                              lists Pico alone, so the tag could never single out a game.
    ///    - Rift, Rift S, Vive,
    ///      Index               → tethered PC-VR displays (the PCVR reason again) on retired
    ///                              hardware: Meta ended Rift S support after 2026-04-20, Valve
    ///                              made the Index line end-of-life in January 2026.
    ///    - Windows Mixed Reality → 1 card in 562; Microsoft ended the platform in November 2025.
    ///  Measured 2026-11-17 across 562 cards: PCVR 513, Quest 208, PSVR 124, PSVR2 80, Pico 39,
    ///  Rift 21, Rift S 8, Vive 8, Index 3, Windows Mixed Reality 1.
    ///  A LIMIT the user documentation keeps stating: the site publishes platforms per GAME and
    ///  never per route, so a Multi-Route card listing PCVR and Quest does not say which one the
    ///  mod targets. From this source the tag reads "CompoundVR covers this game for Quest";
    ///  UnflatVR, which labels every mod's platform, keeps the stronger claim.
    ///  Unrecognised platform tokens and unrecognised data-route values are counted and named on the
    ///  run log — never mapped, never allowed to suppress a recognised token in the same card, and
    ///  named whether or not the verdict happened to come out as Unknown.
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

        /// <summary>
        /// data-tech tokens that name a mod, or the tool that makes one. A named tool is stronger
        /// evidence than any route label, so one of these outranks "Native VR": measured 2026-11-17,
        /// two cards the site labels <c>Native VR</c> name mod tools (Grand Theft Auto IV VR, Lies of P
        /// VR), and a route-only rule would have dropped both. "Mod Support" and "Moddable" are
        /// deliberately NOT here — a game that can take mods is not itself a flat-to-VR mod
        /// (VTOL VR, The Elder Scrolls V: Skyrim VR).
        /// </summary>
        private static readonly string[] ModProofTokens =
        {
            "full vr mod",
            "community vr port",
            "uevr",
            "reframework",
            "vorpx",
            "geovr",
            "injection driver",
            "xash3d",
            "raze engine",
            "ppsspp vr",
            "dolphin vr",
            "citravr",
            "swingvr",
            "emulator",
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
            var (entries, _, _, _) = ParseCards(html);

            _lastContentHash = ComputeHash(string.Join("\n", BuildHashLines(entries)));
            return entries;
        }

        /// <summary>
        /// Parses the index page into one in-scope entry per card (flat-to-VR mods and
        /// emulator/retro titles that gain VR via a mod). Public so unit tests can feed a
        /// recorded page fragment directly.
        /// </summary>
        /// <param name="html">The full <c>/games/</c> index page.</param>
        /// <returns>
        /// The in-scope entries plus the card count, the count excluded by a natively-VR tech token,
        /// and the count gated out by the route classification — the last two are reported apart
        /// because they answer different questions (what the source does not cover, versus what the
        /// source says is not a mod), and an operator reading the run log has to tell them apart.
        /// </returns>
        public (List<VrModEntry> Entries, int Total, int ExcludedByTech, int GatedByRoute) ParseCards(string? html)
        {
            var entries = new List<VrModEntry>();
            var total = 0;
            var excluded = 0;
            var gated = 0;
            // Gate reasons counted per route label the source used, so an operator can tell "the
            // site re-labels things as Native VR again" from "the hybrid gate is doing the job it
            // was added for". Keyed by whatever spelling the rejecting label had, so a future
            // NotMod value cannot hide behind this line's wording.
            var gatedBy = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var gatedSample = new List<string>();
            var unknownPlatforms = new List<string>();
            var unknownRoutes = new List<string>();
            var modCount = 0;
            var notModCount = 0;
            var unknownCount = 0;
            if (string.IsNullOrWhiteSpace(html))
                return (entries, total, excluded, gated);

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
                var route = FirstCapture(block, "data-route=\"([^\"]*)\"");
                var platforms = FirstCapture(block, "data-platforms=\"([^\"]*)\"");
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

                // Classification. NotMod is the only reject here, and since 2026-11-17 it is applied
                // at the card rather than merely holding back a tag: a card the site
                // itself calls Native VR or Official Hybrid, with nothing on it naming a mod, is not
                // something this source can offer. Unknown keeps arriving — an unseen route value or
                // a missing data-route fails open, which is what stops a vocabulary change on the
                // site from deleting somebody's mod.
                var routeKind = ClassifyCard(route, tech);
                switch (routeKind)
                {
                    case CardRoute.Mod: modCount++; break;
                    case CardRoute.NotMod: notModCount++; break;
                    default: unknownCount++; break;
                }

                // Drift is named whatever the verdict came out as. Tied to the verdict it would stay
                // invisible on exactly the cards that also carry a mod-proof token — which is most of
                // the ones this parser cares about — and a renamed route would go unnoticed.
                var seenRoute = route.Trim();
                if (seenRoute.Length == 0)
                    seenRoute = "(none)";
                if (!KnownRouteValues.Contains(seenRoute) &&
                    unknownRoutes.Find(r => string.Equals(r, seenRoute, StringComparison.OrdinalIgnoreCase)) == null)
                    unknownRoutes.Add(seenRoute);

                // Headset tags: token-exact. Computed before the gate below on purpose
                // — the platform vocabulary of a gated card is still evidence about the site, and
                // dropping a mass of cards must not also drop the drift report that tells an
                // operator the site changed. Unknown platforms are recorded whichever way this goes.
                var headsets = HeadsetCategories(platforms, unknownPlatforms);

                // The route gate (owner read of the affected cards, 2026-11-17). Blast radius is
                // this source alone: the library tag is the union of what every source emits, so a
                // game another source still lists keeps its tags — pinned service-side by the
                // two-source scan tests.
                if (routeKind == CardRoute.NotMod)
                {
                    gated++;
                    var gateLabel = route.Trim();
                    if (gateLabel.Length == 0)
                        gateLabel = "(none)";
                    gatedBy[gateLabel] = gatedBy.TryGetValue(gateLabel, out var seenLabel) ? seenLabel + 1 : 1;
                    if (gatedSample.Count < 3)
                        gatedSample.Add(title.Trim());
                    continue;
                }

                var categories = new List<string> { VrModCategories.CompoundVr };
                categories.AddRange(headsets);

                var additional = new StringBuilder();
                if (tier.Length > 0)
                    additional.Append("Tier ").Append(tier);
                // The route verbatim, in the site's own spelling: the one way a reviewer can tell a
                // Full VR Mod row from a Native VR row without leaving Playnite.
                var routeLabel = route.Trim();
                if (routeLabel.Length > 0)
                    additional.Append(additional.Length > 0 ? " · " : "").Append(routeLabel);
                var updatedDate = UpdatedDate(updated);
                if (updatedDate.Length > 0)
                    additional.Append(additional.Length > 0 ? " · " : "").Append("updated ").Append(updatedDate);

                entries.Add(new VrModEntry
                {
                    GameName = title.Trim(),
                    Categories = categories,
                    SourceUrls = new List<string> { deepLink },
                    AdditionalInfo = additional.Length > 0 ? additional.ToString() : null,
                });
            }

            // Vocabulary drift, named rather than swallowed: both lists stay empty on a page this
            // parser understands, and say what changed the day it stops understanding it.
            var gateReasons = "none";
            if (gated > 0)
            {
                var reasons = new StringBuilder();
                foreach (var pair in gatedBy)
                {
                    if (reasons.Length > 0)
                        reasons.Append(", ");
                    reasons.Append(pair.Key).Append(' ').Append(pair.Value);
                }
                gateReasons = reasons.ToString();
            }
            LogRun($"classified mod {modCount} / not-mod {notModCount} / unknown {unknownCount}" +
                   (gated > 0
                       ? $" · gated {gated} as not-a-mod ({gateReasons}; first: {string.Join(", ", gatedSample)})"
                       : string.Empty) +
                   (unknownRoutes.Count > 0
                       ? $" · unrecognised data-route value(s): {string.Join(", ", unknownRoutes)}"
                       : string.Empty) +
                   (unknownPlatforms.Count > 0
                       ? $" · unrecognised platform token(s) not mapped: {string.Join(", ", unknownPlatforms)}"
                       : string.Empty));

            // What this source published, and the two reasons it published fewer cards than the page
            // held. Logged here rather than in FetchEntriesAsync so the numbers an operator reads are
            // the ones a test can pin: a mass drop is only an early warning if the line reporting it
            // cannot rot silently.
            LogRun($"parsed {total} card(s) → {entries.Count} in scope · " +
                   $"excluded {excluded} by tech + {gated} by route ({gateReasons}) of {total}");

            return (entries, total, excluded, gated);
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
        /// Classifies one card as a mod or as a natively-VR product, from the two fields the source
        /// publishes about it. A <see cref="ModProofTokens"/> token in data-tech
        /// outranks the route label, because the label is measurably wrong on some cards. Only
        /// <see cref="CardRoute.NotMod"/> ever holds work back: an unseen route value and a missing
        /// data-route are <see cref="CardRoute.Unknown"/>, and Unknown is treated as in scope
        /// everywhere, so a vocabulary change on the site fails open instead of deleting somebody's
        /// mod. Internal so the test assembly (<c>InternalsVisibleTo</c>) can pin the table directly.
        /// </summary>
        /// <param name="route">The card's raw <c>data-route</c> value (may be empty).</param>
        /// <param name="tech">The card's raw pipe-separated <c>data-tech</c> value (may be empty).</param>
        internal static CardRoute ClassifyCard(string? route, string? tech)
        {
            if (ContainsAny(tech ?? string.Empty, ModProofTokens))
                return CardRoute.Mod;

            switch ((route ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "full vr mod":
                case "framework only":
                case "multi-route coverage":
                    return CardRoute.Mod;
                case "native vr":
                case "official hybrid":
                    // "Official Hybrid" is developer-made VR support — the site sells it as a
                    // feature of the game, not as something the community added. The owner parked it
                    // on 2026-11-17 ("do not decide now") and closed it on 2026-11-18 by
                    // reading the 32 such cards this source was still offering: not one names a mod
                    // tool in data-tech, 7 are described by the site's own blurb as an official VR
                    // mode or add-on, and the site's data-tech vocabulary already carries
                    // "official hybrid" among the natively-VR tokens, which is how 16 of the 48
                    // were being dropped before any route gate existed. The hybrid cards that ARE
                    // mods never reach this case: F-Zero X VR and Mario Kart 64 VR carry data-tech
                    // "Full VR Mod | Community VR Port", and the mod-proof test above outranks the
                    // label. The recorded fixture tells the same pair of stories.
                    return CardRoute.NotMod;
                default:
                    // An unseen value, or no data-route at all. Fail open, and name it on the run
                    // log so the drift is evidence rather than a hole.
                    return CardRoute.Unknown;
            }
        }

        /// <summary>
        /// The headset categories a card's <c>data-platforms</c> value earns: token-exact
        /// (split on <c>|</c>, trimmed, case-insensitive), never a substring test. Any token
        /// outside the mapped vocabulary is recorded in <paramref name="unknownPlatforms"/> —
        /// reported, never mapped, and never allowed to suppress a recognised token in the same
        /// card.
        /// </summary>
        private static List<string> HeadsetCategories(string platforms, List<string> unknownPlatforms)
        {
            var categories = new List<string>();
            if (string.IsNullOrWhiteSpace(platforms))
                return categories;

            foreach (var raw in platforms.Split('|'))
            {
                var token = raw.Trim();
                if (token.Length == 0)
                    continue;

                if (string.Equals(token, "Quest", StringComparison.OrdinalIgnoreCase))
                {
                    if (!categories.Contains(VrModCategories.Quest))
                        categories.Add(VrModCategories.Quest);
                    continue;
                }

                // Known-but-unmapped is the documented out-list in the class header; anything
                // else is new, and only the new ones are worth naming on the run log.
                if (!KnownUnmappedPlatforms.Contains(token) &&
                    unknownPlatforms.Find(p => string.Equals(p, token, StringComparison.OrdinalIgnoreCase)) == null)
                    unknownPlatforms.Add(token);
            }

            return categories;
        }

        /// <summary>
        /// The platform tokens this source publishes that are deliberately not mapped, so seeing
        /// them is expected and does not belong on the run log. Each one's reason is in the class
        /// header; the list is asserted against by the tests, so the header and this list cannot
        /// drift apart silently.
        /// </summary>
        private static readonly HashSet<string> KnownUnmappedPlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PCVR", "PSVR", "PSVR2", "Pico", "Rift", "Rift S", "Vive", "Index", "Windows Mixed Reality",
        };

        /// <summary>
        /// The <c>data-route</c> values this source was measured to publish (2026-11-17), so the run
        /// log names the ones it has not seen instead of staying quiet about them. Five values, from
        /// the list page's own filter; the site's /methodology/ page defines four of them and calls
        /// one by another name — the header above records the disagreement.
        /// </summary>
        private static readonly HashSet<string> KnownRouteValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Native VR", "Full VR Mod", "Framework Only", "Multi-Route Coverage", "Official Hybrid",
        };

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

        /// <summary>
        /// The content this parse published, as one string to hash into <see cref="ChangeIdentifier"/>.
        /// Name and URL alone were not enough: the headset tags now come from data-platforms and the
        /// classification from data-route, so a site change that adds a Quest port to a card whose
        /// title and URL are unchanged has to move the hash — otherwise HasSourceChanged keeps
        /// serving the cached parse and the tag stays frozen at whatever the site said the first
        /// time the game was cached. The categories carry the platform
        /// verdict and AdditionalInfo carries the route label, which is what makes both visible here.
        /// Internal so the test assembly (<c>InternalsVisibleTo</c>) can pin that sensitivity
        /// directly, the way the UnflatVR tests pin theirs.
        /// </summary>
        internal static List<string> BuildHashLines(IEnumerable<VrModEntry> entries)
        {
            var lines = new List<string>();
            foreach (var e in entries)
                lines.Add(e.GameName + "|" +
                          (e.SourceUrls.Count > 0 ? e.SourceUrls[0] : string.Empty) + "|" +
                          string.Join(",", e.Categories) + "|" +
                          (e.AdditionalInfo ?? string.Empty));
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

    /// <summary>
    /// What a scanned CompoundVR card is, as far as the two fields the source publishes can say
    /// at all. <see cref="NotMod"/> is the only value that ever holds work back:
    /// <see cref="Unknown"/> maps forward like <see cref="Mod"/> everywhere, so a vocabulary change
    /// on the site fails open instead of silently deleting a user's mod.
    /// </summary>
    internal enum CardRoute
    {
        /// <summary>The card names a mod, or names a route the site defines as one.</summary>
        Mod,

        /// <summary>The site labels the card a natively-VR product, and nothing on it names a mod.</summary>
        NotMod,

        /// <summary>An unseen route value, no route at all, or a route this source has not ruled on.</summary>
        Unknown,
    }
}
