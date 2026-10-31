using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class VorpXParserTests
    {
        // A realistic slice of the supported-games page: one <table> whose <td> cells each
        // hold a <br>-separated list of game names (the real page uses two cells). The rows
        // exercise the parsing rules: a plain <br>, a <br /> variant, an &amp; entity, and a
        // name that repeats across the two cells (dedup).
        private const string SampleTable =
            "<html><body>" +
            "<table>" +
            "  <tr>" +
            "    <td>Assassin's Creed<br>Dark Souls<br>The Witcher 3<br>Tom &amp; Jerry<br>Metro 2033</td>" +
            "    <td>Tomb Raider<br />Red Dead Redemption 2<br>Cyberpunk 2077<br>Assassin's Creed</td>" +
            "  </tr>" +
            "</table>" +
            "</body></html>";

        [Test]
        public void SourceId_ReturnsVorpX()
        {
            var parser = new VorpXParser();
            Assert.That(parser.SourceId, Is.EqualTo("VorpX"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new VorpXParser();
            Assert.That(parser.DisplayName, Is.EqualTo("VorpX"));
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new VorpXParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_ReturnsGameName()
        {
            var parser = new VorpXParser();
            var entry = new VrModEntry { GameName = "Dark Souls" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Dark Souls"));
        }

        [Test]
        public void ParseGames_ExtractsNamesAcrossBothCells()
        {
            var parser = new VorpXParser();
            var result = VorpXParser.ParseGames(SampleTable);

            // 5 names in cell 1 + 4 in cell 2, minus the one repeated name = 8 unique.
            Assert.That(result, Has.Count.EqualTo(8));
            Assert.That(result[0].GameName, Is.EqualTo("Assassin's Creed"));
            Assert.That(result.Select(e => e.GameName), Does.Contain("Dark Souls"));
            Assert.That(result.Select(e => e.GameName), Does.Contain("Cyberpunk 2077"));
        }

        [Test]
        public void ParseGames_UnescapesEntities()
        {
            var parser = new VorpXParser();
            var result = VorpXParser.ParseGames(SampleTable);

            // "Tom &amp; Jerry" -> "Tom & Jerry".
            Assert.That(result.Select(e => e.GameName), Does.Contain("Tom & Jerry"));
        }

        [Test]
        public void ParseGames_DecodesNumericTypographicEntities()
        {
            var parser = new VorpXParser();
            // Real page data uses numeric entities: &#8211; (em-dash) and &#8217; (smart quote).
            var html = "<table><tr><td>Aliens &#8211; Colonial Marines</td><td>Game&#8217;s</td></tr></table>";
            var result = VorpXParser.ParseGames(html);

            string emDash = new string((char)0x2014, 1);
            string smartQuote = new string((char)0x2019, 1);
            Assert.That(result[0].GameName, Is.EqualTo("Aliens " + emDash + " Colonial Marines"));
            Assert.That(result.Select(e => e.GameName), Does.Contain("Game" + smartQuote + "s"));
        }

        [Test]
        public void ParseGames_HandlesSelfClosingBrVariant()
        {
            var parser = new VorpXParser();
            // The <br /> variant (with a space) must separate names exactly like <br>.
            var html = "<table><tr><td>Game A<br />Game B</td></tr></table>";
            var result = VorpXParser.ParseGames(html);

            Assert.That(result.Select(e => e.GameName), Is.EquivalentTo(new[] { "Game A", "Game B" }));
        }

        [Test]
        public void ParseGames_DeduplicatesAcrossCells()
        {
            var parser = new VorpXParser();
            var result = VorpXParser.ParseGames(SampleTable);

            // "Assassin's Creed" appears in both cells but must be listed once.
            Assert.That(result.Count(e => e.GameName == "Assassin's Creed"), Is.EqualTo(1));
        }

        [Test]
        public void ParseGames_SetsVorpXCategoryAnd6DOF()
        {
            var parser = new VorpXParser();
            var result = VorpXParser.ParseGames(SampleTable);

            Assert.That(result, Is.Not.Empty);
            foreach (var entry in result)
            {
                Assert.That(entry.Category, Is.EqualTo("VorpX"));
                // VorpX's purpose is 6DoF head-tracking, so every supported game is flagged.
                Assert.That(entry.Is6DOF, Is.True);
            }
        }

        [Test]
        public void ParseGames_EmptyOrBlank_ReturnsEmpty()
        {
            var parser = new VorpXParser();
            Assert.That(VorpXParser.ParseGames(""), Is.Empty);
            Assert.That(VorpXParser.ParseGames(null), Is.Empty);
            Assert.That(VorpXParser.ParseGames("   "), Is.Empty);
        }

        [Test]
        public void ParseGames_NoTable_ReturnsEmpty()
        {
            var parser = new VorpXParser();
            // A page with no <table> block (e.g. a maintenance page) yields no entries.
            var html = "<html><body><p>Site temporarily unavailable.</p></body></html>";
            Assert.That(VorpXParser.ParseGames(html), Is.Empty);
        }
    }
}
