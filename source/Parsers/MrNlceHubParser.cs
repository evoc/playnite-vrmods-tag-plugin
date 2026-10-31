using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the Mr. Nlce "PCVR Mods Installer Hub" (https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub).
    ///
    /// The repo's root <c>README.md</c> contains a hand-maintained "Supported games" list:
    /// a single collapsible (&lt;details&gt;) block with one Markdown bullet per supported title.
    /// That list is the complete, single-request catalog, so this source is CATALOG-LEVEL
    /// coverage: one entry per listed title, each carrying the single category "PCVR Mods Hub"
    /// (the "VRMod PCVR Mods Hub" tag).
    ///
    /// The bullet text is the title plus optional annotations such as "(motion controls)",
    /// "[EARLY WIP]" or a trailing " VR". Those annotations are stripped so the resulting
    /// name is close to the canonical game name and matches the user's library well.
    /// Sub-entries that are themselves parenthesised (e.g. "(Dolphin VR + Redux)") are not
    /// real titles and are dropped.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A SINGLE request returns the whole list: the repo's root <c>README.md</c> (raw, main
    ///  branch). The catalog is the bullet list inside the collapsible "Supported games" block:
    ///
    ///      GET https://raw.githubusercontent.com/Mr-Nlce/PCVR-Mods-Installer-Hub/main/README.md
    ///
    ///      ...the following listed titles...
    ///      &lt;details&gt;
    ///        * Cyberpunk 2077
    ///        * DOOM (motion controls)                 ← "(motion controls)" note is stripped
    ///        * Some Title [EARLY WIP]                 ← bracketed tag is stripped
    ///        * Another Title VR                        ← trailing " VR" is stripped
    ///        * (Dolphin VR + Redux)                   ← parenthesised sub-entry: DROPPED
    ///      &lt;/details&gt;
    ///
    ///  The list starts at the line containing "listed titles" and ends at the next
    ///  &lt;/details&gt; (or EOF). Fields read per bullet: the text after the leading "*" → strip
    ///  "(motion controls)", any "[..." tag, and a trailing " VR" → the game name; a bullet
    ///  that still starts with "(" is a sub-entry and dropped. If the "listed titles" phrase
    ///  or the &lt;details&gt; block is renamed/removed, the parser returns nothing (the start is
    ///  found by that exact phrase).
    /// </summary>
    public class MrNlceHubParser : BaseVrModParser
    {
        /// <summary>The repo's root README (the single-request catalog of supported titles).</summary>
        private const string ReadmeUrl =
            "https://raw.githubusercontent.com/Mr-Nlce/PCVR-Mods-Installer-Hub/main/README.md";

        /// <summary>Category applied to every entry (drives the "VRMod PCVR Mods Hub" tag).</summary>
        private const string Category = VrModCategories.MrNlceHub;

        private string? _lastContentHash;

        /// <summary>Creates a new MrNlceHubParser instance.</summary>
        public MrNlceHubParser() : base("MrNlceHub", "PCVR Mods Installer Hub", "https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub")
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
            var content = await FetchStringAsync(ReadmeUrl);
            _lastContentHash = ComputeHash(content);
            return ParseSupportedGames(content);
        }

        /// <summary>
        /// Parses the root README's "Supported games" block into one entry per listed title,
        /// deduplicated by cleaned name.
        /// </summary>
        /// <param name="readme">The full root <c>README.md</c> content.</param>
        /// <returns>One entry per unique supported title; empty for blank/missing list.</returns>
        public List<VrModEntry> ParseSupportedGames(string? readme)
        {
            var entries = new List<VrModEntry>();
            if (string.IsNullOrWhiteSpace(readme))
                return entries;

            var lines = readme!.Split('\n');

            int start = Array.FindIndex(lines, l =>
                l.IndexOf("listed titles", StringComparison.OrdinalIgnoreCase) >= 0);
            if (start < 0)
            {
                // The README's "Supported games" block is gone - the repo layout changed.
                LogRun("No 'listed titles' block in the README - the repo layout may have changed");
                return entries;
            }

            int end = Array.FindIndex(lines, start + 1, l =>
                l.IndexOf("</details>", StringComparison.OrdinalIgnoreCase) >= 0);
            if (end < 0)
                end = lines.Length;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            for (int i = start + 1; i < end; i++)
            {
                string raw = lines[i].TrimStart().TrimStart(' ', '*').Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                if (raw.StartsWith("(", StringComparison.Ordinal))
                {
                    skipped++;
                    LogRun($"Bullet skipped (parenthesised sub-entry): {Snip(raw)}");
                    continue;
                }

                string name = CleanName(raw);
                if (string.IsNullOrWhiteSpace(name))
                {
                    skipped++;
                    LogRun($"Bullet skipped (empty after cleaning): {Snip(raw)}");
                    continue;
                }
                if (!seen.Add(name))
                    continue;

                entries.Add(new VrModEntry
                {
                    GameName = name,
                    Category = Category
                });
            }

            LogRun($"Parsed {entries.Count} supported titles ({skipped} bullets skipped)");
            return entries;
        }

        /// <summary>
        /// Strips source annotations from a title: any "(motion controls)" note, any
        /// bracketed tag such as "[EARLY WIP]", and a trailing " VR". Whitespace is trimmed
        /// and internal runs of spaces are collapsed.
        /// </summary>
        private static string CleanName(string raw)
        {
            string s = ReplaceScanned(raw, @"\s*\((motion controls)\)\s*", " ", RegexOptions.IgnoreCase);
            s = ReplaceScanned(s, @"\[[^\]]*\]", " ");
            s = ReplaceScanned(s, @"\s+VR\s*$", "");
            s = ReplaceScanned(s, @"\s{2,}", " ", RegexOptions.IgnoreCase);
            return s.Trim();
        }
    }
}
