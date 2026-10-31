using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for Flat2VR Studios' website.
    ///
    /// Fetches VR mod data from https://flat2vrstudios.com/. Flat2VR Studios creates
    /// official VR ports of existing games. Each entry represents a game that has been
    /// ported to VR by Flat2VR.
    ///
    /// Category: "Flat2VR Port" (results in "VRMod Flat2VR Port" tag)
    ///
    /// Flat2VR game names include a " VR" suffix (e.g., "WRATH: AEON OF RUIN VR", "ROBOQUEST VR").
    /// This suffix is stripped during parsing by StripVrSuffixes() before the game name
    /// is stored. The "VR" suffix is then re-added to the GameName property because
    /// Flat2VR's site lists games with the "VR" label.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  ONE request returns the whole catalog: the site's home page, one card per listed
    ///  game. Fields read per card: the title in &lt;h3&gt;, the per-game deep link from the
    ///  card-image-link href, and (when present) the Steam App ID from the title="Steam VR"
    ///  platform link:
    ///
    ///      GET https://flat2vrstudios.com/
    ///
    ///      &lt;div class="game-card" data-platforms="..."&gt;
    ///        &lt;a href="games/&lt;slug&gt;.html" class="card-image-link"&gt;...&lt;/a&gt;   ← per-game page (SourceUrl)
    ///        &lt;div class="card-footer"&gt;
    ///          &lt;h3&gt;Game Name&lt;/h3&gt;
    ///          &lt;div class="card-platforms"&gt;
    ///            &lt;a title="Steam VR" href="https://store.steampowered.com/app/&lt;id&gt;/"&gt;...
    ///          &lt;/div&gt;
    ///        &lt;/div&gt;
    ///      &lt;/div&gt;
    ///
    ///  The card image link is the per-game deep link (SourceUrl) — either a page on
    ///  flat2vrstudios.com or the game's own site when it lives elsewhere. The Steam
    ///  platform link carries the Steam App ID (preferred matching over name matching).
    ///  Fallback (markup change, no game cards found): a legacy loose scan of
    ///  &lt;h2/h3/h4&gt; headings, title divs, and game anchors (names only — no deep link, no
    ///  Steam App ID; matching then relies on name-based fuzzy matching).
    /// </summary>
    public class Flat2VrParser : BaseVrModParser
    {
        /// <summary>
        /// URL for the Flat2VR Studios website.
        /// </summary>
        private const string Url = "https://flat2vrstudios.com/";

        /// <summary>
        /// SHA-256 hash of the last fetched content.
        /// Used as the ChangeIdentifier to detect when the source has been updated.
        /// </summary>
        private string? _lastContentHash;

        /// <summary>
        /// Creates a new Flat2VrParser instance.
        /// Initializes the base class with source ID "Flat2VR" and display name "Flat2VR Studios".
        /// </summary>
        public Flat2VrParser() : base("Flat2VR", "Flat2VR Studios", "https://flat2vrstudios.com/")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        private static readonly char[] InlineWhitespaceChars = new[] { ' ', '\t' };

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var html = await FetchStringAsync(Url);
            _lastContentHash = ComputeHash(html);
            return ParseHtml(html);
        }

        /// <summary>
        /// Parses game entries from the Flat2VR website HTML.
        ///
        /// Primary: the site's game cards (one per listed game) — each entry carries
        /// the per-game deep link (SourceUrl) and, when present, the Steam App ID from
        /// the card's Steam platform link. Fallback: when the page has no game cards
        /// (markup change), a legacy loose scan of headings, title divs, and game
        /// anchors (names only).
        ///
        /// The "VR" suffix is re-added to every name because Flat2VR's site lists
        /// games as "GameName VR".
        /// </summary>
        /// <param name="html">The Flat2VR website HTML content.</param>
        /// <returns>List of VR mod entries parsed from the HTML.</returns>
        public virtual List<VrModEntry> ParseHtml(string html)
        {
            // Current site structure: one <div class="game-card"> per listed game, with the
            // title in <h3>, the per-game page as the card-image-link href, and a
            // title="Steam VR" link carrying the Steam store URL (the app ID).
            var cardEntries = ParseGameCards(html);
            if (cardEntries.Count > 0)
                return cardEntries;

            // Fallback (markup changed, no game cards): the legacy loose scan.
            LogRun("No game cards found (markup change?) - using the legacy loose scan");
            return ParseLegacy(html);
        }

        /// <summary>
        /// Parses the site's game cards (one <c>&lt;div class="game-card"&gt;</c> per listed
        /// game) into entries carrying the per-game deep link (SourceUrl) and, when the
        /// card has a Steam platform link, the Steam App ID.
        /// </summary>
        /// <param name="html">The Flat2VR website HTML content.</param>
        /// <returns>One entry per game card (deduped by cleaned name); empty when the
        /// page has no game cards.</returns>
        internal List<VrModEntry> ParseGameCards(string html)
        {
            var entries = new List<VrModEntry>();
            if (string.IsNullOrEmpty(html))
                return entries;

            var cardStarts = MatchesScanned(html!, @"<div\s+class=""game-card""");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int skipped = 0;
            for (int i = 0; i < cardStarts.Count; i++)
            {
                int start = cardStarts[i].Index;
                int end = i + 1 < cardStarts.Count ? cardStarts[i + 1].Index : html!.Length;
                string card = html!.Substring(start, end - start);

                // Title: the card's <h3> in the footer.
                var nameMatch = MatchScanned(card, @"<h3[^>]*>(.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!nameMatch.Success)
                {
                    // A game card with no title: the card's markup changed.
                    skipped++;
                    LogRun($"Game card skipped (no <h3> title): {Snip(card)}");
                    continue;
                }

                string name = ReplaceScanned(nameMatch.Groups[1].Value, @"<[^>]+>", "").Trim();
                if (name.Length < 3)
                {
                    skipped++;
                    LogRun($"Game card skipped (title too short after cleanup): {Snip(name)}");
                    continue;
                }

                // Deep link: the per-game page the card image links to (games/<slug>.html,
                // or the game's own site when it lives elsewhere).
                string? sourceUrl = null;
                var linkTag = MatchScanned(card, @"<a[^>]*class=""card-image-link""[^>]*>", RegexOptions.IgnoreCase);
                if (linkTag.Success)
                {
                    var href = ExtractHref(linkTag.Value);
                    if (!string.IsNullOrWhiteSpace(href))
                        sourceUrl = ResolveUrl(Url, href!);
                }

                // Steam App ID: the card's title="Steam VR" platform link.
                string? steamAppId = null;
                var steamTag = MatchScanned(card, @"<a[^>]*title=""Steam VR""[^>]*>", RegexOptions.IgnoreCase);
                if (steamTag.Success)
                {
                    var steamHref = ExtractHref(steamTag.Value);
                    if (!string.IsNullOrWhiteSpace(steamHref))
                    {
                        var appMatch = MatchScanned(steamHref!, @"/app/(\d+)/");
                        if (appMatch.Success)
                            steamAppId = appMatch.Groups[1].Value;
                    }
                }

                var cleanName = StripVrSuffixes(name).Trim();
                if (cleanName.Length < 3)
                {
                    skipped++;
                    LogRun($"Game card skipped (name too short after VR-suffix strip): {Snip(name)}");
                    continue;
                }
                if (!seen.Add(cleanName))
                    continue;

                entries.Add(new VrModEntry
                {
                    GameName = cleanName + " VR",
                    Category = "Flat2VR Port",
                    SourceUrl = sourceUrl,
                    SteamAppId = steamAppId
                });
            }

            LogRun($"Parsed {entries.Count} game cards ({skipped} skipped)");
            return entries;
        }

        /// <summary>
        /// Extracts the <c>href</c> attribute value from an anchor tag, or null when absent.
        /// </summary>
        private static string? ExtractHref(string tag)
        {
            var m = MatchScanned(tag, @"href=""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Resolves a (possibly relative) href against the site root into an absolute URL.
        /// </summary>
        private static string ResolveUrl(string baseUrl, string href)
        {
            try
            {
                return new Uri(new Uri(baseUrl), href).ToString();
            }
            catch (UriFormatException)
            {
                return href;
            }
            catch (ArgumentException)
            {
                return href;
            }
        }

        /// <summary>
        /// Legacy fallback parse (used when the page has no game cards): a loose scan of
        /// heading tags, title divs, and game anchors for game names. Produces no deep
        /// links or Steam App IDs.
        /// </summary>
        /// <param name="html">The Flat2VR website HTML content.</param>
        /// <returns>List of VR mod entries parsed from the HTML.</returns>
        private List<VrModEntry> ParseLegacy(string html)
        {
            var entries = new List<VrModEntry>();

            var hPattern = @"<h[234][^>]*>(.*?)</h[234]>";
            var divPattern = @"<div[^>]*class=""[^""]*title[^""]*""[^>]*>(.*?)</div>";
            var aPattern = @"<a[^>]*class=""[^""]*game[^""]*""[^>]*>(.*?)</a>";

            var nonGameTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "flat2vr studios", "we bring games", "to vr", "re-imagining the best flat screen games in vr",
                "impact reality", "our games", "view all games", "want to", "bring your game",
                "contact us", "join our team", "continue reading", "stay in the loop",
                "get updates on our latest", "announcements", "studio news",
                "your subscription could not be saved", "you're subscribed",
                "email address", "subscribe", "i agree to receive newsletters",
                "sign in", "create an account", "username or email", "password",
                "login", "lost your password", "flat2vr community",
                "© 2026 flat2vr studios", "all rights reserved", "privacy policy",
                "our merch", "home", "newsletter", "to change the game"
            };

            var options = RegexOptions.IgnoreCase | RegexOptions.Singleline;

            foreach (var pattern in new[] { hPattern, divPattern, aPattern })
            {
                var matches = MatchesScanned(html, pattern, options);
                foreach (Match match in matches)
                {
                    var text = match.Groups[1].Value;
                    text = ReplaceScanned(text, @"<[^>]+>", "").Trim();
                    text = ReplaceScanned(text, @"\s+", " ");

                    if (string.IsNullOrEmpty(text)) continue;
                    if (text.Length < 3) continue;
                    if (text.Length > 60) continue;

                    var normalized = text.ToLowerInvariant().Trim();
                    if (nonGameTexts.Any(t => normalized.Contains(t))) continue;
                    if (normalized.Contains("©")) continue;
                    if (normalized.Contains("agree to receive")) continue;
                    if (text.Contains("/")) continue;
                    if (text.Contains("?")) continue;
                    if (text.Contains(":") && !text.Contains("VR")) continue;

                    var wordCount = text.Split(InlineWhitespaceChars, StringSplitOptions.RemoveEmptyEntries).Length;
                    if (wordCount > 8) continue;

                    var cleanName = StripVrSuffixes(text).Trim();
                    if (!string.IsNullOrEmpty(cleanName) && cleanName.Length > 2)
                    {
                        entries.Add(new VrModEntry
                        {
                            GameName = cleanName + " VR",
                            Category = "Flat2VR Port"
                        });
                    }
                }
            }

            LogRun($"Parsed {entries.Count} entries with the legacy loose scan");
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
