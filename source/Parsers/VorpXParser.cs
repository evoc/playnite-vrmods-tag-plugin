using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the VorpX supported-games list (https://www.vorpx.com).
    ///
    /// VorpX is a single "VR driver" — one shared mod that enables 6DoF head-tracking for many
    /// non-native PC games — rather than per-game mod files. Its coverage is therefore the
    /// (relatively static) list of games VorpX supports, which the site serves on one page as
    /// a two-column HTML <c>&lt;table&gt;</c>: a single row with two <c>&lt;td&gt;</c> cells,
    /// each cell a long list of game names separated by <c>&lt;br&gt;</c>.
    ///
    /// This source is CATALOG-LEVEL coverage: one entry per supported game, each carrying the
    /// single category "VorpX" (the "VRMod VorpX" tag). It is disabled by default because the
    /// list is a plain supported-games catalog (there are no per-mod files to link); enable it
    /// in settings to tag your VorpX-supported games.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A SINGLE request returns the whole list: the supported-games page server-renders one
    ///  <c>&lt;table&gt;</c> with two <c>&lt;td&gt;</c> cells, each a long <c>&lt;br&gt;</c>-separated list of
    ///  game names:
    ///
    ///      GET https://www.vorpx.com/supported-games/
    ///
    ///      &lt;table&gt;
    ///        &lt;tr&gt;&lt;td&gt;
    ///          Cyberpunk 2077&lt;br&gt;DOOM&lt;br&gt;Resident Evil 4&lt;br&gt;...   ← cell 1
    ///        &lt;/td&gt;&lt;td&gt;
    ///          Elden Ring&lt;br&gt;Horizon Forbidden West&lt;br&gt;...        ← cell 2
    ///        &lt;/td&gt;&lt;/tr&gt;
    ///      &lt;/table&gt;
    ///
    ///  Fields read per cell: each <c>&lt;br&gt;</c>-separated line → strip tags, decode entities
    ///  (incl. numeric typographic entities - em-dashes / smart quotes), collapse whitespace →
    ///  one game name (deduped). The first <c>&lt;table&gt;</c> on the page is assumed to be the
    ///  supported-games catalog; every emitted entry is marked 6DOF (VorpX's whole purpose is
    ///  6DoF head-tracking for flat games).
    /// </summary>
    public class VorpXParser : BaseVrModParser
    {
        /// <summary>The supported-games page (the single-request catalog table).</summary>
        private const string SiteUrl = "https://www.vorpx.com/supported-games/";

        /// <summary>Category applied to every entry (drives the "VRMod VorpX" tag).</summary>
        private const string Category = VrModCategories.VorpX;

        private string? _lastContentHash;

        /// <summary>Creates a new VorpXParser instance.</summary>
        public VorpXParser() : base("VorpX", "VorpX", "https://www.vorpx.com")
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
            var content = await FetchStringAsync(SiteUrl);
            _lastContentHash = ComputeHash(content);
            return ParseGames(content);
        }

        /// <summary>
        /// Parses the supported-games table into one entry per game. The page is a single
        /// <c>&lt;table&gt;</c> whose <c>&lt;td&gt;</c> cells each hold a <c>&lt;br&gt;</c>-separated
        /// list of game names; each unique name becomes one entry.
        /// </summary>
        /// <param name="html">The full page HTML.</param>
        /// <returns>One entry per unique supported game; empty for a blank/missing table.</returns>
        public static List<VrModEntry> ParseGames(string? html)
        {
            var entries = new List<VrModEntry>();
            if (string.IsNullOrWhiteSpace(html))
                return entries;

            // Scope to the first <table>...</table> block (the supported-games catalog).
            var tableMatch = MatchScanned(html!, @"<table[^>]*>([\s\S]*?)</table>", RegexOptions.IgnoreCase);
            if (!tableMatch.Success)
                return entries;
            string table = tableMatch.Groups[1].Value;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Each <td>...</td> cell is a <br>-separated list of names (the page uses two cells).
            foreach (Match cellMatch in MatchesScanned(table, @"<td[^>]*>([\s\S]*?)</td>", RegexOptions.IgnoreCase))
            {
                // Normalise each <br> separator to a newline, then read one name per line.
                string cellText = ReplaceScanned(cellMatch.Groups[1].Value, @"<br[^>]*>", "\n", RegexOptions.IgnoreCase);
                foreach (string line in cellText.Split('\n'))
                {
                    string name = CleanName(line);
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                        continue;

                    entries.Add(new VrModEntry
                    {
                        GameName = name,
                        Category = Category,
                        // VorpX's whole purpose is 6DoF head-tracking for flat games, so every
                        // supported game is VR/6DOF-capable through it.
                        Is6DOF = true
                    });
                }
            }

            return entries;
        }

        /// <summary>
        /// Strips tags/entities from a cell fragment and normalises the game name
        /// (collapse internal whitespace runs, trim).
        /// </summary>
        private static string CleanName(string raw)
        {
            string s = ReplaceScanned(raw, @"<[^>]+>", "");
            s = UnescapeHtml(s);
            s = ReplaceScanned(s, @"\s{2,}", " ", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        /// <summary>
        /// Decodes the common HTML entities that appear in the source.
        /// </summary>
        private static string UnescapeHtml(string s)
        {
            s = s.Replace("&amp;", "&");
            s = s.Replace("&lt;", "<");
            s = s.Replace("&gt;", ">");
            s = s.Replace("&quot;", "\"");
            s = s.Replace("&#39;", "'");
            s = s.Replace("&apos;", "'");
            s = s.Replace("&nbsp;", " ");
            // Common numeric typographic entities (game titles use em-dashes / smart quotes).
            s = s.Replace("&#8211;", CodePointToString(0x2014));
            s = s.Replace("&#8212;", CodePointToString(0x2014));
            s = s.Replace("&#8213;", CodePointToString(0x2013));
            s = s.Replace("&#8216;", CodePointToString(0x2018));
            s = s.Replace("&#8217;", CodePointToString(0x2019));
            s = s.Replace("&#8220;", CodePointToString(0x201C));
            s = s.Replace("&#8221;", CodePointToString(0x201D));
            s = s.Replace("&#8230;", CodePointToString(0x2026));
            s = s.Replace("&#160;", CodePointToString(0x00A0));
            return s;
        }

        /// <summary>
        /// Builds a one-character string from a Unicode code point (used to decode numeric
        /// HTML entities without embedding literal escape sequences in the source).
        /// </summary>
        private static string CodePointToString(int codePoint)
        {
            return new string((char)codePoint, 1);
        }
    }
}
