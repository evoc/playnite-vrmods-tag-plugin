using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class ReFrameworkParserTests
    {
        private const string SampleReadme =
            "# REFramework\r\n" +
            "\r\n" +
            "Generic 6DOF VR support for all games.\r\n" +
            "\r\n" +
            "## Supported Games\r\n" +
            "* Resident Evil 2\r\n" +
            "* Resident Evil 3\r\n" +
            "* Devil May Cry 5 (Using `RE8` build)\r\n" +
            "* Street Fighter 6\r\n" +
            "* Monster Hunter Stories 3\r\n" +
            "\r\n" +
            "## Thanks\r\n" +
            "Thanks to contributors.\r\n";

        [Test]
        public void SourceId_ReturnsReFramework()
        {
            var parser = new ReFrameworkParser();
            Assert.That(parser.SourceId, Is.EqualTo("ReFramework"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new ReFrameworkParser();
            Assert.That(parser.DisplayName, Is.EqualTo("REFramework"));
        }

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            var parser = new ReFrameworkParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new ReFrameworkParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_ReturnsGameName()
        {
            var parser = new ReFrameworkParser();
            var entry = new VrModEntry { GameName = "Resident Evil 2" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Resident Evil 2"));
        }

        [Test]
        public void ParseSupportedGames_ExtractsBulletList()
        {
            var parser = new ReFrameworkParser();
            var result = ReFrameworkParser.ParseSupportedGames(SampleReadme);

            Assert.That(result, Has.Count.EqualTo(5));
            Assert.That(result[0].GameName, Is.EqualTo("Resident Evil 2"));
            Assert.That(result[1].GameName, Is.EqualTo("Resident Evil 3"));
            Assert.That(result[2].GameName, Is.EqualTo("Devil May Cry 5"));
            Assert.That(result[3].GameName, Is.EqualTo("Street Fighter 6"));
            Assert.That(result[4].GameName, Is.EqualTo("Monster Hunter Stories 3"));
        }

        [Test]
        public void ParseSupportedGames_StripsCrossBuildNote()
        {
            var parser = new ReFrameworkParser();
            var result = ReFrameworkParser.ParseSupportedGames(SampleReadme);

            // "Devil May Cry 5 (Using `RE8` build)" -> "Devil May Cry 5"
            Assert.That(result[2].GameName, Is.EqualTo("Devil May Cry 5"));
        }

        [Test]
        public void ParseSupportedGames_SetsReFrameworkCategory()
        {
            var parser = new ReFrameworkParser();
            var result = ReFrameworkParser.ParseSupportedGames(SampleReadme);

            foreach (var entry in result)
                Assert.That(entry.Category, Is.EqualTo("REFramework"));
        }

        [Test]
        public void ParseSupportedGames_Sets6DOFWhenAdvertised()
        {
            var parser = new ReFrameworkParser();
            var result = ReFrameworkParser.ParseSupportedGames(SampleReadme);

            foreach (var entry in result)
                Assert.That(entry.Is6DOF, Is.True);
        }

        [Test]
        public void ParseSupportedGames_6DOFNullWhenNotAdvertised()
        {
            var parser = new ReFrameworkParser();
            var readme = "## Supported Games\r\n* Resident Evil 2\r\n";
            var result = ReFrameworkParser.ParseSupportedGames(readme);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Is6DOF, Is.Null);
        }

        [Test]
        public void ParseSupportedGames_OnlyMatchesSectionBullets()
        {
            var parser = new ReFrameworkParser();
            // Bullets outside the section (before it) must be ignored; the "## Thanks"
            // bullet must end the section.
            var readme =
                "* Not a supported game\r\n" +
                "## Supported Games\r\n" +
                "* Real Game\r\n" +
                "* Real Game 2\r\n" +
                "## Thanks\r\n" +
                "* Not in the section\r\n";

            var result = ReFrameworkParser.ParseSupportedGames(readme);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("Real Game"));
            Assert.That(result[1].GameName, Is.EqualTo("Real Game 2"));
        }

        [Test]
        public void ParseSupportedGames_MissingSection_ReturnsEmpty()
        {
            var parser = new ReFrameworkParser();
            var result = ReFrameworkParser.ParseSupportedGames("# REFramework\nNo supported games heading here.\n");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseSupportedGames_EmptyMarkdown_ReturnsEmpty()
        {
            var parser = new ReFrameworkParser();
            Assert.That(ReFrameworkParser.ParseSupportedGames(""), Is.Empty);
        }

        [Test]
        public void ParseSupportedGames_SkipsShortNames()
        {
            var parser = new ReFrameworkParser();
            var readme = "## Supported Games\r\n* AB\r\n* A Real Game\r\n";
            var result = ReFrameworkParser.ParseSupportedGames(readme);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("A Real Game"));
        }
    }
}
