using System;
using System.Collections.Generic;
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
    /// The game context menu's "Reject all assigned VR mods on this game..."
    /// (<see cref="VrModTagService.RejectAssignedVrMods"/>): the user pointing at a game and saying the
    /// mod on it is wrong.
    ///
    /// The action answers a narrower question than it looks like. The user says "THIS mod is wrong" - not
    /// "every mod anyone could ever suggest for this game is wrong" - so the veto lands on the pairings
    /// this game is in, and nothing else: the other games in the library keep their mods, and this game
    /// keeps its other pairings' standing, which is why a later scan can still offer it something better.
    ///
    /// A veto has to be a saved <see cref="MatchDecision.Rejected"/>, not just an unwriting of tags: tags
    /// are library state that a scan, an import or the user rewrites, so removing them alone would let the
    /// next scan put the same mod straight back. Rejected is also what makes the pairing visible as a
    /// decision in the review dialog, and what "Reset my decisions for this game..." can undo.
    ///
    /// What these tests hold:
    /// 1. An accepted pairing comes back as a saved Rejected, and the tags AND the source link it had put
    ///    on the game come off with it.
    /// 2. The neighbour keeps its mod and its decision: the veto is scoped to the selection.
    /// 3. With no cached listings (a fresh session: the listing cache is in-memory) the tags still come
    ///    off, the stored decision is still vetoed, and the limitation is said out loud in the log.
    /// 4. A listing the catalog matches to the game right now is vetoed even where no decision record
    ///    names the assignment - the belt for a mod that got on the game without a record.
    /// 5. Nothing to do is really nothing to do: no writes, no tag edits.
    /// 6. A pairing already vetoed is not written a second time, and its tags still come off.
    /// </summary>
    [TestFixture]
    public class VrModRejectAssignedVrModsTests
    {
        private static readonly Guid UmbrellaTagId = new Guid("bbbbbbbb-0000-0000-0000-000000000001");
        private static readonly Guid UserTagId = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid OtherGameTagId = new Guid("bbbbbbbb-0000-0000-0000-000000000003");
        private const string SourceId = "src-reject";
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagSettings _settings = null!;
        private VrModSourceRegistry _registry = null!;
        private RecordingLogger _logger = null!;
        private VrModTagService _service = null!;

        [SetUp]
        public void SetUp()
        {
            var api = Substitute.For<IPlayniteAPI>();
            var db = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            db.Games.Returns(_games);
            db.Tags.Returns(_tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());
            api.Database.Returns(db);
            _settings = new VrModTagSettings();
            _registry = new VrModSourceRegistry();
            _logger = new RecordingLogger();
            _service = new VrModTagService(api, _settings, _logger, _registry);
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            AddTag(UserTagId, "Works great on my Index");
        }

        // -----------------------------------------------------------------------------------------
        // 1: the pairing is vetoed, and everything that pairing put on the game comes off.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RejectAssignedVrMods_AcceptedPairing_VetoesItAndTakesItsTagsAndLinkOff()
        {
            var parser = new TestParser(SourceId, new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/game/skyrim" }
            });
            _registry.Register(parser);
            var game = AddGame("Skyrim");
            _settings.AcceptListing(parser, "Skyrim", game.Id);
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The arrangement has to be the real thing: a rejected-pairing test that never got the tags
            // on in the first place proves nothing about taking them off.
            Assert.Multiple(() =>
            {
                Assert.That(TagNames(game), Does.Contain(VrModCategories.TagUmbrella),
                    "setup: the accepted pairing put our umbrella tag on the game");
                Assert.That(game.Links, Has.Count.EqualTo(1),
                    "setup: and the source link the same pairing ensured");
            });

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(1));
                Assert.That(gamesChanged, Is.EqualTo(1));
                Assert.That(_settings.GetDecisionForListing(parser.EntryFor("Skyrim"), SourceId, game.Id),
                    Is.EqualTo(MatchDecision.Rejected),
                    "the answer is a saved Rejected: the next scan offers it as rejected instead of " +
                    "re-applying it, and \"Reset my decisions...\" is what takes it back");
                Assert.That(TagNames(game), Does.Not.Contain(VrModCategories.TagUmbrella));
                Assert.That(TagNames(game), Does.Not.Contain(VrModCategories.TagNameFor("UEVR")));
                Assert.That(game.Links, Is.Empty,
                    "a link to the mod the user just called wrong is the same mistake in the Details view");
            });
        }

        [Test]
        public void RejectAssignedVrMods_KeepsATagThePluginNeverMinted()
        {
            AddTag(OtherGameTagId, "VR Ready");
            var game = AddGame("Skyrim", UmbrellaTagId, UserTagId, OtherGameTagId);
            _settings.SetDecisionByKey(VrModNameMatcher.MakeDecisionKey(SourceId, "Skyrim UEVR", game.Id),
                MatchDecision.Accepted);

            _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(TagNames(game), Is.EquivalentTo(new[] { "Works great on my Index", "VR Ready" }),
                    "the user's own tags are not this plugin's to remove, whatever the user rejected");
                Assert.That(TagNames(game), Does.Not.Contain(VrModCategories.TagUmbrella));
            });
        }

        // -----------------------------------------------------------------------------------------
        // 2: the veto is scoped to the selection.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RejectAssignedVrMods_OtherGameKeepsItsModAndItsDecision()
        {
            // "This mod is wrong" about Skyrim must not reach Half-Life: Alyx. The count returned is part
            // of the assertion: one veto written means the other game's accepted pairing was never even
            // offered to the veto list.
            var parser = new TestParser(SourceId, new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "Half-Life: Alyx", Category = "Native VR" }
            });
            _registry.Register(parser);
            var skyrim = AddGame("Skyrim");
            var alyx = AddGame("Half-Life: Alyx");
            _settings.AcceptListing(parser, "Skyrim", skyrim.Id);
            _settings.AcceptListing(parser, "Half-Life: Alyx", alyx.Id);
            _service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(TagNames(alyx), Does.Contain(VrModCategories.TagUmbrella),
                "setup: both games carry their mod");

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { skyrim });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(1), "only the selected game's pairing is vetoed");
                Assert.That(gamesChanged, Is.EqualTo(1));
                Assert.That(TagNames(skyrim), Does.Not.Contain(VrModCategories.TagUmbrella));
                Assert.That(TagNames(alyx), Does.Contain(VrModCategories.TagUmbrella),
                    "the neighbour keeps its mod");
                Assert.That(_settings.GetDecisionForListing(parser.EntryFor("Half-Life: Alyx"), SourceId, alyx.Id),
                    Is.EqualTo(MatchDecision.Accepted),
                    "and keeps the decision that put it there");
            });
        }

        [Test]
        public void RejectAssignedVrMods_RejectsEveryAssignedPairingOfTheGame()
        {
            // Two mods on one game. From two sources, because a source is merged to one entry per game
            // (BaseVrModParser.MergeDuplicateGames), and this is what the item's wording promises: "all
            // assigned VR mods", so the user does not have to run it twice to be rid of both.
            var first = new TestParser(SourceId, new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            var second = new TestParser("src-reject-b", new[] { new VrModEntry { GameName = "Skyrim", Category = "Native VR" } });
            _registry.Register(first);
            _registry.Register(second);
            var game = AddGame("Skyrim");
            _settings.SetDecisionForListing(first.EntryFor("Skyrim"), first.SourceId, game.Id, MatchDecision.Accepted);
            _settings.SetDecisionForListing(second.EntryFor("Skyrim"), second.SourceId, game.Id, MatchDecision.Accepted);

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(2), "both pairings of this game are vetoed");
                Assert.That(gamesChanged, Is.EqualTo(0),
                    "nothing was on the game yet, so there was nothing to take off it");
                Assert.That(_settings.GetDecisionForListing(first.EntryFor("Skyrim"), first.SourceId, game.Id),
                    Is.EqualTo(MatchDecision.Rejected));
                Assert.That(_settings.GetDecisionForListing(second.EntryFor("Skyrim"), second.SourceId, game.Id),
                    Is.EqualTo(MatchDecision.Rejected));
            });
        }

        [Test]
        public void RejectAssignedVrMods_OtherListingsThatWereNeverAnswered_StaySuggestions()
        {
            // The label ("all assigned VR mods") and the confirmation dialog ("other VR mods that fit the
            // game are still suggested") both promise a veto that stops at the assignment. Two sources,
            // because a source is merged to one entry per game: the first is the mod the user accepted and
            // took tags from, the second merely fits the same game and was never answered. A veto that
            // sweeps the catalog would refuse the second one too, and the user would find out only when it
            // stopped being offered - which is the opposite of what he was told.
            var assigned = new TestParser(SourceId, new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            var suggested = new TestParser("src-reject-suggested", new[] { new VrModEntry { GameName = "Skyrim", Category = "Native VR" } });
            _registry.Register(assigned);
            _registry.Register(suggested);
            var game = AddGame("Skyrim");
            _settings.AcceptListing(assigned, "Skyrim", game.Id);
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(TagNames(game), Does.Contain(VrModCategories.TagUmbrella),
                    "setup: the accepted pairing is what put this game in the plugin's care");
                Assert.That(
                    _settings.GetDecisionForListing(suggested.EntryFor("Skyrim"), "src-reject-suggested", game.Id),
                    Is.Not.EqualTo(MatchDecision.Rejected),
                    "setup: the other source's listing fits this game and carries no answer yet");
            });

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(1),
                    "only the assigned pairing is vetoed, not every listing the catalog matches");
                Assert.That(_settings.GetDecisionForListing(assigned.EntryFor("Skyrim"), SourceId, game.Id),
                    Is.EqualTo(MatchDecision.Rejected), "the assigned pairing is the one that gets the veto");
                Assert.That(
                    _settings.GetDecisionForListing(suggested.EntryFor("Skyrim"), "src-reject-suggested", game.Id),
                    Is.Not.EqualTo(MatchDecision.Rejected),
                    "and the pairing he never answered is still a suggestion, not a refusal he never made");
                Assert.That(gamesChanged, Is.EqualTo(1));
                Assert.That(TagNames(game), Does.Not.Contain(VrModCategories.TagUmbrella));
            });
        }

        // -----------------------------------------------------------------------------------------
        // 3: no cached listings.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RejectAssignedVrMods_NoCachedListings_StillTakesTagsOffAndVetoesTheStoredDecision()
        {
            // The listing cache is in-memory, so this is the ordinary state of the first right-click
            // after a Playnite restart: the decisions survived on disk, the listings did not. The user's
            // answer must not depend on whether a source happened to be fetched this session.
            var game = AddGame("Skyrim", UmbrellaTagId);
            var key = VrModNameMatcher.MakeDecisionKey(SourceId, "Skyrim UEVR", game.Id);
            _settings.SetDecisionByKey(key, MatchDecision.Accepted);

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(1), "the stored decision is vetoed from the record alone");
                Assert.That(gamesChanged, Is.EqualTo(1));
                Assert.That(TagNames(game), Is.Empty, "and the tag comes off, listings or no listings");
                Assert.That(_settings.GetDecisionByKey(key), Is.EqualTo(MatchDecision.Rejected));
            });
        }

        [Test]
        public void RejectAssignedVrMods_NoCachedListings_SaysTheLimitationOutLoud()
        {
            // What the empty cache DOES cost: a mod an unattended scan applied without recording a
            // decision cannot be named, so its pairing is not vetoed and a later scan will offer it
            // again. The tags come off either way, which is why the action looks like it worked - so the
            // run has to say the gap happened, or nobody ever learns to expect the reappearance.
            var game = AddGame("Skyrim", UmbrellaTagId);

            _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(_logger.WarnLines, Is.Not.Empty,
                    "the one thing this path cannot do is reported, not swallowed");
                Assert.That(string.Join(" | ", _logger.WarnLines), Does.Contain(SourceId).Or.Contain("cached"),
                    "the warning names what was not reachable");
            });
        }

        // -----------------------------------------------------------------------------------------
        // 4: an assignment no record names, caught by what the catalog matches now.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RejectAssignedVrMods_AssignmentWithoutADecision_VetoesTheListingTheCatalogMatchesNow()
        {
            // The one case the catalog is consulted for: a game wearing our tags while no decision names
            // any pairing for it - a tag added by hand, or one an older build applied without asking. The
            // record cannot veto what it never saw, so the listings the catalog matches to that game are
            // vetoed instead. Reachable from the menu in a mixed selection (a decided game makes the item
            // appear, and this legacy game is swept on its own merits); a game with none of our tags is
            // never swept, which is what the test above pins.
            var parser = new TestParser(SourceId, new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(parser);
            var game = AddGame("Skyrim", UmbrellaTagId);
            // Fill the listing cache without accepting anything: no decision, so no tags were applied.
            _service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(TagNames(game), Is.EquivalentTo(new[] { VrModCategories.TagUmbrella }),
                "setup: the tag on this game came from nowhere the plugin recorded");

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(1),
                    "the cached listing that matches this game today gets the veto the record could not give it");
                Assert.That(_settings.GetDecisionForListing(parser.EntryFor("Skyrim"), SourceId, game.Id),
                    Is.EqualTo(MatchDecision.Rejected),
                    "so the next scan shows it as rejected instead of offering it back");
                Assert.That(gamesChanged, Is.EqualTo(1));
                Assert.That(TagNames(game), Is.Empty);
            });
        }

        // -----------------------------------------------------------------------------------------
        // 5 + 6: nothing to do, and already done.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RejectAssignedVrMods_NoSelection_IsANoOp()
        {
            var game = AddGame("Skyrim", UmbrellaTagId);

            Assert.Multiple(() =>
            {
                Assert.That(_service.RejectAssignedVrMods(null), Is.EqualTo((0, 0)));
                Assert.That(_service.RejectAssignedVrMods(new List<Game>()), Is.EqualTo((0, 0)));
                Assert.That(_service.RejectAssignedVrMods(new List<Game> { (Game)null! }), Is.EqualTo((0, 0)));
                Assert.That(TagNames(game), Is.EquivalentTo(new[] { VrModCategories.TagUmbrella }),
                    "and a game nobody selected is untouched");
            });
        }

        [Test]
        public void RejectAssignedVrMods_NothingDecided_WritesNoVetoAndEditsNoTags()
        {
            // A game the plugin has never been asked about: the menu does not offer this item for it (the
            // decision index says so), and the service agrees - it does not invent a veto or guess at tags.
            var game = AddGame("Skyrim", UserTagId);

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(0));
                Assert.That(gamesChanged, Is.EqualTo(0));
                Assert.That(TagNames(game), Is.EquivalentTo(new[] { "Works great on my Index" }));
            });
        }

        [Test]
        public void RejectAssignedVrMods_AlreadyVetoed_PairingIsNotWrittenTwice_TagsStillComeOff()
        {
            var game = AddGame("Skyrim", UmbrellaTagId);
            var key = VrModNameMatcher.MakeDecisionKey(SourceId, "Skyrim UEVR", game.Id);
            _settings.SetDecisionByKey(key, MatchDecision.Rejected);

            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(0), "a no the user already gave is not written again");
                Assert.That(gamesChanged, Is.EqualTo(1),
                    "while the tag that should not be there goes, whatever the decision says");
                Assert.That(TagNames(game), Is.Empty);
                Assert.That(_settings.GetDecisionByKey(key), Is.EqualTo(MatchDecision.Rejected));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Helpers, duplicated per fixture as the other scan tests do (this suite has no shared mock
        // fixture to inherit).
        // -----------------------------------------------------------------------------------------

        private void AddTag(Guid id, string name) => _tags.Items.Add(new Tag(name) { Id = id });

        private Game AddGame(string name, params Guid[] tagIds)
        {
            var game = new Game(name) { TagIds = tagIds.ToList(), Links = new System.Collections.ObjectModel.ObservableCollection<Link>() };
            _games.Items.Add(game);
            return game;
        }

        /// <summary>The names of the tags on a game - what a user reads in the Tags column.</summary>
        private List<string> TagNames(Game game)
            => game.TagIds.Select(id => _tags.Items.FirstOrDefault(t => t.Id == id)?.Name ?? $"<missing {id}>").ToList();

        private class TestParser : BaseVrModParser
        {
            private readonly List<VrModEntry> _entries;

            public TestParser(string sourceId, IEnumerable<VrModEntry> entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries.ToList();
            }

            /// <summary>The listing published under this display name, for keying a decision against it.</summary>
            public VrModEntry EntryFor(string gameName)
                => _entries.Single(e => string.Equals(e.GameName, gameName, StringComparison.OrdinalIgnoreCase));

            public override string? ChangeIdentifier => null;
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(_entries);
        }

        private class RecordingLogger : ILogger
        {
            private readonly List<string> _warn = new();

            /// <summary>A copy: the warn can arrive from a scan's thread while the test thread asserts.</summary>
            public List<string> WarnLines
            {
                get { lock (_warn) return new List<string>(_warn); }
            }

            private void Add(string line)
            {
                lock (_warn) _warn.Add(line);
            }

            public void Warn(string message) => Add(message);
            public void Warn(Exception ex, string message) => Add(message + " " + ex);
            public void Error(string message) { }
            public void Error(Exception ex) { }
            public void Error(Exception ex, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception ex, string message) { }
            public void Info(string message) { }
            public void Info(Exception ex, string message) { }
            public void Debug(string message) { }
            public void Debug(Exception ex, string message) { }
        }
    }
}
