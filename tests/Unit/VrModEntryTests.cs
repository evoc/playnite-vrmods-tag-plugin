using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    [TestFixture]
    public class VrModEntryTests
    {
        /// <summary>
        /// Entry deep links are written into the user's library as Playnite Links, and Playnite
        /// opens a Link with the OS shell without checking the scheme. VrModEntry therefore filters
        /// every assigned URL through WebUrl.IsSafeLinkUrl: only absolute http(s) URLs survive, and
        /// a hostile value is dropped (the entry keeps matching and tagging, it just owns no link).
        /// </summary>
        [TestCase("file:///C:/Windows/System32/calc.exe")]
        [TestCase("file:///etc/passwd")]
        [TestCase("javascript:alert(1)")]
        [TestCase("steam://rungameid/730")]
        [TestCase("ms-settings:windowsupdate")]
        [TestCase("ftp://example.com/mod.zip")]
        [TestCase("https://pcvr-central.com@evil.tld/skyrim")]
        [TestCase("data:text/html;base64,PHNjcmlwdD4=")]
        public void SourceUrl_HostileValue_IsRejected(string url)
        {
            var entry = new VrModEntry { GameName = "Skyrim", SourceUrl = url };

            Assert.That(entry.SourceUrl, Is.Null);
            Assert.That(entry.SourceUrls, Is.Empty);
        }

        [Test]
        public void SourceUrl_LegitDeepLink_IsKept()
        {
            var entry = new VrModEntry
            {
                GameName = "Skyrim",
                SourceUrl = "https://pcvrcentral.com/games/skyrim"
            };

            Assert.That(entry.SourceUrl, Is.EqualTo("https://pcvrcentral.com/games/skyrim"));
            Assert.That(entry.SourceUrls, Has.Count.EqualTo(1));
        }

        [Test]
        public void SourceUrls_MixedList_KeepsOnlySafeUrls()
        {
            var entry = new VrModEntry
            {
                GameName = "Skyrim",
                SourceUrls = new System.Collections.Generic.List<string>
                {
                    "file:///C:/Windows/System32/calc.exe",
                    "https://pcvrcentral.com/games/skyrim",
                    "steam://rungameid/730",
                    "https://evil.tld/a?next=" + new string('a', 3000)
                }
            };

            Assert.That(entry.SourceUrls, Is.EquivalentTo(new[] { "https://pcvrcentral.com/games/skyrim" }));
        }

        [Test]
        public void SourceUrls_DuplicateDeepLinks_AreDeduplicatedCaseInsensitively()
        {
            var entry = new VrModEntry
            {
                GameName = "Skyrim",
                SourceUrls = new System.Collections.Generic.List<string>
                {
                    "https://pcvrcentral.com/games/Skyrim",
                    "https://pcvrcentral.com/games/skyrim"
                }
            };

            Assert.That(entry.SourceUrls, Has.Count.EqualTo(1));
        }

        [Test]
        public void ToString_ReturnsFormattedName()
        {
            var entry = new VrModEntry
            {
                GameName = "Skyrim",
                Category = "Full Motion Controls"
            };

            Assert.That(entry.ToString(), Is.EqualTo("Skyrim [Full Motion Controls]"));
        }

        [Test]
        public void DefaultProperties_AreNonEmpty()
        {
            var entry = new VrModEntry();
            Assert.That(entry.GameName, Is.EqualTo(string.Empty));
            Assert.That(entry.Category, Is.EqualTo(string.Empty));
            Assert.That(entry.SteamAppId, Is.Null);
            Assert.That(entry.SourceUrl, Is.Null);
            Assert.That(entry.Author, Is.Null);
            Assert.That(entry.AdditionalInfo, Is.Null);
            Assert.That(entry.Quality, Is.Null);
        }

        [Test]
        public void ToString_WithAllProperties()
        {
            var entry = new VrModEntry
            {
                GameName = "DOOM",
                Category = "UEVR",
                SteamAppId = "12345",
                SourceUrl = "https://example.com",
                Author = "modder"
            };

            Assert.That(entry.ToString(), Is.EqualTo("DOOM [UEVR]"));
            Assert.That(entry.SteamAppId, Is.EqualTo("12345"));
        }

        [Test]
        public void ToString_IgnoresQuality()
        {
            var entry = new VrModEntry
            {
                GameName = "Resident Evil 2",
                Category = "REFramework",
                Quality = "Recommended"
            };

            // Quality is a facet tag, not part of the display string.
            Assert.That(entry.ToString(), Is.EqualTo("Resident Evil 2 [REFramework]"));
        }

        /// <summary>
        /// Every free-text field a source supplies is capped. A scraped field is attacker-chosen text
        /// that this plugin then stores per entry, so its size would otherwise be the site's choice.
        /// The cut is a truncation, not a rejection: an oversized value still matches and still tags
        /// its game, it simply stops being longer than the cap (unlike a deep link, where an unsafe
        /// value is dropped because an unsafe URL is worthless).
        /// </summary>
        [Test]
        public void ScrapedTextFields_AreTruncatedToTheirCaps()
        {
            var huge = new string('x', 5000);
            var entry = new VrModEntry
            {
                GameName = huge,
                Author = huge,
                AdditionalInfo = huge,
                SteamAppId = huge
            };

            Assert.That(entry.GameName.Length, Is.EqualTo(200), "GameName cap (Playnite's own name limit).");
            Assert.That(entry.Author.Length, Is.EqualTo(100), "Author cap.");
            Assert.That(entry.AdditionalInfo.Length, Is.EqualTo(400), "AdditionalInfo cap (longest scraped text).");
            Assert.That(entry.SteamAppId.Length, Is.EqualTo(32), "SteamAppId cap (a real App ID is a short number).");
            Assert.That(entry.Author.Substring(0, 5), Is.EqualTo("xxxxx"), "Truncation keeps the head of the value.");
        }

        /// <summary>
        /// The caps must not damage normal values, and must not turn "the source did not provide it"
        /// into an empty string: the duplicate-listing merge treats null as "still unset" and fills it
        /// from the next occurrence of the same game.
        /// </summary>
        [Test]
        public void ScrapedTextFields_ShortValuesAndNulls_AreStoredAsGiven()
        {
            var entry = new VrModEntry { Author = "ModderX", AdditionalInfo = "UEVR v12", SteamAppId = "1234567" };
            Assert.That(entry.Author, Is.EqualTo("ModderX"));
            Assert.That(entry.AdditionalInfo, Is.EqualTo("UEVR v12"));
            Assert.That(entry.SteamAppId, Is.EqualTo("1234567"));

            var empty = new VrModEntry();
            Assert.That(empty.Author, Is.Null);
            Assert.That(empty.AdditionalInfo, Is.Null);
            Assert.That(empty.SteamAppId, Is.Null);
        }
    }
}
