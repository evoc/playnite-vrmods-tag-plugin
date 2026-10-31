using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class MrNlceHubParserTests
    {
        // A realistic slice of the hub's root README. The "Supported games" <details>
        // block lists six titles in the exact form the repo uses (a "VR" suffix and a
        // "(motion controls)" / "[EARLY WIP]" annotation on many). A seventh bullet after
        // the closing </details> proves the parse is scoped to the block, not the file.
        private const string SampleReadme =
            "# PCVR Mods Installer Hub\n" +
            "\n" +
            "# Overview\n" +
            "Some intro text that is not a game list.\n" +
            "\n" +
            "## Supported games\n" +
            "\n" +
            "<details>\n" +
            "<summary>Show all currently listed titles (6)</summary>\n" +
            "\n" +
            "* 7 Days to Die VR (motion controls)\n" +
            "* Cyberpunk 2077 VR (motion controls)\n" +
            "* Assassin's Creed Mirage\n" +
            "* ARMA 3 [EARLY WIP]\n" +
            "* Call of Duty 4: Modern Warfare (2007) (motion controls)\n" +
            "* Alba VR\n" +
            "\n" +
            "</details>\n" +
            "\n" +
            "## Other\n" +
            "* Trailing Bullet Should Be Ignored\n" +
            "Trailing text.\n";

        [Test]
        public void SourceId_ReturnsMrNlceHub()
        {
            var parser = new MrNlceHubParser();
            Assert.That(parser.SourceId, Is.EqualTo("MrNlceHub"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new MrNlceHubParser();
            Assert.That(parser.DisplayName, Is.EqualTo("PCVR Mods Installer Hub"));
        }

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            var parser = new MrNlceHubParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new MrNlceHubParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_ReturnsGameName()
        {
            var parser = new MrNlceHubParser();
            var entry = new VrModEntry { GameName = "Cyberpunk 2077" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Cyberpunk 2077"));
        }

        [Test]
        public void ParseSupportedGames_ExtractsAndCleansNames()
        {
            var parser = new MrNlceHubParser();
            var result = parser.ParseSupportedGames(SampleReadme);

            Assert.That(result, Has.Count.EqualTo(6));
            Assert.That(result[0].GameName, Is.EqualTo("7 Days to Die"));
            Assert.That(result[1].GameName, Is.EqualTo("Cyberpunk 2077"));
            Assert.That(result[2].GameName, Is.EqualTo("Assassin's Creed Mirage"));
            Assert.That(result[3].GameName, Is.EqualTo("ARMA 3"));
            Assert.That(result[4].GameName, Is.EqualTo("Call of Duty 4: Modern Warfare (2007)"));
            Assert.That(result[5].GameName, Is.EqualTo("Alba"));
        }

        [Test]
        public void ParseSupportedGames_SetsMrNlceHubCategory()
        {
            var parser = new MrNlceHubParser();
            var result = parser.ParseSupportedGames(SampleReadme);

            foreach (var entry in result)
                Assert.That(entry.Category, Is.EqualTo("PCVR Mods Hub"));
        }

        [Test]
        public void ParseSupportedGames_ExcludesBulletsOutsideTheDetailsBlock()
        {
            var parser = new MrNlceHubParser();
            var result = parser.ParseSupportedGames(SampleReadme);

            // The "## Other" bullet after </details> must not appear; the sample has seven
            // bullets total, so exactly six entries proves the trailing one was excluded.
            Assert.That(result, Has.Count.EqualTo(6));
            foreach (var entry in result)
                Assert.That(entry.GameName, Is.Not.EqualTo("Trailing Bullet Should Be Ignored"));
        }

        [Test]
        public void ParseSupportedGames_DropsParenthesizedSubEntries()
        {
            var parser = new MrNlceHubParser();
            var readme =
                "<details>\n" +
                "<summary>Show all currently listed titles (2)</summary>\n" +
                "* Dolphin\n" +
                "* (Dolphin VR + Redux)\n" +
                "</details>\n";

            var result = parser.ParseSupportedGames(readme);

            // The parenthesised sub-entry is not a real title and is dropped.
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Dolphin"));
        }

        [Test]
        public void ParseSupportedGames_DeduplicatesByName()
        {
            var parser = new MrNlceHubParser();
            // "Alba VR" and "Alba" clean to the same name and must not duplicate.
            var readme =
                "<details>\n" +
                "<summary>Show all currently listed titles (2)</summary>\n" +
                "* Alba VR\n" +
                "* Alba\n" +
                "</details>\n";

            var result = parser.ParseSupportedGames(readme);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Alba"));
        }

        [Test]
        public void ParseSupportedGames_EmptyOrBlank_ReturnsEmpty()
        {
            var parser = new MrNlceHubParser();
            Assert.That(parser.ParseSupportedGames(""), Is.Empty);
            Assert.That(parser.ParseSupportedGames(null), Is.Empty);
            Assert.That(parser.ParseSupportedGames("   "), Is.Empty);
        }

        [Test]
        public void ParseSupportedGames_NoListedTitlesMarker_ReturnsEmpty()
        {
            var parser = new MrNlceHubParser();
            // Bullets exist but the "listed titles" marker is absent, so nothing is parsed.
            var readme = "# Hub\n\n## Supported games\n\n* Some Game\n* Another\n";
            Assert.That(parser.ParseSupportedGames(readme), Is.Empty);
        }
    }
}
