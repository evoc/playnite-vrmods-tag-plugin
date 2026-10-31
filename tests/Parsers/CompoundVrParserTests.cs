using System.IO;
using System.Reflection;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Tests for <see cref="CompoundVrParser"/>.
    ///
    /// Scope rule under test (see the parser's doc comment): a card is EXCLUDED only when its
    /// data-tech carries an unambiguous "natively-VR product" token (e.g. "Official VR Mode",
    /// "Quest Exclusive", "Mixed Reality"). Everything else is EMITTED — including flat-to-VR
    /// mods that merely use a render API (OpenXR/SteamVR) and emulator/retro titles that gain
    /// VR via a mod. When in doubt, emit.
    /// </summary>
    [TestFixture]
    public class CompoundVrParserTests
    {
        [Test]
        public void SourceId_ReturnsCompoundVR()
        {
            var parser = new CompoundVrParser();
            Assert.That(parser.SourceId, Is.EqualTo("CompoundVR"));
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new CompoundVrParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_StripsVrSuffixes()
        {
            var parser = new CompoundVrParser();
            var entry = new VrModEntry { GameName = "Skyrim VR" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Skyrim"));
        }

        [Test]
        public void ParseCards_EmptyHtml_ReturnsEmpty()
        {
            var parser = new CompoundVrParser();
            var (entries, total, excluded) = parser.ParseCards("");
            Assert.That(entries, Is.Empty);
            Assert.That(total, Is.EqualTo(0));
            Assert.That(excluded, Is.EqualTo(0));

            var (entries2, total2, excluded2) = parser.ParseCards(null);
            Assert.That(entries2, Is.Empty);
            Assert.That(total2, Is.EqualTo(0));
            Assert.That(excluded2, Is.EqualTo(0));
        }

        [Test]
        public void ParseCards_EmitsApiUsingMod_AndEmulator_ExcludesOnlyOfficialNative()
        {
            var parser = new CompoundVrParser();
            var html = Card("cyberpunk-2077", "Cyberpunk 2077 VR", "Full VR Mod|OpenXR")
                     + Card("res-evil-village", "Resident Evil Village VR", "Official VR Mode|Sense Controllers")
                     + Card("metroid-prime", "Metroid Prime VR", "Emulator|6DOF Head Tracking");

            var (entries, total, excluded) = parser.ParseCards(html);

            Assert.That(total, Is.EqualTo(3));
            // Only "Official VR Mode" is an unambiguous natively-VR signal.
            Assert.That(excluded, Is.EqualTo(1));
            Assert.That(entries, Has.Count.EqualTo(2));
            // OpenXR is a render API, not a native-VR signal → the mod is emitted.
            Assert.That(entries[0].GameName, Is.EqualTo("Cyberpunk 2077 VR"));
            // An emulator/retro title that gains VR via a mod is in scope → emitted.
            Assert.That(entries[1].GameName, Is.EqualTo("Metroid Prime VR"));
        }

        [Test]
        public void ParseCards_SetsGameNameCategoryAndDeepLink()
        {
            var parser = new CompoundVrParser();
            var html = Card("cyberpunk-2077", "Cyberpunk 2077 VR", "Full VR Mod|OpenXR");

            var (entries, _, _) = parser.ParseCards(html);

            Assert.That(entries, Has.Count.EqualTo(1));
            var e = entries[0];
            Assert.That(e.GameName, Is.EqualTo("Cyberpunk 2077 VR"));
            Assert.That(e.Categories, Does.Contain(VrModCategories.CompoundVr));
            Assert.That(e.SourceUrl, Is.EqualTo("https://compoundvr.com/games/cyberpunk-2077/"));
        }

        [Test]
        public void ParseCards_DecodesHtmlEntitiesInTitle()
        {
            var parser = new CompoundVrParser();
            var html = Card("assassins-creed", "Assassin&#39;s Creed VR", "Full VR Mod");

            var (entries, _, _) = parser.ParseCards(html);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].GameName, Is.EqualTo("Assassin's Creed VR"));
        }

        [Test]
        public void ParseCards_SkipsCardWithoutTitle()
        {
            var parser = new CompoundVrParser();
            // A card anchor with no <h3> title is counted in Total but produces no entry.
            var html = "<a class=\"game-card\" href=\"/games/x/\" data-tech=\"Full VR Mod\"> no title </a>";

            var (entries, total, excluded) = parser.ParseCards(html);

            Assert.That(total, Is.EqualTo(1));
            Assert.That(excluded, Is.EqualTo(0));
            Assert.That(entries, Is.Empty);
        }

        [Test]
        public void ParseCards_Fixture_ProducesExpectedCounts()
        {
            var html = ReadFixture("compoundvr-games.html");
            var parser = new CompoundVrParser();
            var (entries, total, excluded) = parser.ParseCards(html);

            // The 35-card fixture: 4 unambiguous natively-VR products excluded, 31 emitted.
            Assert.That(total, Is.EqualTo(35));
            Assert.That(excluded, Is.EqualTo(4));
            Assert.That(entries, Has.Count.EqualTo(31));
        }

        [Test]
        public void ParseCards_Fixture_EmitsKnownFlatToVrModsAndEmulators()
        {
            var html = ReadFixture("compoundvr-games.html");
            var parser = new CompoundVrParser();
            var (entries, _, _) = parser.ParseCards(html);

            var names = new System.Collections.Generic.List<string>();
            foreach (var e in entries)
                names.Add(e.GameName);

            // Flat-to-VR mods that use a render API — must be emitted (not excluded as native).
            Assert.That(names, Does.Contain("Cyberpunk 2077 VR"));
            Assert.That(names, Does.Contain("Grand Theft Auto: Vice City VR"));
            // The ambiguous "native vr" label no longer excludes (lenient) — Beat Saber is emitted.
            Assert.That(names, Does.Contain("Beat Saber VR"));
            // Emulator/retro titles that gain VR via a mod — must be emitted.
            Assert.That(names, Does.Contain("Metroid Prime VR"));
            // The 4 unambiguous natively-VR products — must be excluded.
            Assert.That(names, Does.Not.Contain("Resident Evil Village VR"));
            Assert.That(names, Does.Not.Contain("Asgard's Wrath 2 VR"));
            Assert.That(names, Does.Not.Contain("Asgard's Wrath VR"));
            Assert.That(names, Does.Not.Contain("Assassin's Creed Nexus VR"));
        }

        private static string GetSnapshotPath(string fileName)
        {
            var assemblyDir = Path.GetDirectoryName(typeof(CompoundVrParserTests).Assembly.Location);
            return Path.GetFullPath(Path.Combine(assemblyDir!, "..", "..", "..", "Parsers", "snapshots", fileName));
        }

        private static string ReadFixture(string fileName)
        {
            var path = GetSnapshotPath(fileName);
            Assert.That(File.Exists(path), Is.True, $"Snapshot file not found: {path}");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Builds a game card in the shape of the live site (game-card anchor, /games/ href,
        /// data-tech, h3 title).
        /// </summary>
        private static string Card(string slug, string title, string tech)
        {
            return $@"<a class=""game-card"" href=""/games/{slug}/"" data-sort-updated=""1790640000000"" data-tech=""{tech}""> <h3 class=""game-card__title"">{title}</h3> </a>";
        }
    }
}
