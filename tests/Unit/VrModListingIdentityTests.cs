using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// What identifies a source listing in the user's saved decisions.
    ///
    /// A decision is a statement about a mapping ("this Playnite game is this listing"), and the
    /// settings store keys it by <see cref="VrModEntry.MakeDecisionKey(string, Guid)"/>: the
    /// listing's Steam App ID, else its own page URL, else - only when the site offers nothing
    /// else - its display name, plus the game's Id. These tests pin both halves of that rule: what
    /// a rename no longer touches, and what it deliberately still does touch.
    /// </summary>
    [TestFixture]
    public class VrModListingIdentityTests
    {
        private const string SourceId = "test-pcvr";
        private const string SkyrimUrl = "https://pcvrcentral.com/game/skyrim";

        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IPlayniteAPI _api = null!;

        [SetUp]
        public void SetUp()
        {
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();

            var db = Substitute.For<IGameDatabaseAPI>();
            db.Games.Returns(_games);
            db.Tags.Returns(_tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());

            _api = Substitute.For<IPlayniteAPI>();
            _api.Database.Returns(db);
        }

        // --- the identity component itself -----------------------------------------------------

        [Test]
        public void DecisionKeyComponent_PrefersAppIdThenPageUrlThenName()
        {
            var withAppId = new VrModEntry
            {
                GameName = "Skyrim VR",
                SteamAppId = "1740790",
                SourceUrl = SkyrimUrl
            };
            var withUrlOnly = new VrModEntry { GameName = "Skyrim VR", SourceUrl = SkyrimUrl };
            var nameOnly = new VrModEntry { GameName = "Skyrim VR" };

            // An App ID wins even though the listing also has a page: a site rewriting its own
            // slugs (or a slug derived from the title that gets renamed) must not cost a decision.
            Assert.That(withAppId.DecisionKeyComponent, Is.EqualTo("steam:1740790"));
            Assert.That(withUrlOnly.DecisionKeyComponent, Is.EqualTo("url:pcvrcentral.com/game/skyrim"));
            Assert.That(nameOnly.DecisionKeyComponent, Is.EqualTo("Skyrim VR"));
        }

        [Test]
        public void DecisionKeyComponent_UrlIdentityIgnoresSchemeQueryAndTrailingSlash()
        {
            var http = new VrModEntry { GameName = "Skyrim VR", SourceUrl = "http://pcvrcentral.com/game/skyrim/" };
            var https = new VrModEntry { GameName = "Skyrim VR", SourceUrl = "https://www.pcvrcentral.com/game/skyrim/?ref=rss" };

            // A site moving to HTTPS, adding a tracking parameter or shortening a slug's trailing
            // slash is not a different listing, so it must not read as one.
            Assert.That(http.DecisionKeyComponent, Is.EqualTo("url:pcvrcentral.com/game/skyrim"));
            Assert.That(https.DecisionKeyComponent, Is.EqualTo("url:www.pcvrcentral.com/game/skyrim"));
        }

        [Test]
        public void DecisionKeyComponent_UrlIdentityDropsAnUnsafeUrlAndFallsBackToTheName()
        {
            // SourceUrls is gated by WebUrl.IsSafeLinkUrl; an unsafe scraped link never becomes
            // part of a key (it would otherwise smuggle a scheme into the identity).
            var entry = new VrModEntry { GameName = "Skyrim VR" };
            entry.SourceUrls = new List<string> { "steam://rungameid/1740790" };
            Assert.That(entry.SourceUrls, Is.Empty);
            Assert.That(entry.DecisionKeyComponent, Is.EqualTo("Skyrim VR"));
        }

        [Test]
        public void DecisionKeyComponent_IsRecomputedWhenTheListingFieldsChange()
        {
            var entry = new VrModEntry { GameName = "Skyrim VR", SourceUrl = SkyrimUrl };
            var asUrl = entry.DecisionKeyComponent;

            entry.SteamAppId = "1740790";
            Assert.That(entry.DecisionKeyComponent, Is.EqualTo("steam:1740790").And.Not.EqualTo(asUrl));

            entry.SteamAppId = null;
            Assert.That(entry.DecisionKeyComponent, Is.EqualTo(asUrl));
        }

        [Test]
        public void TwoListingsOfOneGame_GetDifferentKeys()
        {
            // The reason the key is not the Steam App ID alone: PCVR Central publishes several mod
            // listings for one base game, and a decision on one of them must not decide the other.
            var seMod = new VrModEntry { GameName = "Skyrim SE VR", SourceUrl = "https://pcvrcentral.com/game/skyrim" };
            var bigSkyrim = new VrModEntry { GameName = "Big Skyrim Mod", SourceUrl = "https://pcvrcentral.com/mods/big-skyrim" };
            var gameId = Guid.NewGuid();

            Assert.That(seMod.MakeDecisionKey(SourceId, gameId),
                Is.Not.EqualTo(bigSkyrim.MakeDecisionKey(SourceId, gameId)));
        }

        // --- a rename that the identity absorbs ---------------------------------------------------

        [Test]
        public void RenameOfAUrlIdentifiedListing_KeepsTheSameDecisionKey()
        {
            var gameId = Guid.NewGuid();
            var before = new VrModEntry { GameName = "Skyrim", SourceUrl = SkyrimUrl, SourceId = SourceId };
            var after = new VrModEntry { GameName = "Skyrim VR Complete Edition", SourceUrl = SkyrimUrl, SourceId = SourceId };

            Assert.That(after.MakeDecisionKey(SourceId, gameId), Is.EqualTo(before.MakeDecisionKey(SourceId, gameId)));
        }

        [Test]
        public void Scan_HonoursADecisionAcrossARename_AndStillTagsTheGame()
        {
            var game = AddGame("Skyrim");
            var parser = new StubParser(SourceId, new VrModEntry
            {
                GameName = "Skyrim",
                Category = "Full Motion Controls",
                SourceUrl = SkyrimUrl
            });
            var settings = new VrModTagSettings();
            var service = NewService(settings, parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            var listing = parser.Entries[0];
            settings.SetDecisionForListing(listing, SourceId, game.Id, MatchDecision.Accepted);

            // The site renames the listing; the page and the mod are the same.
            listing.GameName = "Skyrim VR";
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(settings.GetDecisionForListing(listing, SourceId, game.Id), Is.EqualTo(MatchDecision.Accepted));
            AssertThatGameIsTagged(game, VrModCategories.TagUmbrella);
        }

        [Test]
        public void Scan_MovesANameKeyedDecisionOfAnEarlierVersionOntoTheIdentityKey()
        {
            var game = AddGame("Skyrim");
            var parser = new StubParser(SourceId, new VrModEntry
            {
                GameName = "Skyrim",
                Category = "Full Motion Controls",
                SourceUrl = SkyrimUrl
            });
            var settings = new VrModTagSettings();
            var service = NewService(settings, parser);
            var listing = parser.Entries[0];

            // The store as an earlier version of the plugin left it: one decision, keyed by the
            // listing name, with a timestamp and a tag baseline of its own. Nothing at the
            // identity key yet - that key did not exist in the older version.
            var legacyKey = DecisionKeys.Named(SourceId, "Skyrim", game.Id);
            settings.SetDecisionByKey(legacyKey, MatchDecision.Accepted, VrModCategories.TagUmbrella);
            var stampedAt = settings.EnsureDecisionTimestampUtc(legacyKey, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));

            var identityKey = listing.MakeDecisionKey(SourceId, game.Id);
            Assert.That(identityKey, Is.Not.EqualTo(legacyKey));

            service.RunFullScanAsync().GetAwaiter().GetResult();

            // Read at the identity key, and nothing left behind at the name key to double-count.
            Assert.That(settings.GetDecisionByKey(identityKey), Is.EqualTo(MatchDecision.Accepted));
            Assert.That(settings.GetDecisionByKey(legacyKey), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetSourceTags(identityKey), Is.EquivalentTo(new[] { VrModCategories.TagUmbrella }));
            Assert.That(settings.GetDecisionTimestampUtc(identityKey), Is.EqualTo(stampedAt));
            Assert.That(settings.GetDecisionsForGame(game.Id), Has.Count.EqualTo(1));
        }

        [Test]
        public void Scan_RenameAfterAMigrationStillKeepsTheDecision()
        {
            var game = AddGame("Skyrim");
            var parser = new StubParser(SourceId, new VrModEntry
            {
                GameName = "Skyrim",
                Category = "Full Motion Controls",
                SourceUrl = SkyrimUrl
            });
            var settings = new VrModTagSettings();
            var service = NewService(settings, parser);
            var listing = parser.Entries[0];
            settings.SetDecisionByKey(DecisionKeys.Named(SourceId, "Skyrim", game.Id),
                MatchDecision.Accepted, VrModCategories.TagUmbrella);

            service.RunFullScanAsync().GetAwaiter().GetResult();   // migrates the key
            listing.GameName = "Skyrim VR";       // then the site renames it
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(settings.GetDecisionForListing(listing, SourceId, game.Id), Is.EqualTo(MatchDecision.Accepted));
        }

        // --- the rename that deliberately goes back through review --------------------------------

        [Test]
        public void RenameOfANameOnlyListing_ReturnsTheMappingToReview()
        {
            // A bare name list (VorpX, MrNlceHub, ...) offers nothing but the name, so nothing in
            // the response separates "renamed" from "removed and something else added". Re-deciding
            // is the safe outcome, and this test is the guard that it stays that way.
            var game = AddGame("Skyrim");
            var parser = new StubParser("test-vorpx", new VrModEntry
            {
                GameName = "Skyrim",
                Category = "VorpX"
            });
            var settings = new VrModTagSettings();
            var service = NewService(settings, parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            var listing = parser.Entries[0];
            settings.SetDecisionForListing(listing, "test-vorpx", game.Id, MatchDecision.Accepted);

            listing.GameName = "Skyrim VR";
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(settings.GetDecisionForListing(listing, "test-vorpx", game.Id),
                Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void Migration_RunsOnce_AndASecondScanChangesNothing()
        {
            // The migration is not a per-scan rewrite: it moves each mapping once, and the scans
            // afterwards compute identity keys and find them already there. A second scan that kept
            // moving keys would mean every scan rewrote stored decisions.
            var game = AddGame("Hell Let Loose");
            var parser = new StubParser("test-source", new VrModEntry
            {
                GameName = "Hell Let Loose",          // name-keyed today, but the listing has an App ID
                SteamAppId = "436620"
            });
            var settings = new VrModTagSettings();
            settings.SetDecision("test-source", "Hell Let Loose", game.Id, MatchDecision.Accepted);
            var legacyKey = DecisionKeys.Named("test-source", "Hell Let Loose", game.Id);
            var identityKey = "test-source||steam:436620||" + game.Id;
            var stampedAt = settings.EnsureDecisionTimestampUtc(
                legacyKey, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var service = NewService(settings, parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(settings.GetDecisionByKey(identityKey), Is.EqualTo(MatchDecision.Accepted),
                "the scan moved the decision onto the listing identity");
            Assert.That(settings.GetDecisionTimestampUtc(identityKey), Is.EqualTo(stampedAt));

            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(settings.GetDecisionByKey(identityKey), Is.EqualTo(MatchDecision.Accepted));
            Assert.That(settings.GetDecisionTimestampUtc(identityKey), Is.EqualTo(stampedAt),
                "the second scan did not re-stamp or re-move it");
            Assert.That(settings.SnapshotMatchDecisions().Count,
                Is.EqualTo(1), "no second key for the same mapping");
        }

        // --- the review dialog writes the mapping the scan found ----------------------------------

        [Test]
        public void ReviewDialogRecordsUnderTheRowsKey_NotUnderItsDisplayedName()
        {
            var game = new Game("Skyrim VR") { Id = Guid.NewGuid(), TagIds = new List<Guid>() };
            _games.Items.Add(game);
            var settings = new VrModTagSettings();

            var row = new VrModReviewMatchEntry
            {
                SourceId = SourceId,
                SourceGameName = "Skyrim",                     // what the user reads
                DecisionKey = "test-pcvr|url:pcvrcentral.com/game/skyrim|" + game.Id,
                PlayniteGame = game,
                GameDisplayName = game.Name,
                Categories = new List<string> { "UEVR" },
                Category = "Full Motion Controls",
                Status = MatchDecision.Accepted
            };

            VrModReviewDialog.ApplyDecisions(_api, settings,
                new List<VrModReviewMatchEntry> { row });

            Assert.That(settings.GetDecisionByKey(row.DecisionKey), Is.EqualTo(MatchDecision.Accepted));
            Assert.That(settings.GetDecisionByKey(DecisionKeys.Named(SourceId, "Skyrim", game.Id)),
                Is.EqualTo(MatchDecision.Pending),
                "accepting a row must not leave a second decision keyed by the listing's name");
        }

        // --- fixture ------------------------------------------------------------------------------

        private VrModTagService NewService(VrModTagSettings settings, StubParser parser)
        {
            var registry = new VrModSourceRegistry();
            registry.Register(parser);
            return new VrModTagService(_api, settings, new NullLogger(), registry);
        }

        private Game AddGame(string name)
        {
            var game = new Game(name) { Id = Guid.NewGuid(), TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            return game;
        }

        private void AssertThatGameIsTagged(Game game, string tagName)
        {
            var names = game.TagIds.Select(id => _tags.FirstOrDefault(t => t.Id == id)?.Name).ToList();
            Assert.That(names, Does.Contain(tagName), $"game tags: {string.Join(", ", names)}");
        }

        /// <summary>A source whose published listings a test can rename mid-test.</summary>
        private class StubParser : BaseVrModParser
        {
            public List<VrModEntry> Entries { get; }

            public StubParser(string sourceId, params VrModEntry[] entries)
                : base(sourceId, "Stub Source", "https://pcvrcentral.com/")
            {
                Entries = entries.ToList();
                foreach (var entry in Entries)
                    entry.SourceId = sourceId;
            }

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(Entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
