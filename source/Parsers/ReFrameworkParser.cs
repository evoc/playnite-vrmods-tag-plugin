using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the REFramework project's "Supported Games" list.
    ///
    /// REFramework (https://github.com/praydog/REFramework) is a mod framework that adds
    /// VR support to RE Engine (Resident Evil) titles. Per the project's README it provides
    /// "Generic 6DOF VR support for all games", so every supported entry is 6DOF.
    ///
    /// Data source: the "Supported Games" section of the project's README on GitHub — a
    /// live, maintainer-updated list. Each bullet is a supported game title; an optional
    /// "(Using `X` build)" note indicates which RE Engine build the title runs on and is
    /// stripped from the stored game name.
    ///
    /// Category: "REFramework" (results in the "VRMod REFramework" tag).
    /// Capability facet: entries are marked 6DOF (Is6DOF = true) when the README still
    /// advertises 6DOF support, so the flag tracks the project rather than hardcoding it.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A SINGLE request returns the whole list: the project's <c>README.md</c> on GitHub (raw,
    ///  master branch). The parser reads the "Supported Games" section — one bullet per game:
    ///
    ///      GET https://raw.githubusercontent.com/praydog/REFramework/master/README.md
    ///
    ///      ## Supported Games
    ///      * Resident Evil 2
    ///      * Resident Evil 4 (Using `RE8` build)      ← "(Using `X` build)" note is stripped
    ///      * Resident Evil Village
    ///      ## Some Other Section                        ← the section ends here
    ///
    ///  Fields read per bullet: the text after the leading "*" / "-" → strip a trailing
    ///  " (Using `X` build)" note → the game name. A bullet is any line starting with "*" or
    ///  "-"; the section runs until the next "## " heading (or EOF). The 6DOF facet is set
    ///  from whether "6DOF" appears anywhere in the README (so the flag tracks the project
    ///  rather than hardcoding it). If the "## Supported Games" heading is renamed/removed,
    ///  the parser returns nothing (the section is found by that exact heading text).
    /// </summary>
    public class ReFrameworkParser : BaseVrModParser
    {
        /// <summary>Raw README on the master branch (the project's default branch).</summary>
        private const string Url =
            "https://raw.githubusercontent.com/praydog/REFramework/master/README.md";

        /// <summary>Category applied to every entry (drives the "VRMod REFramework" tag).</summary>
        private const string Category = VrModCategories.ReFramework;

        private string? _lastContentHash;

        /// <summary>Creates a new ReFrameworkParser instance.</summary>
        public ReFrameworkParser() : base("ReFramework", "REFramework", "https://github.com/praydog/REFramework")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var content = await FetchStringAsync(Url);
            _lastContentHash = ComputeHash(content);
            return ParseSupportedGames(content);
        }

        /// <summary>
        /// Parses the "Supported Games" section of the README into VR mod entries.
        /// </summary>
        /// <param name="markdown">The full README markdown content.</param>
        /// <returns>Parsed entries (empty if the section is missing).</returns>
        public static List<VrModEntry> ParseSupportedGames(string markdown)
        {
            var entries = new List<VrModEntry>();
            if (string.IsNullOrEmpty(markdown))
                return entries;

            var section = ExtractSection(markdown, "## Supported Games");
            if (section == null)
                return entries;

            // REFramework advertises "Generic 6DOF VR support for all games"; detect the
            // capability from the README so the 6DOF flag tracks the project.
            var has6DOF = markdown.IndexOf("6DOF", StringComparison.OrdinalIgnoreCase) >= 0;

            foreach (var rawLine in section.Split('\n'))
            {
                var line = rawLine.Trim();
                // Only list bullets: "* Game Name" or "- Game Name".
                if (line.Length < 2) continue;
                if (line[0] != '*' && line[0] != '-') continue;

                var name = line.Substring(1).Trim();
                // Strip a trailing cross-build note: " (Using `RE8` build)".
                var noteIndex = name.IndexOf(" (Using", StringComparison.Ordinal);
                if (noteIndex > 0)
                    name = name.Substring(0, noteIndex).Trim();

                name = name.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
                    continue;

                entries.Add(new VrModEntry
                {
                    GameName = name,
                    Category = Category,
                    Is6DOF = has6DOF ? (bool?)true : null
                });
            }

            return entries;
        }

        /// <summary>
        /// Returns the body of a "## " markdown section (from after the heading to the
        /// next "## " heading), or null if the heading is not present.
        /// </summary>
        private static string? ExtractSection(string markdown, string heading)
        {
            var inSection = false;
            var sb = new StringBuilder();
            foreach (var line in markdown.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(heading, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    continue;
                }

                if (!inSection)
                    continue;

                // The next same-level heading ends the section.
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                    break;

                sb.AppendLine(line);
            }

            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return entry.GameName;
        }
    }
}
