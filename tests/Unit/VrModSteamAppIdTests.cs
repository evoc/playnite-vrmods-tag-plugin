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
    /// What this plugin knows about the claim "this listing and this library game are the same Steam
    /// product", which is a different claim from "these two names are similar".
    ///
    /// Three rules are pinned here, because each one changed a decision this plugin makes:
    ///
    /// - a game's App ID is read from its store links <b>tolerantly</b>. The old rule was "the link
    ///   starts with https://store.steampowered.com/app/, then take the rest of the string", and the
    ///   rest of the string was the store's own slug or a tracking parameter. Measured on one real
    ///   library: 171 of its 1 208 Steam links produced a key that could never equal the id a parser
    ///   scraped, so those games were matchable by name only. The same rule now also reads
    ///   <c>Game.GameId</c>, which is the copy Playnite's own Steam import wrote and which survives a
    ///   user editing the links.
    /// - an App ID is a key, not a bucket. One App ID can be claimed by several Game rows (store
    ///   duplicates, a GOTY re-release), and every one of them is a candidate — which is what the name
    ///   index has always done for a shared title.
    /// - an App ID that <b>disagrees</b> refuses the pairing. Two entries with the same name and
    ///   different Steam product ids are two products; the name rules cannot see that, and before this
    ///   rule the plugin tagged the wrong game. Silence on either side is not disagreement: a name-only
    ///   source (VorpX, LukeRoss, …) and a game without a Steam link still match by name.
    ///
    /// The refusal is not silent. A pair the names argue for and the ids reject comes back as a review
    /// row that names both ids, ranked behind everything the plugin would itself act on.
    /// </summary>
    [TestFixture]
    public class VrModSteamAppIdTests
    {
        private const string SourceId = "test-appid-source";

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

        // --- reading an App ID out of a game's store link -----------------------------------------

        [TestCase("https://store.steampowered.com/app/434120/Resident_Evil_4", "434120")]
        [TestCase("https://store.steampowered.com/app/2443190/Pine_Creek_The_Book_of_Judith?beta=1", "2443190")]
        [TestCase("https://steampowered.com/app/1160220/Paradise_Killer", "1160220")]
        [TestCase("https://store.steampowered.com/app/1292010", "1292010")]
        [TestCase("https://store.steampowered.com/app/2521050/Outlast_2_Trageous_Hallows_Mod/", "2521050")]
        [TestCase("store.steampowered.com/app/286690/Superhot/", "286690")]
        [TestCase("http://store.steampowered.com/app/220110/Half-Life_Alyx/", "220110")]
        [TestCase("https://store.steampowered.com/agecheck/app/286690/", "286690")]
        [TestCase("https://store.steampowered.com/app/1292010/#reviews", "1292010")]
        [TestCase("https://store.steampowered.com/app/00434120/Whatever/", "434120")]
        [TestCase("  https://store.steampowered.com/app/72850/  ", "72850")]
        public void SteamStoreLink_IsReadAsTheProductItPointsAt(string url, string expectedAppId)
        {
            Assert.That(VrModTagService.TryGetSteamAppIdFromLink(url, out var appId), Is.True);
            Assert.That(appId, Is.EqualTo(expectedAppId));
        }

        [TestCase("https://store.steampowered.com/steamvr")]
        [TestCase("https://store.steampowered.com/app/")]
        [TestCase("https://store.steampowered.com/appreviewzone/12345/")]
        [TestCase("https://store.steampowered.com/search/?term=app%2F12345")]
        [TestCase("https://store.steampowered.com/app/12345678901234/")]
        [TestCase("https://store.steampowered.com/app/12a34/")]
        [TestCase("https://store.steampowered.com/app/0/")]
        [TestCase("https://notsteampowered.com/app/12345/")]
        [TestCase("https://steampowered.com.cn/app/12345/")]
        [TestCase("https://steamcommunity.com/linkfilter/?u=https%3A%2F%2Fstore.steampowered.com%2Fapp%2F12345%2F")]
        [TestCase("steam://rungameid/12345")]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void NothingElseOnASteamFlavouredPage_IsTreatedAsAnAppId(string? url)
        {
            Assert.That(VrModTagService.TryGetSteamAppIdFromLink(url, out var appId), Is.False,
                $"read {appId ?? "<null>"} out of {url ?? "<null>"}");
            Assert.That(appId, Is.Null);
        }

        [TestCase("434120", "434120")]
        [TestCase(" 434120 ", "434120")]
        [TestCase("00434120", "434120")]
        [TestCase("434120x", null)]
        [TestCase("", null)]
        [TestCase("0", null)]
        [TestCase("4341200000000", null)]
        [TestCase(null, null)]
        public void OnlyAnAllDigitProviderId_IsAnAppId(string? gameId, string? expectedAppId)
        {
            var ok = VrModTagService.TryGetSteamAppIdFromProviderId(gameId, out var appId);
            Assert.That(ok, Is.EqualTo(expectedAppId != null), $"shape decided for '{gameId}': {appId ?? "<null>"}");
            Assert.That(appId, Is.EqualTo(expectedAppId));
        }

        // --- a library game's own App IDs ---------------------------------------------------------

        [Test]
        public void TwoGameRowsClaimingOneAppId_BothBecomeCandidates()
        {
            // 55 App IDs in one real library are claimed by more than one Game row. With one slot per
            // App ID, whichever row was enumerated first was the only reachable one - the store
            // duplicate, the GOTY variant, could never be tagged whatever the user wanted.
            var original = AddGame("Resident Evil 4", "https://store.steampowered.com/app/434120/Resident_Evil_4");
            var duplicate = AddGame("Resident Evil 4 (Store duplicate)", "https://store.steampowered.com/app/434120/");
            var listing = new VrModEntry { GameName = "RE4 Kosh Arenas", SteamAppId = "434120" };

            var service = NewService(new VrModTagSettings(), new StubParser(SourceId, listing));
            RunFullScan(service);

            var matches = service.FindGamesByIndex(listing);
            Assert.That(matches.Select(g => g.Id),
                Is.EquivalentTo(new[] { original.Id, duplicate.Id }));
        }

        [Test]
        public void APairReachedByTwoRoutes_IsCountedOnce()
        {
            // The App ID route and the saved-decision route both point at the same game. A second
            // candidate for one pairing is a second review row for one fact, and the duplicate skipped
            // the tags baseline the first row carried.
            var game = AddGame("Resident Evil 4", "https://store.steampowered.com/app/434120/Resident_Evil_4");
            var listing = new VrModEntry { GameName = "Resident Evil 4", SteamAppId = "434120" };
            var settings = new VrModTagSettings();
            var service = NewService(settings, new StubParser(SourceId, listing));

            RunFullScan(service);
            settings.SetDecisionByKey(DecisionKeys.ForListing(listing, SourceId, game.Id),
                MatchDecision.Accepted, VrModCategories.TagUmbrella);
            RunFullScan(service);

            Assert.That(service.FindGamesByIndex(listing).Count, Is.EqualTo(1));
        }

        [Test]
        public void ASteamImportedGamesGameId_IsAnAppIdEvenWithNoLinks()
        {
            // Game.GameId is written by Playnite's own Steam integration and survives the user editing
            // or removing links. Measured on one real library: 524 games carry one.
            var game = new Game("Resident Evil 4 Interactive")
            {
                Id = Guid.NewGuid(),
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>(),
                PluginId = VrModTagService.SteamLibraryPluginId,
                GameId = "00434120"
            };
            _games.Items.Add(game);
            var listing = new VrModEntry { GameName = "Kosh Arenas", SteamAppId = "434120" };

            var rows = RunFullScan(NewService(new VrModTagSettings(), new StubParser(SourceId, listing)));

            var row = rows.FirstOrDefault(r => r.PlayniteGame.Id == game.Id && !r.IsSuggestion);
            Assert.That(row, Is.Not.Null, "the App ID both sides publish matched them");
            Assert.That(row.Status, Is.EqualTo(MatchDecision.Pending),
                "matched by that id, and shown for the decision rather than applied on it");
        }

        [Test]
        public void AProviderIdFromAnotherStore_IsNotASteamAppId()
        {
            // A Gog/ManuallyAdded game whose GameId happens to be digits is not a Steam product claim.
            // Reading it as one would match this listing against an unrelated store's copy of a game.
            var game = new Game("Resident Evil 4 Interactive")
            {
                Id = Guid.NewGuid(),
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>(),
                GameId = "434120"
            };
            _games.Items.Add(game);
            var listing = new VrModEntry { GameName = "Kosh Arenas", SteamAppId = "434120" };

            NewService(new VrModTagSettings(), new StubParser(SourceId, listing))
                .RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(TagNames(game), Is.Empty);
        }

        // --- the App ID is a match on its own terms -----------------------------------------------

        [Test]
        public void AnAppIdMatchIsShownAsPendingInsteadOfBeingAppliedOnItsOwn()
        {
            // A listing called after the mod and a game called after the product: the name rules score
            // this near zero and the length prune used to drop it before any comparison. The App ID is
            // the thing that says they are the same product - it is not the thing that says the user
            // wants this mod on it, so the match is shown and left undecided.
            var game = AddGame("Outlast 2", "https://store.steampowered.com/app/2521050/Outlast_2/");
            var listing = new VrModEntry { GameName = "Trageous Hallows", SteamAppId = "2521050", Category = "UEVR" };
            var settings = new VrModTagSettings();
            var service = NewService(settings, new StubParser(SourceId, listing));

            RunFullScan(service);   // the reviewing pass, the one a user opens a dialog for

            Assert.That(TagNames(game), Is.Empty,
                "nothing is applied to a pairing nobody has decided on");
            Assert.That(RunFullScan(service).Where(r => r.PlayniteGame.Id == game.Id && !r.IsSuggestion).ToList(),
                Has.Count.EqualTo(1), "the match is reported, not swallowed");
            var row = RunFullScan(service).First(r => r.PlayniteGame.Id == game.Id && !r.IsSuggestion);
            Assert.That(row.Status, Is.EqualTo(MatchDecision.Pending),
                "a new match is something the user sees and decides, not a silent change");
            Assert.That(row.Score, Is.EqualTo(1.0f),
                "the pairing itself is certain; only the decision is missing");

            // Deciding it is what applies it.
            settings.SetDecisionByKey(DecisionKeys.ForListing(listing, SourceId, game.Id),
                MatchDecision.Accepted, VrModCategories.TagNameFor(VrModCategories.UeVr));
            RunFullScan(service);
            Assert.That(TagNames(game), Does.Contain(VrModCategories.TagNameFor(VrModCategories.UeVr)),
                "accepted -> tagged");
        }

        [Test]
        public void TheGameDrivenScanMatchesByAppIdToo()
        {
            // ScanGameCore (a new game, a rename) used to be name-only: the same pairing matched on a
            // full scan and not on the scan that fires when the game appears in the library.
            var game = AddGame("Outlast 2", "https://store.steampowered.com/app/2521050/Outlast_2/");
            var listing = new VrModEntry { GameName = "Trageous Hallows", SteamAppId = "2521050", Category = "UEVR" };
            var service = NewService(new VrModTagSettings(), new StubParser(SourceId, listing));

            var rows = new List<VrModReviewMatchEntry>();
            service.ScanGameAsync(game, rows2 => rows.AddRange(rows2)).GetAwaiter().GetResult();

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].PlayniteGame.Id, Is.EqualTo(game.Id));
            Assert.That(rows[0].Score, Is.EqualTo(1.0f),
                "an App ID match is not a fuzzy guess, so it is not scored as one");
            Assert.That(rows[0].Status, Is.EqualTo(MatchDecision.Pending),
                "certain about the product, undecided as a mod: it arrives as a row to look at");
        }

        [Test]
        public void GetVrModEntriesForGameAndComputeTagsUseTheSameRulesAsAScan()
        {
            var game = AddGame("Outlast 2", "https://store.steampowered.com/app/2521050/Outlast_2/");
            var byAppId = new VrModEntry { GameName = "Trageous Hallows", SteamAppId = "2521050", Category = "UEVR" };
            var byName = new VrModEntry { GameName = "Outlast 2", Category = "UEVR" };
            var refused = new VrModEntry { GameName = "Outlast 2", SteamAppId = "1160220", Category = "UEVR" };
            var service = NewService(new VrModTagSettings(), new StubParser(SourceId, byAppId, byName, refused));
            RunFullScan(service);   // the cache ComputeTagsForGame reads is what a scan fills

            var entries = service.GetVrModEntriesForGame(game, new[] { byAppId, byName, refused }).ToList();
            Assert.That(entries, Is.EquivalentTo(new[] { byAppId, byName }),
                "an App ID match and a name match are both answers; an App ID disagreement is not");

            Assert.That(service.ComputeTagsForGame(game).Select(t => t.Name),
                Does.Contain(VrModCategories.TagNameFor(VrModCategories.UeVr)),
                $"the same rules that matched the listings must produce the tags; got [{string.Join(", ", service.ComputeTagsForGame(game).Select(t => t.Name))}]");
        }

        // --- an App ID disagreement refuses the pairing --------------------------------------------

        [Test]
        public void ConflictingAppIds_RefuseANameMatchThatTheNamesWouldMake()
        {
            // Identical names, different Steam products. Before this rule both were the same game as
            // far as this plugin was concerned, and the tags and the source link went on the wrong one.
            var game = AddGame("Resident Evil 4", "https://store.steampowered.com/app/2443190/Resident_Evil_4");
            var listing = new VrModEntry { GameName = "Resident Evil 4", SteamAppId = "434120", Category = "UEVR" };

            NewService(new VrModTagSettings(), new StubParser(SourceId, listing))
                .RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(TagNames(game), Is.Empty,
                "the names agree and the App IDs do not: nothing is applied");
        }

        [Test]
        public void ARefusedPairingIsStillOfferedForReview_WithBothIdsNamed()
        {
            // The refusal takes away the plugin's certainty, not the user's decision. What the pairing
            // needs is a person who can see that the listing says 434120 and the library's copy says
            // 2443190 - so both ids are in the row, and the row is ranked behind everything the plugin
            // would itself act on.
            var game = AddGame("Resident Evil 4", "https://store.steampowered.com/app/2443190/Resident_Evil_4");
            var listing = new VrModEntry { GameName = "Resident Evil 4", SteamAppId = "434120", Category = "UEVR" };
            var service = NewService(new VrModTagSettings(), new StubParser(SourceId, listing));

            var rows = RunFullScan(service, reviewAll: true);
            var row = rows.FirstOrDefault(r => r.PlayniteGame.Id == game.Id);

            Assert.That(row, Is.Not.Null, "the pairing is shown, not swallowed");
            Assert.That(row!.IsSuggestion, Is.True, "it is offered, not asserted");
            Assert.That(row.Reason, Does.Contain("434120").And.Contain("2443190"), row.Reason ?? "<no reason>");
        }

        [Test]
        public void APairingSomebodyDecidedIsNotRefusedByAnAppIdDisagreement()
        {
            // A decision IS the user's answer to "is this listing this game". The veto runs on
            // pairings nobody has judged; re-refusing a decided pair would quietly un-tag a game the
            // user already confirmed.
            var game = AddGame("Resident Evil 4", "https://store.steampowered.com/app/2443190/Resident_Evil_4");
            var listing = new VrModEntry { GameName = "Resident Evil 4", SteamAppId = "434120", Category = "UEVR" };
            var settings = new VrModTagSettings();
            var service = NewService(settings, new StubParser(SourceId, listing));

            RunFullScan(service);
            settings.SetDecisionByKey(DecisionKeys.ForListing(listing, SourceId, game.Id),
                MatchDecision.Accepted, VrModCategories.TagNameFor(VrModCategories.UeVr));
            RunFullScan(service);

            Assert.That(TagNames(game), Does.Contain(VrModCategories.TagNameFor(VrModCategories.UeVr)));
            Assert.That(RunFullScan(service).Any(r => r.PlayniteGame.Id == game.Id && r.IsSuggestion),
                Is.False, "a decided pairing is not asked again");
        }

        [Test]
        public void AListingWithoutAnAppIdStillMatchesAGameThatClaimsOne()
        {
            // The veto has to stop at silence or it switches off every match this plugin makes today:
            // the name-only sources (VorpX, LukeRoss, MrNlceHub, ...) publish no App ID at all.
            var game = AddGame("Resident Evil 4", "https://store.steampowered.com/app/2443190/Resident_Evil_4");
            var listing = new VrModEntry { GameName = "Resident Evil 4", Category = "VorpX" };

            NewService(new VrModTagSettings(), new StubParser(SourceId, listing))
                .RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(TagNames(game), Does.Contain(VrModCategories.TagNameFor(VrModCategories.VorpX)),
                $"silence about App IDs is not disagreement; that game ended up with [{string.Join(", ", TagNames(game))}]");
        }

        [Test]
        public void AListingWithAnAppIdStillMatchesAGameThatClaimsNone()
        {
            // The other half of the same rule: a library game with no Steam link in its library entry
            // is still the game a listing names, and a plugin that treated "the game says nothing" as
            // a disagreement would refuse most of what it matches today.
            var game = AddGame("Resident Evil 4");
            var listing = new VrModEntry { GameName = "Resident Evil 4", SteamAppId = "434120", Category = "VorpX" };

            NewService(new VrModTagSettings(), new StubParser(SourceId, listing))
                .RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(TagNames(game), Does.Contain(VrModCategories.TagNameFor(VrModCategories.VorpX)),
                $"silence about App IDs is not disagreement; that game ended up with [{string.Join(", ", TagNames(game))}]");
        }

        [Test]
        public void AGameThatClaimsTwoAppIds_IsMatchedByEither()
        {
            // A game whose library entry carries two store links (the product page plus an agecheck or
            // re-release link) is the same product for either id; the veto only fires when NONE is
            // shared.
            var game = new Game("Resident Evil 4")
            {
                Id = Guid.NewGuid(),
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>
                {
                    new Link("Steam", "https://store.steampowered.com/app/2443190/Resident_Evil_4"),
                    new Link("Steam product", "https://store.steampowered.com/app/434120/")
                }
            };
            _games.Items.Add(game);
            var listing = new VrModEntry { GameName = "Kosh Arenas", SteamAppId = "434120", Category = "UEVR" };

            var rows = RunFullScan(NewService(new VrModTagSettings(), new StubParser(SourceId, listing)));

            Assert.That(rows.Where(r => r.PlayniteGame.Id == game.Id && !r.IsSuggestion).ToList(),
                Has.Count.EqualTo(1),
                "either id pairs them; the second one is not a near miss and is not treated as one");
            Assert.That(rows.First(r => r.PlayniteGame.Id == game.Id && !r.IsSuggestion).Score,
                Is.EqualTo(1.0f));
        }

        [Test]
        public void ConflictedCandidatesRankBehindOnesThePluginWouldActOn()
        {
            // Three candidates for one listing ("Resident Evil 4", App ID 434120):
            //   'agrees'  - the library's copy of THAT product, found by App ID; it is a match, not a
            //               suggestion, and it waits in review for a decision.
            //   'silent'  - another title in the library with no Steam claim of its own: the name rules
            //               argue for it and nothing refuses it, so it is the recommendation.
            //   'conflicts' - the same name on a different Steam product: the ids refuse it, so it is
            //               still shown (a person can settle it) but flagged and ranked last.
            var conflicts = AddGame("Resident Evil 4", "https://store.steampowered.com/app/2443190/Resident_Evil_4");
            var agrees = AddGame("Resident Evil 4 Gold", "https://store.steampowered.com/app/434120/Resident_Evil_4_Gold");
            var silent = AddGame("Resident Evil 4 Deluxe");
            var listing = new VrModEntry { GameName = "Resident Evil 4", SteamAppId = "434120", Category = "UEVR" };
            var service = NewService(new VrModTagSettings(), new StubParser(SourceId, listing));

            // No review callback: this is the automatic pass, where a match is acted on. The indexes
            // FindSuggestedGames searches are what any scan builds.
            service.RunFullScanAsync().GetAwaiter().GetResult();
            var suggestions = service.FindSuggestedGames(listing);

            Assert.That(suggestions.Any(s => s.Game.Id == agrees.Id), Is.False,
                "a pairing the rules match outright is not reported as a guess");
            Assert.That(TagNames(agrees), Is.Empty,
                "the App ID pairs them, the names name different products: shown for review, not applied");
            Assert.That(TagNames(conflicts), Is.Empty, "the ids refuse it, so nothing is applied");

            Assert.That(suggestions.Any(s => s.Game.Id == conflicts.Id), Is.True,
                "a refusal is not a swallow: the pairing is still shown");
            Assert.That(suggestions.First(s => s.Game.Id == conflicts.Id).SteamIdsDisagree, Is.True);
            Assert.That(suggestions.First().SteamIdsDisagree, Is.False,
                "the plugin does not recommend what its own rules refuse");
            Assert.That(suggestions.First().Game.Id, Is.EqualTo(silent.Id));
        }

        // --- a decision survives the listing gaining an identity -----------------------------------

        [Test]
        public void ADecisionFiledUnderAListingsUrl_MovesOntoItsNewAppIdIdentity()
        {
            // A parser fix (or a site that starts publishing the id) gives a listing an App ID. The
            // decisions about that listing live under its URL, and a scan that looks only for the App
            // ID key reads them as "nobody decided" - a finished match is back in review, and a
            // mapping somebody accepted stops tagging. The scan is the only place that still holds the
            // URL and name the old keys were built from, so the move happens here, once.
            var game = AddGame("Skyrim");
            var listing = new VrModEntry
            {
                GameName = "Skyrim",
                SourceUrl = "https://pcvrcentral.com/game/skyrim",
                Category = "UEVR"
            };
            var settings = new VrModTagSettings();
            var service = NewService(settings, new StubParser(SourceId, listing));

            RunFullScan(service);
            var urlKey = DecisionKeys.ForListing(listing, SourceId, game.Id);
            Assert.That(urlKey, Does.Contain("url:"));
            settings.SetDecisionByKey(urlKey, MatchDecision.Accepted, VrModCategories.TagNameFor(VrModCategories.UeVr));
            var stampedAt = settings.EnsureDecisionTimestampUtc(urlKey,
                new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            RunFullScan(service);

            listing.SteamAppId = "436620";      // the listing gained an identity
            RunFullScan(service);

            var identityKey = $"{SourceId}||steam:436620||{game.Id}";
            Assert.That(settings.GetDecisionByKey(identityKey), Is.EqualTo(MatchDecision.Accepted),
                "the decision followed the listing to its new identity");
            Assert.That(settings.GetDecisionTimestampUtc(identityKey), Is.EqualTo(stampedAt),
                "with its timestamp, not re-stamped as a new decision");
            Assert.That(settings.GetDecisionByKey(urlKey), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(1),
                "moved, not copied: one key for one mapping");
            Assert.That(TagNames(game), Does.Contain(VrModCategories.TagNameFor(VrModCategories.UeVr)),
                "and the game it was accepted for is still tagged");
        }

        [Test]
        public void ARenamedListingWithAnAppIdStillRestoresTheMappingFiledUnderItsOldUrl()
        {
            // The decided-mapping index is what keeps an accepted pairing a match when the two names no
            // longer agree, and it reads the LISTING half of the stored key. If that index only knows the
            // identity of today, a mapping accepted under a URL-shaped key is invisible to it - and here
            // the name fallback cannot rescue it either, because the site reworded the listing. This is
            // the state an upgrade into this plugin actually produces: the same page, now read as an App
            // ID, under a title that has since changed.
            var game = AddGame("Resident Evil 4");
            var listing = new VrModEntry
            {
                GameName = "Resident Evil 4",
                SourceUrl = "https://pcvrcentral.com/game/resident-evil-4",
                Category = "UEVR"
            };
            var settings = new VrModTagSettings();
            var service = NewService(settings, new StubParser(SourceId, listing));

            RunFullScan(service);
            var urlKey = DecisionKeys.ForListing(listing, SourceId, game.Id);
            Assert.That(urlKey, Does.Contain("url:"), "the shape a decision is filed under before the id exists");
            settings.SetDecisionByKey(urlKey, MatchDecision.Accepted, VrModCategories.TagNameFor(VrModCategories.UeVr));

            listing.SteamAppId = "2443190";
            listing.GameName = "Compatibility Patch Bundle";   // names no longer agree: only the saved mapping can carry this
            RunFullScan(service);

            Assert.That(TagNames(game), Does.Contain(VrModCategories.TagNameFor(VrModCategories.UeVr)),
                "the accepted mapping is still a mapping, through its old URL");
            Assert.That(settings.GetDecisionByKey($"{SourceId}||steam:2443190||{game.Id}"),
                Is.EqualTo(MatchDecision.Accepted), "and it is filed under the identity it has now");
        }

        [Test]
        public void ADecisionFiledUnderAListingsName_MovesOntoItsNewAppIdIdentity()
        {
            // The oldest shape this plugin wrote: a key whose identity half was the listing's display
            // name. Same rule, older key.
            var game = AddGame("Hell Let Loose");
            var listing = new VrModEntry { GameName = "Hell Let Loose", Category = "UEVR" };
            var settings = new VrModTagSettings();
            settings.SetDecisionByKey(DecisionKeys.Named(SourceId, "Hell Let Loose", game.Id),
                MatchDecision.Accepted, VrModCategories.TagNameFor(VrModCategories.UeVr));
            var service = NewService(settings, new StubParser(SourceId, listing));

            RunFullScan(service);

            Assert.That(settings.GetDecisionByKey($"{SourceId}||steam:436620||{game.Id}"),
                Is.EqualTo(MatchDecision.Pending), "the listing has no App ID yet, so nothing has moved");

            listing.SteamAppId = "436620";
            RunFullScan(service);

            Assert.That(settings.GetDecisionByKey($"{SourceId}||steam:436620||{game.Id}"),
                Is.EqualTo(MatchDecision.Accepted));
            Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(1));
        }

        // --- a scraped App ID is canonicalised where it is written ---------------------------------

        [Test]
        public void AListingsAppIdIsCanonicalised_WithoutLosingTheListingItIdentifies()
        {
            var padded = new VrModEntry { GameName = "Skyrim", SteamAppId = " 00436620 " };
            Assert.That(padded.SteamAppId, Is.EqualTo("436620"));
            Assert.That(padded.SteamAppIdForMatch, Is.EqualTo("436620"));

            // Text that cannot be a product id keeps the listing's identity (it survives a site rename)
            // and gains no matching power: the game side only ever publishes digit keys.
            var placeholder = new VrModEntry { GameName = "Skyrim", SteamAppId = "$undefined" };
            Assert.That(placeholder.SteamAppId, Is.EqualTo("$undefined"));
            Assert.That(placeholder.SteamAppIdForMatch, Is.Null);
            Assert.That(placeholder.DecisionKeyComponent, Is.EqualTo("steam:$undefined"));

            var none = new VrModEntry { GameName = "Skyrim", SteamAppId = "0" };
            Assert.That(none.SteamAppId, Is.Null);
            Assert.That(none.SteamAppIdForMatch, Is.Null);
        }

        // --- fixture ------------------------------------------------------------------------------

        private static List<VrModReviewMatchEntry> RunFullScan(VrModTagService service, bool reviewAll = false)
        {
            var rows = new List<VrModReviewMatchEntry>();
            service.RunFullScanAsync(r => rows.AddRange(r), reviewAll).GetAwaiter().GetResult();
            return rows;
        }

        /// <summary>Tag names a scan left on a game (TagIds holds Guids; the names come from the tag store).</summary>
        private List<string> TagNames(Game game)
        {
            var names = new List<string>();
            foreach (var id in game.TagIds ?? new List<Guid>())
            {
                var tag = _tags.Items.FirstOrDefault(t => t.Id == id);
                names.Add(tag?.Name ?? id.ToString());
            }
            return names;
        }

        private Game AddGame(string name, string? link = null)
        {
            var links = new ObservableCollection<Link>();
            if (link != null) links.Add(new Link("Steam", link));
            var game = new Game(name) { Id = Guid.NewGuid(), TagIds = new List<Guid>(), Links = links };
            _games.Items.Add(game);
            return game;
        }

        private VrModTagService NewService(VrModTagSettings settings, params StubParser[] parsers)
        {
            var registry = new VrModSourceRegistry();
            foreach (var parser in parsers)
                registry.Register(parser);
            return new VrModTagService(_api, settings, new NullLogger(), registry);
        }

        private class StubParser : BaseVrModParser
        {
            public StubParser(string sourceId, params VrModEntry[] entries)
                : base(sourceId, "Stub Source", "https://example.com/")
            {
                Entries = entries.ToList();
                foreach (var entry in Entries)
                    entry.SourceId = sourceId;
            }

            public List<VrModEntry> Entries { get; }

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(Entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
