using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The game menu's "Reset my decisions for this game...". It forgets the stored decisions AND takes off
    /// what those decisions put on the game, in one action - the tags are the record's projection, so a reset
    /// that left them up would leave the game claiming a mod the plugin no longer has any record of, which no
    /// later scan is obliged to notice.
    ///
    /// The tag work goes through the same rule every other path uses (a plugin-owned tag comes off when
    /// nothing backs it), so these tests also pin the one thing that rule must never do: touch a tag the
    /// plugin never owned.
    /// </summary>
    [TestFixture]
    public class VrModResetDecisionsTests
    {
        private static readonly Guid GameId = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid OtherGameId = new Guid("22222222-2222-2222-2222-222222222222");

        private static readonly Guid UmbrellaTagId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid CategoryTagId = new Guid("aaaaaaaa-0000-0000-0000-000000000002");
        private static readonly Guid UserTagId = new Guid("aaaaaaaa-0000-0000-0000-000000000003");
        private static readonly Guid OtherGameTagId = new Guid("aaaaaaaa-0000-0000-0000-000000000004");

        private const string SourceId = "src-test";

        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagSettings _settings = null!;
        private VrModTagService _service = null!;
        private IPlayniteAPI _api = null!;

        [SetUp]
        public void SetUp()
        {
            _api = Substitute.For<IPlayniteAPI>();
            var db = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            db.Games.Returns(_games);
            db.Tags.Returns(_tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _api.Database.Returns(db);

            _settings = new VrModTagSettings();
            _service = new VrModTagService(_api, _settings, new NullLogger(), new VrModSourceRegistry());
        }

        /// <summary>A game carrying what an applied match leaves behind: both plugin tags, plus a tag the
        /// user made themselves, plus the Accepted pairing that put them there.</summary>
        private Game AddTaggedGame(Guid gameId, string name, params Guid[] tagIds)
        {
            var game = new Game(name) { Id = gameId, TagIds = tagIds.ToList() };
            _games.Items.Add(game);
            return game;
        }

        private void AddTag(Guid id, string name) => _tags.Items.Add(new Tag(name) { Id = id });

        private string KeyFor(Guid gameId, string listing = "mod-one")
            => VrModNameMatcher.MakeDecisionKey(SourceId, listing, gameId);

        private void AddAcceptedDecision(Guid gameId, string listing = "mod-one")
            => _settings.SetDecisionByKey(KeyFor(gameId, listing), MatchDecision.Accepted);

        [Test]
        public void ResetDecisionsForGames_TakesOffThePluginTagsAndForgetsTheDecision()
        {
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            AddTag(CategoryTagId, "VRMod UEVR");
            AddTag(UserTagId, "Works great on my Index");
            var game = AddTaggedGame(GameId, "Skyrim", UmbrellaTagId, CategoryTagId, UserTagId);
            AddAcceptedDecision(GameId);

            var changed = _service.ResetDecisionsForGames(new List<Game> { game });

            Assert.That(changed, Is.EqualTo(1));
            Assert.That(game.TagIds, Is.EquivalentTo(new[] { UserTagId }),
                "the tags the decision put there come off; the user's own tag is not ours to remove");
            Assert.That(_settings.GetDecisionByKey(KeyFor(GameId)), Is.Not.EqualTo(MatchDecision.Accepted),
                "the pairing is forgotten, so the next scan offers it again");
        }

        [Test]
        public void ResetDecisionsForGames_RemovesNothingThePluginDoesNotOwn()
        {
            // The whole point of deciding ownership by tag NAME: a tag id seen on a game is not proof the
            // plugin made it, and a user tag that merely looks VR-ish must survive a reset.
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            AddTag(UserTagId, "VR Ready");
            var game = AddTaggedGame(GameId, "Skyrim", UmbrellaTagId, UserTagId);
            AddAcceptedDecision(GameId);

            _service.ResetDecisionsForGames(new List<Game> { game });

            Assert.That(game.TagIds, Contains.Item(UserTagId), "\"VR Ready\" is not a tag this plugin mints");
            Assert.That(game.TagIds, Does.Not.Contain(UmbrellaTagId));
        }

        [Test]
        public void ResetDecisionsForGames_LeavesOtherGamesAndTheirDecisionsAlone()
        {
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            AddTag(OtherGameTagId, "VRMod 6DOF");
            var game = AddTaggedGame(GameId, "Skyrim", UmbrellaTagId);
            var other = AddTaggedGame(OtherGameId, "Half-Life: Alyx", OtherGameTagId);
            AddAcceptedDecision(GameId);
            AddAcceptedDecision(OtherGameId, "mod-two");

            _service.ResetDecisionsForGames(new List<Game> { game });

            Assert.That(other.TagIds, Is.EquivalentTo(new[] { OtherGameTagId }),
                "a reset of one game reaches no other game");
            Assert.That(_settings.GetDecisionByKey(KeyFor(OtherGameId, "mod-two")), Is.EqualTo(MatchDecision.Accepted),
                "the other game's pairing is still recorded");
        }

        [Test]
        public void ResetDecisionsForGames_AGameWithTagsButNoDecision_IsStillCleaned()
        {
            // The case a tags-as-truth gate could not see: the plugin's tags are on the game with no pairing
            // behind them (a match an unattended scan applied and recorded nothing). Reset has to be able to
            // clean that too - the rule is "nothing backs it", not "something told us to remove it".
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            var game = AddTaggedGame(GameId, "Skyrim", UmbrellaTagId);

            var changed = _service.ResetDecisionsForGames(new List<Game> { game });

            Assert.That(changed, Is.EqualTo(1));
            Assert.That(game.TagIds, Is.Empty);
        }

        [Test]
        public void ResetDecisionsForGames_SecondResetChangesNothing()
        {
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            var game = AddTaggedGame(GameId, "Skyrim", UmbrellaTagId);
            AddAcceptedDecision(GameId);

            Assert.That(_service.ResetDecisionsForGames(new List<Game> { game }), Is.EqualTo(1));
            Assert.That(_service.ResetDecisionsForGames(new List<Game> { game }), Is.EqualTo(0),
                "idempotent: a game already clean is not reported as changed");
        }

        [Test]
        public void ResetDecisionsForGames_EmptySelection_TouchesNothing()
        {
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            var game = AddTaggedGame(GameId, "Skyrim", UmbrellaTagId);
            AddAcceptedDecision(GameId);

            Assert.That(_service.ResetDecisionsForGames(null), Is.EqualTo(0));
            Assert.That(_service.ResetDecisionsForGames(new List<Game>()), Is.EqualTo(0));
            Assert.That(game.TagIds, Is.EquivalentTo(new[] { UmbrellaTagId }));
            Assert.That(_settings.GetDecisionByKey(KeyFor(GameId)), Is.EqualTo(MatchDecision.Accepted));
        }
    }
}
