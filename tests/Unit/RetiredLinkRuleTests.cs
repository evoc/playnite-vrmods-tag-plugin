using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for <see cref="RetiredLinkRule"/> — the mechanism by which the plugin removes a link
    /// row that one of its own older versions wrote and that current data can no longer re-derive.
    /// The whole value of a rule is that it fires for rows we wrote and for nothing else, so the
    /// rejections here matter more than the matches: every shape listed as rejected is a link
    /// belonging to the user or to another tool that sits in the same library.
    /// </summary>
    [TestFixture]
    public class RetiredLinkRuleTests
    {
        /// <summary>The rule the curator lists use, spelled out here so the tests read as the
        /// decision it is rather than as a string passed around.</summary>
        /// <summary>The shape an older curator version wrote: a store page whose App ID is the
        /// whole path segment. A rule that deletes rows in someone's library does not guess at
        /// near-misses like an id of "72a50", so the digits have to end at /, ?, # or the URL.</summary>
        private const string StoreAppShape = @"^https?://store\.steampowered\.com/app/\d+(?:[/?#]|$)";

        private const string CuratorName = "Steam Curator VorpX Games";

        private static RetiredLinkRule Rule() => new RetiredLinkRule(CuratorName, StoreAppShape, "test reason");

        /// <summary>The row an older curator version wrote: our name over a store page. Written
        /// under either scheme, since Playnite itself writes store links as http and the plugin
        /// followed whatever it scraped.</summary>
        [TestCase(CuratorName, "https://store.steampowered.com/app/72850/")]
        [TestCase(CuratorName, "https://store.steampowered.com/app/225260/Brutal_Legend/")]
        [TestCase(CuratorName, "http://store.steampowered.com/app/225260/Brutal_Legend/")]
        [TestCase(CuratorName, "https://store.steampowered.com/app/72850")]
        [TestCase(CuratorName, "https://store.steampowered.com/app/72850/?snr=1")]
        public void Matches_TheRowOurOwnOlderVersionWrote(string name, string url)
        {
            Assert.That(Rule().Matches(name, url), Is.True);
        }

        /// <summary>Names we do not control. Playnite's Steam import names its link "Steam", the
        /// metadata providers write "Store Page", and a user can name a link anything — none of
        /// them is ours to delete, however much the URL looks like one.</summary>
        [TestCase("Steam", "https://store.steampowered.com/app/72850/")]
        [TestCase("Store Page", "https://store.steampowered.com/app/72850/")]
        [TestCase("", "https://store.steampowered.com/app/72850/")]
        [TestCase(null, "https://store.steampowered.com/app/72850/")]
        [TestCase("Steam Curator VorpX", "https://store.steampowered.com/app/72850/")]
        [TestCase("Steam Curator VorpX Games Extra", "https://store.steampowered.com/app/72850/")]
        public void DoesNotMatchALinkNameWeDidNotWrite(string? name, string url)
        {
            Assert.That(Rule().Matches(name, url), Is.False);
        }

        /// <summary>Shapes that are not the one we wrote — including the curator page this source
        /// writes instead of the store page now (retiring that would undo the change that
        /// introduced it), a store path with no usable App ID, an age-check detour, and a URL that
        /// merely contains the store host behind an attacker's own.</summary>
        [TestCase(CuratorName, "https://store.steampowered.com/curator/38688993-VorpX-Games/")]
        [TestCase(CuratorName, "https://store.steampowered.com/app/")]
        [TestCase(CuratorName, "https://store.steampowered.com/app/72a50/")]
        [TestCase(CuratorName, "https://store.steampowered.com/app/72850evil/")]
        [TestCase(CuratorName, "https://store.steampowered.com/agecheck/app/286690/")]
        [TestCase(CuratorName, "https://store.steampowered.com/steamvr")]
        [TestCase(CuratorName, "https://evil.tld/store.steampowered.com/app/72850/")]
        [TestCase(CuratorName, "https://store.steampowered.com.evil.tld/app/72850/")]
        [TestCase(CuratorName, "https://other.example/app/72850/")]
        [TestCase(CuratorName, "steam://store.steampowered.com/app/72850/")]
        public void DoesNotMatchAShapeWeDidNotWrite(string name, string? url)
        {
            Assert.That(Rule().Matches(name, url), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void AnEmptyUrlOrNameNeverMatches(string? missing)
        {
            Assert.That(Rule().Matches(missing, "https://store.steampowered.com/app/1/"), Is.False);
            Assert.That(Rule().Matches(CuratorName, missing), Is.False);
        }

        /// <summary>A rule that retires "everything" is the failure mode this class exists to
        /// avoid, so an unnamed or unshaped rule is refused at construction rather than at the
        /// moment it deletes a user's links.</summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Construction_RequiresBothHalvesOfTheEvidence(string? missing)
        {
            Assert.That(
                () => new RetiredLinkRule(missing!, StoreAppShape, "reason"),
                Throws.ArgumentException);
            Assert.That(
                () => new RetiredLinkRule(CuratorName, missing!, "reason"),
                Throws.ArgumentException);
        }

        [Test]
        public void Why_IsCarriedForTheRunLog()
        {
            Assert.That(Rule().Why, Is.EqualTo("test reason"));
        }

        /// <summary>The curator source's own rule, checked against the real parser: exactly one
        /// rule, keyed to the display name that source registers under, retiring the store-page
        /// shape and nothing else. If a curator's display name ever changes, this test is where the
        /// old name has to be added as a second rule — otherwise the rows it wrote stay.</summary>
        [Test]
        public void SteamCuratorParser_RetiresItsStorePageUnderItsOwnNameOnly()
        {
            var parser = new SteamCuratorParser(
                "SteamCuratorTest",
                "Steam Curator VorpX Games",
                "https://store.steampowered.com/curator/38688993-VorpX-Games/",
                38688993,
                "VorpX");

            Assert.That(parser.RetiredLinks, Has.Count.EqualTo(1));
            var rule = parser.RetiredLinks[0];
            Assert.That(rule.LinkName, Is.EqualTo("Steam Curator VorpX Games"));
            Assert.That(rule.Matches("Steam Curator VorpX Games", "https://store.steampowered.com/app/35140/"), Is.True);
            Assert.That(rule.Matches("Steam", "https://store.steampowered.com/app/35140/"), Is.False);
            Assert.That(rule.Matches("Steam Curator VorpX Games", "https://store.steampowered.com/curator/38688993-VorpX-Games/"), Is.False);
        }

        /// <summary>A source that never changed what it links to retires nothing, so the reconcile
        /// pays nothing for the mechanism. Every shipped source but the curator lists is this one.</summary>
        [Test]
        public void ASourceThatNeverChangedItsLinks_RetiresNothing()
        {
            var parser = new CompoundVrParser();

            Assert.That(parser.RetiredLinks, Is.Empty);
        }
    }
}
