using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class Flat2VrParserTests
    {
        [Test]
        public void SourceId_ReturnsFlat2VR()
        {
            var parser = new Flat2VrParser();
            Assert.That(parser.SourceId, Is.EqualTo("Flat2VR"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new Flat2VrParser();
            Assert.That(parser.DisplayName, Is.EqualTo("Flat2VR Studios"));
        }

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            var parser = new Flat2VrParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new Flat2VrParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_StripsVrSuffixes()
        {
            var parser = new Flat2VrParser();
            var entry = new VrModEntry { GameName = "Skyrim VR" };
            var result = parser.GetCanonicalName(entry);
            Assert.That(result, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void GetCanonicalName_StripsFlat2VRSuffix()
        {
            var parser = new Flat2VrParser();
            var entry = new VrModEntry { GameName = "WRATH: AEON OF RUIN Flat2VR" };
            var result = parser.GetCanonicalName(entry);
            Assert.That(result, Is.EqualTo("WRATH: AEON OF RUIN"));
        }

        [Test]
        public void GetCanonicalName_KeepsNameWithoutSuffix()
        {
            var parser = new Flat2VrParser();
            var entry = new VrModEntry { GameName = "Some Game" };
            var result = parser.GetCanonicalName(entry);
            Assert.That(result, Is.EqualTo("Some Game"));
        }

        [Test]
        public void ParseHtml_ExtractsHTagTitles()
        {
            var parser = new Flat2VrParser();
            var html = "<h2>Skyrim</h2><h3>Fallout 4</h3>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim VR"));
            Assert.That(result[0].Category, Is.EqualTo("Flat2VR Port"));
            Assert.That(result[1].GameName, Is.EqualTo("Fallout 4 VR"));
            Assert.That(result[1].Category, Is.EqualTo("Flat2VR Port"));
        }

        [Test]
        public void ParseHtml_ExtractsTitleClassDivs()
        {
            var parser = new Flat2VrParser();
            var html = "<div class=\"game-title\">DOOM</div><div class=\"title\">Quake</div>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("DOOM VR"));
            Assert.That(result[1].GameName, Is.EqualTo("Quake VR"));
        }

        [Test]
        public void ParseHtml_ExtractsGameAnchorLinks()
        {
            var parser = new Flat2VrParser();
            var html = "<a class=\"game-link\">Skyrim</a>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim VR"));
        }

        [Test]
        public void ParseHtml_AppendsVRSuffix()
        {
            var parser = new Flat2VrParser();
            var html = "<h2>Skyrim</h2>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim VR"));
        }

        [Test]
        public void ParseHtml_SetsFlat2VRPortCategory()
        {
            var parser = new Flat2VrParser();
            var html = "<h2>Test Game</h2>";
            var result = parser.ParseHtml(html);

            Assert.That(result[0].Category, Is.EqualTo("Flat2VR Port"));
        }

        [Test]
        public void ParseHtml_StripsHTMLTagsFromTitles()
        {
            var parser = new Flat2VrParser();
            var html = "<h2><span class=\"bold\">Skyrim</span></h2>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim VR"));
        }

        [Test]
        public void ParseHtml_SkipsShortNames()
        {
            var parser = new Flat2VrParser();
            var html = "<h2>AB</h2><h2>Game</h2>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Game VR"));
        }

        [Test]
        public void ParseHtml_SkipsEmptyNames()
        {
            var parser = new Flat2VrParser();
            var html = "<h2></h2><h2>   </h2><h2>Skyrim</h2>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void ParseHtml_StripsVRSuffixBeforeAppending()
        {
            var parser = new Flat2VrParser();
            var html = "<h2>Skyrim VR</h2>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim VR"));
        }

        [Test]
        public void ParseHtml_EmptyHTML_ReturnsEmpty()
        {
            var parser = new Flat2VrParser();
            var result = parser.ParseHtml("");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseHtml_NoMatchingPatterns_ReturnsEmpty()
        {
            var parser = new Flat2VrParser();
            var html = "<p>Some random text</p><div>Another div</div>";
            var result = parser.ParseHtml(html);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseHtml_HandlesMixedCaseTitles()
        {
            var parser = new Flat2VrParser();
            var html = "<H2>Skyrim</H2><h3>Fallout 4</H3><div class=\"GAME-TITLE\">DOOM</div>";
            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(3));
        }

        /// <summary>Builds a game card in the shape of the live site (h3 title, card-image-link,
        /// Steam platform link).</summary>
        private static string Card(string title, string pageHref, string? steamHref)
        {
            var steam = string.IsNullOrEmpty(steamHref)
                ? string.Empty
                : $"<a href=\"{steamHref}\" target=\"_blank\" rel=\"noopener\" title=\"Steam VR\"><i class=\"fa-brands fa-steam\"></i></a>";
            return $@"<div class=""game-card"" data-platforms=""steam,metaquest"">
  <a href=""{pageHref}"" class=""card-image-link""><div class=""card-image""><img src=""x.jpg"" /></div></a>
  <div class=""card-footer"">
    <h3>{title}</h3>
    <div class=""card-platforms"">
      <a href=""#"" title=""Meta Quest""><i></i></a>
      {steam}
      <a href=""#"" title=""Steam Deck""><i></i></a>
    </div>
  </div>
</div>";
        }

        [Test]
        public void ParseHtml_GameCards_ExtractsNameDeepLinkAndSteamId()
        {
            var parser = new Flat2VrParser();
            var html = @"<html><body>" +
                Card("Wrath: Aeon of Ruin", "games/wrath.html", "https://store.steampowered.com/app/2436470/") +
                Card("High On Life VR", "games/high-on-life-vr.html", "https://store.steampowered.com/app/3951190/") +
                "</body></html>";

            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(2));

            var wrath = result[0];
            Assert.That(wrath.GameName, Is.EqualTo("Wrath: Aeon of Ruin VR"));
            Assert.That(wrath.Category, Is.EqualTo("Flat2VR Port"));
            Assert.That(wrath.SourceUrl, Is.EqualTo("https://flat2vrstudios.com/games/wrath.html"));
            Assert.That(wrath.SteamAppId, Is.EqualTo("2436470"));

            var hol = result[1];
            Assert.That(hol.GameName, Is.EqualTo("High On Life VR"));
            Assert.That(hol.SourceUrl, Is.EqualTo("https://flat2vrstudios.com/games/high-on-life-vr.html"));
            Assert.That(hol.SteamAppId, Is.EqualTo("3951190"));
        }

        [Test]
        public void ParseHtml_GameCard_ExternalPageHref_KeptAbsolute()
        {
            var parser = new Flat2VrParser();
            var html = Card("RoboQuest VR", "https://roboquestvr.com/", null);

            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SourceUrl, Is.EqualTo("https://roboquestvr.com/"));
            Assert.That(result[0].SteamAppId, Is.Null);
        }

        [Test]
        public void ParseHtml_GameCard_NoSteamLink_NullSteamAppId()
        {
            var parser = new Flat2VrParser();
            var html = Card("Postal 2 Redux", "https://postal2vr.com/", null);

            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SteamAppId, Is.Null);
        }

        [Test]
        public void ParseHtml_GameCards_DedupesByCleanedName()
        {
            var parser = new Flat2VrParser();
            var html = Card("Wrath: Aeon of Ruin", "games/wrath.html", null)
                     + Card("Wrath: Aeon of Ruin VR", "games/wrath.html", null);

            var result = parser.ParseHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void ParseHtml_GameCardWithoutTitle_Skipped()
        {
            var parser = new Flat2VrParser();
            var html = @"<div class=""game-card""><a href=""games/x.html"" class=""card-image-link""></a></div>";

            var result = parser.ParseHtml(html);

            Assert.That(result, Is.Empty);
        }
    }
}
