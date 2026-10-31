using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class MrSurvivorHubParserTests
    {
        // A realistic slice of the site's catalog table: a two-column <table> with a
        // header row (Game / Supported Platform(s)) and one data row per game. The
        // game name is wrapped in <b> and the second column holds store/platform info
        // (which is intentionally ignored). The rows exercise the cleaning rules: a
        // trailing " VR" suffix, a trailing " VRO" suffix, and an &amp; entity.
        private const string SampleTable =
            "<html><body>\n" +
            "<table>\n" +
            "  <tr>\n" +
            "    <th>Game</th>\n" +
            "    <th>Supported Platform(s)</th>\n" +
            "  </tr>\n" +
            "  <tr><td><b>Alien Isolation</b></td><td>Steam</td></tr>\n" +
            "  <tr><td><b>Cyberpunk 2077</b></td><td>Steam</td></tr>\n" +
            "  <tr><td><b>GTA San Andreas VR</b></td><td>Meta Quest</td></tr>\n" +
            "  <tr><td><b>The Simpsons: Hit &amp; Run VR</b></td><td>Meta Quest</td></tr>\n" +
            "  <tr><td><b>Ready or Not VRO</b></td><td>Steam</td></tr>\n" +
            "</table>\n" +
            "</body></html>";

        [Test]
        public void SourceId_ReturnsMrSurvivorHub()
        {
            var parser = new MrSurvivorHubParser();
            Assert.That(parser.SourceId, Is.EqualTo("MrSurvivorHub"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new MrSurvivorHubParser();
            Assert.That(parser.DisplayName, Is.EqualTo("Mr Survivor VR HUB"));
        }

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            var parser = new MrSurvivorHubParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new MrSurvivorHubParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_ReturnsGameName()
        {
            var parser = new MrSurvivorHubParser();
            var entry = new VrModEntry { GameName = "Alien Isolation" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Alien Isolation"));
        }

        [Test]
        public void ParseGames_ExtractsAndCleansNames()
        {
            var parser = new MrSurvivorHubParser();
            var result = MrSurvivorHubParser.ParseGames(SampleTable);

            Assert.That(result, Has.Count.EqualTo(5));
            Assert.That(result[0].GameName, Is.EqualTo("Alien Isolation"));
            Assert.That(result[1].GameName, Is.EqualTo("Cyberpunk 2077"));
            // "GTA San Andreas VR" -> trailing " VR" stripped.
            Assert.That(result[2].GameName, Is.EqualTo("GTA San Andreas"));
            // "The Simpsons: Hit &amp; Run VR" -> entity unescaped + trailing " VR" stripped.
            Assert.That(result[3].GameName, Is.EqualTo("The Simpsons: Hit & Run"));
            // "Ready or Not VRO" -> trailing " VRO" stripped.
            Assert.That(result[4].GameName, Is.EqualTo("Ready or Not"));
        }

        [Test]
        public void ParseGames_SetsMrSurvivorCategory()
        {
            var parser = new MrSurvivorHubParser();
            var result = MrSurvivorHubParser.ParseGames(SampleTable);

            foreach (var entry in result)
                Assert.That(entry.Category, Is.EqualTo("Mr Survivor"));
        }

        [Test]
        public void ParseGames_SkipsHeaderRow()
        {
            var parser = new MrSurvivorHubParser();
            var result = MrSurvivorHubParser.ParseGames(SampleTable);

            // The header <th> row has no <td>, so it is not counted; "Game" never appears.
            Assert.That(result, Has.Count.EqualTo(5));
            foreach (var entry in result)
                Assert.That(entry.GameName, Is.Not.EqualTo("Game"));
        }

        [Test]
        public void ParseGames_DeduplicatesByName()
        {
            var parser = new MrSurvivorHubParser();
            // "Cyberpunk 2077" and "Cyberpunk 2077 VR" clean to the same name and must not duplicate.
            var html =
                "<table>\n" +
                "  <tr><th>Game</th><th>Supported Platform(s)</th></tr>\n" +
                "  <tr><td><b>Cyberpunk 2077</b></td><td>Steam</td></tr>\n" +
                "  <tr><td><b>Cyberpunk 2077 VR</b></td><td>GOG</td></tr>\n" +
                "</table>";

            var result = MrSurvivorHubParser.ParseGames(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Cyberpunk 2077"));
        }

        [Test]
        public void ParseGames_EmptyOrBlank_ReturnsEmpty()
        {
            var parser = new MrSurvivorHubParser();
            Assert.That(MrSurvivorHubParser.ParseGames(""), Is.Empty);
            Assert.That(MrSurvivorHubParser.ParseGames(null), Is.Empty);
            Assert.That(MrSurvivorHubParser.ParseGames("   "), Is.Empty);
        }

        [Test]
        public void ParseGames_NoTable_ReturnsEmpty()
        {
            var parser = new MrSurvivorHubParser();
            // HTML that has no <table> block at all (e.g. a maintenance page) yields no entries.
            var html = "<html><body><p>Site temporarily unavailable.</p></body></html>";
            Assert.That(MrSurvivorHubParser.ParseGames(html), Is.Empty);
        }
    }
}
