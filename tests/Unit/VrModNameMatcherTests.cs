using System;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    [TestFixture]
    public class VrModNameMatcherTests
    {
        #region StripVrSuffixes

        [TestCase("Game VR", "Game")]
        [TestCase("Game Flat2VR", "Game")]
        [TestCase("Game VR Mod by Author", "Game")]
        [TestCase("Game VR by Author", "Game")]
        [TestCase("Game by Author", "Game")]
        [TestCase("  Game VR  ", "Game")]
        [TestCase("WRATH: AEON OF RUIN VR", "WRATH: AEON OF RUIN")]
        [TestCase("ROBOQUEST VR", "ROBOQUEST")]
        [TestCase("POSTAL 2 VR", "POSTAL 2")]
        [TestCase("Game", "Game")]
        [TestCase("Half-Life 2", "Half-Life 2")]
        [TestCase("Game VR Mod by Author VR", "Game")]
        [TestCase("Game (VR Port)", "Game")]
        [TestCase("Game (VR Mod)", "Game")]
        [TestCase("Game (UEVR)", "Game")]
        [TestCase("Game (VR)", "Game")]
        [TestCase("Game VR Mod by Author Name", "Game")]
        [TestCase("Game by Author Name", "Game")]
        [TestCase("Game VR by Author Name", "Game")]
        [TestCase("Game Flat2VR mod by Author", "Game Flat2VR mod")]
        public void StripVrSuffixes_RemovesKnownSuffixes(string input, string expected)
        {
            var result = VrModNameMatcher.StripVrSuffixes(input);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("VR", "VR")]
        [TestCase(" ", "")]
        [TestCase("  ", "")]
        [TestCase("Game", "Game")]
        [TestCase("Game VR Port", "Game VR Port")]
        public void StripVrSuffixes_PreservesNonSuffixStrings(string input, string expected)
        {
            var result = VrModNameMatcher.StripVrSuffixes(input);
            Assert.That(result, Is.EqualTo(expected));
        }

        #endregion

        #region Normalize

        [TestCase("Half-Life 2", "half life 2")]
        [TestCase("Half-Life_2", "half life 2")]
        [TestCase("HalfLife.2", "halflife2")]
        [TestCase("SKYRIM", "skyrim")]
        [TestCase("  Game  ", "game")]
        [TestCase("Game_Name", "game name")]
        [TestCase("Game-Name", "game name")]
        [TestCase("Game VR", "game")]
        [TestCase("Game (VR Mod)", "game")]
        public void Normalize_HandlesSpecialCharsAndSuffixes(string input, string expected)
        {
            var result = VrModNameMatcher.Normalize(input);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("", "")]
        [TestCase(null, "")]
        [TestCase("   ", "")]
        public void Normalize_HandlesEmptyInput(string? input, string expected)
        {
            var result = VrModNameMatcher.Normalize(input!);
            Assert.That(result, Is.EqualTo(expected));
        }

        #endregion

        #region False Positives — Must Return -1

        [TestCase("hl", "Torchlight II", 0.8f)]
        [TestCase("hl", "Half-Life 2", 0.8f)]
        [TestCase("arma", "Worms Armageddon", 0.8f)]
        [TestCase("dusk", "Duskers", 0.8f)]
        [TestCase("necro", "Necromunda", 0.8f)]
        [TestCase("post", "Postal 2", 0.8f)]
        [TestCase("doom", "DOOM 3", 0.8f)]
        [TestCase("doom", "DOOM Eternal", 0.8f)]
        [TestCase("fallout", "Fallout 76", 0.8f)]
        [TestCase("doom 3", "DOOM 3: BFG", 0.8f)]
        [TestCase("half life", "Half-Life 2", 0.8f)]
        [TestCase("resident evil", "Resident Evil 7", 0.8f)]
        public void GetMatchScore_NoSubstringFalsePositives(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.EqualTo(-1),
                $"'{sourceName}' should NOT match '{gameName}' (false positive)");
        }

        #endregion

        #region Sequel Differentiation

        [TestCase("DOOM", "DOOM 3", 0.8f)]
        [TestCase("DOOM", "DOOM Eternal", 0.8f)]
        [TestCase("DOOM 3", "DOOM Eternal", 0.8f)]
        [TestCase("Half-Life", "Half-Life 2", 0.8f)]
        [TestCase("Resident Evil", "Resident Evil 7", 0.8f)]
        [TestCase("Fallout", "Fallout 76", 0.8f)]
        [TestCase("half life", "Half-Life 2", 0.8f)]
        [TestCase("resident evil", "Resident Evil 7", 0.8f)]
        public void GetMatchScore_DifferentSequelsReturnNegative(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.EqualTo(-1),
                $"'{sourceName}' and '{gameName}' are different games, should not match");
        }

        #endregion

        #region Exact Matches — Score 1.0

        [TestCase("Skyrim", "Skyrim", 0.8f)]
        [TestCase("Skyrim", "skyrim", 0.8f)]
        [TestCase("Half-Life 2", "Half Life 2", 0.8f)]
        [TestCase("Roboquest", "Roboquest", 0.8f)]
        [TestCase("Left 4 Dead 2", "Left 4 Dead 2", 0.8f)]
        [TestCase("Cyberpunk 2077", "Cyberpunk 2077", 0.8f)]
        [TestCase("DOOM 3", "DOOM 3", 0.8f)]
        [TestCase("DOOM 3", "DOOM 3 VR Mod by Author", 0.8f)]
        public void GetMatchScore_ExactMatchesReturnOne(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.EqualTo(1.0f));
        }

        #endregion

        #region Fuzzy Matches — Score [0.8..0.94]

        [TestCase("Skyrim", "Skyrm", 0.8f)]
        [TestCase("Half-Life", "HalfLife", 0.8f)]
        public void GetMatchScore_FuzzyMatchesReturnPositiveScore(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.GreaterThanOrEqualTo(0.8f).And.LessThanOrEqualTo(0.99f),
                $"Expected fuzzy match in [0.8, 0.94] range, got {result}");
        }

        #endregion

        #region No Match — Score -1

        [TestCase("foo", "bar", 0.8f)]
        [TestCase("skyrim", "fallout", 0.8f)]
        [TestCase("", "skyrim", 0.8f)]
        [TestCase("skyrim", "", 0.8f)]
        [TestCase("a", "xyzabc", 0.8f)]
        public void GetMatchScore_NoMatchReturnsNegative(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.EqualTo(-1f));
        }

        #endregion

        #region Decision Key

        [Test]
        public void MakeDecisionKey_FormatsCorrectly()
        {
            // Per-mapping key is 3-part (sourceId||sourceGame||{gameId:D}) — no category.
            var gameId = new Guid("12345678-1234-1234-1234-123456789abc");
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "Left 4 Dead 2", gameId);
            Assert.That(key, Is.EqualTo("rototrobot||Left 4 Dead 2||12345678-1234-1234-1234-123456789abc"));
        }

        [Test]
        public void MakeDecisionKey_SeparateKeysPerSource()
        {
            var gameId = Guid.NewGuid();
            var key1 = VrModNameMatcher.MakeDecisionKey("rototrobot", "Game", gameId);
            var key2 = VrModNameMatcher.MakeDecisionKey("flat2vr", "Game", gameId);
            Assert.That(key1, Is.Not.EqualTo(key2));
        }

        #endregion

        #region ComputeSimilarity Edge Cases

        [TestCase("abc", "abc", 1.0f)]
        [TestCase("", "abc", 0.0f)]
        [TestCase("abc", "", 0.0f)]
        [TestCase("", "", 1.0f)]
        [TestCase("a", "a", 1.0f)]
        [TestCase("abc", "abd", 2.0f / 3.0f)]
        [TestCase("kitten", "sitting", 4.0f / 7.0f)]
        [TestCase("skyrim", "skyrim", 1.0f)]
        [TestCase("skyrim", "skyyrim", 6.0f / 7.0f)]
        public void ComputeSimilarity_ReturnsCorrectValue(string a, string b, float expected)
        {
            var result = VrModNameMatcher.ComputeSimilarity(a, b);
            Assert.That(result, Is.EqualTo(expected).Within(0.0001));
        }

        [Test]
        public void ComputeSimilarity_CapsAt200Chars_ReturnsZeroForOver200()
        {
            var a = new string('a', 201);
            var b = "b";
            var result = VrModNameMatcher.ComputeSimilarity(a, b);
            Assert.That(result, Is.EqualTo(0.0f));
        }

        [Test]
        public void ComputeSimilarity_CapsAt200Chars_ReturnsZeroForLongSecond()
        {
            var a = "a";
            var b = new string('b', 201);
            var result = VrModNameMatcher.ComputeSimilarity(a, b);
            Assert.That(result, Is.EqualTo(0.0f));
        }

        [Test]
        public void ComputeSimilarity_ExactAt200Chars_ReturnsOne()
        {
            var a = new string('a', 200);
            var b = new string('a', 200);
            var result = VrModNameMatcher.ComputeSimilarity(a, b);
            Assert.That(result, Is.EqualTo(1.0f));
        }

        #endregion

        #region LevenshteinDistance

        [TestCase("abc", "abc", 0)]
        [TestCase("abc", "abd", 1)]
        [TestCase("abc", "bc", 1)]
        [TestCase("abc", "abcde", 2)]
        [TestCase("", "abc", 3)]
        [TestCase("abc", "", 3)]
        [TestCase("", "", 0)]
        [TestCase("a", "b", 1)]
        [TestCase("skyrim", "skyrim", 0)]
        [TestCase("skyrim", "skyyrim", 1)]
        public void LevenshteinDistance_ReturnsCorrectValue(string a, string b, int expected)
        {
            var result = VrModNameMatcher.LevenshteinDistance(a, b);
            Assert.That(result, Is.EqualTo(expected));
        }

        /// <summary>
        /// The hot loops score with GetMatchScoreNormalized: they normalize each side ONCE and then
        /// score it against many partners, using the service's fast normalizer. That must give the
        /// same verdict as the raw-name GetMatchScore, whose substring and sequel rules run after
        /// normalization. Locked here because a divergence would silently change which games get
        /// tagged on the per-game path.
        /// </summary>
        [Test]
        [TestCase("DOOM VR", "DOOM")]
        [TestCase("Half Life 2", "Half Life 2 VR")]
        [TestCase("Street Fighter 6", "Street Fighter V")]
        [TestCase("NieR: Automata VR", "Nier Automata")]
        [TestCase("Sleeping Dogs-VR", "Sleeping Dogs")]
        [TestCase("BoneKing VR Edition", "BoneKing VR Edition")]
        [TestCase("Totally Different Name", "Tractor Operator 2023 Deluxe Edition")]
        public void GetMatchScoreNormalized_AgreesWithGetMatchScoreOnRawNames(string sourceName, string gameName)
        {
            const float threshold = 0.85f;

            var canonical = VrModNameMatcher.GetMatchScoreNormalized(
                VrModNameMatcher.Normalize(sourceName), VrModNameMatcher.Normalize(gameName), threshold);
            Assert.That(canonical, Is.EqualTo(VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold)),
                $"normalizing once must not change the verdict for '{sourceName}' vs '{gameName}'");

            var fastPath = VrModNameMatcher.GetMatchScoreNormalized(
                Playnite.VRMods.VrModTagService.NormalizeForMatchFast(sourceName),
                Playnite.VRMods.VrModTagService.NormalizeForMatchFast(gameName), threshold);
            Assert.That(fastPath, Is.EqualTo(VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold)),
                $"the service's fast normalizer must score identically to the canonical one for '{sourceName}' vs '{gameName}'");
        }

        #endregion

        #region GetMatchScore Configurable Threshold

        [TestCase("Skyrim", "Skyrm", 0.7f, true)]
        [TestCase("Skyrim", "Skyrm", 0.9f, false)]
        [TestCase("Skyrim", "Skyrm", 0.8f, true)]
        public void GetMatchScore_RespectsConfigurableThreshold(string sourceName, string gameName, float threshold, bool shouldMatch)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            if (shouldMatch)
                Assert.That(result, Is.GreaterThan(0));
            else
                Assert.That(result, Is.EqualTo(-1f));
        }

        [TestCase("aaaaaaaa bbbbbbbb", "aaaaaaaa cccccccc", 0.5f, true)]
        [TestCase("aaaaaaaa bbbbbbbb", "aaaaaaaa cccccccc", 0.8f, false)]
        public void GetMatchScore_ThresholdBelowOldDefault_IsRespected(string sourceName, string gameName, float threshold, bool shouldMatch)
        {
            // 8-character difference in a 16-character name => similarity exactly 0.5, below
            // the old 0.8f default parameter. These pairs only match when the caller's
            // threshold is honored, which pins the "no default threshold" contract.
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            if (shouldMatch)
                Assert.That(result, Is.GreaterThanOrEqualTo(0).And.LessThan(1));
            else
                Assert.That(result, Is.LessThan(0));
        }

        #endregion

        #region Identifier Differentiation (Arabic vs Roman numerals)

        [TestCase("Street Fighter 6", "Street Fighter V", 0.8f)]
        [TestCase("Street Fighter 6", "Street Fighter 5", 0.8f)]
        [TestCase("Devil May Cry 5", "Devil May Cry HD", 0.8f)]
        [TestCase("Devil May Cry 5", "Devil May Cry 4", 0.8f)]
        [TestCase("Grand Theft Auto 5", "Grand Theft AutoIV", 0.8f)]
        [TestCase("Resident Evil 2", "Resident Evil 3", 0.8f)]
        public void GetMatchScore_DifferentIdentifiers_ReturnNoMatch(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.EqualTo(-1),
                $"'{sourceName}' must NOT match '{gameName}' — different edition identifiers");
        }

        [TestCase("Grand Theft Auto V", "Grand Theft Auto 5", 0.8f)]
        [TestCase("Assassin's Creed 2", "Assassin's Creed II", 0.8f)]
        [TestCase("Left 4 Dead 2", "Left 4 Dead II", 0.8f)]
        public void GetMatchScore_RomanVsArabic_SameIdentifier_Matches(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.GreaterThan(0),
                $"'{sourceName}' and '{gameName}' are the same edition (roman/arabic), should match");
        }

        [Test]
        public void GetMatchScore_ColonAfterNumberDoesNotBreakIdentifier()
        {
            // "Resident Evil 7: Biohazard" (source) vs "Resident Evil 7 Biohazard" (Playnite game).
            // The colon after "7" must not turn the source's identifier into a non-identifier;
            // otherwise the identifier sequences differ ("[7]" vs "[]") and a valid match is rejected.
            var result = VrModNameMatcher.GetMatchScore("Resident Evil 7: Biohazard", "Resident Evil 7 Biohazard", 0.8f);
            Assert.That(result, Is.GreaterThan(0),
                "'Resident Evil 7: Biohazard' and 'Resident Evil 7 Biohazard' are the same game and should match");
        }

        // Rational-number identifiers: dot vs comma decimals agree, and a decimal value
        // differs from plain integers. (Real data has no true "X 2.5" editions; these lock
        // in the canonicalisation so "2,5"=="2.5" and "2.5"!="2" stay true.)
        [TestCase("X 2,5", "X 2.5", 0.8f)]
        [TestCase("X 2.5", "X 2.5", 0.8f)]
        [TestCase("Outcast 1.1", "Outcast 1.1", 0.8f)]
        public void GetMatchScore_DecimalIdentifiers_SameValueMatch(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.GreaterThan(0),
                $"'{sourceName}' and '{gameName}' are the same value and should match");
        }

        [TestCase("X 2.5", "X 2", 0.8f)]
        [TestCase("X 2.5", "X 3", 0.8f)]
        [TestCase("Outcast 1.1", "Outcast 1.2", 0.8f)]
        public void GetMatchScore_DecimalIdentifiers_DifferentValueNoMatch(string sourceName, string gameName, float threshold)
        {
            var result = VrModNameMatcher.GetMatchScore(sourceName, gameName, threshold);
            Assert.That(result, Is.EqualTo(-1),
                $"'{sourceName}' and '{gameName}' are different values and must NOT match");
        }

        #endregion

        #region Rolling Levenshtein equivalence

        /// <summary>
        /// Reference implementation: the classic full-matrix DP. Pins that the production
        /// two-row rolling implementation returns identical distances.
        /// </summary>
        private static int ReferenceLevenshtein(string s, string t)
        {
            var m = s.Length;
            var n = t.Length;
            var d = new int[m + 1, n + 1];
            for (var i = 0; i <= m; i++) d[i, 0] = i;
            for (var j = 0; j <= n; j++) d[0, j] = j;
            for (var i = 1; i <= m; i++)
            {
                for (var j = 1; j <= n; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = System.Math.Min(
                        System.Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[m, n];
        }

        [TestCase("", "")]
        [TestCase("kitten", "sitting")]
        [TestCase("sitting", "kitten")]
        [TestCase("doom", "doem")]
        [TestCase("", "abc")]
        [TestCase("abc", "")]
        [TestCase("flaw", "lawn")]
        [TestCase("book", "back")]
        [TestCase("resident evil 7 biohazard", "resident evil vii biohazard")]
        public void LevenshteinDistance_RollingMatchesReference(string s, string t)
        {
            Assert.That(VrModNameMatcher.LevenshteinDistance(s, t),
                Is.EqualTo(ReferenceLevenshtein(s, t)));
        }

        [Test]
        public void LevenshteinDistance_RandomPairsMatchReference()
        {
            var random = new System.Random(1234);
            for (var iter = 0; iter < 300; iter++)
            {
                var s = RandomString(random);
                var t = RandomString(random);
                Assert.That(VrModNameMatcher.LevenshteinDistance(s, t),
                    Is.EqualTo(ReferenceLevenshtein(s, t)),
                    $"mismatch for '{s}' vs '{t}'");
            }
        }

        private static string RandomString(System.Random random)
        {
            const string alphabet = "abcdefg 0123456789";
            var length = random.Next(0, 40);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
                chars[i] = alphabet[random.Next(alphabet.Length)];
            return new string(chars);
        }

        [Test]
        public void LevenshteinDistance_MaxLengthStringsMatchReference()
        {
            // At the 200-char cap the old full matrix allocated ~160 KB per call.
            var longA = new string('a', 100) + new string('b', 100);
            var longB = new string('a', 99) + new string('c', 101);
            Assert.That(VrModNameMatcher.LevenshteinDistance(longA, longB),
                Is.EqualTo(ReferenceLevenshtein(longA, longB)));
        }

        #endregion

        #region Normalize equivalence

        // Guards the deliberate duplication between VrModNameMatcher.Normalize (canonical,
        // whole-string lowercasing, used by GetMatchScore) and
        // VrModTagService.NormalizeForMatchFast (fast single-pass, hot path). They MUST
        // produce identical output for realistic (ASCII) names; a divergence here means one
        // changed and matching could silently split. This is the LOW-4 "consolidation": a
        // true single-method merge would either add allocations to the hot path or change
        // its per-char lowercasing (a behaviour change), so this differential test is the
        // safe lock instead.
        [TestCase("Game VR")]
        [TestCase("Game Flat2VR")]
        [TestCase("Game VR Mod by Author")]
        [TestCase("WRATH: AEON OF RUIN VR")]
        [TestCase("ROBOQUEST VR")]
        [TestCase("POSTAL 2 VR")]
        [TestCase("Game (VR Port)")]
        [TestCase("Game (UEVR)")]
        [TestCase("  Game VR  ")]
        [TestCase("DOOM_Eternal-VR")]
        [TestCase("half.life.2.vr")]
        [TestCase("")]
        public void NormalizeForMatchFast_MatchesCanonicalNormalize(string name)
        {
            Assert.That(VrModTagService.NormalizeForMatchFast(name),
                Is.EqualTo(VrModNameMatcher.Normalize(name)));
        }

        #endregion

        #region Length-difference prune (matching cost bound)

        // GetMatchScore skips the identifier check and the whole Levenshtein table when the two
        // lengths alone make the threshold unreachable (distance >= |Δlength|, so the score can never
        // exceed 1 - |Δlength|/maxLength). These cases pin where that line sits.
        [TestCase("doom", "doom", 0.8f, true)]
        [TestCase("doom", "dxom", 0.8f, true)]
        [TestCase("resident evil 7", "resident evil vii", 0.8f, true)]
        [TestCase("doom", "doom eternal", 0.8f, false)]
        [TestCase("half life 2", "half life 2 vr", 0.8f, false)]
        [TestCase("abc", "abcdef", 0.6f, false)]
        [TestCase("abc", "abcdef", 0.5f, true)]
        [TestCase("", "", 0.8f, false)]
        [TestCase("", "a", 0.0f, true)]
        public void CanReachSimilarity_KeepsOnlyPairsTheLengthsAllow(string a, string b, float threshold, bool expected)
        {
            Assert.That(VrModNameMatcher.CanReachSimilarity(a, b, threshold), Is.EqualTo(expected));
        }

        /// <summary>
        /// The prune is only safe if the bound never rejects a pair that would have scored high
        /// enough. Checked both directions over a random corpus: a pair the bound rejects must score
        /// below the threshold, and a pair that scores at or above it must never be rejected.
        /// Without this, the cost bound could silently drop real matches.
        /// </summary>
        [Test]
        public void PruneBound_NeverRejectsAPairThatWouldHaveMatched()
        {
            var random = new System.Random(90210);
            const float threshold = 0.8f;
            var pruned = 0;

            for (var iter = 0; iter < 400; iter++)
            {
                var a = VrModNameMatcher.Normalize(RandomString(random));
                var b = VrModNameMatcher.Normalize(RandomString(random));
                var similarity = VrModNameMatcher.ComputeSimilarity(a, b);

                if (!VrModNameMatcher.CanReachSimilarity(a, b, threshold))
                {
                    pruned++;
                    Assert.That(similarity, Is.LessThan(threshold),
                        $"pruned '{a}' vs '{b}' but its real similarity was {similarity:F3}");
                    Assert.That(VrModNameMatcher.GetMatchScore(a, b, threshold), Is.EqualTo(-1),
                        $"pruning changed the verdict for '{a}' vs '{b}'");
                }
                else
                {
                    Assert.That(similarity < threshold || VrModNameMatcher.CanReachSimilarity(a, b, threshold), Is.True);
                }
            }

            Assert.That(pruned, Is.GreaterThan(0),
                "the corpus contained no prunable pair, so this test proved nothing");
        }

        /// <summary>
        /// The worst case the prune is for: two long library names that differ wildly. Before it, the
        /// full 200x200 DP table was paid per candidate to conclude what the lengths already said.
        /// </summary>
        [Test]
        public void TwoLongWildlyDifferentNamesAreRuledOutByLengthAlone()
        {
            var a = new string('a', 200);
            var b = new string('b', 200);

            Assert.That(VrModNameMatcher.CanReachSimilarity(a, b, 0.5f), Is.True, "equal lengths are not the prune's case");
            Assert.That(VrModNameMatcher.CanReachSimilarity(a, new string('b', 60), 0.5f), Is.False);
            Assert.That(VrModNameMatcher.GetMatchScore(a, new string('b', 60), 0.5f), Is.EqualTo(-1));
        }

        #endregion
    }
}
