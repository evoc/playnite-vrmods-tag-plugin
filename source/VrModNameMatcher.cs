using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Playnite.VRMods
{
    /// <summary>
    /// Shared utility class for game name matching between VR mod entries and Playnite games.
    ///
    /// This is the heart of the matching pipeline. It provides methods to:
    /// 1. Clean up game names by stripping VR suffixes and author credits (e.g., "DOOM VR mod by Author" → "DOOM")
    /// 2. Normalize names for comparison (lowercase, remove special characters)
    /// 3. Compute similarity scores using the Levenshtein distance algorithm
    /// 4. Generate unique keys for persisting user decisions about matches
    ///
    /// The matching pipeline works as follows:
    ///   Source entry: "DOOM VR mod by ModderX"    →  Normalize: "doom"
    ///   Playnite game: "DOOM Eternal"              →  Normalize: "doom eternal"
    ///   GetMatchScore: 1.0 == "doom" vs "doom eternal" → -1 (substring match = reject)
    ///
    /// This no-substring policy prevents false positives like "doom" matching "DOOM 3" or "DOOM Eternal".
    /// Sequel games are treated as entirely different games.
    /// </summary>
    public static class VrModNameMatcher
    {
        /// <summary>
        /// Strips VR-related suffixes and author credits from a game name.
        ///
        /// This is the first step in the matching pipeline. External sources often include
        /// extra text after the game name that would prevent matching against Playnite's
        /// clean game names. This method removes those suffixes in a specific order
        /// (most specific first) to handle compound patterns correctly.
        ///
        /// Strip order (each step only runs if the previous parenthetical step didn't match):
        /// 1. "(VR Port)" - 10 chars, for Flat2VR-style VR port labels
        /// 2. "(VR Mod)" - 9 chars, for explicitly labeled VR mods
        /// 3. "(UEVR)" - 7 chars, for UEVR profile listings
        /// 4. "(VR)" - 5 chars, generic VR suffix in parentheses
        /// 5. " VR Mod by" - RototRobot convention: "DOOM VR Mod by Author" → "DOOM"
        /// 6. " by " - General author credit removal: "DOOM by Author" → "DOOM"
        /// 7. " VR" - Trailing VR suffix: "DOOM VR" → "DOOM"
        /// 8. " Flat2VR" - Trailing Flat2VR suffix: "DOOM Flat2VR" → "DOOM"
        ///
        /// Examples:
        ///   "DOOM VR mod by Author" → "DOOM"
        ///   "WRATH: AEON OF RUIN VR" → "WRATH: AEON OF RUIN"
        ///   "Half-Life 2 (VR)" → "Half-Life 2"
        ///   "Fallout 4 (VR Port)" → "Fallout 4"
        ///   "Resident Evil 7 Flat2VR" → "Resident Evil 7"
        ///
        /// Edge cases handled:
        /// - Null input returns empty string
        /// - Multiple " by " occurrences: uses the LAST one (AuthorName at end)
        /// - Chained suffixes: parentheticals are mutually exclusive (only first match wins)
        ///
        /// IMPORTANT: This method is called on BOTH source entries AND Playnite game names
        /// before comparison, ensuring both are cleaned to the same baseline.
        /// </summary>
        /// <param name="name">The raw game name that may contain VR suffixes or author credits.</param>
        /// <returns>The cleaned game name with suffixes removed, or empty string if input was null/empty.</returns>
        public static string StripVrSuffixes(string name)
        {
            var result = name?.Trim() ?? string.Empty;

            // Strip parenthetical suffixes (mutually exclusive - only the first match is applied)
            // These are checked longest-first to avoid partial matches (e.g., "(VR Port)" before "(VR)")
            var strippedParen = false;
            if (result.EndsWith(" (VR Port)", StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - 10).Trim();
                strippedParen = true;
            }
            else if (!strippedParen && result.EndsWith(" (VR Mod)", StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - 9).Trim();
                strippedParen = true;
            }
            else if (!strippedParen && result.EndsWith(" (VR)", StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - 5).Trim();
                strippedParen = true;
            }
            else if (!strippedParen && result.EndsWith(" (UEVR)", StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - 7).Trim();
                strippedParen = true;
            }

            // Strip " VR Mod by Author" (RototRobot VR mod naming convention)
            // Uses LastIndexOf to find the rightmost occurrence, handling cases like
            // "Game Name VR Mod by Author Name" → "Game Name"
            var vrModByIdx = result.LastIndexOf(" VR Mod by", StringComparison.OrdinalIgnoreCase);
            if (vrModByIdx >= 0)
            {
                result = result.Substring(0, vrModByIdx).Trim();
            }
            else
            {
                // Strip " by AuthorName" suffix (general pattern for other sources)
                // Also handles "Game by" at end of string (no space after "by")
                var byIdx = result.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
                if (byIdx < 0 && result.EndsWith(" by", StringComparison.OrdinalIgnoreCase))
                    byIdx = result.Length - 3;
                if (byIdx > 0)
                    result = result.Substring(0, byIdx).Trim();
            }

            // Strip trailing VR suffixes (generic " VR" and source-specific " Flat2VR")
            if (result.EndsWith(" VR", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - 3).Trim();

            if (result.EndsWith(" Flat2VR", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - 8).Trim();

            return result;
        }

        /// <summary>
        /// Normalizes a game name for comparison.
        ///
        /// This is the complete normalization pipeline applied to both source entries and
        /// Playnite game names before matching. It produces a canonical form where
        /// "DOOM", "doom", "D-O-O-M", and "DOOM VR" all become "doom".
        ///
        /// Pipeline steps (applied in order):
        /// 1. StripVrSuffixes() - Remove VR suffixes and author credits
        /// 2. ToLowerInvariant() - Convert to lowercase
        /// 3. Replace("-", " ") - Replace dashes with spaces ("DOOM-Eternal" → "doom eternal")
        /// 4. Replace("_", " ") - Replace underscores with spaces ("DOOM_Eternal" → "doom eternal")
        /// 5. Replace(".", "") - Remove periods ("Half-Life 2.0" → "half life 20")
        /// 6. Trim() - Remove leading/trailing whitespace
        ///
        /// Examples:
        ///   "DOOM Eternal" → "doom eternal"
        ///   "Half-Life 2" → "half life 2"
        ///   "Resident_Evil_7" → "resident evil 7"
        ///   "Fallout.4" → "fallout4"
        ///
        /// This method is thread-safe and can be called from any context.
        /// Null input is treated as empty string.
        /// </summary>
        /// <param name="name">The game name to normalize.</param>
        /// <returns>The normalized name suitable for string comparison.</returns>
        public static string Normalize(string name)
        {
            var normalized = StripVrSuffixes(name);
            return normalized.ToLowerInvariant()
                .Replace("-", " ")
                .Replace("_", " ")
                .Replace(".", "")
                .Trim();
        }

        /// <summary>
        /// Maximum string length supported by the Levenshtein similarity calculation.
        /// Strings longer than this limit return 0.0 similarity to avoid performance issues.
        /// Set to 200 to match Playnite's game name limit.
        /// </summary>
        private const int MaxLevenshteinStringLength = 200;

        /// <summary>
        /// Computes a similarity score between two strings (0.0 to 1.0).
        ///
        /// Uses the Levenshtein distance algorithm to measure how many character edits
        /// (insertions, deletions, substitutions) are needed to transform one string into another.
        ///
        /// Similarity = 1 - (distance / max_length)
        /// Where max_length is the length of the longer string.
        ///
        /// Examples:
        ///   "doom" vs "doom" → 1.0 (identical)
        ///   "doom" vs "doem" → 0.75 (1 edit out of 4 chars)
        ///   "half life 2" vs "half life 2 vr" → 0.6 (6 edits out of 12 chars)
        ///   "" vs "doom" → 0.0 (empty string)
        ///   "a very long name exceeding two hundred characters..." → 0.0 (too long)
        ///
        /// The score is capped at 200 characters per string to prevent O(n*m) memory
        /// issues with very long inputs. Strings exceeding this limit return 0.0.
        /// </summary>
        /// <param name="a">First string to compare.</param>
        /// <param name="b">Second string to compare.</param>
        /// <returns>Similarity score from 0.0 (completely different) to 1.0 (identical).</returns>
        public static float ComputeSimilarity(string a, string b)
        {
            if (a == b) return 1.0f;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0f;
            if (a.Length > MaxLevenshteinStringLength || b.Length > MaxLevenshteinStringLength)
                return 0.0f;

            var distance = LevenshteinDistance(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            return 1.0f - (distance / (float)maxLen);
        }

        /// <summary>
        /// Computes the Levenshtein distance between two strings.
        ///
        /// The Levenshtein distance is the minimum number of single-character edits
        /// (insertions, deletions, or substitutions) required to change one string
        /// into the other.
        ///
        /// Implementation uses the standard dynamic programming approach with a
        /// two-row rolling buffer over the shorter string, so memory is O(min(m, n))
        /// instead of the full (m+1) x (n+1) matrix (which peaks at ~160 KB per call
        /// for 200-char names and lands on the large-object heap).
        ///
        /// Examples:
        ///   LevenshteinDistance("kitten", "sitting") = 3
        ///   LevenshteinDistance("doom", "doom") = 0
        ///   LevenshteinDistance("doom", "doem") = 1
        ///
        /// Time complexity: O(m * n)
        /// Space complexity: O(min(m, n))
        /// </summary>
        /// <param name="s">The first string.</param>
        /// <param name="t">The second string.</param>
        /// <returns>The minimum edit distance between the two strings.</returns>
        public static int LevenshteinDistance(string s, string t)
        {
            // Iterate the shorter string as the row dimension so the rolling buffer
            // stays at O(min(m, n)) entries.
            if (s.Length > t.Length)
            {
                var swap = s;
                s = t;
                t = swap;
            }

            var m = s.Length;
            var n = t.Length;
            var previous = new int[n + 1];
            var current = new int[n + 1];

            // Base case: distance from the empty string
            for (var j = 0; j <= n; j++)
                previous[j] = j;

            for (var i = 1; i <= m; i++)
            {
                current[0] = i;
                for (var j = 1; j <= n; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + cost);
                }

                var tmp = previous;
                previous = current;
                current = tmp;
            }

            return previous[n];
        }

        /// <summary>
        /// Computes a match score between a source game name and a Playnite game name.
        ///
        /// This is the primary matching function used throughout the plugin. It combines
        /// normalization, substring rejection, and similarity scoring into a single call.
        ///
        /// Scoring rules:
        /// 1. Exact match (after normalization): returns 1.0
        /// 2. One name is a proper substring of the other: returns -1 (REJECT)
        ///    - This prevents "doom" from matching "DOOM 3" or "DOOM Eternal"
        ///    - This prevents "hl" from matching "Half-Life 2"
        ///    - Sequel games (DOOM, DOOM 2, DOOM Eternal) are all treated as distinct
        /// 3. Levenshtein similarity >= threshold: returns the similarity score
        ///    - "doom" vs "doem" → 0.75 (below a 0.8 threshold → -1)
        ///    - "residents evil 7" vs "resident evil 7" → 0.89 (above 0.8 → 0.89)
        /// 4. Levenshtein similarity < threshold: returns -1 (REJECT)
        ///
        /// The -1 return value means "no match" and is used by callers to skip the entry.
        /// Scores from the threshold up to 0.999 indicate fuzzy matches that go through the
        /// review dialog. Score of 1.0 indicates an exact match that can be auto-applied.
        ///
        /// Fuzzy matching (scores just above the threshold, up to 0.99) is useful for:
        /// - Typos in source data: "Resident Evil 7" vs "Resident Evil 7 bihocolateral"
        /// - Different naming conventions: "Half-Life 2" vs "Half Life 2"
        /// - Minor variations: "DOOM Eternal" vs "Doom Eternal"
        ///
        /// Examples:
        ///   "DOOM VR mod by Author" vs "DOOM" → 1.0 (exact after normalization)
        ///   "DOOM VR mod by Author" vs "DOOM 3" → -1 ("doom" is substring of "doom 3")
        ///   "DOOM Eternal" vs "Doom Eternal" → 1.0 (case-insensitive exact match)
        ///   "Half Life 2" vs "Half Life 2 VR" → -1 ("half life 2" is substring of "half life 2 vr")
        ///   "Fallout 4" vs "Fallout 4 VR" → -1 ("fallout 4" is substring of "fallout 4 vr")
        /// </summary>
        /// <param name="sourceName">The game name from the VR mod source (e.g., "DOOM VR mod by Author").</param>
        /// <param name="gameName">The game name from the Playnite library (e.g., "DOOM").</param>
        /// <param name="threshold">Minimum similarity score to consider as a match.
        /// Callers must pass the configured threshold explicitly (no default) so the user's
        /// FuzzyMatchThreshold setting is never silently overridden.</param>
        /// <returns>
        /// 1.0 for exact match, similarity score (threshold-0.99) for fuzzy match, or -1 for no match.
        /// </returns>
        public static float GetMatchScore(string sourceName, string gameName, float threshold)
        => GetMatchScoreNormalized(Normalize(sourceName), Normalize(gameName), threshold);

        /// <summary>
        /// Score a pair whose names are ALREADY normalized, so a caller that reuses one normalized
        /// name across many candidates (a whole source catalog against one game) pays normalization
        /// once per pair-side instead of once per pair. Hot loops call this with
        /// <see cref="Playnite.VRMods.VrModTagService.NormalizeForMatchFast"/> output; the
        /// <c>NormalizeForMatchFast_MatchesCanonicalNormalize</c> differential test locks the two
        /// normalizers to the same result.
        /// </summary>
        /// <param name="s">One normalized name.</param>
        /// <param name="g">The other normalized name.</param>
        /// <param name="threshold">Similarity the pair has to reach to count as a match.</param>
        /// <returns>1.0 for an exact match, the similarity score when it reaches the threshold, otherwise -1.</returns>
        public static float GetMatchScoreNormalized(string s, string g, float threshold)
        {
            if (s == g) return 1.0f;

            // No substring matching — proper substring returns -1
            // This eliminates false positives like "doom" matching "DOOM 3" or "DOOM Eternal"
            // Sequel games are treated as entirely different games
            if (s.Length > 0 && g.Length > 0 && s.Length < g.Length && g.Contains(s))
                return -1;
            if (s.Length > 0 && g.Length > 0 && g.Length < s.Length && s.Contains(g))
                return -1;

            // Length-difference prune (see CanReachSimilarity): these candidates cannot be scored up
            // to the threshold by any edit distance, and they are the ones whose edit distance would
            // otherwise be computed in full for nothing.
            if (!CanReachSimilarity(s, g, threshold))
                return -1;

            // Reject when the numeric/roman "identifier" values differ (e.g. "street fighter 6"
            // vs "street fighter v", "devil may cry 5" vs "devil may cry hd"). Levenshtein
            // similarity is too tolerant of a single differing trailing token in long names
            // that share a common prefix, producing false positives between sequels/editions.
            if (IdentifierValuesDiffer(s, g))
                return -1;

            var similarity = ComputeSimilarity(s, g);
            return similarity >= threshold ? similarity : -1;
        }

        /// <summary>
        /// Cheap upper bound on the similarity two names can reach, used to skip the identifier check
        /// and the whole O(m·n) Levenshtein table for candidates that cannot score high enough.
        /// Levenshtein distance is never smaller than the difference in length, so
        /// <c>1 - distance/maxLen</c> can never exceed <c>1 - |Δlength|/maxLen</c>.
        ///
        /// Pruning on this bound cannot change a match result: every pair it rejects would have come
        /// out below <paramref name="threshold"/> anyway (an exact match has Δlength = 0 and is already
        /// handled earlier in <see cref="GetMatchScore"/>, and a proper-substring pair returns -1 on
        /// both paths). Locked by the differential test over the name corpus.
        /// </summary>
        /// <param name="a">One normalized name.</param>
        /// <param name="b">The other normalized name.</param>
        /// <param name="threshold">Similarity the pair has to reach to be considered a match.</param>
        /// <returns>False when the two lengths alone rule the pair out.</returns>
        internal static bool CanReachSimilarity(string a, string b, float threshold)
        {
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0)
                return threshold <= 0.0f;
            var ceiling = 1.0f - (Math.Abs(a.Length - b.Length) / (float)maxLen);
            return ceiling >= threshold;
        }

        private const int MaxRomanNumeralValue = 20;

        private static readonly char[] RomanLetters = { 'i', 'v', 'x', 'l', 'c', 'd', 'm' };

        private static bool IdentifierValuesDiffer(string a, string b)
        {
            var va = IdentifierValues(a);
            var vb = IdentifierValues(b);
            if (va.Count != vb.Count)
                return true;
            for (var i = 0; i < va.Count; i++)
            {
                if (va[i] != vb[i])
                    return true;
            }
            return false;
        }

        // Each token that is (or reduces to) a number becomes one identifier value. Because
        // CleanTokenForIdentifier strips punctuation before the numeric check, every numeric
        // spelling of the same value collapses to the same integer: "2.5" and "2,5" (dot vs
        // comma decimal) both become 25, and the thousands separator "40,000" becomes 40000.
        // So the same value always agrees and a different value (2 vs 3, 2.5 vs 2) always
        // differs. Roman numerals (i..xx, value 1..20) are canonicalised the same way, so a
        // roman and an arabic form of the same number agree ("V" == "5").
        private static List<int> IdentifierValues(string s)
        {
            var values = new List<int>();
            if (string.IsNullOrEmpty(s))
                return values;

            var tokens = s.Split(' ');
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = CleanTokenForIdentifier(tokens[i]);
                if (token.Length == 0)
                    continue;

                int numericValue;
                if (IsArabicDigits(token, out numericValue))
                {
                    values.Add(numericValue);
                }
                else
                {
                    var romanValue = ParseRomanNumeral(token);
                    if (romanValue.HasValue)
                        values.Add(romanValue.Value);
                }
            }
            return values;
        }

        private static string CleanTokenForIdentifier(string token)
        {
            // Drop punctuation so that a numeric identifier survives surrounding
            // marks (e.g. "7: Biohazard" -> "7"), matching how the other name is
            // normalized. Only lowercase letters and digits are kept.
            var sb = new StringBuilder(token.Length);
            for (var i = 0; i < token.Length; i++)
            {
                var ch = token[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool IsArabicDigits(string token, out int value)
        {
            value = 0;
            foreach (var ch in token)
            {
                if (ch < '0' || ch > '9')
                    return false;
            }
            return int.TryParse(token, out value);
        }

        private static int? ParseRomanNumeral(string token)
        {
            if (token.Length == 0)
                return null;

            foreach (var ch in token)
            {
                if (Array.IndexOf(RomanLetters, char.ToLowerInvariant(ch)) < 0)
                    return null;
            }

            var value = 0;
            var prev = 0;
            for (var i = token.Length - 1; i >= 0; i--)
            {
                var v = RomanValue(char.ToLowerInvariant(token[i]));
                value += v < prev ? -v : v;
                prev = v;
            }

            return value >= 1 && value <= MaxRomanNumeralValue ? (int?)value : null;
        }

        private static int RomanValue(char c)
        {
            switch (c)
            {
                case 'i': return 1;
                case 'v': return 5;
                case 'x': return 10;
                case 'l': return 50;
                case 'c': return 100;
                case 'd': return 500;
                case 'm': return 1000;
                default: return 0;
            }
        }

        /// <summary>
        /// Per-mapping decision key (NO category): "sourceId||sourceGame||gameId".
        /// The decision "is source-game X the same as Playnite game Y?" is a property of the
        /// mapping, not the tags — a mod that yields several categories is still one yes/no.
        ///
        /// Keyed by the Playnite game's stable Id (not its display name) so that store
        /// duplicates of the same game — e.g. one Epic + one Steam entry, both named
        /// "Alien: Isolation" — each carry their own independent decision instead of sharing
        /// one name-keyed entry that a stale-removal on either duplicate would wipe for both.
        /// </summary>
        /// <returns>A unique decision key in format "sourceId||sourceGame||gameId".</returns>
        public static string MakeDecisionKey(string sourceId, string sourceGameName,
            Guid gameId)
        {
            return $"{sourceId}||{sourceGameName}||{gameId:D}";
        }

        // ---------------------------------------------------------------------------------------
        // Token-run rules: what it means when one name's words appear as one unbroken run inside
        // the other name's words.
        //
        // Whole-string similarity cannot answer this question, and the code already carries a patch
        // for each direction of its failure: similarity dilutes when a true subset picks up edition
        // wording ("morrowind" against "the elder scrolls iii: morrowind goty edition" is 0.20), and
        // similarity flatters when a long shared prefix carries one differing token — which is why
        // IdentifierValuesDiffer exists. Both patches score the pair as one string. Names are not
        // strings to be measured, they are a title plus decorations, and the decorations are whole
        // words. So these rules work on words and ask a question with an answer:
        //
        //   Does one name's words appear inside the other as ONE UNBROKEN RUN, and what is left
        //   over on the longer side?
        //
        //   left over, and it is a number or roman numeral the other name does not carry
        //       -> a different entry in the same series. "alan wake" against "alan wake ii", "street
        //          fighter 6" against "street fighter v", "crysis 2 revolution" against "crysis
        //          remastered". This is the shape the substring rule was written to refuse, recovered
        //          as a reason instead of a score.
        //   left over, and it is edition/packaging wording (PackagingWords below) or an ordinal the
        //   other name carries as well ("alan wake 2" against "alan wake ii": the same number)
        //       -> the same product written at different lengths. "doom 3" against "doom 3 bfg
        //          edition".
        //   left over, and it is a real word ("legends", "goodies collection")
        //       -> no name rule knows; the pair is a question, not an answer.
        //
        // One asymmetry is deliberate and is the honest limit of the rule: an ordinal INSIDE the run,
        // or in a leftover on the side whose name is fully carried, is decisive, but an ordinal in
        // wording that PRECEDES the run is not. That is what makes "morrowind" fit "the elder scrolls
        // III: Morrowind GOTY Edition" (the ordinal is inside the series name that comes before the
        // shared title) while "alan wake" still refuses "Alan Wake II" (the ordinal trails the shared
        // title). A library title that puts its ordinal before a shared subtitle AND means a different
        // product from the listing would be read as the same family — this rule cannot see that, so
        // the pairs it accepts are candidates for a person to confirm, never auto-apply evidence.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Words that decorate a title without naming a different product: edition and re-release
        /// wording. Used only to word a verdict — a leftover from this set never makes a pair a
        /// different product, and being in it is never by itself evidence of a match.
        /// Kept short on purpose: every word here can also be a real subtitle, and the cost of a
        /// wrong entry in it is a pair reported as "same product" instead of "have a look".
        /// </summary>
        private static readonly HashSet<string> PackagingWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "of", "and", "a", "an", "game", "games", "vr", "vr1", "vr2",
            "edition", "editions", "goty", "year", "complete", "gold", "vanilla",
            "enhanced", "definitive", "directorial", "directiorals", "anniversary",
            "holiday", "bfg", "deluxe", "premium", "standard", "trilogy", "legendary"
        };

        /// <summary>
        /// A normalized name split into the words a name rule can reason about.
        ///
        /// <see cref="Normalize"/> lowercases, turns <c>-</c> and <c>_</c> into spaces and drops
        /// <c>.</c>, and it does not touch the rest of punctuation, so a token is cleaned the same way
        /// <see cref="CleanTokenForIdentifier"/> cleans it: only letters and digits survive. That glue
        /// is what makes "III:" and "2077" comparable to their neighbours — splitting on whitespace
        /// alone would leave "iii:" as a token that no ordinal rule recognises.
        /// </summary>
        /// <param name="name">A raw or normalized name.</param>
        /// <returns>Lowercase alphanumeric tokens, in order; empty when the name has no words.</returns>
        public static List<string> TokenizeForMatch(string name)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(name))
                return tokens;

            foreach (var part in Normalize(name).Split(' '))
            {
                var token = CleanTokenForIdentifier(part);
                if (token.Length > 0)
                    tokens.Add(token);
            }
            return tokens;
        }

        /// <summary>
        /// Whether a token names a series ordinal rather than a word: Arabic digits, or a roman
        /// numeral within <see cref="MaxRomanNumeralValue"/>. Same classifier the similarity path uses
        /// for <see cref="IdentifierValuesDiffer"/>, so the two rules cannot disagree about what an
        /// ordinal is.
        /// </summary>
        public static bool IsIdentifierToken(string token)
        {
            var clean = CleanTokenForIdentifier(token ?? string.Empty);
            if (clean.Length == 0)
                return false;
            if (IsArabicDigits(clean, out _))
                return true;
            return ParseRomanNumeral(clean).HasValue;
        }

        /// <summary>
        /// Compare a source-catalog listing name and a library game name by token runs.
        /// See the block comment above this member for the rule and its deliberate limit.
        /// </summary>
        /// <param name="sourceName">The listing's name as the source publishes it.</param>
        /// <param name="gameName">The library game's name.</param>
        /// <returns>The verdict plus the leftover tokens on each side, so a reason can name them.</returns>
        public static TokenRunResult CompareTokenRuns(string sourceName, string gameName)
        {
            var src = TokenizeForMatch(sourceName);
            var game = TokenizeForMatch(gameName);
            if (src.Count == 0 || game.Count == 0)
                return TokenRunResult.Unrelated(src, game);

            var srcToGame = BestTokenRun(src, game);
            var gameToSrc = BestTokenRun(game, src);

            // A direction is the containment hypothesis when the name it treats as contained is
            // carried whole. Prefer that hypothesis; with neither holding, the longer run.
            var srcCarried = srcToGame.Length == src.Count;
            var gameCarried = gameToSrc.Length == game.Count;
            var useSourceAsInner = srcCarried || (!gameCarried && srcToGame.Length >= gameToSrc.Length);

            var run = useSourceAsInner ? srcToGame : gameToSrc;
            var inner = useSourceAsInner ? src : game;
            var outer = useSourceAsInner ? game : src;
            if (run.Length == 0)
                return TokenRunResult.Unrelated(src, game);

            // The run has to carry a decent share of the SHORTER name, or the two names merely
            // happen to share a stretch of words. "doom" inside "legend of the galactic heroes"
            // shares one word with a name that is not about DOOM at all.
            if (run.Length * 2 < Math.Min(src.Count, game.Count))
                return TokenRunResult.Unrelated(src, game);

            // A run made only of ordinals proves nothing: "2" inside "borderlands 2" is a number
            // landing on a number, not a title landing on a title.
            var runIds = new List<int>();
            var runCarriesAWord = false;
            for (var i = run.InnerStart; i < run.InnerStart + run.Length; i++)
            {
                var ordinal = IdentifierValueOf(inner[i]);
                if (ordinal.HasValue)
                    runIds.Add(ordinal.Value);
                else
                    runCarriesAWord = true;
            }
            if (!runCarriesAWord)
                return TokenRunResult.Unrelated(src, game);

            var innerExtra = TokensOutside(inner, run.InnerStart, run.Length);
            var outerAfter = outer.GetRange(run.OuterStart + run.Length, outer.Count - run.OuterStart - run.Length);
            var outerExtra = Concat(outer.GetRange(0, run.OuterStart), outerAfter);

            // The decisive ordinals: one on the contained side anywhere, or on the carrying side
            // AFTER the run. Ordinals BEFORE the run are inside the series wording that introduces
            // the shared title, and that is the shape of a full name, not of a sequel (see the block
            // comment above). innerIds/outerIds are looked at so that an ordinal both names carry
            // does not read as a disagreement about which product is meant.
            if (HasUnagreedOrdinal(outerAfter, runIds, IdentifierValuesOf(inner))
                || HasUnagreedOrdinal(innerExtra, runIds, IdentifierValuesOf(outer)))
            {
                return new TokenRunResult(TokenRunKind.DifferentProduct, run.Length, src.Count, game.Count,
                    useSourceAsInner ? innerExtra : outerExtra,
                    useSourceAsInner ? outerExtra : innerExtra);
            }

            var kind = LeftoversAreAgreedWording(innerExtra, IdentifierValuesOf(outer))
                && LeftoversAreAgreedWording(outerExtra, IdentifierValuesOf(inner))
                ? TokenRunKind.SameProduct
                : TokenRunKind.NeedsALook;
            return new TokenRunResult(kind, run.Length, src.Count, game.Count,
                useSourceAsInner ? innerExtra : outerExtra,
                useSourceAsInner ? outerExtra : innerExtra);
        }

        /// <summary>
        /// The longest stretch of <paramref name="inner"/> that appears contiguously in
        /// <paramref name="outer"/>. Ties go to the earliest position in <paramref name="inner"/>, then
        /// the earliest in <paramref name="outer"/>, so the result does not depend on list order beyond
        /// that.
        /// </summary>
        private static (int Length, int InnerStart, int OuterStart) BestTokenRun(List<string> inner, List<string> outer)
        {
            var best = (Length: 0, InnerStart: 0, OuterStart: 0);
            for (var i = 0; i < inner.Count; i++)
            {
                for (var j = 0; j < outer.Count; j++)
                {
                    var len = 0;
                    while (i + len < inner.Count && j + len < outer.Count
                        && string.Equals(inner[i + len], outer[j + len], StringComparison.Ordinal))
                    {
                        len++;
                    }
                    if (len > best.Length)
                        best = (len, i, j);
                }
            }
            return best;
        }

        private static List<string> TokensOutside(List<string> tokens, int runStart, int runLength)
        {
            var outside = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (i >= runStart && i < runStart + runLength)
                    continue;
                outside.Add(tokens[i]);
            }
            return outside;
        }

        private static List<string> Concat(List<string> head, List<string> tail)
        {
            var all = new List<string>(head.Count + tail.Count);
            all.AddRange(head);
            all.AddRange(tail);
            return all;
        }

        /// <summary>
        /// Whether every leftover token is wording that agrees rather than a title: edition/packaging
        /// wording, or an ordinal whose value the OTHER name carries too ("Alan Wake 2" against
        /// "Alan Wake II" is one product spelled two ways; the ordinals are the same number).
        /// An empty leftover set counts as agreed: a name that adds nothing adds no wording.
        /// </summary>
        private static bool LeftoversAreAgreedWording(List<string> leftovers, List<int> otherSideIds)
        {
            foreach (var word in leftovers)
            {
                if (PackagingWords.Contains(word))
                    continue;
                var ordinal = IdentifierValueOf(word);
                if (ordinal.HasValue && otherSideIds.Contains(ordinal.Value))
                    continue;
                return false;
            }
            return true;
        }

        private static List<int> IdentifierValuesOf(List<string> tokens)
        {
            var values = new List<int>();
            foreach (var token in tokens)
            {
                var value = IdentifierValueOf(token);
                if (value.HasValue)
                    values.Add(value.Value);
            }
            return values;
        }

        private static int? IdentifierValueOf(string token)
        {
            var clean = CleanTokenForIdentifier(token);
            if (clean.Length == 0)
                return null;
            if (IsArabicDigits(clean, out var numeric))
                return numeric;
            return ParseRomanNumeral(clean);
        }

        /// <summary>
        /// True when some token in <paramref name="side"/> is an ordinal that agrees with neither the
        /// run's ordinals nor <paramref name="otherSideIds"/> — the pair is two entries in a series.
        /// </summary>
        private static bool HasUnagreedOrdinal(List<string> side, List<int> runIds, List<int> otherSideIds)
        {
            foreach (var token in side)
            {
                var value = IdentifierValueOf(token);
                if (!value.HasValue)
                    continue;
                if (runIds.Contains(value.Value))
                    continue;
                if (otherSideIds.Contains(value.Value))
                    continue;
                return true;
            }
            return false;
        }
    }
}
