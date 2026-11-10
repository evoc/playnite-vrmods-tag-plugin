using System;
using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using NUnit.Framework;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The game-centred view over the plugin's saved decisions, and the two answers the game context
    /// menu needs from it.
    ///
    /// The decisions are keyed the other way round from how the menu asks: "sourceId||listing||gameId",
    /// so a right-click question used to cost a split of every stored key plus a walk of every tag in
    /// the library (the tag walk was there to catch a pairing an unattended scan applied without
    /// recording one). Both are gone: the decisions are the plugin's own record of what it did, so they
    /// answer the question on their own, and they answer it from an index built once per change rather
    /// than once per right-click.
    ///
    /// What these tests hold:
    /// 1. The key's game half parses back out of the key, including when the listing identity in the
    ///    middle contains the separator itself, and refuses the legacy name-keyed shape.
    /// 2. The index says what the stored decisions say - per game, all of them OR-ed, rebuilt after a
    ///    write or a clear, and rebuilt after Playnite's deserializer replaces the map wholesale.
    /// 3. "Reset my decisions..." is offered where a game has only rejected pairings - the one state
    ///    where nothing is assigned and only Reset can undo what the user did.
    /// 4. A game carrying the plugin's tags but holding no decision is offered neither item: the saved
    ///    decisions are the source of truth, and tags on a game are library state anyone can edit.
    /// </summary>
    [TestFixture]
    public class VrModGameDecisionIndexTests
    {
        private IPlayniteAPI _mockApi = null!;
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagSettings _settings = null!;
        private VrModTagService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            var mockDb = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            _mockApi.Database.Games.Returns(_games);
            _mockApi.Database.Tags.Returns(_tags);
            _mockApi.Database.BufferedUpdate().Returns(new FakeBufferedUpdate());

            _settings = new VrModTagSettings();
            _service = new VrModTagService(_mockApi, _settings, new NullLogger(), new VrModSourceRegistry());
        }

        /// <summary>The decision key of one (listing, game) pairing, built the only sanctioned way.</summary>
        private static string KeyFor(Guid gameId, string listing = "uevr-reframework")
            => VrModNameMatcher.MakeDecisionKey("src", listing, gameId);

        private static void Decide(VrModTagSettings settings, Guid gameId, MatchDecision decision, string listing = "uevr-reframework")
            => settings.SetDecisionsByKey(new List<(string Key, MatchDecision Decision, string? SourceTags)>
            {
                (KeyFor(gameId, listing), decision, null)
            });

        private static Game AddGame(FakeItemCollection<Game> games, string name = "Test Game")
        {
            var game = new Game(name) { TagIds = new List<Guid>() };
            games.Add(game);
            return game;
        }

        // -----------------------------------------------------------------------------------------
        // 1: the key's game half.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void TryGetDecisionGameId_RoundTripsTheKey()
        {
            var gameId = Guid.NewGuid();

            var ok = VrModNameMatcher.TryGetDecisionGameId(KeyFor(gameId), out var parsed);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True);
                Assert.That(parsed, Is.EqualTo(gameId), "the game a key names must come back out of the key");
            });
        }

        [Test]
        public void TryGetDecisionGameId_ListingIdentityContainingTheSeparator_StillYieldsTheGame()
        {
            // The middle part is a page path or a listing title, and a title is allowed to contain
            // "||". A split from the left would read the second word of such a title as the game.
            var gameId = Guid.NewGuid();

            var ok = VrModNameMatcher.TryGetDecisionGameId(KeyFor(gameId, "some||listing"), out var parsed);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True);
                Assert.That(parsed, Is.EqualTo(gameId));
            });
        }

        [Test]
        public void TryGetDecisionGameId_LegacyOrCorruptKey_IsFalse()
        {
            var cases = new[]
            {
                "src||Some Game Name",                        // legacy: keyed by game name, no Guid tail
                "src||listing||not-a-guid",                   // corrupt
                "no-separator-at-all",
                "",
                null
            };

            foreach (var key in cases)
            {
                Assert.That(VrModNameMatcher.TryGetDecisionGameId(key, out var parsed), Is.False,
                    $"'{key}' names no game and must not be treated as one");
                Assert.That(parsed, Is.EqualTo(Guid.Empty));
            }
        }

        // -----------------------------------------------------------------------------------------
        // 2: the index says what the stored decisions say.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void GetDecisionStateForGame_UnknownGame_IsNone()
        {
            Decide(_settings, Guid.NewGuid(), MatchDecision.Accepted);

            Assert.That(_settings.GetDecisionStateForGame(Guid.NewGuid()), Is.EqualTo(GameDecisionState.None),
                "another game's decisions must not leak into this one");
            Assert.That(_settings.GetAcceptedDecisionKeysForGame(Guid.NewGuid()), Is.Empty);
        }

        [Test]
        public void GetDecisionStateForGame_RejectedOnly_IsRejectedAndNoAcceptedKeys()
        {
            var gameId = Guid.NewGuid();

            Decide(_settings, gameId, MatchDecision.Rejected);

            Assert.Multiple(() =>
            {
                Assert.That(_settings.GetDecisionStateForGame(gameId), Is.EqualTo(GameDecisionState.HasRejected));
                Assert.That(_settings.GetAcceptedDecisionKeysForGame(gameId), Is.Empty,
                    "a voted-down pairing assigns nothing to the game");
            });
        }

        [Test]
        public void GetDecisionStateForGame_EveryPairingOfTheGame_IsFlagged()
        {
            var gameId = Guid.NewGuid();

            Decide(_settings, gameId, MatchDecision.Accepted, "uevr-reframework");
            Decide(_settings, gameId, MatchDecision.Rejected, "flat2vr");
            Decide(_settings, gameId, MatchDecision.Pending, "vorp-x");

            Assert.That(_settings.GetDecisionStateForGame(gameId),
                Is.EqualTo(GameDecisionState.HasAccepted | GameDecisionState.HasRejected | GameDecisionState.HasPending),
                "the state is everything the game appears in, OR-ed");
            Assert.That(_settings.GetAcceptedDecisionKeysForGame(gameId),
                Is.EquivalentTo(new[] { KeyFor(gameId, "uevr-reframework") }),
                "only the accepted pairing names an assignment");
        }

        [Test]
        public void GetDecisionStateForGame_RebuiltAfterTheDecisionsChange()
        {
            var gameId = Guid.NewGuid();

            Decide(_settings, gameId, MatchDecision.Accepted);
            Assert.That(_settings.GetDecisionStateForGame(gameId) & GameDecisionState.HasAccepted,
                Is.Not.EqualTo(GameDecisionState.None));

            _settings.ClearDecisionsByKey(new List<string> { KeyFor(gameId) });
            Assert.That(_settings.GetDecisionStateForGame(gameId), Is.EqualTo(GameDecisionState.None),
                "a clear must not leave a stale flag behind - the menu would keep offering Reset forever");
            Assert.That(_settings.GetAcceptedDecisionKeysForGame(gameId), Is.Empty);
        }

        [Test]
        public void GetDecisionStateForGame_RebuiltAfterDeserializationReplacesTheMap()
        {
            // Playnite's serializer fills the settings by replacing the map wholesale through the
            // public setter - the load path, and the one writer that does not hold the settings lock.
            var gameId = Guid.NewGuid();
            Decide(_settings, gameId, MatchDecision.Accepted);
            Assert.That(_settings.GetDecisionStateForGame(gameId) & GameDecisionState.HasAccepted,
                Is.Not.EqualTo(GameDecisionState.None),
                "the index has to be built first, or this test proves nothing about dropping it");

            var loaded = Guid.NewGuid();
            _settings.MatchDecisions = new Dictionary<string, string>
            {
                [KeyFor(loaded, "flat2vr")] = MatchDecision.Rejected.ToString()
            };

            Assert.Multiple(() =>
            {
                Assert.That(_settings.GetDecisionStateForGame(gameId), Is.EqualTo(GameDecisionState.None),
                    "the loaded settings replaced the map; the old game is not in them");
                Assert.That(_settings.GetDecisionStateForGame(loaded), Is.EqualTo(GameDecisionState.HasRejected));
            });
        }

        [Test]
        public void GetDecisionStateForGame_KeysWithoutAGameHalf_AreIgnored()
        {
            // A steady-state index over a user's real config.json still holds legacy name-keyed
            // decisions: no game can be asked about them, and they must not break the build.
            _settings.SetDecisionsByKey(new List<(string Key, MatchDecision Decision, string? SourceTags)>
            {
                ("src||Alien: Isolation", MatchDecision.Accepted, null)
            });
            var gameId = Guid.NewGuid();
            Decide(_settings, gameId, MatchDecision.Rejected);

            Assert.That(_settings.GetDecisionStateForGame(gameId), Is.EqualTo(GameDecisionState.HasRejected));
        }

        // -----------------------------------------------------------------------------------------
        // 3 + 4: the two answers the game context menu needs.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void GetDecisionStateForGames_NothingStored_OffersNeitherItem()
        {
            var game = AddGame(_games);

            var state = _service.GetDecisionStateForGames(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(state.AnyAssigned, Is.False, "nothing to reject");
                Assert.That(state.AnyStoredDecision, Is.False, "nothing to reset");
            });
        }

        [Test]
        public void GetDecisionStateForGames_NullOrEmptySelection_OffersNeitherItem()
        {
            Assert.That(_service.GetDecisionStateForGames(null), Is.EqualTo((false, false)));
            Assert.That(_service.GetDecisionStateForGames(new List<Game>()), Is.EqualTo((false, false)));
        }

        [Test]
        public void GetDecisionStateForGames_RejectedOnly_OffersResetButNotReject()
        {
            // The state the user reaches by turning down every suggested mod: nothing is assigned, so
            // there are no tags to take off, and Reset is the only thing that can undo the decision.
            var game = AddGame(_games);
            Decide(_settings, game.Id, MatchDecision.Rejected);

            var state = _service.GetDecisionStateForGames(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(state.AnyAssigned, Is.False);
                Assert.That(state.AnyStoredDecision, Is.True,
                    "a rejected pairing is still a decision the user made, and Reset is how they take it back");
            });
        }

        [Test]
        public void GetDecisionStateForGames_Accepted_OffersBothItems()
        {
            var game = AddGame(_games);
            Decide(_settings, game.Id, MatchDecision.Accepted);

            Assert.That(_service.GetDecisionStateForGames(new List<Game> { game }), Is.EqualTo((true, true)));
        }

        [Test]
        public void GetDecisionStateForGames_OneGameOfTheSelection_IsEnoughForItsAnswer()
        {
            var rejected = AddGame(_games, "Rejected Game");
            var accepted = AddGame(_games, "Accepted Game");
            AddGame(_games, "Untouched Game");
            Decide(_settings, rejected.Id, MatchDecision.Rejected);
            Decide(_settings, accepted.Id, MatchDecision.Accepted);

            var state = _service.GetDecisionStateForGames(new List<Game> { rejected, accepted });

            Assert.That(state, Is.EqualTo((true, true)),
                "the menu is shared by the whole selection, so one game carrying an answer is enough");
        }

        [Test]
        public void GetDecisionStateForGames_TagsOnTheGameWithoutADecision_OfferNeitherItem()
        {
            // Tags are library state: the user, another plugin or an import can add or rename them, so
            // they are not the record of what this plugin did. A game carrying our tag with nothing
            // stored behind it is not offered the two items; "Check this game for VR mods..." and its
            // row in the review dialog are where that pairing gets decided.
            var game = AddGame(_games);
            var tag = new Tag(VrModCategories.TagUmbrella);
            _tags.Add(tag);
            game.TagIds = new List<Guid> { tag.Id };

            var state = _service.GetDecisionStateForGames(new List<Game> { game });

            Assert.That(state, Is.EqualTo((false, false)),
                "the saved decisions answer for the plugin; a tag alone does not");
        }
    }
}
