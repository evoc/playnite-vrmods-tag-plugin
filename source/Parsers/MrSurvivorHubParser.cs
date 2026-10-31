using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the Mr. Survivor VR HUB (https://mrsurvivor-installers.com/).
    ///
    /// The site is a single server-rendered HTML <c>&lt;table&gt;</c> listing every
    /// supported title: one row per game, with the title (wrapped in <c>&lt;b&gt;</c>)
    /// in the first column and the supported distribution platforms in the second.
    /// That table is the complete, single-request catalog, so this source is
    /// CATALOG-LEVEL coverage: one entry per listed game, each carrying the single
    /// category "Mr Survivor" (the "VRMod Mr Survivor" tag).
    ///
    /// The platform column (Steam / GOG / Epic / Meta Quest, ...) is store/distribution
    /// info, not a VR framework, so it is intentionally not used as a facet. A trailing
    /// " VR" / " VRO" on a title is stripped so the name is close to the canonical game
    /// name and matches the user's library well.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A SINGLE request returns the whole catalog: the site's home page server-renders one
    ///  <c>&lt;table&gt;</c> with a row per game (the first <c>&lt;td&gt;</c> is the title):
    ///
    ///      GET https://mrsurvivor-installers.com/
    ///
    ///      &lt;table&gt;
    ///        &lt;tr&gt;&lt;th&gt;Title&lt;/th&gt;&lt;th&gt;Platform&lt;/th&gt;&lt;/tr&gt;      ← header row (uses &lt;th&gt;,
    ///                                                              skipped)
    ///        &lt;tr&gt;&lt;td&gt;&lt;b&gt;Cyberpunk 2077&lt;/b&gt;&lt;/td&gt;&lt;td&gt;Steam&lt;/td&gt;&lt;/tr&gt;     ← data row
    ///        &lt;tr&gt;&lt;td&gt;&lt;b&gt;DOOM VFR&lt;/b&gt;&lt;/td&gt;&lt;td&gt;Steam / GOG&lt;/td&gt;&lt;/tr&gt;
    ///      &lt;/table&gt;
    ///
    ///  Fields read per row: the FIRST data-row <c>&lt;td&gt;</c> → strip tags, decode entities,
    ///  drop a trailing " VR" / " VRO", collapse whitespace → the game name. The platform
    ///  column is ignored (store/distribution info, not a VR framework). The first <c>&lt;table&gt;</c>
    ///  on the page is assumed to be the catalog (a markup that adds an earlier table would
    ///  break this - scope the regex to the right table if that ever happens).
    /// </summary>
    public class MrSurvivorHubParser : BaseVrModParser
    {
        /// <summary>The site root (the single-request catalog table).</summary>
        private const string SiteUrl = "https://mrsurvivor-installers.com/";

        /// <summary>Category applied to every entry (drives the "VRMod Mr Survivor" tag).</summary>
        private const string Category = VrModCategories.MrSurvivorHub;

        private string? _lastContentHash;

        /// <summary>Creates a new MrSurvivorHubParser instance.</summary>
        public MrSurvivorHubParser() : base("MrSurvivorHub", "Mr Survivor VR HUB", "https://mrsurvivor-installers.com/")
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
        /// Parses the site's <c>&lt;table&gt;</c> into one entry per listed game (the first
        /// <c>&lt;td&gt;</c> of each data row), deduplicated by cleaned name.
        /// </summary>
        /// <param name="html">The full page HTML.</param>
        /// <returns>One entry per unique listed game; empty for blank/missing table.</returns>
        public static List<VrModEntry> ParseGames(string? html)
        {
            var entries = new List<VrModEntry>();
            if (string.IsNullOrWhiteSpace(html))
                return entries;

            // Scope to the first <table>...</table> block (the only catalog table).
            var tableMatch = MatchScanned(html!, @"<table[^>]*>([\s\S]*?)</table>", RegexOptions.IgnoreCase);
            if (!tableMatch.Success)
                return entries;
            string table = tableMatch.Groups[1].Value;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match rowMatch in MatchesScanned(table, @"<tr[^>]*>([\s\S]*?)</tr>", RegexOptions.IgnoreCase))
            {
                string row = rowMatch.Groups[1].Value;

                // A data row has at least one <td>; the header row uses <th> and is skipped.
                var cellMatch = MatchScanned(row, @"<td[^>]*>([\s\S]*?)</td>", RegexOptions.IgnoreCase);
                if (!cellMatch.Success)
                    continue;

                string name = CleanName(cellMatch.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    continue;

                entries.Add(new VrModEntry
                {
                    GameName = name,
                    Category = Category
                });
            }

            return entries;
        }

        /// <summary>
        /// Strips tags/entities from a table cell and normalises the game name: removes a
        /// trailing " VR" / " VRO" suffix, collapses internal whitespace runs, and trims.
        /// </summary>
        private static string CleanName(string raw)
        {
            string s = ReplaceScanned(raw, @"<[^>]+>", "");
            s = UnescapeHtml(s);
            s = ReplaceScanned(s, @"\s+(VR|VRO)\s*$", "");
            s = ReplaceScanned(s, @"\s{2,}", " ", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        /// <summary>
        /// Decodes the common HTML entities that appear in the source (at least <c>&amp;amp;</c>).
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
            return s;
        }
    }
}
