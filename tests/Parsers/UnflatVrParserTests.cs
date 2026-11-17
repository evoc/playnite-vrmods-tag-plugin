using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Tests for <see cref="UnflatVrParser"/>.
    ///
    /// Two rules hold this parser together, and both are tested against the recorded catalogue:
    /// <list type="bullet">
    /// <item><description>A game is only ever tagged for what the site's own platform token says.
    /// PCVR earns no tag of its own (the umbrella already says the game has a VR mod); "Quest" and
    /// "Steam Frame" earn <c>VRMod Quest</c> / <c>VRMod SteamFrame</c>; a token nobody measured earns
    /// nothing and is named on the run log.</description></item>
    /// <item><description>Nothing the site writes becomes a tag name directly. The only tag names this
    /// source can mint are the two headset tags, and a test asserts that over every hub in the
    /// recorded catalogue rather than over a handful of fixtures.</description></item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class UnflatVrParserTests
    {
        /// <summary>
        /// An excerpt of https://unflatvr.com/steam-library-check/ recorded on 2026-11-15. Each hub
        /// inside it is the byte-for-byte island the site served; only which hubs are present is ours,
        /// by the selection rule printed in the header comment of the file itself. The page's markup,
        /// styles, scripts and analytics are not reproduced here at all.
        /// Measured over the excerpt: 65 hubs, 85 mods (PCVR 62, Quest 16, Steam Frame 7), 53 hubs
        /// carrying a Steam App ID.
        /// </summary>
        private const string Snapshot = "unflatvr-steam-library-check.html";

        private static string SnapshotPath()
        {
            var dir = Path.GetDirectoryName(typeof(UnflatVrParserTests).Assembly.Location);
            return Path.GetFullPath(Path.Combine(dir!, "..", "..", "..", "Parsers", "snapshots", Snapshot));
        }

        private static string ReadSnapshot() => File.ReadAllText(SnapshotPath());

        /// <summary>An island holding exactly these hub JSON objects, in page markup.</summary>
        private static string PageWith(string hubsJson)
            => "<!doctype html><body><script type=\"application/json\" id=\"slc-data\">{\"g\":["
               + hubsJson + "]}</script></body>";

        // ── the source's identity ──────────────────────────────────────────────────────────────

        [Test]
        public void SourceId_ReturnsUnflatVR()
        {
            Assert.That(new UnflatVrParser().SourceId, Is.EqualTo("UnflatVR"));
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            Assert.That(new UnflatVrParser().ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_StripsVrSuffixes()
        {
            var entry = new VrModEntry { GameName = "Minecraft VR" };
            Assert.That(new UnflatVrParser().GetCanonicalName(entry), Is.EqualTo("Minecraft"));
        }

        // ── a page that is not the catalogue this parser reads ─────────────────────────────────

        [Test]
        public void ParseCatalog_NoIsland_ReturnsNothing()
        {
            var parser = new UnflatVrParser();

            foreach (var page in new string?[] { null, string.Empty, "<html><body>404</body></html>" })
            {
                var (entries, hubs, modsTotal, _, _, _) = parser.ParseCatalog(page);
                Assert.That(entries, Is.Empty);
                Assert.That(hubs, Is.EqualTo(0));
                Assert.That(modsTotal, Is.EqualTo(0));
            }
        }

        [Test]
        public void ParseCatalog_IslandIsNotJson_ReturnsNothing()
        {
            var parser = new UnflatVrParser();
            var (entries, _, _, _, _, _) = parser.ParseCatalog(PageWith("{\"g\":[ "));
            Assert.That(entries, Is.Empty);
        }

        [Test]
        public void ParseCatalog_IslandHasNoGamesArray_ReturnsNothing()
        {
            // The shape this parser reads is the whole contract: when the site renames "g", the run
            // says so instead of reporting an empty library.
            var parser = new UnflatVrParser();
            var (entries, _, _, _, _, _) = parser.ParseCatalog(
                "<script type=\"application/json\" id=\"slc-data\">{\"games\":[]}</script>");
            Assert.That(entries, Is.Empty);
        }

        // ── platform tokens are the only thing that decides a tag ──────────────────────────────

        [Test]
        public void ParseCatalog_PcvrMod_EarnsNoCategoryTagOfItsOwn()
        {
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Duke Nukem 3D\"],\"a\":\"434050\",\"h\":\"../vr/duke-nukem-3d/\","
                + "\"v\":[[\"EDuke VR\",\"../game/pcvr/eduke-vr-obesecatlord/\",\"PCVR\",\"B\"]]}"));

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Categories, Is.EquivalentTo(new[] { VrModCategories.UnflatVr }));
            Assert.That(VrModCategories.TagNamesFor(entries[0]), Is.Empty,
                "a PCVR mod adds no tag of its own; the umbrella covers it");
        }

        [Test]
        public void ParseCatalog_QuestOnlyHub_StillTagsTheGame()
        {
            // Owner decision (task 216): a standalone port is worth showing even though a PC library
            // cannot install it, so the hub is not dropped any more - it just says which headset.
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"0 A.D.\"],\"a\":\"\",\"h\":\"../vr/0-a-d/\","
                + "\"v\":[[\"0 A.D. VR\",\"../game/quest/0-ad-vr-sgtbilko76/\",\"Quest\",\"C\"]]}"));

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].SteamAppId, Is.Null, "the site writes \"\" when it cannot resolve the game");
            Assert.That(VrModCategories.TagNamesFor(entries[0]),
                Is.EquivalentTo(new[] { "VRMod Quest" }));
        }

        [Test]
        public void ParseCatalog_SteamFrameSpellingOnTheSite_BecomesTheSpacelessTag()
        {
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Duke Nukem 3D\"],\"a\":\"434050\",\"h\":\"../vr/duke-nukem-3d/\","
                + "\"v\":[[\"EDuke VR\",\"../game/pcvr/eduke-vr-obesecatlord/\",\"PCVR\",\"B\"],"
                + "[\"EDuke VR (Steam Frame)\",\"../game/frame/eduke-vr-steam-frame-obesecatlord/\",\"Steam Frame\",\"B\"]]}"));

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Categories,
                Is.EquivalentTo(new[] { VrModCategories.UnflatVr, VrModCategories.SteamFrame }));
            Assert.That(VrModCategories.TagNamesFor(entries[0]), Is.EquivalentTo(new[] { "VRMod SteamFrame" }));
        }

        [Test]
        public void ParseCatalog_HubWithBothHeadsetPlatforms_EarnsBothTags()
        {
            // 16 hubs in the recorded catalogue are exactly this (Halo: CE, GoldenEye 007, ...).
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"GoldenEye 007\"],\"a\":\"\",\"h\":\"../vr/goldeneye-007/\","
                + "\"v\":[[\"GE Q\",\"../game/quest/ge-quest-x/\",\"Quest\",\"C\"],"
                + "[\"GE F\",\"../game/frame/ge-frame-x/\",\"Steam Frame\",\"B\"]]}"));

            Assert.That(VrModCategories.TagNamesFor(entries[0]),
                Is.EquivalentTo(new[] { "VRMod Quest", "VRMod SteamFrame" }));
        }

        [Test]
        public void ParseCatalog_PlatformNobodyMeasured_UntaggedAndNamedOnce()
        {
            var (entries, _, _, _, _, unknown) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Some PICO Port\"],\"a\":\"\",\"h\":\"../vr/pico-game/\","
                + "\"v\":[[\"A\",\"../game/pico/a/\",\"PICO\",\"C\"],[\"B\",\"../game/pico/b/\",\"PICO\",\"C\"]]},{"
                + "\"n\":[\"Half-Life\"],\"a\":\"220\",\"h\":\"../vr/half-life/\","
                + "\"v\":[[\"HL VR\",\"../game/pcvr/hl-vr-x/\",\"PCVR\",\"A\"],[\"HL P\",\"../game/pico/hl-p/\",\"PICO\",\"D\"]]}"));

            // A hub whose only mods sit on an unread platform is not a row at all.
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].GameName, Is.EqualTo("Half-Life"));
            Assert.That(VrModCategories.TagNamesFor(entries[0]), Is.Empty);
            Assert.That(unknown, Is.EquivalentTo(new[] { "PICO" }), "named once, not once per mod");
        }

        // ── the recorded catalogue, hub for hub as the site served it ───────────────────────────

        [Test]
        public void ParseCatalog_RecordedSite_MatchesTheMeasuredCounts()
        {
            var (entries, hubs, modsTotal, byPlatform, hubsNoAppId, unknown) =
                new UnflatVrParser().ParseCatalog(ReadSnapshot());

            // Counted over the excerpt, not over the live site: see Snapshot for what it holds.
            Assert.That(hubs, Is.EqualTo(65));
            Assert.That(modsTotal, Is.EqualTo(85));
            Assert.That(byPlatform["PCVR"], Is.EqualTo(62));
            Assert.That(byPlatform["Quest"], Is.EqualTo(16));
            Assert.That(byPlatform["Steam Frame"], Is.EqualTo(7));
            Assert.That(hubsNoAppId, Is.EqualTo(65 - 53));
            Assert.That(unknown, Is.Empty, "every platform token on the site is one the parser knows");

            // Every hub in the excerpt carries at least one mod on a known platform, so every hub
            // becomes a row. A future site change that adds a hub with nothing to say drops out
            // of this count, which is the honest direction for it to fail in.
            Assert.That(entries, Has.Count.EqualTo(65));
        }

        [Test]
        public void ParseCatalog_RecordedSite_EveryEntryIsCatalogTaggedWithANumericAppId()
        {
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(ReadSnapshot());

            foreach (var entry in entries)
            {
                Assert.That(entry.GameName, Is.Not.Empty);
                Assert.That(entry.Categories, Does.Contain(VrModCategories.UnflatVr),
                    $"{entry.GameName}: every row from this source is catalog-tagged");
                Assert.That(entry.SteamAppId, Is.Null.Or.Matches(@"^\d+$"),
                    $"{entry.GameName}: App ID reaches the matcher as digits or not at all");
                foreach (var category in entry.Categories)
                    Assert.That(VrModCategories.IsKnownTagValue(category), Is.True,
                        $"{entry.GameName}: category \"{category}\" is outside the tag vocabulary");
            }
        }

        [Test]
        public void ParseCatalog_RecordedSite_CannotMintATagBeyondTheTwoHeadsetTags()
        {
            // The point of the vocabulary check, stated over every hub the site published rather
            // than over a fixture: whatever the site writes into a platform field, the only names
            // this source can put in a library are these two.
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(ReadSnapshot());

            var minted = entries.SelectMany(VrModCategories.TagNamesFor).Distinct()
                .OrderBy(x => x, System.StringComparer.Ordinal)
                .ToList();

            Assert.That(minted, Is.EquivalentTo(new[] { "VRMod Quest", "VRMod SteamFrame" }),
                "the site changed a platform token, or the parser grew a way to mint a name nobody vetted");
            Assert.That(minted, Is.All.Matches<string>(VrModCategories.IsValidTagName));
        }

        [Test]
        public void ParseCatalog_RecordedSite_TaggedGamesAreTheOnesTheSiteItselfCallsHeadset()
        {
            // Ground truth taken from the island, not from the parser: a game earns VRMod Quest iff
            // the site lists a Quest mod for it. Same for the frame.
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(ReadSnapshot());
            var byName = entries.ToDictionary(e => e.GameName, e => VrModCategories.TagNamesFor(e));

            var island = UnflatVrParser.ExtractIsland(ReadSnapshot());
            using (var doc = System.Text.Json.JsonDocument.Parse(island!))
            {
                foreach (var hub in doc.RootElement.GetProperty("g").EnumerateArray())
                {
                    var name = hub.GetProperty("n")[0].GetString()!;
                    var platforms = hub.GetProperty("v").EnumerateArray()
                        .Select(mod => mod[2].GetString()).ToList();

                    Assert.That(byName.TryGetValue(name, out var tags), Is.True, $"{name}: expected a row");
                    Assert.That(tags!.Contains("VRMod Quest"),
                        Is.EqualTo(platforms.Contains("Quest")), $"{name}: VRMod Quest");
                    Assert.That(tags.Contains("VRMod SteamFrame"),
                        Is.EqualTo(platforms.Contains("Steam Frame")), $"{name}: VRMod SteamFrame");
                }
            }
        }

        [Test]
        public void ParseCatalog_RecordedSite_DukeNukemShowsTheTwoPlatformsItReallyHas()
        {
            // The row the class doc quotes, kept honest by a test.
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(ReadSnapshot());
            var duke = entries.Single(e => e.GameName == "Duke Nukem 3D");

            Assert.That(duke.SteamAppId, Is.EqualTo("434050"));
            Assert.That(duke.Categories, Does.Contain(VrModCategories.SteamFrame));
            Assert.That(duke.AdditionalInfo, Does.Contain("EDuke VR (B, PCVR)"));
            Assert.That(duke.AdditionalInfo, Does.Contain("EDuke VR (Steam Frame) (B, Steam Frame)"));
        }

        [Test]
        public void ParseCatalog_RecordedSite_LinksStayOnTheSourceHostAndStayBounded()
        {
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(ReadSnapshot());

            foreach (var entry in entries)
            {
                Assert.That(entry.SourceUrls, Is.Not.Empty, $"{entry.GameName}: the hub page is always a link");
                Assert.That(entry.SourceUrls, Has.Count.LessThanOrEqualTo(UnflatVrParser.MaxDeepLinksPerEntry),
                    $"{entry.GameName}: the library Link list must not fill up with one entry per mod");
                foreach (var url in entry.SourceUrls)
                    Assert.That(url, Does.StartWith("https://unflatvr.com/"),
                        $"{entry.GameName}: {url} is not this source's own page");
            }

            // The hub page leads, because it is the page that describes the game.
            var duke = entries.Single(e => e.GameName == "Duke Nukem 3D");
            Assert.That(duke.SourceUrls[0], Is.EqualTo("https://unflatvr.com/vr/duke-nukem-3d/"));
        }

        // ── values that arrive in a shape the parser must not guess about ──────────────────────

        [Test]
        public void ParseCatalog_HubWithNoName_IsSkipped()
        {
            var (entries, hubs, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[],\"a\":\"1\",\"h\":\"../vr/a/\",\"v\":[[\"M\",\"../game/pcvr/a/\",\"PCVR\",\"A\"]]},"
                + "{\"n\":[\"Good\"],\"a\":\"2\",\"h\":\"../vr/b/\",\"v\":[[\"M\",\"../game/pcvr/b/\",\"PCVR\",\"A\"]]}"));

            Assert.That(hubs, Is.EqualTo(2), "the hub is counted even when its row is refused");
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].GameName, Is.EqualTo("Good"));
        }

        [Test]
        public void ParseCatalog_HubWithNoMods_IsNotARow()
        {
            var (entries, hubs, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Empty\"],\"a\":\"1\",\"h\":\"../vr/empty/\",\"v\":[]}"));

            Assert.That(hubs, Is.EqualTo(1));
            Assert.That(entries, Is.Empty, "a hub the site lists no mod for says nothing about this game");
        }

        [Test]
        public void ParseCatalog_ModRowThatIsNotFourStrings_IsSkipped()
        {
            var (entries, _, modsTotal, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Half-Life\"],\"a\":\"220\",\"h\":\"../vr/half-life/\","
                + "\"v\":[[\"HL VR\",\"../game/pcvr/hl-vr-x/\"],[\"Short\"],\"a string\","
                + "[\"OK\",\"../game/pcvr/ok/\",\"PCVR\",\"A\"]]}"));

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(modsTotal, Is.EqualTo(1), "only a row with all four fields counts as a mod");
            Assert.That(entries[0].AdditionalInfo, Does.Contain("OK"));
        }

        [Test]
        public void ParseCatalog_OffSourceHref_IsNotMadeIntoALink()
        {
            // An href in scraped text is not a statement about where this game lives: this value would
            // otherwise reach the library as a Link the shell can open.
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Half-Life\"],\"a\":\"220\",\"h\":\"../vr/half-life/\","
                + "\"v\":[[\"Evil\",\"https://example.com/download/\",\"PCVR\",\"A\"],"
                + "[\"Good\",\"../game/pcvr/hl-vr-x/\",\"PCVR\",\"B\"]]}"));

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].SourceUrls, Has.None.Contains("https://example.com/download/"));
            Assert.That(entries[0].SourceUrls, Has.Some.Contains("https://unflatvr.com/game/pcvr/hl-vr-x/"));
            // The same gate covers the uncapped listing set: an unsafe href must not sit on the entry
            // waiting for a later fetch stage to trust it.
            Assert.That(entries[0].ListingUrls, Has.None.Contains("https://example.com/download/"));
            Assert.That(entries[0].ListingUrls, Has.Some.Contains("https://unflatvr.com/game/pcvr/hl-vr-x/"));
        }

        [Test]
        public void ParseCatalog_NonNumericAppId_IsNotAnAppId()
        {
            foreach (var raw in new[] { "", "  ", "not-a-number", "-7", "GOG:1234" })
            {
                var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                    "{\"n\":[\"Half-Life\"],\"a\":\"" + raw + "\",\"h\":\"../vr/half-life/\","
                    + "\"v\":[[\"HL VR\",\"../game/pcvr/hl/\",\"PCVR\",\"A\"]]}"));

                Assert.That(entries, Has.Count.EqualTo(1));
                Assert.That(entries[0].SteamAppId, Is.Null, $"\"{raw}\" is not a Steam App ID");
            }
        }

        [Test]
        public void ParseCatalog_ManyMods_LinkCountIsCappedButEveryModIsStillNamed()
        {
            var mods = string.Join(",", Enumerable.Range(1, 8).Select(i =>
                "[\"Mod " + i + "\",\"../game/pcvr/m" + i + "/\",\"PCVR\",\"A\"]"));
            var (entries, _, modsTotal, byPlatform, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Quake\"],\"a\":\"2210\",\"h\":\"../vr/quake/\",\"v\":[" + mods + "]}"));

            Assert.That(modsTotal, Is.EqualTo(8));
            Assert.That(byPlatform["PCVR"], Is.EqualTo(8));
            Assert.That(entries[0].SourceUrls, Has.Count.EqualTo(UnflatVrParser.MaxDeepLinksPerEntry));
            // The review dialog still lists all eight: capping the Links must not hide the mods.
            for (int i = 1; i <= 8; i++)
                Assert.That(entries[0].AdditionalInfo, Does.Contain("Mod " + i));
            // And capping them must not lose them either: every listing the site published stays on
            // the entry, so a later fetch stage can address each of the pages it has.
            Assert.That(entries[0].ListingUrls, Has.Count.EqualTo(8));
        }

        [Test]
        public void ParseCatalog_ListingUrls_AreTheSitesOwnPagesInSiteOrder()
        {
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Duke Nukem 3D\"],\"a\":\"434050\",\"h\":\"../vr/duke-nukem-3d/\","
                + "\"v\":[[\"EDuke VR\",\"../game/pcvr/eduke-vr-obesecatlord/\",\"PCVR\",\"B\"],"
                + "[\"EDuke VR (Frame)\",\"../game/frame/eduke-vr-frame-obesecatlord/\",\"Steam Frame\",\"B\"]]}"));

            var urls = entries[0].ListingUrls;

            // Resolved off the catalogue URL like the Links are (a site-relative href is not a URL),
            // in the order the site writes them, and only the mod listings: the game's own hub page
            // is where the site lists nothing (measured), so it is not a page to go and read.
            Assert.That(urls, Is.EqualTo(new[]
            {
                "https://unflatvr.com/game/pcvr/eduke-vr-obesecatlord/",
                "https://unflatvr.com/game/frame/eduke-vr-frame-obesecatlord/"
            }));
        }

        [Test]
        public void ParseCatalog_ContentIdentity_MovesWhenOnlyAModPageUrlMoves()
        {
            // Same game, same mods, same grades and platforms — the site moved one listing. If the
            // content hash missed that, HasSourceChanged would report "nothing changed", the fetch
            // would be skipped, and the Link already written into the library would keep pointing at
            // the old address for the rest of the session.
            string Island(string frameSlug)
                => "{\"n\":[\"Duke Nukem 3D\"],\"a\":\"434050\",\"h\":\"../vr/duke-nukem-3d/\","
                   + "\"v\":[[\"EDuke VR\",\"../game/pcvr/eduke-vr-obesecatlord/\",\"PCVR\",\"B\"],"
                   + "[\"EDuke VR (Frame)\",\"" + frameSlug + "\",\"Steam Frame\",\"B\"]]}";

            var before = new UnflatVrParser().ParseCatalog(PageWith(Island("../game/frame/old-slug/"))).Entries;
            var after = new UnflatVrParser().ParseCatalog(PageWith(Island("../game/frame/new-slug/"))).Entries;

            // Everything the review dialog shows is identical…
            Assert.That(after[0].GameName, Is.EqualTo(before[0].GameName));
            Assert.That(after[0].AdditionalInfo, Is.EqualTo(before[0].AdditionalInfo));
            // …so it is the listing address alone that has to move the content identity.
            Assert.That(UnflatVrParser.BuildHashLines(after),
                Is.Not.EqualTo(UnflatVrParser.BuildHashLines(before)));
        }

        [Test]
        public void ParseCatalog_AliasArray_UsesTheFirstNameTheSiteWrites()
        {
            var (entries, _, _, _, _, _) = new UnflatVrParser().ParseCatalog(PageWith(
                "{\"n\":[\"Dead Space\",\"Dead Space (2008)\"],\"a\":\"16900\",\"h\":\"../vr/dead-space/\","
                + "\"v\":[[\"DS VR\",\"../game/pcvr/ds-vr-x/\",\"PCVR\",\"B\"]]}"));

            // An entry has one name and the matcher has no alias input, so only the site's own
            // heading can become the name.
            Assert.That(entries[0].GameName, Is.EqualTo("Dead Space"));
        }
    }
}
